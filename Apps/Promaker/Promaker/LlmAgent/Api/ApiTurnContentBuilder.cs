using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.LlmAgent;
using Microsoft.Extensions.AI;

namespace Promaker.LlmAgent.Api;

// Round-trip 최적화 — doc: Apps/Promaker/Docs/todo-promaker-llm-roundtrip-optimization.md §C1 / §H1 / §5.2
//
// ApiChatProvider 의 turn user message 구성 로직 (sticky snapshot 갱신 / multi-content build /
// prompt-for-history 요약) 을 분리한 internal helper. McpClient / IChatClient 의존 없이 단위 테스트
// 가능하도록 추출 — review trigger (sticky stale, cache_control 부착 실수, attachment 누락) 회귀 방어.
//
// 의도적 비-책임: hasSnapshot/hasAttachments 분기는 caller (ApiChatProvider) 에서 유지. 본 helper 는
// 분기 결과에 따른 contents list 만 생성. Anthropic cache_control 부착은 람다로 주입 (Anthropic SDK
// 직접 참조 회피 + capability 비트 분기 caller 책임).
internal static class ApiTurnContentBuilder
{
    /// <summary>
    /// round-trip §H1 — sticky snapshot 갱신 정책: incoming 이 비어있지 않으면 갱신, 비어있으면 기존 유지.
    /// revision 무변경 turn (incoming = null/empty) 에서도 sticky 유지로 LLM 의 store 상태 인지 보장.
    /// </summary>
    internal static string? UpdateStickySnapshot(string? current, string? incoming)
        => string.IsNullOrEmpty(incoming) ? current : incoming;

    /// <summary>
    /// round-trip §C1 — history 누적용 prompt. non-text attachment (image/pdf) 의 summary metadata 만
    /// text 로 누적 (bytes 는 본 turn 호출에서만 multi-content 로 전달, history bloat 회피).
    /// </summary>
    internal static string BuildPromptForHistory(string text, IReadOnlyList<Attachment> nonTextAttachments)
    {
        if (nonTextAttachments.Count == 0) return text;
        var summaries = string.Join(' ', nonTextAttachments.Select(AttachmentRendering.summarize));
        return summaries + "\n" + text;
    }

    /// <summary>
    /// round-trip §R10 — _history 안에 누적되는 user message 의 contents.
    ///   [1] sticky snapshot block (있을 때) — **cache_control 부착 없음** (오직 본 turn 의 마지막 user 만 부착)
    ///   [2] text prompt block
    ///
    /// PoC `e2e-cache-hit.fsx` 의 옵션 A 패턴 — snapshot 토큰이 매 turn 동일 위치에 누적되어 Anthropic
    /// prompt cache 의 prefix-match 가 자라며 hit ratio 가 정착 (PoC 측정: steady 91.3%).
    /// 현 ApiChatProvider 의 plain-text history 누적이면 snapshot 위치가 매 turn 변동 → cache miss.
    ///
    /// attachment bytes 는 미포함 (정책 17 — history bloat / OOM 회피). attachment summary 는
    /// `BuildPromptForHistory` 에서 promptText 에 이미 prepend 되어 있음.
    /// </summary>
    internal static List<AIContent> BuildHistoryContents(string? stickySnapshot, string promptText)
    {
        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(stickySnapshot))
            contents.Add(new TextContent(stickySnapshot));
        contents.Add(new TextContent(promptText));
        return contents;
    }

    /// <summary>
    /// round-trip §C1/§H1/§5.2 — 본 turn 호출용 multi-content (TextContent[]+DataContent[]) 생성.
    ///   [1] sticky snapshot (있을 때) — caller 가 cache_control 람다 전달 시 부착
    ///   [2] text prompt
    ///   [3..] image / pdf DataContent (non-text 만)
    /// 분기:
    ///   - caller 의 hasSnapshot || hasAttachments 가 true 일 때만 호출 (그 외는 history 마지막 user msg 그대로).
    ///   - applyCacheControlForSnapshot=null → 부착 skip (OpenAI/Groq/Ollama 어댑터 또는 capability 미지원).
    ///
    /// 구현: `BuildHistoryContents` 로 [snapshot?, prompt] 기본 구조 생성 → cache_control 부착 (있을 때) →
    /// attachment DataContent append. snapshot TextContent 생성이 한 곳 (`BuildHistoryContents`) 으로 일원화되어
    /// history 와 본 turn 사이 byte-identical invariant 가 코드 구조로 강제 (drift 차단).
    /// </summary>
    internal static List<AIContent> BuildMultiContents(
        string? stickySnapshot,
        string promptText,
        IReadOnlyList<Attachment> nonTextAttachments,
        Func<AIContent, AIContent>? applyCacheControlForSnapshot)
    {
        var contents = BuildHistoryContents(stickySnapshot, promptText);
        // snapshot 이 있고 capability 가 지원할 때만 [0] 의 snapshot TextContent 에 cache_control 부착.
        // 두 가드 모두 BuildHistoryContents 의 snapshot 부착 가드와 동일 식 — [0] 접근 안전.
        if (!string.IsNullOrEmpty(stickySnapshot) && applyCacheControlForSnapshot != null)
            contents[0] = applyCacheControlForSnapshot(contents[0]);
        foreach (var att in nonTextAttachments)
        {
            var imgOpt = AttachmentInfo.tryGetImage(att);
            if (imgOpt != null)
            {
                var img = imgOpt.Value;
                contents.Add(new DataContent(img.Bytes, img.Mime) { Name = img.Name });
                continue;
            }
            var pdfOpt = AttachmentInfo.tryGetPdf(att);
            if (pdfOpt != null)
            {
                var pdf = pdfOpt.Value;
                contents.Add(new DataContent(pdf.Bytes, "application/pdf") { Name = pdf.Name });
            }
        }
        return contents;
    }
}
