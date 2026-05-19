using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Promaker.LlmAgent;

namespace Promaker.Knowledge;

/// <summary>
/// LightHouse Service 의 HTTP client wrapper (todo-lighthouse-kb-server.md §3.7 / §3.8 / §3.9 / §4.2 Phase S5).
///
/// 책임:
/// - `Authorization: Bearer &lt;PSK&gt;` + `X-User-Identity` 자동 동봉
/// - HTTPS-only 강제 (plain HTTP 거부, §3.7)
/// - collection CRUD (`POST /collections` / `GET /collections` / `DELETE /collections/{id}`)
/// - session CRUD (`POST /sessions` / `DELETE /sessions/{token}`)
/// - **401/403 자동 회복 (CR6 L3)** — MCP 호출은 본 client 가 wrap 안 하지만 session 발급 retry hook 제공
///
/// PSK lifetime: 매 요청마다 `LlmConfig.GetLightHousePsk(serviceId)` 호출 → 평문 byte 변수 lifetime 짧게 유지 (D-S7-3a per-service path).
/// HttpClient 자체는 long-lived (caller 가 DI / single instance) — DNS / connection pool 정합.
///
/// 본 phase (S5a) 의 surface 는 protocol contract 만. UI 통합 (KbManagerDialog) 은 Phase S5b.
/// </summary>
public sealed class LightHouseClient : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LightHouseClient));

    private readonly HttpClient _http;
    private readonly Func<string?> _pskProvider;
    private readonly string _userIdentity;
    private readonly Func<IReadOnlyList<string>>? _activeCollectionIdsProvider;
    private readonly bool _ownsHttp;

    /// <summary>
    /// JSON 직렬화 옵션 SSOT — body 의 camelCase 정합 (service 의 §3.3.1 / Registry / SessionEndpoints 와 같은 convention).
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// production constructor — `HttpClient` 생성 + BaseUrl 박제. caller 가 Dispose.
    /// `LightHouseServiceConfig.BaseUrl` 가 HTTPS 가 아니면 ArgumentException (§3.7 plain HTTP 거부).
    /// </summary>
    /// <param name="baseUrl">HTTPS URL (e.g. "https://service.company.local:8443").</param>
    /// <param name="pskProvider">매 요청 시 호출 — DPAPI 복호화된 평문 PSK 반환. null = 인증 불가 (401 의도적 발생).</param>
    /// <param name="userIdentity">`X-User-Identity` 헤더 값. 일반 = `Environment.UserName` 또는 LlmConfig 의 user 식별자.</param>
    /// <param name="activeCollectionIdsProvider">L3 자동 회복 시 재발급에 사용할 active 셋. null = 회복 비활성.</param>
    public LightHouseClient(
        string baseUrl,
        Func<string?> pskProvider,
        string userIdentity,
        Func<IReadOnlyList<string>>? activeCollectionIdsProvider = null)
        : this(BuildHttp(baseUrl), pskProvider, userIdentity, activeCollectionIdsProvider, ownsHttp: true)
    {
    }

    /// <summary>
    /// test 친화 constructor — caller 가 mock `HttpMessageHandler` 박제 가능.
    /// `httpClient.BaseAddress` 가 미설정이면 throw — protocol contract 강제.
    /// </summary>
    internal LightHouseClient(
        HttpClient httpClient,
        Func<string?> pskProvider,
        string userIdentity,
        Func<IReadOnlyList<string>>? activeCollectionIdsProvider,
        bool ownsHttp)
    {
        if (httpClient is null) throw new ArgumentNullException(nameof(httpClient));
        if (httpClient.BaseAddress is null)
            throw new ArgumentException("HttpClient.BaseAddress 필수.", nameof(httpClient));
        if (httpClient.BaseAddress.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException(
                $"BaseAddress 가 HTTPS 가 아님 — {httpClient.BaseAddress} (plain HTTP 거부, §3.7).", nameof(httpClient));

        _http = httpClient;
        _pskProvider = pskProvider ?? throw new ArgumentNullException(nameof(pskProvider));
        _userIdentity = string.IsNullOrWhiteSpace(userIdentity)
            ? throw new ArgumentException("userIdentity 빈 값 금지 (§3.7 X-User-Identity 헤더 의무).", nameof(userIdentity))
            : userIdentity;
        _activeCollectionIdsProvider = activeCollectionIdsProvider;
        _ownsHttp = ownsHttp;
    }

    private static HttpClient BuildHttp(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("baseUrl 빈 값 금지.", nameof(baseUrl));
        var uri = new Uri(baseUrl);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException(
                $"baseUrl 이 HTTPS 가 아님 — {baseUrl} (plain HTTP 거부, §3.7).", nameof(baseUrl));
        var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"),
            Timeout = TimeSpan.FromMinutes(10),  // 큰 zip upload 대비
        };
        return client;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    /// <summary>현재 PSK / X-User-Identity 헤더를 박제한 HttpRequestMessage 빌더. 매 호출마다 호출.</summary>
    private HttpRequestMessage NewRequest(HttpMethod method, string relativeUri)
    {
        var req = new HttpRequestMessage(method, relativeUri);
        var psk = _pskProvider();
        if (!string.IsNullOrEmpty(psk))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", psk);
        req.Headers.Add("X-User-Identity", _userIdentity);
        return req;
    }

    /// <summary>
    /// `POST /collections` — multipart (title + zip stream) 으로 등록 후 server 가 발급한 guid 반환.
    ///
    /// **`zipStream` ownership**: `StreamContent` wrap 후 `MultipartFormDataContent.Dispose` 가 child stream 까지
    /// dispose. caller 가 `using FileStream` 로 열어 본 메서드에 넘기면 본 호출 후 stream 은 *이미 dispose 됨*.
    /// caller 는 `MemoryStream` 같은 throwaway 또는 본 호출 후 stream 재사용 금지 (review S5a-M1).
    /// </summary>
    /// <returns>collection guid (server-assigned, D3).</returns>
    public async Task<string> UploadCollectionAsync(
        string title,
        Stream zipStream,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title 필수.", nameof(title));
        if (zipStream is null) throw new ArgumentNullException(nameof(zipStream));

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(title), "title");
        var zip = new StreamContent(zipStream);
        zip.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(zip, "zip", "payload.zip");

        using var req = NewRequest(HttpMethod.Post, "collections");
        req.Content = content;

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrow(resp, "POST /collections", ct).ConfigureAwait(false);

        var body = await resp.Content.ReadFromJsonAsync<UploadResponse>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new LightHouseProtocolException("POST /collections response body 빈 값.");
        if (string.IsNullOrEmpty(body.Id))
            throw new LightHouseProtocolException("POST /collections response 의 id 누락.");
        return body.Id;
    }

    /// <summary>
    /// `POST /collections/{id}/payload` — 기존 collection 의 zip swap (재업로드, §3.9 / D5).
    /// title 변경은 본 호출로 안 함 (server-side display name 유지). `UploadCollectionAsync` 와 동일한 zip ownership 정책
    /// (caller 가 `MultipartFormDataContent.Dispose` 로 child stream 까지 dispose 됨을 인지).
    /// </summary>
    public async Task ReuploadCollectionPayloadAsync(
        string collectionId,
        Stream zipStream,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(collectionId))
            throw new ArgumentException("collectionId 필수.", nameof(collectionId));
        if (zipStream is null) throw new ArgumentNullException(nameof(zipStream));

        using var content = new MultipartFormDataContent();
        var zip = new StreamContent(zipStream);
        zip.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(zip, "zip", "payload.zip");

        using var req = NewRequest(HttpMethod.Post, $"collections/{Uri.EscapeDataString(collectionId)}/payload");
        req.Content = content;

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrow(resp, $"POST /collections/{collectionId}/payload", ct).ConfigureAwait(false);
    }

    /// <summary>`GET /collections` — registry list (T1 flat).</summary>
    public async Task<CollectionListResponse> ListCollectionsAsync(CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, "collections");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrow(resp, "GET /collections", ct).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<CollectionListResponse>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new LightHouseProtocolException("GET /collections response body 빈 값.");
    }

    /// <summary>`DELETE /collections/{id}` — registry 제거 + 디스크 purge (D7).</summary>
    public async Task DeleteCollectionAsync(string collectionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(collectionId))
            throw new ArgumentException("collectionId 필수.", nameof(collectionId));
        using var req = NewRequest(HttpMethod.Delete, $"collections/{Uri.EscapeDataString(collectionId)}");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrow(resp, $"DELETE /collections/{collectionId}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// `POST /sessions { collectionIds }` — active 셋 routing token 발급 (Q4 lazy sync 응답 포함).
    /// </summary>
    public async Task<SessionCreateResponse> CreateSessionAsync(
        IReadOnlyList<string> collectionIds,
        CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Post, "sessions");
        req.Content = JsonContent.Create(new SessionCreateRequest { CollectionIds = collectionIds }, options: JsonOptions);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrow(resp, "POST /sessions", ct).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<SessionCreateResponse>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new LightHouseProtocolException("POST /sessions response body 빈 값.");
    }

    /// <summary>`DELETE /sessions/{token}` — 명시 해제 (L2-1).</summary>
    public async Task DeleteSessionAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("token 필수.", nameof(token));
        using var req = NewRequest(HttpMethod.Delete, $"sessions/{Uri.EscapeDataString(token)}");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrow(resp, $"DELETE /sessions/{token}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// CR6 L3 자동 회복용 helper. MCP 호출이 401/403 받으면 caller 가 본 메서드 호출 → 신 token 반환.
    /// `activeCollectionIdsProvider` 가 null 이면 회복 비활성 (caller 가 미설정).
    ///
    /// **SSOT — caller orchestration 정책 (§3.8 L3)**: caller (chat invoke wrapper) 는 본 메서드를 **1회만 retry**
    /// 호출. 재실패 시 사용자/LLM 에게 명확한 fail 보고 (chip + log). 무한 retry 금지 — storm 위험 (review S5a-M3).
    /// 본 hook 은 retry counter 강제하지 않음 — caller 책임.
    /// </summary>
    public async Task<SessionCreateResponse> RecoverSessionAsync(CancellationToken ct = default)
    {
        if (_activeCollectionIdsProvider is null)
            throw new InvalidOperationException(
                "activeCollectionIdsProvider 미설정 — L3 자동 회복 비활성.");
        var ids = _activeCollectionIdsProvider();
        return await CreateSessionAsync(ids, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// **L3 자동 회복 wrapper (P2-r3 facade)** — caller 가 session token-bound 작업을 lambda 로 넘기면
    /// 401/403 발생 시 본 wrapper 가 <see cref="RecoverSessionAsync"/> 1회 호출 후 새 token 으로 재시도.
    /// 재시도까지 실패하면 원본 <see cref="LightHouseAuthException"/> 을 재throw (caller 에 명확 fail 보고).
    ///
    /// <para>**의도된 사용처 (Phase S7 / future MCP relay)**: Promaker 자체의 LightHouseClient 호출은 PSK 만
    /// 사용하므로 401 = PSK 결함 = retry 무의미 (§3.8 L3 caller 정책 박제, LlmChatViewModel.cs 의 catch 분기 정합).
    /// 본 wrapper 의 sweet spot 은 *session-bound MCP relay* — lighthouse `/mcp` endpoint 가 stale token 401 받을 때
    /// proxy/relay layer 가 본 wrapper 로 caller op 를 감싸서 자동 retry. 본 phase (s6-r6) 에서는 facade 만 박제.</para>
    ///
    /// <para>**계약**: <paramref name="operation"/> lambda 는 신선한 session token 을 받아 작업 수행 + 결과 반환.
    /// 첫 호출 token 은 신규 발급. 재시도 token 은 RecoverSessionAsync 의 응답. 무한 retry 금지 (1회 retry only).</para>
    /// </summary>
    /// <typeparam name="T">operation 의 결과 type.</typeparam>
    /// <param name="operation">신선 token 받아서 호출하는 async 작업. 401/403 시 wrapper 가 catch.</param>
    /// <param name="ct">caller 의 CancellationToken — RecoverSession + retry 모두 전파.</param>
    /// <returns>operation 의 결과. retry 까지 실패 시 LightHouseAuthException throw.</returns>
    public async Task<T> ExecuteWithSessionRetryAsync<T>(
        Func<string, CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        if (_activeCollectionIdsProvider is null)
            throw new InvalidOperationException(
                "activeCollectionIdsProvider 미설정 — L3 자동 회복 비활성 (LightHouseClient ctor 시 박제 필요).");

        // 첫 token 발급.
        var sess = await RecoverSessionAsync(ct).ConfigureAwait(false);
        try
        {
            return await operation(sess.Token, ct).ConfigureAwait(false);
        }
        // 자가 검열 M2: caller 의 cancellation 의도는 retry 우선 — OCE 는 retry 진입 차단 후 그대로 전파.
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LightHouseAuthException firstFail)
        {
            // 1회 retry — 새 session 발급 후 재시도. CR6 L3 sweet spot.
            Log.Warn($"session-bound op 401/403 — retry 1회 (status={firstFail.StatusCode}).");
            SessionCreateResponse retrySess;
            try
            {
                retrySess = await RecoverSessionAsync(ct).ConfigureAwait(false);
            }
            catch (LightHouseAuthException recoverFail)
            {
                // RecoverSession 자체 401 = PSK 결함 → retry 의미 없음. 원본 firstFail 박제 + recover 결합.
                throw new LightHouseAuthException(
                    $"L3 retry 의 RecoverSession 자체 실패 (PSK 결함 의심) — first={firstFail.Message} recover={recoverFail.Message}",
                    recoverFail.StatusCode);
            }
            return await operation(retrySess.Token, ct).ConfigureAwait(false);
        }
    }

    private static async Task EnsureSuccessOrThrow(HttpResponseMessage resp, string operation, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = "";
        try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { /* body read 자체 실패는 swallow — status code 가 SSOT */ }

        var msg = $"{operation} 실패 — HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. body={Truncate(body, 200)}";
        Log.Warn(msg);
        // review S5a-M2: 모든 분기에서 StatusCode 박제 — caller 가 415/409/4xx 별 안내 분기 가능.
        throw resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new LightHouseAuthException(msg, resp.StatusCode),
            HttpStatusCode.Forbidden    => new LightHouseAuthException(msg, resp.StatusCode),
            HttpStatusCode.NotFound     => new LightHouseProtocolException($"{operation}: 404 NotFound.", resp.StatusCode),
            _ => new LightHouseProtocolException(msg, resp.StatusCode),
        };
    }

    private static string Truncate(string s, int limit) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= limit ? s : s.Substring(0, limit) + "…");

    /// <summary>
    /// `GET /events` SSE stream 을 subscribe — long-running (CancellationToken 으로 종료).
    ///
    /// **wire**: text/event-stream + `data: {json}\n\n` per line. ndjson 형식의 `data:` prefix 만 parse,
    /// 그 외 (빈 line / comment / event:/id: 등) 는 silent skip (server 가 emit 안 하므로 무관, future-compat).
    ///
    /// **lifecycle**: `onEvent` callback 이 각 event 마다 호출됨. callback 안에서 long task 는 self-await
    /// 하지 말 것 (read loop 진행 중단). UI thread marshalling 은 caller 책임.
    ///
    /// **종료**: `ct.Cancel()` 또는 server-side close → `OperationCanceledException` reraise. 인증 결함
    /// (401/403) 은 본 호출 진입 시점에 `LightHouseAuthException` throw.
    ///
    /// **D-S7-2b (s6-r28)** — server-side D-S7-2a (`9e9698e`) 의 ndjson schema 정합.
    /// </summary>
    public async Task OpenEventsStreamAsync(
        Func<ServerEventDto, Task> onEvent,
        CancellationToken ct = default)
    {
        if (onEvent is null) throw new ArgumentNullException(nameof(onEvent));

        using var req = NewRequest(HttpMethod.Get, "events");
        // SSE 는 long-running — HttpClient.Timeout 의 default 가 lock 가능. caller 가 PER-호출 timeout 회피.
        // BaseAddress timeout (10분) 우회 — server-side keepalive 가 disconnect 검출 SSOT.
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try
        {
            await EnsureSuccessOrThrow(resp, "GET /events", ct).ConfigureAwait(false);

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                var json = line.Substring(6);
                ServerEventDto? evt;
                try { evt = JsonSerializer.Deserialize<ServerEventDto>(json, JsonOptions); }
                catch (JsonException ex)
                {
                    // wire 결함 fail-safe — 1줄 skip + 다음 line 진행. server 박제 결함이면 모든 line fail 이라 빠른 진단.
                    Log.Warn($"OpenEventsStreamAsync: malformed JSON 1줄 skip — {ex.Message}");
                    continue;
                }
                if (evt is not null) await onEvent(evt).ConfigureAwait(false);
            }
        }
        finally { resp.Dispose(); }
    }
}

