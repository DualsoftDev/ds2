namespace Ds2.LlmAgent

open System
open System.Collections.Generic
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

    /// 사용자 prompt 평문 redact — `-p <user msg>` 의 prompt 위치만. 다른 인자 (model / mcp-config /
    /// allowed-tools) 는 노출 유지 (1d-4 B 검증 시나리오에 필요).
    let redactPromptArg (args: string list) : string list =
        let arr = args |> List.toArray
        arr
        |> Array.mapi (fun i a ->
            if i > 0 && arr.[i - 1] = "-p" then sprintf "<redacted, len=%d>" a.Length
            else a)
        |> Array.toList

    /// Phase 1a: ensureMinimum 실패 시 Result.IsValid=false + Message 반환.
    member _.EnsureCli () : ClaudeCliVersion.Result =
        ClaudeCliVersion.ensureMinimum ()

    /// 단일 turn 의 stream 이벤트를 비동기로 흘려보낸다.
    /// 호출자는 `await foreach` 로 소비. 외부 cancellation 으로 중단 가능.
    member this.Send (prompt: string, [<Runtime.InteropServices.Optional>] ?cancellationToken: CancellationToken) : IAsyncEnumerable<LlmEvent> =
        let ct = defaultArg cancellationToken CancellationToken.None
        let args = ClaudeCliArgs.build options sessionId prompt
        let spec : CliProcessHost.Spec = {
            Executable = executable
            Args = args
            EnvOverrides = []
            Redact = redactPromptArg
            Parser = StreamJsonParser.parseLine
            OnSessionStarted = fun sid -> sessionId <- Some sid
            OnProcessStarted =
                options.OnProcessStarted
                |> Option.map (fun cb -> fun proc -> cb.Invoke(proc))
            OnExitNonZero = fun code _stderrLast ->
                $"Claude CLI 비정상 종료 (exit code = {code})."
            Label = "Claude"
            ChannelCapacity = options.ChannelCapacity
        }
        CliProcessHost.runStream spec ct

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
