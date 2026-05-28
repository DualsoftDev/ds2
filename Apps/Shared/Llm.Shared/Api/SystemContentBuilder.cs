using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace Llm.Shared.Api;

/// <summary>
/// **PR-G (todo-lighthouse-index-summary.md §5.2 v-b)** — system ChatMessage 의 AIContent 목록 빌더.
/// <para/>
/// 의도: base prompt 와 KB digest, specialized digest 를 *분리된 TextContent* 로 박제 → 각각에
/// cache_control: ephemeral 부착 → Anthropic prompt cache 의 prefix-match 가 base 영역 (chat-lifetime 불변),
/// KB digest 영역 (KB 변경 시 갱신), specialized digest 영역 (자료 재색인 시 갱신) 의 cache breakpoint 분리.
/// <list type="bullet">
///   <item>digest 둘 다 빈 시 → base 1 TextContent 만 (회귀 0 — 기존 단일 prompt 와 동치)</item>
///   <item>KB digest 만 박제 → base + KB digest 2 TextContent (PR-G v-b 회귀)</item>
///   <item>**PR-I4** — specialized digest 도 박제 시 → base + KB digest + specialized digest 3 TextContent
///         (breakpoint 3/4, snapshot 합산 4/4 cap)</item>
/// </list>
/// <para/>
/// **PR-I4 (todo-documents-based-gfm.md §2 PR-I4 + §8.3 / documents-based-gfm.md §8.3)** — 3번째 cache breakpoint
/// = `SpecializedDigestBuilder.build` 산출 markdown 합본 (`.lighthouse-kb/summary/*.md`). 자료 A/B/C 의 strategy
/// 정제 markdown (IoList ~30K + WorkOrder ~3K + PdfControlSpec ~5K ≈ 38K tokens) 박제. chat 시작 시 강제 주입 —
/// LLM 이 첫 turn 부터 전문 인식. 자료 재색인 시 본 breakpoint 만 invalidate (base / KB digest 영역 cache hit 유지).
/// <para/>
/// breakpoint 순서:
/// <list type="number">
///   <item>base system prompt — chat-lifetime 불변</item>
///   <item>KB digest — collection 토글 / KeywordExtractor 재실행 시 갱신</item>
///   <item>specialized digest — strategy 재색인 (`.lighthouse-kb/summary/*.md` rewrite) 시 갱신</item>
///   <item>(snapshot) — turn 별 sticky snapshot (ApiTurnContentBuilder 가 user message 에 부착)</item>
/// </list>
/// 총 4 breakpoint = Anthropic prompt cache 의 4 cap 정확 사용. 여유 0 — 추가 breakpoint 시 가장
/// 변동 빈도 높은 영역의 cache_control 제거 필요 (현재는 모두 stable).
/// <see cref="ApiChatProvider"/> 의 firstTurn 박제 분기에서 호출.
/// </summary>
internal static class SystemContentBuilder
{
    public static IList<AIContent> Build(
        string basePrompt,
        string? kbDigest,
        Func<AIContent, AIContent>? applyCacheControl)
    {
        // PR-G v-b 회귀 — 2-arg overload 호출은 specializedDigest=null 로 forward.
        return Build(basePrompt, kbDigest, specializedDigest: null, applyCacheControl);
    }

    /// <summary>
    /// **PR-I4** — specialized digest (cache breakpoint 3) 박제 overload.
    /// <paramref name="specializedDigest"/> 가 빈/null 이면 cache breakpoint 3 박제 skip (회귀 0 —
    /// PR-G v-b 와 동치 wire).
    /// </summary>
    public static IList<AIContent> Build(
        string basePrompt,
        string? kbDigest,
        string? specializedDigest,
        Func<AIContent, AIContent>? applyCacheControl)
    {
        var contents = new List<AIContent>(3);
        AIContent baseContent = new TextContent(basePrompt ?? "");
        if (applyCacheControl != null) baseContent = applyCacheControl(baseContent);
        contents.Add(baseContent);

        if (!string.IsNullOrEmpty(kbDigest))
        {
            AIContent digestContent = new TextContent(kbDigest);
            if (applyCacheControl != null) digestContent = applyCacheControl(digestContent);
            contents.Add(digestContent);
        }

        // PR-I4 — cache breakpoint 3. base + KB digest 다음 박제 강제 (순서 정합 = wire format SSOT).
        // KB digest 가 빈 경우에도 specialized digest 박제 가능 — KB digest 가 collection 토글 OFF 라도
        // strategy specialized markdown 은 색인된 collection 의 summary 만 보면 되므로 독립.
        if (!string.IsNullOrEmpty(specializedDigest))
        {
            AIContent specializedContent = new TextContent(specializedDigest);
            if (applyCacheControl != null) specializedContent = applyCacheControl(specializedContent);
            contents.Add(specializedContent);
        }

        return contents;
    }
}
