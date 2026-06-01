namespace Ds2.LlmAgent

open System.Collections.Generic
open System.Threading

/// Multi-turn LLM provider 추상화. Phase 1 단계에서는 `ClaudeCliProvider` 만 1st-class 였고
/// Phase 2 의 `CodexCliProvider` 도입과 함께 인터페이스를 노출해 ChatPanel 측이 dispatch 가능.
///
/// **review M5 후속**: 1차 review 의 "ILlmProvider 5종 over-engineering" 우려에 대해 phase 1 은
/// concrete 1종으로 축소했고, phase 2 진입 시점에 실제 두 번째 concrete (`CodexCliProvider`) 가 동일한
/// 4 surface (EnsureCli / Send / SessionId / ClearSession) 로 정리된 후 인터페이스로 추출.
/// 추가 provider (OpenAI API / Anthropic API / Ollama) 도입 시 본 인터페이스 + provider fallback 정책으로 확장.
///
/// **호출자 계약 (race / concurrency)**:
/// - 동일 instance 의 `Send` 호출은 호출자가 직렬화해야 한다. 이전 turn 의 `IAsyncEnumerable` 소비가 끝나기
///   전에 다음 `Send` 를 시작하면 race — `ClaudeCliProvider`/`CodexCliProvider` 의 `mutable sessionId` 가
///   inconsistent 상태가 되거나 `ApiChatProvider` 의 `_history` (lock-free List) 가 손상될 수 있음.
///   `LlmChatViewModel` 측은 `_cts` + `IsSending` flag 로 직렬화 보장.
/// - `ClearSession` 도 진행 중 turn 이 없을 때만 안전. provider 측 lock 부재.
/// - 비동기 cancel 은 `Send` 호출 시 `cancellationToken` 으로 — 중간에 ClearSession 부르지 않을 것.
type ILlmProvider =
    /// CLI 존재 검증. 실패 시 `IsValid=false` + Message 포함. provider 별 검증 정책 차이는
    /// `ClaudeCliVersion.Result` schema 를 공통 record 로 재활용 — 새 provider 가 도입돼도 schema 변경 없이
    /// 자체 검증 정책으로 IsValid/Message/VersionString 셋팅 (예: Codex 는 SemVer 강제 X, 단순 `--version` 호출).
    abstract EnsureCli : unit -> ClaudeCliVersion.Result

    /// 단일 turn 의 stream 이벤트를 비동기로 흘려보낸다. `await foreach` 로 소비.
    /// `cancellationToken` cancel 시 background process kill + ProviderError emit + writer.Complete().
    ///
    /// rev 4 (2026-05-09 / commit-2): `string prompt` → `LlmUserMessage msg` 마이그레이션.
    /// 현 단계에서는 어댑터 모두 `msg.Attachments` 를 무시하고 `msg.Text` 만 사용 (회귀 호환).
    /// 첨부 wire 는 commit-4..N (UI) + Phase 3b (PDF) 에서 점증적으로 활성화.
    abstract Send : msg: LlmUserMessage * cancellationToken: CancellationToken -> IAsyncEnumerable<LlmEvent>

    /// 현재 캡처된 sessionId. 첫 turn 전에는 None. resume 인자 빌드 / UI status 표시에 사용.
    abstract SessionId : string option

    /// Session 강제 초기화. 다음 Send 가 새 session 으로 시작.
    abstract ClearSession : unit -> unit

    /// 본 provider 의 multimodal 능력. Claude/Codex CLI 는 `EnsureCli` 시점 정적 확정,
    /// Ollama 는 모델별 `/api/show` 동적 갱신. 첨부 미지원 provider 는 `Capabilities.TextOnly`.
    abstract Capabilities : Capabilities

/// system prompt 에 RAG digest 를 lazy 주입하는 sink. provider 가 본 인터페이스를 구현하면
/// `LlmChatViewModel` 의 layer A(KB keyword digest) / layer E(specialized digest) 적용 path 가
/// `is ILlmSystemPromptDigestSink` 캐스팅으로 진입 → CLI / API provider 모두 동일 wire.
///
/// **구현체 계약**:
/// - 두 Set 은 background thread (SSE invalidate / provider 토글) 에서 호출 가능 → 구현체가 race-free 박제.
/// - 빈 string = digest 비활성 (이전 digest 제거). null 인자도 빈 string 으로 정규화.
/// - 실제 적용 시점은 구현체 정책 — API provider 는 firstTurn swap, CLI provider 도 firstTurn(=sessionId None)
///   고정 (turn 중간 digest 변경으로 CLI prompt cache 가 흔들리지 않게).
///
/// **cross-language**: 본 인터페이스는 F# 에 두지만 C# `ApiChatProvider` 가 구현 (이미 F# `ILlmProvider` 를
/// C#이 구현 중이라 동일 패턴). 메서드 시그니처는 ApiChatProvider 의 기존 `SetPendingSystemPrompt(string)` /
/// `SetPendingSpecializedDigest(string)` 와 1:1.
type ILlmSystemPromptDigestSink =
    /// KB keyword digest(layer A). 빈 string = 비활성.
    abstract SetPendingSystemPrompt : digest: string -> unit
    /// specialized digest(layer E). 빈 string = 비활성.
    abstract SetPendingSpecializedDigest : digest: string -> unit

/// CLI provider 의 effective system prompt 합성 SSOT. ApiChatProvider 의 `SystemContentBuilder.Build`
/// (별 TextContent + cache_control) 와 동치 wire 를 단일 string 으로 표현 — CLI 는 system-prompt-file 이
/// 한 덩어리라 breakpoint 분리는 불가하나 본문 순서/구성은 동일 (base → KB digest → specialized digest).
[<RequireQualifiedAccess>]
module SystemPromptDigest =
    /// base(Phase1c) + (kbDigest 있으면 "\n\n"+digest) + (specialized 있으면 "\n\n"+digest).
    /// `--system-prompt-file`(Claude) / `experimental_instructions_file`(Codex) 는 CLI default 를 완전
    /// 치환하므로 base 를 반드시 포함. 두 digest 모두 빈 시 base 단독 (회귀 0).
    let compose (basePrompt: string) (kbDigest: string) (specializedDigest: string) : string =
        let sb = System.Text.StringBuilder(basePrompt)
        if not (System.String.IsNullOrEmpty kbDigest) then
            sb.Append("\n\n").Append(kbDigest) |> ignore
        if not (System.String.IsNullOrEmpty specializedDigest) then
            sb.Append("\n\n").Append(specializedDigest) |> ignore
        sb.ToString()
