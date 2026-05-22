using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Promaker.Knowledge;

namespace Promaker.LlmAgent;

/// <summary>
/// **PR-G (todo-lighthouse-index-summary.md §5.2)** — KB profile dict → system prompt digest text.
/// <para/>
/// 산출물 형식 (§5.2 예시):
/// <code>
/// # ─── Active Knowledge Bases ───
///
/// 다음 영역에 해당하는 질문이면 `attachment_search(query)` MCP tool 을 호출하세요.
/// 전체 본문 필요 시 `attachment_fulltext(fileId)` 호출 (search 만으로 부족할 때).
///
/// - "Poc"
///     keywords: cache_rd, cache_cr, token, turn, cache, hit, steady
/// - "Promaker Docs"
///     keywords: prompt, cache, MCP, ApiChatProvider, ...
/// </code>
/// <para/>
/// 빈 dict / 모든 service 빈 list → 빈 string (digest section 자체 생략, ApiChatProvider 가 system 박제 시 1 TextContent 만).
/// keyword 빈 collection 은 title 만 노출 (description 도 함께 노출 시).
/// </summary>
internal static class KbDigestBuilder
{
    public const string SectionHeader = "# ─── Active Knowledge Bases ───";

    public static string Build(IReadOnlyDictionary<string, IReadOnlyList<CollectionInfo>> profiles)
    {
        if (profiles is null || profiles.Count == 0) return "";

        var hasAny = false;
        var sb = new StringBuilder();
        sb.AppendLine(SectionHeader);
        sb.AppendLine();
        sb.AppendLine("다음 영역에 해당하는 질문이면 `attachment_search(query)` MCP tool 을 호출하세요.");
        sb.AppendLine("전체 본문 필요 시 `attachment_fulltext(fileId)` 호출 (search 만으로 부족할 때).");
        // **PR-H3 (todo §11)** — γ hybrid: keyword digest 만으로 file 인지 부족 시 collection 의 doc-level
        // overview 를 on-demand fetch. `attachment_search` / `attachment_fulltext` 호출 전 file narrowing 정보원.
        sb.AppendLine("collection 의 doc 목록 + 1줄 요약 필요 시 `attachment_summary(collectionId)` 호출.");
        sb.AppendLine();

        // **review m-3 fix** — service id 정렬 (Ordinal) 으로 Anthropic prompt cache prefix-match 정합.
        // Dictionary<TKey, TValue> 의 iteration order 가 .NET 에서 통상 삽입 순서를 유지하나 *문서상 미보장* —
        // KB digest 의 텍스트 결정성을 보장해 동일 KB 셋에서 같은 cache key 생성.
        foreach (var kv in profiles.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            // **review m-8 fix** — value null 방어 (IReadOnlyDictionary contract 가 value not-null 미보장).
            if (kv.Value is null) continue;
            foreach (var coll in kv.Value)
            {
                if (coll is null || string.IsNullOrEmpty(coll.DisplayName)) continue;
                hasAny = true;
                sb.Append("- \"").Append(coll.DisplayName).Append('"');
                if (!string.IsNullOrEmpty(coll.Description))
                    sb.Append(" — ").Append(coll.Description);
                sb.AppendLine();
                if (coll.Keywords != null && coll.Keywords.Length > 0)
                {
                    sb.Append("    keywords: ");
                    sb.AppendLine(string.Join(", ", coll.Keywords));
                }
            }
        }

        return hasAny ? sb.ToString() : "";
    }
}