// ── Response DTOs (camelCase 직렬화) ─────────────────────────────────────────

internal sealed class UploadResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("storageRelPath")] public string StorageRelPath { get; set; } = "";
}

public sealed class CollectionInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("indexerVersion")] public string IndexerVersion { get; set; } = "";
    [JsonPropertyName("fileCount")] public int FileCount { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "idle";
    [JsonPropertyName("errorReason")] public string? ErrorReason { get; set; }
    [JsonPropertyName("lastImportedAt")] public string LastImportedAt { get; set; } = "";
}

public sealed class CollectionListResponse
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("collections")] public List<CollectionInfo> Collections { get; set; } = new();
}

/// <summary>
/// `GET /events` SSE payload (D-S7-2b s6-r28). server-side `EventBus.ServerEvent` 정합.
/// `event` = "collection-added" / "collection-updated" / "collection-deleted" / "keepalive".
/// </summary>
public sealed class ServerEventDto
{
    [JsonPropertyName("event")] public string Event { get; set; } = "";
    [JsonPropertyName("collectionId")] public string? CollectionId { get; set; }
    [JsonPropertyName("progress")] public int? Progress { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = "";
}

internal sealed class SessionCreateRequest
{
    [JsonPropertyName("collectionIds")] public IReadOnlyList<string> CollectionIds { get; set; } = Array.Empty<string>();
}

public sealed class SessionCreateResponse
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("acceptedCollectionIds")] public List<string> AcceptedCollectionIds { get; set; } = new();
    [JsonPropertyName("unknownIds")] public List<string> UnknownIds { get; set; } = new();
    [JsonPropertyName("unindexableIds")] public List<string> UnindexableIds { get; set; } = new();
}

// ── Exceptions ──────────────────────────────────────────────────────────────

/// <summary>
/// HTTP 401/403 — PSK 또는 session token 유효성 실패. CR6 L3 caller 가 본 예외 catch 후 retry.
/// </summary>
public sealed class LightHouseAuthException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public LightHouseAuthException(string message, HttpStatusCode status) : base(message) { StatusCode = status; }
}

/// <summary>
/// service 의 protocol 결함 (response 비어있음 / format 위반 / 4xx 5xx non-auth).
///
/// `StatusCode` — HTTP 응답이 있었으면 그 코드 (415 IndexerVersion gate / 409 Conflict / 404 NotFound / 5xx),
/// 미존재 (response 자체 못 받음 또는 JSON parse 실패) 시 null. caller (KbManagerDialog) 가 본 값으로
/// 사용자 안내 분기 (e.g. 415 → "client lib 업그레이드 필요", 5xx → "service 오류"). review S5a-M2.
/// </summary>
public sealed class LightHouseProtocolException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public LightHouseProtocolException(string message) : base(message) { }
    public LightHouseProtocolException(string message, HttpStatusCode statusCode) : base(message)
    {
        StatusCode = statusCode;
    }
}
