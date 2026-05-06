namespace Ds2.LlmAgent

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// Phase 1a Claude CLI provider 옵션. mutation tool / mcp-config 는 phase 1b 에서 채움.
type ClaudeCliOptions = {
    /// `claude` 실행 파일 경로. None 이면 PATH 에서 탐색.
    ExecutablePath: string option
    /// `--mcp-config <path>` 인자. phase 1a 에선 None.
    McpConfigPath: string option
    /// `--permission-mode` 값. None 이면 인자 미전달 (default).
    PermissionMode: string option
    /// `--model` 값. None 이면 인자 미전달.
    Model: string option
    /// stream backpressure 채널 capacity. 기본 256.
    ChannelCapacity: int
} with
    static member Default = {
        ExecutablePath = None
        McpConfigPath = None
        PermissionMode = None
        Model = None
        ChannelCapacity = 256
    }

/// Multi-turn Claude CLI provider.
///
/// 1 Send = `claude -p <msg> --output-format stream-json --verbose` 1회 spawn.
/// 첫 turn 의 `SessionStarted` 가 `session_id` 를 캡처 → 이후 turn 은 `--resume <sid>` 추가.
/// stream-json 라인 파싱 → `LlmEvent` 시퀀스 → bounded `Channel<LlmEvent>` 로 backpressure.
///
/// Phase 1a 범위:
///   - mutation tool 미통합 (echo only)
///   - Job Object attach 미적용 (Promaker 정상 종료 시 process orphan 가능 — phase 1b 추가)
///   - 구현은 concrete (인터페이스 없음, phase 2 에서 패턴 추출)
type ClaudeCliProvider(options: ClaudeCliOptions) =

    let mutable sessionId : string option = None
    let executable = options.ExecutablePath |> Option.defaultValue "claude"

    let buildArgs (prompt: string) : string list =
        [
            yield "-p"
            yield prompt
            yield "--output-format"
            yield "stream-json"
            yield "--verbose"
            match sessionId with
            | Some sid ->
                yield "--resume"
                yield sid
            | None -> ()
            match options.McpConfigPath with
            | Some path ->
                yield "--mcp-config"
                yield path
            | None -> ()
            match options.PermissionMode with
            | Some mode ->
                yield "--permission-mode"
                yield mode
            | None -> ()
            match options.Model with
            | Some m ->
                yield "--model"
                yield m
            | None -> ()
        ]

    let quoteArg (s: string) : string =
        if s.Contains(' ') || s.Contains('"') then
            "\"" + s.Replace("\"", "\\\"") + "\""
        else s

    let formatArgs (args: string list) : string =
        args |> List.map quoteArg |> String.concat " "

    /// Phase 1a: ensureMinimum 실패 시 Result.IsValid=false + Message 반환.
    member _.EnsureCli () : ClaudeCliVersion.Result =
        ClaudeCliVersion.ensureMinimum ()

    /// 단일 turn 의 stream 이벤트를 비동기로 흘려보낸다.
    /// 호출자는 `await foreach` 로 소비. 외부 cancellation 으로 중단 가능.
    member this.Send (prompt: string, [<Runtime.InteropServices.Optional>] ?cancellationToken: CancellationToken) : IAsyncEnumerable<LlmEvent> =
        let ct = defaultArg cancellationToken CancellationToken.None
        let opts = BoundedChannelOptions(options.ChannelCapacity, FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false)
        let channel = Channel.CreateBounded<LlmEvent>(opts)

        let writer = channel.Writer
        let reader = channel.Reader

        let writeAsync (evt: LlmEvent) : Task =
            task {
                try
                    do! writer.WriteAsync(evt, ct)
                with
                | :? OperationCanceledException -> ()
            } :> Task

        let runProcess () =
            task {
                let psi = ProcessStartInfo(executable)
                let args = buildArgs prompt
                for a in args do psi.ArgumentList.Add(a)
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                psi.StandardOutputEncoding <- System.Text.Encoding.UTF8
                psi.StandardErrorEncoding <- System.Text.Encoding.UTF8

                Log.provider.Debug($"Spawning: {executable} {formatArgs args}")

                let mutable proc : Process = null
                try
                    proc <- Process.Start(psi)
                with ex ->
                    do! writeAsync(ProviderError $"Claude CLI 실행 실패: {ex.Message}")
                    writer.TryComplete() |> ignore
                    return ()

                if isNull proc then
                    do! writeAsync(ProviderError "Claude CLI process 시작 실패 (null process).")
                    writer.TryComplete() |> ignore
                    return ()

                use p = proc

                let stderrTask =
                    task {
                        let mutable line = ""
                        while not (isNull line) do
                            let! l = p.StandardError.ReadLineAsync(ct).AsTask()
                            line <- l
                            if not (isNull line) && line.Length > 0 then
                                Log.provider.Warn($"[claude stderr] {line}")
                    }

                let stdoutTask =
                    task {
                        let mutable line = ""
                        while not (isNull line) do
                            let! l = p.StandardOutput.ReadLineAsync(ct).AsTask()
                            line <- l
                            if not (isNull line) then
                                if Log.rawStream.IsDebugEnabled then
                                    Log.rawStream.Debug(line)
                                for evt in StreamJsonParser.parseLine line do
                                    match evt with
                                    | SessionStarted(sid, _, _, _) ->
                                        sessionId <- Some sid
                                    | _ -> ()
                                    do! writeAsync evt
                    }

                let killOnCancel =
                    ct.Register(fun () ->
                        try
                            if not p.HasExited then p.Kill(true)
                        with _ -> ())

                try
                    try
                        let! _ = Task.WhenAll(stdoutTask :> Task, stderrTask :> Task)
                        p.WaitForExit()
                        if p.ExitCode <> 0 then
                            do! writeAsync(ProviderError $"Claude CLI 비정상 종료 (exit code = {p.ExitCode}).")
                    with
                    | :? OperationCanceledException ->
                        do! writeAsync(ProviderError "사용자 취소.")
                    | ex ->
                        do! writeAsync(ProviderError $"Stream 파싱 실패: {ex.Message}")
                finally
                    killOnCancel.Dispose()
                    writer.TryComplete() |> ignore
            }

        // fire and forget — channel 이 종료되면 reader 가 자연스럽게 끝남.
        // ContinueWith 로 unobserved exception 안전망 (writer 가 이미 complete 상태일 수 있어 emit X, 로그만).
        let bg = runProcess()
        bg.ContinueWith(fun (t: Task) ->
            if t.IsFaulted && not (isNull t.Exception) then
                Log.provider.Error("ClaudeCliProvider background task faulted", t.Exception)
            writer.TryComplete() |> ignore
        ) |> ignore

        reader.ReadAllAsync(ct)

    /// 현재 캡처된 session_id (`--resume` 인자로 사용). 첫 turn 전에는 None.
    member _.SessionId = sessionId

    /// Session 강제 초기화 (Reset 명령). 다음 Send 가 `--resume` 없이 새 세션 시작.
    member _.ClearSession () = sessionId <- None
