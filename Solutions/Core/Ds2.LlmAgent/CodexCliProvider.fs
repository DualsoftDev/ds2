namespace Ds2.LlmAgent

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading

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
/// **process spawn / channel / stdout-stderr loop / cancel hook 골격**은 `CliProcessHost.runStream` 가 책임.
/// 본 type 은 옵션 → spec 변환 + sessionId capture / EnsureCli (PATH resolve + `--version` 5초 timeout) 만.
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

    /// 사용자 prompt 평문 redact — `codex exec [resume <sid>] [OPTS...] <prompt>` 의 마지막 위치 인자가 prompt.
    /// resume 시 sessionId 도 위치 인자지만 prompt 가 항상 마지막 (CodexCliArgs.build 보장).
    let redactLastArg (args: string list) : string list =
        let lastIdx = args.Length - 1
        args
        |> List.mapi (fun i a ->
            if i = lastIdx then sprintf "<redacted, len=%d>" a.Length
            else a)

    /// Codex CLI 존재 검증. Claude 의 SemVer 최소버전 강제와 달리 단순 `--version` 호출만 검증
    /// (0.125.0 인자 형식 검증 완료, 향후 버전 분기 필요 시 별도 CodexCliVersion module 도입).
    /// 반환: ClaudeCliVersion.Result (provider 무관 schema 재활용).
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
    ///
    /// rev 4 (commit-2): `LlmUserMessage` 수신 — 현 단계 `msg.Attachments` 무시. 첨부 wire 는 commit-4..N 에서
    /// (`-i/--image <FILE>...` path 인자 + bytes 시 임시 파일 spool — spike S-2 결과).
    member this.Send (msg: LlmUserMessage, [<Runtime.InteropServices.Optional>] ?cancellationToken: CancellationToken) : IAsyncEnumerable<LlmEvent> =
        let ct = defaultArg cancellationToken CancellationToken.None
        // commit-6b — strict 모드. PDF/미지원 image format 도달 시 invalidArg fail-fast (silent drop 차단).
        LlmUserMessageOps.EnforceCapabilityOrFail CapabilityPresets.CodexCliWire msg
        let prompt = msg.Text

        // commit-6b — 이미지 첨부 bytes → 임시 파일 spool. Codex CLI 는 path 만 받음 (`-i <path>`).
        // 위치: %TEMP%\Promaker.LlmAgent\codex-img-<turnGuid>\<n>.<ext>  (단일 cleanup 위해 turn 별 디렉토리)
        // 확장자는 ImageFormat 으로 결정 — rev 18 m3 일원화 후 4 case exhaustive (`.bin` dead fallback 제거).
        // turn 종료 시 OnFinally 로 디렉토리 재귀 삭제.
        let images =
            if isNull (box msg.Attachments) then [||]
            else
                msg.Attachments
                |> Array.choose (fun a ->
                    match a with
                    | Image (n, b, fmt) -> Some (n, b, fmt)
                    | _ -> None)
        let imagePaths, cleanupImageSpool =
            if images.Length = 0 then
                [||], (fun () -> ())
            else
                let dir = Path.Combine(Path.GetTempPath(), "Promaker.LlmAgent", "codex-img-" + Guid.NewGuid().ToString("N"))
                let cleanup () =
                    try
                        if Directory.Exists dir then Directory.Delete(dir, true)
                    with ex ->
                        Log.provider.Warn($"Codex 임시 이미지 디렉토리 삭제 실패 ({dir}): {ex.Message}", ex)
                // M1 fix — spool 단계에서 IO throw (디스크 풀 / 권한) 시 partial 파일 + dir 즉시 정리.
                // OnFinally 는 runStream 진입 후에만 호출되므로 진입 전 leak 을 자체 try/with 로 방어.
                Directory.CreateDirectory(dir) |> ignore
                try
                    // rev 18 review 후속: inline extOf 폐기 — Attachment.extOf SSOT 위임.
                    let paths =
                        images
                        |> Array.mapi (fun i (_, bytes, fmt) ->
                            let path = Path.Combine(dir, sprintf "%d%s" i (Attachment.extOf fmt))
                            File.WriteAllBytes(path, bytes)
                            path)
                    paths, cleanup
                with ex ->
                    cleanup ()
                    reraise ()

        let args = CodexCliArgs.buildWith options sessionId prompt imagePaths
        // CODEX_HOME 격리 — codex 가 sessions/config/log 를 인스턴스별 임시 디렉토리에 둠.
        // 사용자 ~/.codex/sessions/ 에 thread rollout 누적 회피 + 워크스페이스 재귀 삭제 시 자동 cleanup.
        let envOverrides =
            match options.CodexHome with
            | Some h -> [ "CODEX_HOME", h ]
            | None -> []
        let spec : CliProcessHost.Spec = {
            Executable = executable
            Args = args
            EnvOverrides = envOverrides
            Redact = redactLastArg
            Parser = CodexStreamJsonParser.parseLine
            OnSessionStarted = fun sid -> sessionId <- Some sid
            OnProcessStarted = None
            OnExitNonZero = fun code stderrLast ->
                let suffix =
                    if String.IsNullOrEmpty stderrLast then ""
                    else $" — stderr: {stderrLast}"
                $"Codex CLI 비정상 종료 (exit code = {code}){suffix}."
            Label = "Codex"
            ChannelCapacity = options.ChannelCapacity
            // Codex 는 prompt 가 위치 인자 (codex exec [resume <sid>] [OPTS] <prompt>) — stdin 미사용.
            // 32K 한도 도달 가능성은 별도 spike 후 결정.
            Stdin = None
            OnFinally = cleanupImageSpool
        }
        CliProcessHost.runStream spec ct

    /// M1 stale sweep — Promaker 비정상 종료 / OnFinally cleanup 실패로 남은 `codex-img-*` 임시 디렉토리 회수.
    /// App.OnStartup 1회 호출 (McpConfigWriter.SweepStale 와 같은 패턴). mtime > 30분 + 자기 PID 외 디렉토리만 정리.
    static member SweepStale () : unit =
        let root = Path.Combine(Path.GetTempPath(), "Promaker.LlmAgent")
        if Directory.Exists root then
            let cutoff = DateTime.UtcNow.AddMinutes(-30.0)
            // eager array — enumerate-during-delete race 회피 (Sweep 은 OnStartup 1회라 부하 무시 가능).
            for dir in Directory.GetDirectories(root, "codex-img-*") do
                try
                    if Directory.GetLastWriteTimeUtc(dir) < cutoff then
                        Directory.Delete(dir, true)
                with ex ->
                    Log.provider.Warn($"Codex stale sweep 실패 ({dir}): {ex.Message}", ex)

    /// 현재 캡처된 thread_id (`exec resume` 의 sessionId 위치 인자로 사용). 첫 turn 전에는 None.
    member _.SessionId = sessionId

    /// Session 강제 초기화 — 다음 Send 가 `resume` 없이 새 thread 시작.
    member _.ClearSession () = sessionId <- None

    /// Capabilities — Codex CLI 0.128.0 spike S-2 결과: `-i/--image <FILE>...` path 기반.
    /// PDF 옵션 부재 → 미지원. 이미지 cap 은 OpenAI 기준 추정 20MB. SSOT = `CapabilityPresets.CodexCliWire`.
    member _.Capabilities = CapabilityPresets.CodexCliWire

    interface ILlmProvider with
        member this.EnsureCli () = this.EnsureCli ()
        // instance method 의 `?cancellationToken: CancellationToken` 에 named arg 로 forward.
        member this.Send (msg, ct) = this.Send (msg, cancellationToken = ct)
        member this.SessionId = this.SessionId
        member this.ClearSession () = this.ClearSession ()
        member this.Capabilities = this.Capabilities
