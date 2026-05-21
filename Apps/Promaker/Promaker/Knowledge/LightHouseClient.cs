using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
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
    /// **D-S7-5 phase 3 (s6-r67)** — chunked path 자동 선택 임계값 SSOT. 본 값 초과 zip 은 caller (AttachmentIngestService)
    /// 가 `UploadCollectionResumableAsync` 자동 진입 — multipart single-shot 대신 chunked PATCH 로 안전한 resume hook 확보.
    /// <para/>
    /// 256 MiB = (a) HttpClient 의 single multipart buffer alloc 한도 부담 분기점 (~256 MiB 부터 OOM risk 점증) + (b) 사내 LAN
    /// 의 network instability 부담 분기점 (~수 GB 단일 재시도 비용 vs ~4 MiB chunk 재시도 비용). 사용자 환경 별 조정 시
    /// 본 const 만 변경 — caller 박제 단순 유지.
    /// </summary>
    public const long ResumableUploadThresholdBytes = 256L * 1024L * 1024L;

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
    /// <param name="clientCertThumbprint">**B5 D-S7-1 후속 (s6-r61)** — mTLS client cert thumbprint (SHA-1 40 / SHA-256 64 hex). null/빈 값 = PSK 단독.</param>
    public LightHouseClient(
        string baseUrl,
        Func<string?> pskProvider,
        string userIdentity,
        Func<IReadOnlyList<string>>? activeCollectionIdsProvider = null,
        string? clientCertThumbprint = null)
        : this(BuildHttp(baseUrl, clientCertThumbprint), pskProvider, userIdentity, activeCollectionIdsProvider, ownsHttp: true)
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

    /// <summary>
    /// **B5 D-S7-1 후속 (s6-r61)** — HttpClient 빌더 (mTLS client cert optional 박제).
    /// <para/>
    /// `clientCertThumbprint` null/빈 값 시 기존 동작 (HttpClient default). 박제 시 LocalMachine\My X509Store
    /// 에서 thumbprint match cert 1건 lookup → <see cref="HttpClientHandler.ClientCertificates"/> 박제.
    /// cert 미존재 시 <see cref="InvalidOperationException"/> — caller (Holder) 가 사용자 안내.
    /// </summary>
    private static HttpClient BuildHttp(string baseUrl, string? clientCertThumbprint)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("baseUrl 빈 값 금지.", nameof(baseUrl));
        var uri = new Uri(baseUrl);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException(
                $"baseUrl 이 HTTPS 가 아님 — {baseUrl} (plain HTTP 거부, §3.7).", nameof(baseUrl));

        HttpMessageHandler handler;
        if (string.IsNullOrWhiteSpace(clientCertThumbprint))
        {
            handler = new HttpClientHandler();
        }
        else
        {
            var cert = LookupClientCert(clientCertThumbprint);
            var h = new HttpClientHandler();
            h.ClientCertificateOptions = ClientCertificateOption.Manual;
            h.ClientCertificates.Add(cert);
            handler = h;
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"),
            Timeout = TimeSpan.FromMinutes(10),  // 큰 zip upload 대비
        };
        return client;
    }

    /// <summary>
    /// **B5 D-S7-1 후속 (s6-r61)** — LocalMachine\My X509Store 에서 thumbprint match cert 조회.
    /// thumbprint normalize = hex 추출 + 대문자 (':' / 공백 / hyphen 제거). validOnly=false (만료된 cert 도
    /// 시각화 의도 — server 측 chain.Build 가 거부 path SSOT).
    /// </summary>
    /// <exception cref="InvalidOperationException">미존재 / 다중 매칭 시.</exception>
    internal static X509Certificate2 LookupClientCert(string thumbprint)
    {
        var normalized = NormalizeThumbprint(thumbprint);
        if (normalized.Length != 40 && normalized.Length != 64)
            throw new InvalidOperationException(
                $"client cert thumbprint 길이={normalized.Length} (SHA-1=40 / SHA-256=64 hex 필요).");

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
        if (matches.Count == 0)
            throw new InvalidOperationException(
                $"LocalMachine\\My X509Store 에서 client cert 미존재 — thumbprint={normalized}. " +
                $"사내 CA 발급 cert 의 .pfx import (PowerShell Import-PfxCertificate 또는 MMC) 의무.");
        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"client cert thumbprint 다중 매칭={matches.Count} — thumbprint={normalized}. " +
                $"동일 thumbprint cert 가 store 에 여러 개 — 정리 의무.");
        return matches[0];
    }

    /// <summary>thumbprint normalize — hex 추출 + 대문자 (':' / 공백 / hyphen 제거). server-side `Config.normalizeThumbprint` 정합.</summary>
    internal static string NormalizeThumbprint(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if ((ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'F') || (ch >= 'a' && ch <= 'f'))
                sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    /// <summary>현재 PSK / X-User-Identity 헤더를 박제한 HttpRequestMessage 빌더. 매 호출마다 호출.
    /// <para/>
    /// **B6 (M9 보안 sweep, s6-r71+)** — PSK 평문 lifetime 최소화. `pskProvider` 결과를 local var 로 받은 즉시
    /// `AuthenticationHeaderValue` 박제 후 local var `null` 박제 → method scope 종료와 별개로 즉시 GC root 해제.
    /// string immutable + intern pool 이라 zero-fill 강제 불가 (`SecureString` 은 .NET Core deprecated) — 본 fix 는
    /// defense-in-depth 만, process dump 시점 string 잔존 시간 단축. 근본 해소는 `LlmConfig.GetLightHousePsk` 의
    /// signature 를 `byte[]` 로 변경하는 별 phase 의무 박제 (caller 다수 영향).
    /// </summary>
    private HttpRequestMessage NewRequest(HttpMethod method, string relativeUri)
    {
        var req = new HttpRequestMessage(method, relativeUri);
        var psk = _pskProvider();
        if (!string.IsNullOrEmpty(psk))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", psk);
        // M9 — 평문 PSK reference 즉시 해제 (GC root 단축). string 자체는 internal char[] 잔존 가능, defense-in-depth 만.
        psk = null;
        req.Headers.Add(Ds2.LightHouse.Protocol.HeaderNames.UserIdentity, _userIdentity);
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
        // **s6-r70 review C-16**: 다른 10 메서드와 정합 — HttpCompletionOption.ResponseHeadersRead 박제 (body stream 지연 read).
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
    ///
    /// **`using var resp` 의도 (s6-r28 review m-3, s6-r32 docstring 박제)**: 일반 endpoint 와 달리 본 호출은
    /// long-running stream — `using var` 가 함수 종료 시점까지 response (+ 본인 소유 stream) 보유. caller 의
    /// CancellationToken 또는 server-side close 로 ReadLine loop 가 종료되면 함수 exit 시점에 dispose.
    /// `try/finally` 의 `resp.Dispose()` 가 명시 — 본 path 는 stream 의 lifetime 보장 명료성을 위해 일반 `using var`
    /// 가 아닌 explicit dispose 패턴 채택 (HttpCompletionOption.ResponseHeadersRead 의 body stream 이 response
    /// 와 lifetime 결합인 점 박제).
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

    // ── D-S7-5 phase 2 (s6-r63) — resumable chunked upload client API ──────────────────

    /// <summary>
    /// **D-S7-5 phase 2** — `POST /uploads-rs` — resumable upload 시작. uploadId 발급.
    /// </summary>
    public async Task<ResumableUploadStart> StartResumableUploadAsync(
        string title, long totalBytes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title 필수.", nameof(title));
        if (totalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(totalBytes), totalBytes, ">0 필수");

        using var req = NewRequest(HttpMethod.Post, "uploads-rs");
        req.Content = JsonContent.Create(new { title, totalBytes }, options: JsonOptions);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrow(resp, "POST /uploads-rs", ct).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<ResumableUploadStart>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new LightHouseProtocolException("POST /uploads-rs response body 빈 값.");
    }

    /// <summary>
    /// **D-S7-5 phase 2** — `PATCH /uploads-rs/{id}` — chunk append.
    /// `chunk.Length` == `endByte - startByte + 1` 의무. server-side Content-Length 검증 (400 mismatch).
    /// </summary>
    public Task<ResumableUploadStatus> PatchResumableChunkAsync(
        string uploadId, byte[] chunk, long startByte, long endByte, long totalBytes,
        CancellationToken ct = default)
        => PatchResumableChunkAsync(uploadId, chunk, chunk?.Length ?? 0, startByte, endByte, totalBytes, ct);

    /// <summary>
    /// **D-S7-5 phase 2 + s6-r70 review C-10** — `PATCH /uploads-rs/{id}` — buffer + length 명시 overload.
    /// `buffer.Length` ≥ `effectiveLength` 의무 — ArrayPool.Rent 결과 (요청 size 보다 큰 buffer) 박제 path.
    /// `ByteArrayContent(buffer, 0, effectiveLength)` 가 정확 byte 만 wire 로 보내고 traditional buffer.Length 무시.
    /// </summary>
    public async Task<ResumableUploadStatus> PatchResumableChunkAsync(
        string uploadId, byte[] buffer, int effectiveLength, long startByte, long endByte, long totalBytes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uploadId)) throw new ArgumentException("uploadId 필수.", nameof(uploadId));
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (effectiveLength < 0 || effectiveLength > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(effectiveLength),
                $"effectiveLength={effectiveLength} 가 0 또는 buffer.Length={buffer.Length} 와 충돌");
        var expected = endByte - startByte + 1L;
        if ((long)effectiveLength != expected)
            throw new ArgumentException(
                $"effectiveLength={effectiveLength} ≠ Content-Range length={expected}", nameof(effectiveLength));

        using var req = NewRequest(HttpMethod.Patch, $"uploads-rs/{Uri.EscapeDataString(uploadId)}");
        var content = new ByteArrayContent(buffer, 0, effectiveLength);
        content.Headers.ContentLength = effectiveLength;
        content.Headers.Add("Content-Range", $"bytes {startByte}-{endByte}/{totalBytes}");
        req.Content = content;
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrow(resp, $"PATCH /uploads-rs/{uploadId}", ct).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<ResumableUploadStatus>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new LightHouseProtocolException("PATCH /uploads-rs response body 빈 값.");
    }

    /// <summary>**D-S7-5 phase 2** — `GET /uploads-rs/{id}` — 현 offset 조회 (resume hook).</summary>
    public async Task<ResumableUploadStatus> GetResumableUploadStatusAsync(
        string uploadId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uploadId)) throw new ArgumentException("uploadId 필수.", nameof(uploadId));
        using var req = NewRequest(HttpMethod.Get, $"uploads-rs/{Uri.EscapeDataString(uploadId)}");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrow(resp, $"GET /uploads-rs/{uploadId}", ct).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<ResumableUploadStatus>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new LightHouseProtocolException("GET /uploads-rs response body 빈 값.");
    }

    /// <summary>
    /// **D-S7-5 phase 2 + phase 4 (s6-r74 b1)** — `POST /uploads-rs/{id}/finalize` — collection 등록 or swap.
    /// <para/>
    /// swapTargetCollectionId null/빈 값 = new collection path (201 + new id). 박제 = 기존 collection 의 payload swap
    /// (200 + existing id, Registry update + EventBus.collection-updated + OnPayloadSwapped). 응답 status code 분기.
    /// <para/>
    /// 415 = IndexerVersion gate / 400 = zip 결함 / 409 = incomplete / 404 = swap target 미존재 / 403 = swap target read-only.
    /// </summary>
    public async Task<UploadResponse> FinalizeResumableUploadAsync(
        string uploadId,
        string? swapTargetCollectionId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uploadId)) throw new ArgumentException("uploadId 필수.", nameof(uploadId));
        using var req = NewRequest(HttpMethod.Post, $"uploads-rs/{Uri.EscapeDataString(uploadId)}/finalize");
        // body schema = ResumableFinalizeBody — null/빈 값 swap 미진입. server 가 빈 body 도 동일 분기 (null).
        var body = new ResumableFinalizeBody
        {
            SwapTargetCollectionId = string.IsNullOrWhiteSpace(swapTargetCollectionId)
                ? null
                : swapTargetCollectionId.Trim(),
        };
        req.Content = JsonContent.Create(body, options: JsonOptions);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrow(resp, $"POST /uploads-rs/{uploadId}/finalize", ct).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<UploadResponse>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new LightHouseProtocolException("POST /uploads-rs finalize response body 빈 값.");
    }

    /// <summary>**D-S7-5 phase 2** — `DELETE /uploads-rs/{id}` — cancel + staging cleanup.</summary>
    public async Task DeleteResumableUploadAsync(string uploadId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uploadId)) throw new ArgumentException("uploadId 필수.", nameof(uploadId));
        using var req = NewRequest(HttpMethod.Delete, $"uploads-rs/{Uri.EscapeDataString(uploadId)}");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrow(resp, $"DELETE /uploads-rs/{uploadId}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// **D-S7-5 phase 2 + phase 4 (s6-r74 b1) wrapper** — full round-trip: start → chunked PATCH → finalize.
    /// 큰 zip 의 안전한 upload path. caller 가 progress callback 받음 (optional). 실패 시 자동 DELETE (cleanup) —
    /// 단, OperationCanceledException 은 cleanup 생략 (resume hook 보존, 사용자가 명시 DELETE 또는 StagingSweep idle TTL backstop).
    /// <para/>
    /// **swapTargetCollectionId 박제 시 (phase 4)** — finalize 가 기존 collection 의 payload swap 분기 진입 (200 응답).
    /// null/빈 값 = 기존 phase 2 new collection 분기 (201 응답, 회귀 0).
    /// </summary>
    /// <param name="chunkSize">권장 4~8 MB. server-side maxUploadBytes 정합 의무.</param>
    /// <param name="swapTargetCollectionId">박제 시 기존 collection payload swap. null = 신규 collection 등록.</param>
    public async Task<string> UploadCollectionResumableAsync(
        string title,
        Stream zipStream,
        int chunkSize = 4 * 1024 * 1024,
        IProgress<(long sent, long total)>? progress = null,
        string? swapTargetCollectionId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title 필수.", nameof(title));
        if (zipStream is null) throw new ArgumentNullException(nameof(zipStream));
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize), chunkSize, ">0 필수");
        if (!zipStream.CanSeek) throw new ArgumentException("zipStream must be seekable (Length 의무).", nameof(zipStream));

        var totalBytes = zipStream.Length;
        var start = await StartResumableUploadAsync(title, totalBytes, ct).ConfigureAwait(false);
        // **s6-r70 review C-10** — ArrayPool 박제 (LOH 압박 회피). 10GB upload 시 ~2500 chunks × 4MB = 10GB LOH allocation
        // (chunk 마다 new byte[chunkSize]) 정정. ArrayPool.Rent 1회 + try/finally Return + PatchResumableChunkAsync 가
        // ByteArrayContent(buf, 0, len) overload 로 length 명시 → buffer 의 traditional length 무시.
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(chunkSize);
        try
        {
            long offset = 0;
            while (offset < totalBytes)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = totalBytes - offset;
                var thisChunkLen = (int)Math.Min((long)chunkSize, remaining);
                zipStream.Seek(offset, SeekOrigin.Begin);
                var readTotal = 0;
                while (readTotal < thisChunkLen)
                {
                    var r = await zipStream.ReadAsync(buffer.AsMemory(readTotal, thisChunkLen - readTotal), ct).ConfigureAwait(false);
                    if (r == 0) throw new IOException($"zipStream EOF 조기 도달 — offset={offset} read={readTotal}/{thisChunkLen}");
                    readTotal += r;
                }
                // PatchResumableChunkAsync 에 buffer + length 직접 전달 (per-chunk new byte[] 폐기).
                var status = await PatchResumableChunkAsync(
                    start.UploadId, buffer, thisChunkLen, offset, offset + thisChunkLen - 1, totalBytes, ct).ConfigureAwait(false);
                offset = status.Offset;
                progress?.Report((offset, totalBytes));
            }
            var finalResp = await FinalizeResumableUploadAsync(start.UploadId, swapTargetCollectionId, ct).ConfigureAwait(false);
            return finalResp.Id;
        }
        catch (OperationCanceledException)
        {
            // resume 가능 — staging 보존. 사용자가 별 호출로 DELETE 또는 StagingSweep idle TTL backstop.
            throw;
        }
        catch
        {
            // 다른 실패 (size mismatch / 415 / 400) — server-side cleanup. caller 가 retry 시 새 uploadId 발급.
            try { await DeleteResumableUploadAsync(start.UploadId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception delEx) { Log.Warn($"DeleteResumableUpload cleanup 실패 (best-effort): {delEx.Message}"); }
            throw;
        }
        finally
        {
            // **s6-r70 review C-10** — ArrayPool buffer Return. clearArray=false (next Rent 시 caller 가 read 전 overwrite).
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// **D-S7-2c (s6-r32)** — `POST /events/caption-progress` — Phase 2 vision caption 진행률 client → server publish.
    /// server 는 검증 (collectionId / progress 0~100) 후 EventBus 로 fan-out — 다른 subscriber (codex / 별 Promaker
    /// instance) 가 본 SSE 로 진행률 수신.
    /// <para/>
    /// caller 정책: 매 image (또는 batch) caption 완료 시점에 0~100 progress 호출. `progress` 가 100 도달 시 caller
    /// 는 본 publish 직후 별 ReuploadCollectionPayloadAsync 호출 — server 는 본 publish 자체로 collection state 변경 X.
    /// <para/>
    /// 400 (request body 결함) 시 <see cref="LightHouseProtocolException"/>, 401/403 시 <see cref="LightHouseAuthException"/>.
    /// </summary>
    public async Task PublishCaptionProgressAsync(
        string collectionId,
        int progress,
        string? message = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(collectionId))
            throw new ArgumentException("collectionId 필수.", nameof(collectionId));
        if (progress < 0 || progress > 100)
            throw new ArgumentOutOfRangeException(nameof(progress), progress, "progress 는 0~100 범위.");

        using var req = NewRequest(HttpMethod.Post, "events/caption-progress");
        req.Content = JsonContent.Create(
            new CaptionProgressRequest { CollectionId = collectionId, Progress = progress, Message = message },
            options: JsonOptions);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrow(resp, "POST /events/caption-progress", ct).ConfigureAwait(false);
    }
}

