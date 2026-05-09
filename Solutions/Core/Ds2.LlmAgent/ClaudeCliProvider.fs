namespace Ds2.LlmAgent

open System
open System.Collections.Generic
open System.IO
open System.Threading

/// Multi-turn Claude CLI provider.
///
/// 1 Send = `claude -p <msg> --output-format stream-json --verbose` 1회 spawn.
/// 첫 turn 의 `SessionStarted` 가 `session_id` 를 캡처 → 이후 turn 은 `--resume <sid>` 추가.
/// stream-json 라인 파싱 → `LlmEvent` 시퀀스 → bounded `Channel<LlmEvent>` 로 backpressure.
///
/// **process spawn / channel / stdout-stderr loop / cancel hook 골격**은 `CliProcessHost.runStream` 가 책임.
/// 본 type 은 옵션 → spec 변환 + sessionId capture / EnsureCli 만 담당.
type ClaudeCliProvider(options: ClaudeCliOptions) =

    let mutable sessionId : string option = None
    let executableName = options.ExecutablePath |> Option.defaultValue "claude"
    /// PATHEXT 자동 검색 fragility 회피 — ctor 1회 fully-qualified 정규화.
    /// 못 찾으면 raw name 으로 fallback (Process.Start 가 자체 PATH 검색 시도, 실패 시 EnsureCli 가 진단).
    let executable = ProcessUtils.findOnPath executableName |> Option.defaultValue executableName

    /// Phase 1a: ensureMinimum 실패 시 Result.IsValid=false + Message 반환.
    member _.EnsureCli () : ClaudeCliVersion.Result =
        ClaudeCliVersion.ensureMinimum ()

    /// 단일 turn 의 stream 이벤트를 비동기로 흘려보낸다.
    /// 호출자는 `await foreach` 로 소비. 외부 cancellation 으로 중단 가능.
    ///
    /// rev 4 (commit-2): `LlmUserMessage` 수신 — 현 단계 `msg.Attachments` 무시. 첨부 wire 는 commit-4..N 에서.
    member this.Send (msg: LlmUserMessage, [<Runtime.InteropServices.Optional>] ?cancellationToken: CancellationToken) : IAsyncEnumerable<LlmEvent> =
        let ct = defaultArg cancellationToken CancellationToken.None
        // review 2차 M4 — capability 미지원 첨부 silent drop 방지 (warn). commit-6 wire 진입 시 strict 모드 전환.
        LlmUserMessageOps.WarnUnsupportedAttachments CapabilityPresets.AnthropicWire msg
        let prompt = msg.Text

        // SystemPrompt 본문은 임시 파일에 저장 후 `--append-system-prompt-file <path>` 로 전달.
        // **Why**: Windows CreateProcess 의 lpCommandLine 32K 한계 초과 시 [WinError 206]
        // ("파일 이름이나 확장명이 너무 깁니다") 가 발생. 본 회피 패턴은 Ev2.Oracle 의 ClaudeCliProvider
        // (`solutions/Ev2.Backend/src/Ev2.Oracle/Builder/pipeline/llm_providers/claude_cli.py`) 에서 동일
        // 사고 (HelloDSKit 사고, ~43KB 본문) 후 정착된 방식.
        let systemPromptFile, cleanupSystemPromptFile =
            match options.SystemPrompt with
            | Some sp when not (String.IsNullOrEmpty sp) ->
                let dir = Path.Combine(Path.GetTempPath(), "Promaker")
                Directory.CreateDirectory(dir) |> ignore
                let path = Path.Combine(dir, sprintf "claude-append-system-%s.md" (Guid.NewGuid().ToString("N")))
                File.WriteAllText(path, sp, Text.Encoding.UTF8)
                let cleanup () =
                    try if File.Exists path then File.Delete path
                    with ex -> Log.provider.Warn($"임시 system-prompt 파일 삭제 실패 ({path}): {ex.Message}", ex)
                Some path, cleanup
            | _ ->
                None, (fun () -> ())

        let args = ClaudeCliArgs.build options sessionId systemPromptFile
        let spec : CliProcessHost.Spec = {
            Executable = executable
            Args = args
            EnvOverrides = []
            // prompt 본문이 args 에서 stdin 으로 옮겨진 이후로는 args 에 평문 prompt 가 없어 redact 불필요.
            Redact = id
            Parser = StreamJsonParser.parseLine
            OnSessionStarted = fun sid -> sessionId <- Some sid
            OnProcessStarted =
                options.OnProcessStarted
                |> Option.map (fun cb -> fun proc -> cb.Invoke(proc))
            OnExitNonZero = fun code _stderrLast ->
                $"Claude CLI 비정상 종료 (exit code = {code})."
            Label = "Claude"
            ChannelCapacity = options.ChannelCapacity
            // user prompt 본문도 stdin 으로 전달 — 32K 한도 회피 + Ev2.Oracle 동일 패턴.
            Stdin = Some prompt
            OnFinally = cleanupSystemPromptFile
        }
        CliProcessHost.runStream spec ct

    /// 현재 캡처된 session_id (`--resume` 인자로 사용). 첫 turn 전에는 None.
    member _.SessionId = sessionId

    /// Session 강제 초기화 (Reset 명령). 다음 Send 가 `--resume` 없이 새 세션 시작.
    member _.ClearSession () = sessionId <- None

    /// Capabilities — Anthropic API 와 동일 (CLI 가 stream-json 으로 동일 multipart wire).
    /// 이미지 4종 (png/jpg/gif/webp) base64 inline 5MB + PDF document block 32MB. SSOT = `CapabilityPresets.AnthropicWire`.
    member _.Capabilities = CapabilityPresets.AnthropicWire

    interface ILlmProvider with
        member this.EnsureCli () = this.EnsureCli ()
        // instance method 의 `?cancellationToken: CancellationToken` 에 named arg 로 forward —
        // positional 매핑보다 의도가 명시적 + 향후 instance signature 변경 시 안전성 ↑.
        member this.Send (msg, ct) = this.Send (msg, cancellationToken = ct)
        member this.SessionId = this.SessionId
        member this.ClearSession () = this.ClearSession ()
        member this.Capabilities = this.Capabilities
