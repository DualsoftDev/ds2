using System.Collections.Generic;
using System.Linq;
using System.Text;
using Llm.Shared.Abstractions;

namespace Llm.Shared;

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
/// 빈 dict / 모든 service 빈 list → 빈 string (digest section 자체 생략).
/// keyword 빈 collection 은 title 만 노출 (description 도 함께 노출 시).
/// <para/>
/// **PR-S1 박제**: 이전 <c>Promaker.Knowledge.CollectionInfo</c> 의존 → <see cref="KbCollectionDescriptor"/> 추출.
/// caller (Promaker 등) 가 자신의 wire POCO → descriptor 1줄 매핑.
/// </summary>
public static class KbDigestBuilder
{
    public const string SectionHeader = "# ─── Active Knowledge Bases ───";

    public static string Build(IReadOnlyDictionary<string, IReadOnlyList<KbCollectionDescriptor>> profiles)
    {
        if (profiles is null || profiles.Count == 0) return "";

        var hasAny = false;
        var sb = new StringBuilder();
        sb.AppendLine(SectionHeader);
        sb.AppendLine();
        sb.AppendLine("다음 영역에 해당하는 질문이면 `attachment_search(query)` MCP tool 을 호출하세요.");
        sb.AppendLine("전체 본문 필요 시 `attachment_fulltext(fileId)` 호출 (search 만으로 부족할 때).");
        sb.AppendLine("collection 의 doc 목록 + 1줄 요약 필요 시 `attachment_summary(collectionId)` 호출.");
        sb.AppendLine();

        foreach (var kv in profiles.OrderBy(p => p.Key, System.StringComparer.Ordinal))
        {
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