// ── Response DTOs (camelCase 직렬화) ─────────────────────────────────────────

public sealed class UploadResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("storageRelPath")] public string StorageRelPath { get; set; } = "";
}

/// <summary>D-S7-5 phase 2 — POST /uploads-rs 응답.</summary>
public sealed class ResumableUploadStart
{
    [JsonPropertyName("uploadId")] public string UploadId { get; set; } = "";
    [JsonPropertyName("offset")] public long Offset { get; set; }
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; set; }
}

/// <summary>D-S7-5 phase 2 — PATCH / GET /uploads-rs 응답.</summary>
public sealed class ResumableUploadStatus
{
    [JsonPropertyName("uploadId")] public string UploadId { get; set; } = "";
    [JsonPropertyName("offset")] public long Offset { get; set; }
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
}

/// <summary>
/// **D-S7-5 phase 4 (s6-r74 b1)** — POST /uploads-rs/{id}/finalize 요청 body. swapTargetCollectionId null/missing =
/// new collection 분기, 박제 = 기존 collection payload swap 분기. JsonOptions 의 WhenWritingNull 정합으로 null 시
/// JSON 의 swapTargetCollectionId 필드 자체 제외 — server 가 빈 body 도 동일 분기 (회귀 0).
/// </summary>
public sealed class ResumableFinalizeBody
{
    [JsonPropertyName("swapTargetCollectionId")] public string? SwapTargetCollectionId { get; set; }
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
    /// <summary>**PR-A (r0, todo-lighthouse-index-summary.md §3.1)** — collection topic 1줄 합성.
    /// Phase 1 = 빈 string (b1 stats 만으로는 합성 불가). KbDigestBuilder 가 빈 시 title 만 박제.</summary>
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    /// <summary>**PR-A (r0)** — KeywordExtractor 결과 (top-N=15 잠정).
    /// 빈 array = legacy collection (PR-B 이전 색인). KbDigestBuilder fallback path.</summary>
    [JsonPropertyName("keywords")] public string[] Keywords { get; set; } = Array.Empty<string>();
}

