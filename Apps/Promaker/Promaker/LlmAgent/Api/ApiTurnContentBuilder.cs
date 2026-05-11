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
    /// round-trip §C1/§H1/§5.2 — 본 turn 호출용 multi-content (TextContent[]+DataContent[]) 생성.
    ///   [1] sticky snapshot (있을 때) — caller 가 cache_control 람다 전달 시 부착
    ///   [2] text prompt
    ///   [3..] image / pdf DataContent (non-text 만)
    /// 분기:
    ///   - caller 의 hasSnapshot || hasAttachments 가 true 일 때만 호출 (그 외는 history 마지막 user msg 그대로).
    ///   - applyCacheControlForSnapshot=null → 부착 skip (OpenAI/Groq/Ollama 어댑터 또는 capability 미지원).
    /// </summary>
    internal static List<AIContent> BuildMultiContents(
        string? stickySnapshot,
        string promptText,
        IReadOnlyList<Attachment> nonTextAttachments,
        Func<AIContent, AIContent>? applyCacheControlForSnapshot)
    {
        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(stickySnapshot))
        {
            AIContent snapshotContent = new TextContent(stickySnapshot);
            if (applyCacheControlForSnapshot != null)
                snapshotContent = applyCacheControlForSnapshot(snapshotContent);
            contents.Add(snapshotContent);
        }
        contents.Add(new TextContent(promptText));
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
