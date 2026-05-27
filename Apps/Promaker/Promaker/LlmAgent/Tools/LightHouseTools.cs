using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using ModelContextProtocol.Server;
using Promaker.Knowledge;

namespace Promaker.LlmAgent.Tools;

/// <summary>
/// **Plan B (2026-05-27)** — Promaker 자체 MCP host (`mcp__promaker__*`) 가 LightHouseService 의 attachment_* 6종을
/// fan-out wrapper 로 노출. Anthropic Claude CLI 의 mcp HTTP/SSE transport 가 OAuth 2.1 path 강제 (`_authThenStart`)
/// 라 단순 Bearer PSK 와 fundamental mismatch — Claude CLI 가 lighthouse 직접 통신 불가. 본 wrapper 가 우회.
/// <para/>
/// **fan-out 정책**:
/// - <b>search</b>: 각 active service 에 병렬 호출 → round-robin merge (각 server BM25 score 가 corpus 별 IDF
///   정규화 안 되어 global sort 의미 없음). topK = 사용자 요청. 각 server 에서 ceil(topK/N) 씩 받아 interleave.
/// - <b>list</b>: 모든 active service concat. fileId 에 serviceId prefix 박제.
/// - <b>outline/read/fulltext/summary</b>: fileId prefix (`&lt;serviceId-8자&gt;:&lt;orig&gt;`) 파싱 후 single client routing.
/// - 한 service 실패 = 다른 service 결과로 응답 + Log.Warn (사용자 chip 0 — LLM prompt 오염 방지).
/// <para/>
/// **fileId routing prefix**: `&lt;serviceId-8자&gt;:&lt;originalFileId&gt;`. 8자 = Guid v4 첫 segment, collision-free.
/// 응답의 모든 fileId (search hits / list items / outline ref 등) 가 prefix 박제. 후속 호출 시 prefix 파싱.
/// <para/>
/// **wire**: <see cref="LightHouseClient.CallMcpToolAsync"/> 가 JSON-RPC over Streamable HTTP + Mcp-Session-Id 자동 관리.
/// 401/403 시 <see cref="LightHouseClient.ExecuteWithSessionRetryAsync"/> 가 app session 재발급 + 1회 retry.
/// </summary>
[McpServerToolType]
public static class LightHouseTools
{
    private static readonly ILog Log = LogManager.GetLogger("Promaker.LlmAgent.LightHouseTools");

    /// <summary>fileId prefix 의 serviceId 부분 길이 (Guid v4 의 첫 8 hex chars).</summary>
    private const int ServiceIdPrefixLen = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [McpServerTool, Description(
        "List documents in all active KB (knowledge base) collections from configured LightHouse services. " +
        "Returns JSON: {items:[{fileId, fileName, fileKind, pageCount, serviceName}], totalCount, activeServiceCount}. " +
        "fileId is prefixed with service identifier — pass it back as-is to lighthouse_read/outline/fulltext/summary.")]
    public static Task<string> LighthouseList(CancellationToken ct = default) =>
        FanOutAndMerge("attachment_list", null, PrefixFileIdsInListResponse, MergeListResults, ct);

    [McpServerTool, Description(
        "Search documents in all active KB collections (BM25 lexical, per-service). topK default 10 — divided across services " +
        "round-robin (each server returns ceil(topK/N) hits). fileId optional to scope to single document (must be prefixed). " +
        "Returns JSON: {results:[{fileId, fileName, ref, outlinePath, score, excerpt, tokenCount, hasImages, serviceName}], " +
        "perServiceCounts, hint?}. BM25 scores are NOT globally normalized — interpret per-service.")]
    public static Task<string> LighthouseSearch(
        [Description("Search query text (Korean trigram FTS5). Multiple terms OR-combined.")] string query,
        [Description("Max total results across all services (default 10). Distributed round-robin.")] int topK = 10,
        [Description("Optional: limit search to single document (prefixed fileId from lighthouse_list).")] string? fileId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Task.FromResult(JsonSerializer.Serialize(new { results = Array.Empty<object>(), hint = "query is empty" }));

        // **2026-05-27 fix** — attachment_search 의 fileId 는 service signature 가 required (F# 함수 인자, default 없음).
        // 누락 시 mcp-sdk ReflectionAIFunction 이 `ArgumentException: missing 'fileId'` throw → "An error occurred invoking 'attachment_search'".
        // 비어 있으면 ""로 명시 박제 — service 의 `String.IsNullOrEmpty fileId` 가 None scope (전체 검색) 으로 처리.

        // fileId 박제 시 → 그 service 만 호출 (routing).
        if (!string.IsNullOrEmpty(fileId) && TryParseFileId(fileId, out var routedServiceId, out var origFileId))
        {
            return SingleClientCall(routedServiceId, "attachment_search",
                new { query, topK, fileId = origFileId },
                (sid, sname, raw) => PrefixFileIdsInSearchResponse(raw, sid, sname), ct);
        }

        var n = LightHouseClientHolder.GetActiveClients().Count;
        var perServerTopK = n > 0 ? Math.Max(1, (topK + n - 1) / n) : topK;  // ceil(topK / N)

        return FanOutAndMerge("attachment_search",
            new { query, topK = perServerTopK, fileId = "" },
            (sid, sname, raw) => PrefixFileIdsInSearchResponse(raw, sid, sname),
            MergeSearchResultsRoundRobin, ct);
    }

