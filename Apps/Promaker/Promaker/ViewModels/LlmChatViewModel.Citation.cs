using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Promaker.ViewModels;

/// <summary>
/// C6 — attachment_search hit 의 turn/session-scoped cache.
/// MdXaml 의 Hyperlink (attachment://fileId/ref) 클릭 시 LlmChatPanel 가 본 cache lookup → CitationPopup 표시.
/// system prompt <c>5.knowledge-base.md</c> 가 LLM 에게 인용 형식 의무 박제.
/// </summary>
public sealed record CitationHit(
    string FileId,
    string FileName,
    string Ref,
    string? OutlinePath,
    string Excerpt,
    double Score,
    bool HasImages);

public partial class LlmChatViewModel
{
    /// <summary>key = $"{fileId}|{ref}". session-scoped (LlmChatViewModel lifetime).</summary>
    private readonly Dictionary<string, CitationHit> _citationCache = new(StringComparer.Ordinal);

    /// <summary>tool_use_id → tool name. ToolResult 받을 때 어느 tool 의 응답인지 매칭.</summary>
    private readonly Dictionary<string, string> _toolUseNames = new(StringComparer.Ordinal);

    private static string CitationKey(string fileId, string @ref) => fileId + "|" + @ref;

    internal void RecordToolUse(string toolUseId, string name)
    {
        if (string.IsNullOrEmpty(toolUseId) || string.IsNullOrEmpty(name)) return;
        _toolUseNames[toolUseId] = name;
    }

    internal void RecordToolResult(string toolUseId, bool isError, string content)
    {
        if (string.IsNullOrEmpty(toolUseId)) return;
        if (!_toolUseNames.TryGetValue(toolUseId, out var name)) return;
        _toolUseNames.Remove(toolUseId);  // self-cleaning — tool_use_id 는 1:1 대응이라 다음 result 안 옴
        if (isError || name != "attachment_search" || string.IsNullOrEmpty(content)) return;

        try
        {
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return;
            foreach (var hit in results.EnumerateArray())
            {
                var fileId = hit.TryGetProperty("fileId", out var f) ? f.GetString() : null;
                var @ref = hit.TryGetProperty("ref", out var r) ? r.GetString() : null;
                if (string.IsNullOrEmpty(fileId) || string.IsNullOrEmpty(@ref)) continue;
                _citationCache[CitationKey(fileId!, @ref!)] = new CitationHit(
                    FileId: fileId!,
                    FileName: hit.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? "" : "",
                    Ref: @ref!,
                    OutlinePath: hit.TryGetProperty("outlinePath", out var op) ? op.GetString() : null,
                    Excerpt: hit.TryGetProperty("excerpt", out var ex) ? ex.GetString() ?? "" : "",
                    Score: hit.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : 0.0,
                    HasImages: hit.TryGetProperty("hasImages", out var hi) && hi.ValueKind == JsonValueKind.True);
            }
        }
        catch (JsonException ex)
        {
            // attachment_search 결과 JSON parse 실패는 LLM/tool 측 결함 — citation 표시만 누락, chat 자체는 진행.
            Log.Warn($"attachment_search result JSON parse 실패 (citation cache skip): {ex.Message}");
        }
    }

    /// <summary>LlmChatPanel 의 Hyperlink RequestNavigate handler 가 호출. miss = null.</summary>
    public CitationHit? TryGetCitation(string fileId, string @ref)
    {
        if (string.IsNullOrEmpty(fileId) || string.IsNullOrEmpty(@ref)) return null;
        return _citationCache.TryGetValue(CitationKey(fileId, @ref), out var hit) ? hit : null;
    }

    /// <summary>Reset / 새 session 진입 시 호출. 옛 hit 가 새 session 에서 잘못 popup 띄우는 회귀 차단.</summary>
    internal void ClearCitationCache()
    {
        _citationCache.Clear();
        _toolUseNames.Clear();
    }
}
