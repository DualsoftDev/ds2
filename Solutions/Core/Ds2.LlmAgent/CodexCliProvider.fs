namespace Ds2.LlmAgent

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// Multi-turn Codex CLI provider (Phase 2 C-3).
///
/// 1 Send = `codex exec [OPTS] <prompt>` 1회 spawn (또는 `codex exec resume <sid> <prompt>` resume 시).
/// 첫 turn 의 `SessionStarted (thread_id)` 캡처 → 이후 turn 은 `exec resume` subcommand.
/// JSONL 라인 파싱 → `LlmEvent` 시퀀스 → bounded `Channel<LlmEvent>` (Claude provider 와 동일 backpressure 패턴).
///
/// **Claude provider 와의 동작 차이**:
/// - streaming 미관찰 — `item.completed` 1회 패킷 (전체 응답 포함). ChatPanel AssistantDelta throttle 은 noop.
/// - mcp_servers init 패킷 부재 → SessionStarted.mcpServers 는 빈 list. 호출자가 별도 ready 확인 필요
///   (in-process Kestrel HTTP GET / retry, 또는 첫 tool call 실패 시 재시도).
/// - turn.completed.usage 의 cost/duration 정보 LlmEvent 에 미반영 (placeholder 0). 보존이 필요하면
///   LlmEvent 새 case 또는 SessionEnd 확장 — phase 후속.
///
/// **review M5 인정**: ClaudeCliProvider 와 거의 같은 구조지만 인자/parser/executable 차이로 별도 type.
/// 인터페이스 추상화는 phase 후속 (provider fallback 정책 도입 시점). 현 단계는 concrete 1종 추가.
type CodexCliProvider(options: CodexCliOptions) =

    let mutable sessionId : string option = None
    let executableName = options.ExecutablePath |> Option.defaultValue "codex"
    /// PATHEXT 자동 검색 fragility 회피 — ctor 1회 PATH 검색 결과를 캐시. EnsureCli + Send 가 공유.
    /// 일부 사용자 환경 (Promaker process 의 PATH 가 셸 PATH 와 다른 경우) 에서 npm 글로벌 디렉토리
    /// (`%APPDATA%\npm`) 를 ProcessUtils.wellKnownFallbackDirs 로 보완.
    let resolveResult = ProcessUtils.resolveOrDiagnostic executableName
    let executable =
        match resolveResult with
        | Ok p -> p
        | Error _ -> executableName

    let buildArgs (prompt: string) : string list = CodexCliArgs.build options sessionId prompt
    /// formatArgs 는 provider 무관 logger format 용 — Claude module 의 동일 함수 재활용.
    let formatArgs = ClaudeCliArgs.formatArgs

    /// Codex CLI 존재 검증. Claude 의 SemVer 최소버전 강제와 달리 단순 `--version` 호출만 검증
    /// (0.125.0 인자 형식 검증 완료, 향후 버전 분기 필요 시 별도 CodexCliVersion module 도입).
    /// 반환: ClaudeCliVersion.Result (provider 무관 schema 재활용).
    /// 패턴 정합: ClaudeCliVersion.tryGetInstalledVersion 과 동일하게 `WaitForExit |> ignore` + `HasExited && ExitCode=0` 분기로
    /// timeout / non-zero exit / normal exit 케이스를 메시지에 구분 (forensic 단서).
    member _.EnsureCli () : ClaudeCliVersion.Result =
        // PATH 누락은 process 시작 전에 미리 진단 — ctor 의 resolveResult 캐시 재사용 (재검색 회피).
        match resolveResult with
        | Error hint ->
            { ClaudeCliVersion.Result.IsValid = false
              Message = $"Codex CLI 검출 실패 — {hint}"
              VersionString = "" }
        | Ok _ ->
            try
                let psi = ProcessStartInfo(executable, "--version")
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                use p = Process.Start(psi)
                p.WaitForExit(5000) |> ignore
                if not p.HasExited then
                    { ClaudeCliVersion.Result.IsValid = false
                      Message = $"`{executable} --version` 5초 timeout — process 가 응답하지 않음"
                      VersionString = "" }
                elif p.ExitCode <> 0 then
                    { ClaudeCliVersion.Result.IsValid = false
                      Message = $"`{executable} --version` 비정상 종료 (exit code = {p.ExitCode})"
                      VersionString = "" }
                else
                    let ver = p.StandardOutput.ReadToEnd().Trim()
                    { ClaudeCliVersion.Result.IsValid = true; Message = "ok"; VersionString = ver }
            with ex ->
                Log.provider.Warn($"Failed to invoke `{executable} --version`: {ex.Message}")
                { ClaudeCliVersion.Result.IsValid = false
                  Message = $"`{executable} --version` 실행 예외: {ex.Message}"
                  VersionString = "" }

    /// 단일 turn 의 stream 이벤트를 비동기로 흘려보낸다.
    /// 호출자는 `await foreach` 로 소비. 외부 cancellation 으로 중단 가능 (process kill).
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
                // CODEX_HOME 격리 — codex 가 sessions/config/log 를 인스턴스별 임시 디렉토리에 둠.
                // 사용자 ~/.codex/sessions/ 에 thread rollout 누적 회피 + 워크스페이스 재귀 삭제 시 자동 cleanup.
                match options.CodexHome with
                | Some h -> psi.Environment.["CODEX_HOME"] <- h
                | None -> ()

                // 사용자 prompt 평문 redact — `codex exec [resume <sid>] [OPTS...] <prompt>` 의 마지막 위치 인자가 prompt.
                // resume 시 sessionId 도 위치 인자지만 prompt 가 항상 마지막 (CodexCliArgs.build 보장).
                let redacted =
                    args
                    |> List.mapi (fun i a ->
                        if i = args.Length - 1 then sprintf "<redacted, len=%d>" a.Length
                        else a)
                Log.provider.Debug($"Spawning: {executable} {formatArgs redacted}")

                let mutable proc : Process = null
                try
                    proc <- Process.Start(psi)
                with ex ->
                    do! writeAsync(ProviderError $"Codex CLI 실행 실패: {ex.Message}")
                    writer.TryComplete() |> ignore
                    return ()

                if isNull proc then
                    do! writeAsync(ProviderError "Codex CLI process 시작 실패 (null process).")
                    writer.TryComplete() |> ignore
                    return ()

                use p = proc

                // stderr last non-empty line 캡처 → exit code != 0 시 ProviderError 메시지에 append.
                // ChatPanel 에서 codex 의 실제 에러 (auth / mcp connect / sandbox 등) 를 즉시 확인 가능.
                let mutable lastStderr : string = ""

                let stderrTask =
                    task {
                        try
                            let mutable line = ""
                            while not (isNull line) do
                                let! l = p.StandardError.ReadLineAsync(ct).AsTask()
                                line <- l
                                if not (isNull line) && line.Length > 0 then
                                    lastStderr <- line
                                    Log.provider.Warn($"[codex stderr] {line}")
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
                                    for evt in CodexStreamJsonParser.parseLine line do
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
                            let suffix =
                                if String.IsNullOrEmpty lastStderr then ""
                                else $" — stderr: {lastStderr}"
                            do! writeAsync(ProviderError $"Codex CLI 비정상 종료 (exit code = {p.ExitCode}){suffix}.")
                    with
                    | :? OperationCanceledException ->
                        do! writeAsync(ProviderError "사용자 취소.")
                    | ex ->
                        do! writeAsync(ProviderError $"Stream 파싱 실패: {ex.Message}")
                finally
                    killOnCancel.Dispose()
                    writer.TryComplete() |> ignore
            }

        let bg = runProcess()
        bg.ContinueWith(fun (t: Task) ->
            if t.IsFaulted && not (isNull t.Exception) then
                Log.provider.Error("CodexCliProvider background task faulted", t.Exception)
            writer.TryComplete() |> ignore
        ) |> ignore

        reader.ReadAllAsync(ct)

    /// 현재 캡처된 thread_id (`exec resume` 의 sessionId 위치 인자로 사용). 첫 turn 전에는 None.
    member _.SessionId = sessionId

    /// Session 강제 초기화 — 다음 Send 가 `resume` 없이 새 thread 시작.
    member _.ClearSession () = sessionId <- None

    interface ILlmProvider with
        member this.EnsureCli () = this.EnsureCli ()
        // instance method 의 `?cancellationToken: CancellationToken` 에 named arg 로 forward.
        member this.Send (prompt, ct) = this.Send (prompt, cancellationToken = ct)
        member this.SessionId = this.SessionId
        member this.ClearSession () = this.ClearSession ()