    [McpServerTool, Description(
        "Get outline tree of a document. fileId must be prefixed (from lighthouse_list/search).")]
    public static Task<string> LighthouseOutline(
        [Description("Prefixed fileId from lighthouse_list/search.")] string fileId,
        CancellationToken ct = default) =>
        RouteByFileId(fileId, "attachment_outline", origId => new { fileId = origId }, ct);

    [McpServerTool, Description(
        "Read excerpt around a search hit ref. fileId must be prefixed. ref (optional) = locator from search hit.")]
    public static Task<string> LighthouseRead(
        [Description("Prefixed fileId.")] string fileId,
        [Description("Optional outline node ref / page hint (from search hit.ref). Empty = whole document chunks.")] string? @ref = null,
        CancellationToken ct = default) =>
        // service signature: (fileId, ref, includeImages, captionOnly) — 모두 required. ref="" / includeImages=false / captionOnly=true 박제.
        RouteByFileId(fileId, "attachment_read", origId => new { fileId = origId, @ref = @ref ?? "", includeImages = false, captionOnly = true }, ct);

    [McpServerTool, Description(
        "Read full markdown text dump of a document. fileId must be prefixed. " +
        "Use when search excerpts are insufficient.")]
    public static Task<string> LighthouseFulltext(
        [Description("Prefixed fileId.")] string fileId,
        CancellationToken ct = default) =>
        RouteByFileId(fileId, "attachment_fulltext", origId => new { fileId = origId }, ct);