public sealed class CollectionListResponse
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("collections")] public List<CollectionInfo> Collections { get; set; } = new();
}

/// <summary>
/// `GET /events` SSE payload (D-S7-2b s6-r28). server-side `EventBus.ServerEvent` 정합.
/// `event` = "collection-added" / "collection-updated" / "collection-deleted" / "keepalive".
/// <para/>
/// **D-S7-3b (s6-r30) — ServiceId client-side tagging**. server 측 변경 없음 (결정 #4) —
/// server 는 본인의 serviceId 모름. `LightHouseClientHolder` 의 SSE callback 안에서 stream
/// 의 owner ServiceId 를 evt 에 박제 후 publish. wire 에는 본 필드 누락 (JsonIgnore) — server
/// 가 본 필드 deserialize 시 ignore.
/// </summary>
public sealed class ServerEventDto
{
    [JsonPropertyName("event")] public string Event { get; set; } = "";
    [JsonPropertyName("collectionId")] public string? CollectionId { get; set; }
    [JsonPropertyName("progress")] public int? Progress { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = "";

    /// <summary>
    /// **D-S7-3b (s6-r30)** — client-side tag (wire 송수신 아님). holder 의 SSE callback 안에서
    /// stream owner ServiceId 박제. KbManagerDialog 등 caller 가 어느 service tab 을 갱신할지 결정.
    /// </summary>
    [JsonIgnore]
    public string ServiceId { get; set; } = "";
}

internal sealed class SessionCreateRequest
{
    [JsonPropertyName("collectionIds")] public IReadOnlyList<string> CollectionIds { get; set; } = Array.Empty<string>();
}

/// <summary>**D-S7-2c (s6-r32)** — POST /events/caption-progress request body. server-side `CaptionProgressRequest` 정합.</summary>
internal sealed class CaptionProgressRequest
{
    [JsonPropertyName("collectionId")] public string CollectionId { get; set; } = "";
    [JsonPropertyName("progress")] public int Progress { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
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
