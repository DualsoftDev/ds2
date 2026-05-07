namespace Promaker.LlmAgent;

/// <summary>
/// System prompt 진입점. 본문은 LlmAgent/Prompts/*.md (embedded) + 운영자/사용자 override 디렉토리에서 합성.
/// 자세한 로딩 규칙은 <see cref="PromptLoader"/> 참고.
///
/// 상수명 (Phase1c) 은 호환성 위해 유지 — Phase 진행에 따라 .md 파일들로 누적 갱신.
/// </summary>
public static class SystemPromptText
{
    public static readonly string Phase1c = PromptLoader.LoadComposed();
}