    [McpServerTool, Description(
        "Get summary metadata of a collection (not a single document). fileId is prefixed — wrapper extracts " +
        "the collection GUID portion (before ':') and calls attachment_summary(collectionId).")]
    public static Task<string> LighthouseSummary(
        [Description("Prefixed fileId from lighthouse_list/search — wrapper extracts collection-guid for attachment_summary.")] string fileId,
        CancellationToken ct = default)
    {
        // service attachment_summary 는 collectionId (collection guid) 박제 — fileId 의 ":" 앞부분 추출.
        // wrapper 의 prefix 박제 후 fileId 형식: `<sid8>:<collection-guid>:<docId>`. ":" 첫 split → sid8, 그 뒤 (`<guid>:<doc>`) 의 첫 ":" split → collection guid.
        if (!TryParseFileId(fileId, out var prefix, out var origFileId))
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"invalid fileId '{fileId}' — expected '<serviceId-8>:<collection-guid>:<docId>' from lighthouse_list/search." }));
        var colonIdx = origFileId.IndexOf(':');
        var collectionGuid = colonIdx > 0 ? origFileId.Substring(0, colonIdx) : origFileId;  // ":" 없으면 origFileId 전체를 collection guid 로
        var match = FindClientByPrefix(prefix);
        if (match is null)
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"no active LightHouse service matches fileId prefix '{prefix}'." }));
        return SingleClientCall(match.Value.ServiceId, "attachment_summary",
            new { collectionId = collectionGuid }, null, ct);
    }

    // ─── helpers ──────────────────────────────────────────────────────────────────

    private static string MakePrefix(string serviceId) =>
        serviceId.Length >= ServiceIdPrefixLen ? serviceId.Substring(0, ServiceIdPrefixLen) : serviceId;

    /// <summary>fileId 가 `&lt;8자&gt;:&lt;rest&gt;` 형식이면 분리. 분리 실패 = false (legacy / 잘못된 입력).</summary>
    private static bool TryParseFileId(string fileId, out string serviceIdPrefix, out string originalFileId)
    {
        serviceIdPrefix = ""; originalFileId = "";
        if (string.IsNullOrEmpty(fileId)) return false;
        var idx = fileId.IndexOf(':');
        // service id prefix 8자 + ':' = 9자, 그 뒤 orig fileId 가 비어 있지 않아야.
        if (idx != ServiceIdPrefixLen || fileId.Length <= idx + 1) return false;
        serviceIdPrefix = fileId.Substring(0, idx);
        originalFileId = fileId.Substring(idx + 1);
        return true;
    }

    /// <summary>prefix 가 어느 service 의 것인지 활성 client 셋에서 찾음. 미일치 = null.</summary>
    private static (string ServiceId, LightHouseClient Client)? FindClientByPrefix(string prefix)
    {
        foreach (var (sid, client) in LightHouseClientHolder.GetActiveClients())
        {
            if (MakePrefix(sid) == prefix) return (sid, client);
        }
        return null;
    }

    /// <summary>fileId prefix 파싱 → routing → single client 호출. prefix invalid 시 error JSON.</summary>
    private static async Task<string> RouteByFileId(
        string fileId, string toolName, Func<string, object> argsBuilder, CancellationToken ct)
    {
        if (!TryParseFileId(fileId, out var prefix, out var origFileId))
            return JsonSerializer.Serialize(new { error = $"invalid fileId '{fileId}' — expected '<serviceId-8>:<origId>' format from lighthouse_list/search." });
        var match = FindClientByPrefix(prefix);
        if (match is null)
            return JsonSerializer.Serialize(new { error = $"no active LightHouse service matches fileId prefix '{prefix}' — service may have been removed/deactivated." });
        return await SingleClientCall(match.Value.ServiceId, toolName, argsBuilder(origFileId), null, ct).ConfigureAwait(false);
    }

    /// <summary>단일 client 호출 + 예외 catch + 결과 transform (optional).</summary>
    private static async Task<string> SingleClientCall(
        string serviceId, string toolName, object args,
        Func<string, string, string, string>? transform, CancellationToken ct)
    {
        var match = LightHouseClientHolder.GetActiveClients().FirstOrDefault(p => p.ServiceId == serviceId);
        if (match.Client is null)
            return JsonSerializer.Serialize(new { error = $"service '{serviceId}' inactive or removed." });
        try
        {
            var raw = await match.Client.ExecuteWithSessionRetryAsync(
                (token, ct2) => match.Client.CallMcpToolAsync(token, toolName, args, ct2), ct).ConfigureAwait(false);
            return transform is null ? raw : transform(serviceId, "(single)", raw);
        }
        catch (Exception ex)
        {
            Log.Warn($"LightHouseTools.{toolName} single({serviceId}) 실패: {ex.Message}");
            return JsonSerializer.Serialize(new { error = $"{toolName} failed: {ex.Message}" });
        }
    }

    /// <summary>모든 active client 병렬 호출 + 각 결과 transform + merge.</summary>
    private static async Task<string> FanOutAndMerge(
        string toolName, object? args,
        Func<string, string, string, string> perServiceTransform,
        Func<IReadOnlyList<(string ServiceId, string ServiceName, string Raw)>, string> mergeResults,
        CancellationToken ct)
    {
        var clients = LightHouseClientHolder.GetActiveClients();
        if (clients.Count == 0)
            return JsonSerializer.Serialize(new { items = Array.Empty<object>(), totalCount = 0, activeServiceCount = 0, hint = "no active LightHouse services configured." });

        var tasks = clients.Select(async pair =>
        {
            try
            {
                var raw = await pair.Client.ExecuteWithSessionRetryAsync(
                    (token, ct2) => pair.Client.CallMcpToolAsync(token, toolName, args ?? new { }, ct2), ct).ConfigureAwait(false);
                var transformed = perServiceTransform(pair.ServiceId, pair.Client.BaseUrl, raw);
                return (ServiceId: pair.ServiceId, ServiceName: pair.Client.BaseUrl, Transformed: transformed, Error: (string?)null);
            }
            catch (Exception ex)
            {
                Log.Warn($"LightHouseTools.{toolName} fan-out({pair.ServiceId}) 실패: {ex.Message}");
                return (ServiceId: pair.ServiceId, ServiceName: pair.Client.BaseUrl, Transformed: "", Error: (string?)ex.Message);
            }
        }).ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var successResults = results.Where(r => r.Error is null)
            .Select(r => (r.ServiceId, r.ServiceName, Raw: r.Transformed))
            .ToList();
        return mergeResults(successResults);
    }

    /// <summary>list 응답 (array) merge — 모든 service concat + serviceName 박제.</summary>
    private static string MergeListResults(IReadOnlyList<(string ServiceId, string ServiceName, string Raw)> perService)
    {
        var allItems = new List<JsonElement>();
        foreach (var (sid, sname, raw) in perService)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "[]" : raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in doc.RootElement.EnumerateArray())
                    allItems.Add(item.Clone());
            }
            catch (Exception ex) { Log.Warn($"MergeListResults parse 실패 ({sid}): {ex.Message}"); }
        }
        return JsonSerializer.Serialize(new
        {
            items = allItems.Select(e => e.GetRawText()).Select(s => JsonSerializer.Deserialize<object>(s)).ToArray(),
            totalCount = allItems.Count,
            activeServiceCount = perService.Count,
        }, JsonOptions);
    }

    /// <summary>search 응답 (object {results,...}) merge — round-robin interleave.</summary>
    private static string MergeSearchResultsRoundRobin(IReadOnlyList<(string ServiceId, string ServiceName, string Raw)> perService)
    {
        var perServiceHits = new List<List<JsonElement>>();
        var perServerCounts = new Dictionary<string, int>();
        string? combinedHint = null;
        foreach (var (sid, sname, raw) in perService)
        {
            var hits = new List<JsonElement>();
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
                if (doc.RootElement.TryGetProperty("results", out var resArr) && resArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var hit in resArr.EnumerateArray()) hits.Add(hit.Clone());
                }
                if (doc.RootElement.TryGetProperty("hint", out var h) && h.ValueKind == JsonValueKind.String)
                {
                    var hv = h.GetString();
                    if (!string.IsNullOrEmpty(hv)) combinedHint = combinedHint is null ? $"[{sname}] {hv}" : combinedHint + $"; [{sname}] {hv}";
                }
            }
            catch (Exception ex) { Log.Warn($"MergeSearchResults parse 실패 ({sid}): {ex.Message}"); }
            perServiceHits.Add(hits);
            perServerCounts[sname] = hits.Count;
        }
        // round-robin interleave.
        var merged = new List<JsonElement>();
        var maxLen = perServiceHits.Count == 0 ? 0 : perServiceHits.Max(l => l.Count);
        for (int i = 0; i < maxLen; i++)
        {
            foreach (var list in perServiceHits)
                if (i < list.Count) merged.Add(list[i]);
        }
        return JsonSerializer.Serialize(new
        {
            results = merged.Select(e => JsonSerializer.Deserialize<object>(e.GetRawText())).ToArray(),
            perServerCounts,
            hint = combinedHint,
        }, JsonOptions);
    }

    /// <summary>list response (array of {fileId, fileName, ...}) 의 모든 fileId 에 `&lt;serviceId-8&gt;:` prefix + serviceName 박제.
    /// **2026-05-27 fix**: 본 transform 누락 시 list 응답의 fileId 가 raw `&lt;collection-guid&gt;:&lt;docId&gt;` 형식 그대로 노출
    /// → LLM 이 후속 lighthouse_read/outline/fulltext/summary 호출 시 첫 8자 (`c725077d`) 가 serviceId prefix 로 잘못 해석
    /// → "no active LightHouse service matches fileId prefix" 거부.</summary>
    private static string PrefixFileIdsInListResponse(string serviceId, string serviceName, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return raw;
            var prefix = MakePrefix(serviceId);
            var newItems = new List<Dictionary<string, object?>>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var newItem = new Dictionary<string, object?>();
                foreach (var p in item.EnumerateObject())
                {
                    if (p.Name == "fileId" && p.Value.ValueKind == JsonValueKind.String)
                        newItem[p.Name] = $"{prefix}:{p.Value.GetString()}";
                    else
                        newItem[p.Name] = JsonSerializer.Deserialize<object>(p.Value.GetRawText());
                }
                newItem["serviceName"] = serviceName;
                newItems.Add(newItem);
            }
            return JsonSerializer.Serialize(newItems, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warn($"PrefixFileIdsInListResponse 실패 ({serviceId}): {ex.Message}");
            return raw;
        }
    }

    /// <summary>search response 의 모든 fileId 에 `&lt;serviceId-8&gt;:` prefix 박제. serviceName 도 hit 마다 추가.</summary>
    private static string PrefixFileIdsInSearchResponse(string raw, string serviceId, string serviceName)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var prefix = MakePrefix(serviceId);
            var rebuilt = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "results" && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    var newHits = new List<Dictionary<string, object?>>();
                    foreach (var hit in prop.Value.EnumerateArray())
                    {
                        var newHit = new Dictionary<string, object?>();
                        foreach (var hp in hit.EnumerateObject())
                        {
                            if (hp.Name == "fileId" && hp.Value.ValueKind == JsonValueKind.String)
                                newHit[hp.Name] = $"{prefix}:{hp.Value.GetString()}";
                            else
                                newHit[hp.Name] = JsonSerializer.Deserialize<object>(hp.Value.GetRawText());
                        }
                        newHit["serviceName"] = serviceName;
                        newHits.Add(newHit);
                    }
                    rebuilt[prop.Name] = newHits;
                }
                else
                {
                    rebuilt[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
                }
            }
            return JsonSerializer.Serialize(rebuilt, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warn($"PrefixFileIdsInSearchResponse 실패 ({serviceId}): {ex.Message}");
            return raw;
        }
    }
}
