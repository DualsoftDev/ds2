using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace Promaker.LlmAgent.Api;

/// <summary>
/// **PR-G (todo-lighthouse-index-summary.md §5.2 v-b)** — system ChatMessage 의 AIContent 목록 빌더.
/// <para/>
/// 의도: base prompt 와 KB digest 를 *분리된 TextContent* 로 박제 → 각각에 cache_control: ephemeral
/// 부착 → Anthropic prompt cache 의 prefix-match 가 base 영역 (chat-lifetime 불변) 과 digest 영역
/// (KB 변경 시 갱신) 의 cache breakpoint 분리.
/// <list type="bullet">
///   <item>digest 빈 시 → base 1 TextContent 만 (회귀 0 — 기존 단일 prompt 와 동치)</item>
///   <item>digest 박제 시 → base + digest 2 TextContent (breakpoint 2/4, snapshot 합산 3/4, 여유 1)</item>
/// </list>
/// <see cref="ApiChatProvider"/> 의 firstTurn 박제 분기에서 호출.
/// </summary>
internal static class SystemContentBuilder
{
    public static IList<AIContent> Build(
        string basePrompt,
        string? kbDigest,
        Func<AIContent, AIContent>? applyCacheControl)
    {
        var contents = new List<AIContent>(2);
        AIContent baseContent = new TextContent(basePrompt ?? "");
        if (applyCacheControl != null) baseContent = applyCacheControl(baseContent);
        contents.Add(baseContent);

        if (!string.IsNullOrEmpty(kbDigest))
        {
            AIContent digestContent = new TextContent(kbDigest);
            if (applyCacheControl != null) digestContent = applyCacheControl(digestContent);
            contents.Add(digestContent);
        }

        return contents;
    }
}
