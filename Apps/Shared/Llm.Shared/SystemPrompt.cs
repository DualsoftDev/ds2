using Llm.Shared.Abstractions;

namespace Llm.Shared;

/// <summary>
/// System prompt 진입점. 본문은 baseline + App overlay (embedded) + 운영자/사용자 override 디렉토리에서 합성.
/// 자세한 로딩 규칙은 <see cref="PromptLoader"/> 참고.
///
/// <para>PR-S1 — App-specific 박제를 <see cref="ILlmAppProfile"/> 로 추출. caller 가 자신의 profile 주입.</para>
///
/// 캐시 없음 (정석). 비용: embedded read + dir glob 2회 ≈ ms 단위, LLM 호출 latency 대비 무시.
/// 캐시 부재로 인해 user prompts dir 의 *.md 편집은 다음 호출 시점에 자동 반영 (Refresh API 불필요).
/// </summary>
public static class SystemPromptText
{
    public static string Phase1c(ILlmAppProfile profile) => PromptLoader.LoadComposed(profile);
}
