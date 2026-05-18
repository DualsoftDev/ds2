namespace Promaker.LlmAgent;

/// <summary>
/// System prompt 진입점. 본문은 LlmAgent/Prompts/*.md (embedded) + 운영자/사용자 override 디렉토리에서 합성.
/// 자세한 로딩 규칙은 <see cref="PromptLoader"/> 참고.
///
/// 상수명 (Phase1c) 은 호환성 위해 유지. 매 access 마다 <see cref="PromptLoader.LoadComposed"/> 호출 —
/// 캐시 없음 (정석). 비용: embedded read + dir glob 2회 ≈ ms 단위, LLM 호출 latency 대비 무시.
/// 캐시 부재로 인해 user prompts dir 의 *.md 편집은 다음 호출 시점에 자동 반영 (Refresh API 불필요).
/// Codex provider 만 instructions.md 파일을 1회 write 하므로 별도 재기록 필요 (<see cref="LlmChatViewModel"/> 참조).
/// </summary>
public static class SystemPromptText
{
    public static string Phase1c => PromptLoader.LoadComposed();
}
