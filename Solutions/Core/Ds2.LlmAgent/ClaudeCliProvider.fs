namespace Ds2.LlmAgent

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// Claude CLI provider 옵션.
type ClaudeCliOptions = {
    /// `claude` 실행 파일 경로. None 이면 PATH 에서 탐색.
    ExecutablePath: string option
    /// `--mcp-config <path>` 인자.
    McpConfigPath: string option
    /// `--permission-mode` 값. None 이면 인자 미전달 (default).
    PermissionMode: string option
    /// `--model` 값. None 이면 인자 미전달.
    Model: string option
    /// `--append-system-prompt` 본문. None 이면 인자 미전달 (default Claude Code system prompt 그대로 사용).
    SystemPrompt: string option
    /// `--strict-mcp-config` — true 면 `--mcp-config` 외 server 자동 로드 차단 (settings.json 의
    /// 사용자 mcp-server 가 우리 turn 에 끼어들지 않도록). default false (CLI default 동작).
    StrictMcpConfig: bool
    /// `--allowed-tools` 화이트리스트. None 또는 빈 array 면 미전달 (전체 허용). 형식 = `mcp__<server>__<tool>`.
    /// 반복 인자로 전달 (`--allowed-tools T1 --allowed-tools T2 ...`) — 공백 escape / 구분자 변경에 무관하고
    /// CLI 측 호환성 ↑ (단일 인자 + 공백 구분 / 콤마 구분 모두 회피).
    AllowedTools: string array option
    /// stream backpressure 채널 capacity. 기본 256.
    ChannelCapacity: int
    /// Process spawn 직후 호출 callback (1d-5 — Job Object attach 등 lifecycle hook).
    /// None 이면 미호출. 예외 발생해도 provider 동작 영향 없음 (try/with 로 감싸 호출).
    OnProcessStarted: Action<Process> option
} with
    static member Default = {
        ExecutablePath = None
        McpConfigPath = None
        PermissionMode = None
        Model = None
        SystemPrompt = None
        StrictMcpConfig = false
        AllowedTools = None
        ChannelCapacity = 256
        OnProcessStarted = None
    }

/// `claude` CLI 인자 list 빌더. ClaudeCliProvider 외부에서 단위 검증 가능하도록 module-level 노출.
[<RequireQualifiedAccess>]
module ClaudeCliArgs =

    /// 옵션 + sessionId + prompt → CLI 인자 list. process spawn 이전 단계 검증 + golden test 용.
    let build (options: ClaudeCliOptions) (sessionId: string option) (prompt: string) : string list =
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
            match options.SystemPrompt with
            | Some sp ->
                yield "--append-system-prompt"
                yield sp
            | None -> ()
            if options.StrictMcpConfig then
                yield "--strict-mcp-config"
            match options.AllowedTools with
            | Some tools when tools.Length > 0 ->
                for t in tools do
                    yield "--allowed-tools"
                    yield t
            | _ -> ()
        ]

    let quoteArg (s: string) : string =
        if s.Contains(' ') || s.Contains('"') then
            "\"" + s.Replace("\"", "\\\"") + "\""
        else s

    /// 인자 list → 단일 commandline string (공백 구분, 공백/quote 포함 인자는 escape). 로그/검증 용도.
    let formatArgs (args: string list) : string =
        args |> List.map quoteArg |> String.concat " "

/// Multi-turn Claude CLI provider.
///
/// 1 Send = `claude -p <msg> --output-format stream-json --verbose` 1회 spawn.
/// 첫 turn 의 `SessionStarted` 가 `session_id` 를 캡처 → 이후 turn 은 `--resume <sid>` 추가.
/// stream-json 라인 파싱 → `LlmEvent` 시퀀스 → bounded `Channel<LlmEvent>` 로 backpressure.
type ClaudeCliProvider(options: ClaudeCliOptions) =

    let mutable sessionId : string option = None
    let executableName = options.ExecutablePath |> Option.defaultValue "claude"
    /// PATHEXT 자동 검색 fragility 회피 — ctor 1회 fully-qualified 정규화.
    /// 못 찾으면 raw name 으로 fallback (Process.Start 가 자체 PATH 검색 시도, 실패 시 EnsureCli 가 진단).
    let executable = ProcessUtils.findOnPath executableName |> Option.defaultValue executableName

    let buildArgs (prompt: string) : string list = ClaudeCliArgs.build options sessionId prompt
    let formatArgs = ClaudeCliArgs.formatArgs

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
                // M4 — outer try 가 writer 를 complete 한 후 sibling task 가 emit 하면 ChannelClosed.
                // emit 무시하고 silent skip — outer ProviderError 와 이중 emit 회피.
                | :? ChannelClosedException -> ()
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

                // m4 — 사용자 prompt 평문이 log 에 남지 않도록 -p <user msg> 만 redact. 다른 인자 (model / mcp-config / allowed-tools) 는 노출 유지 (1d-4 B 검증 시나리오에 필요).
                let redacted =
                    args
                    |> List.mapi (fun i a ->
                        if i > 0 && args.[i - 1] = "-p" then sprintf "<redacted, len=%d>" a.Length
                        else a)
                Log.provider.Debug($"Spawning: {executable} {formatArgs redacted}")

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

                // 1d-5 — lifecycle hook (Job Object attach 등). 실패해도 provider 진행.
                match options.OnProcessStarted with
                | Some cb ->
                    try cb.Invoke(p) with ex -> Log.provider.Warn("OnProcessStarted callback 실패", ex)
                | None -> ()

                // M4 — inner task 의 unhandled exception 이 sibling task 의 WriteAsync 를 closed channel
                // 으로 보내거나 outer try 까지 escalation 되어 ProviderError 가 두 번 emit 되는 경로 차단.
                // 한 task 가 throw 해도 다른 task 는 ct cancel 또는 EOF 까지 자연 종료.
                let stderrTask =
                    task {
                        try
                            let mutable line = ""
                            while not (isNull line) do
                                let! l = p.StandardError.ReadLineAsync(ct).AsTask()
                                line <- l
                                if not (isNull line) && line.Length > 0 then
                                    Log.provider.Warn($"[claude stderr] {line}")
                        with
                        | :? OperationCanceledException -> ()
                        | ex -> Log.provider.Warn("stderr task 실패", ex)
                    }

                let stdoutTask =
                    task {
                        try
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
                        with
                        | :? OperationCanceledException -> ()
                        | ex ->
                            Log.provider.Warn("stdout task 실패", ex)
                            do! writeAsync(ProviderError $"Stream 파싱 실패: {ex.Message}")
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

    interface ILlmProvider with
        member this.EnsureCli () = this.EnsureCli ()
        // instance method 의 `?cancellationToken: CancellationToken` 에 named arg 로 forward —
        // positional 매핑보다 의도가 명시적 + 향후 instance signature 변경 시 안전성 ↑.
        member this.Send (prompt, ct) = this.Send (prompt, cancellationToken = ct)
        member this.SessionId = this.SessionId
        member this.ClearSession () = this.ClearSession ()
