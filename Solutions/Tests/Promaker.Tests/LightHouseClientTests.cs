using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Promaker.Knowledge;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// Phase S5a — LightHouseClient (HTTP wrapper) 의 protocol contract 시험 (todo-lighthouse-kb-server.md §3.7/§3.8/§3.9).
///
/// scope: mock HttpMessageHandler 로 wire-level 검증 — header 박제 / multipart 빌드 / 401 분기 / URL escape.
/// e2e (WebApplicationFactory) 는 Phase S5c (citation 클릭 시점에 흡수).
/// </summary>
public sealed class LightHouseClientTests
{
    private const string BaseUrl = "https://service.test.local:8443/";

    /// <summary>모든 incoming request 를 캡처 + 미리 set 한 응답 반환하는 mock handler.
    /// LightHouseClient 가 `using var content` 로 dispose 하므로 body 는 SendAsync 시점에 string 으로 미리 capture.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();
        public readonly List<string> RequestBodies = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Responder = _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // request.Content 는 caller 의 `using` 로 SendAsync 반환 후 dispose 됨 — body 를 *지금* string 캡처.
            var body = request.Content is null ? "" :
                await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            RequestBodies.Add(body);
            Requests.Add(request);
            return Responder(request);
        }
    }

    private static (LightHouseClient client, CapturingHandler handler) MakeClient(
        string psk = "test-psk",
        string userIdentity = "tester@example.com",
        Func<IReadOnlyList<string>>? recover = null)
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        // review S5a-m4: InternalsVisibleTo 박제 후 internal ctor 직접 호출 (reflection 우회 제거).
        var client = new LightHouseClient(
            http,
            () => psk,
            userIdentity,
            recover,
            ownsHttp: true);
        return (client, handler);
    }


    // ── HTTPS-only 강제 ───────────────────────────────────────────────────────

    [Fact]
    public void Plain_HTTP_BaseUrl_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new LightHouseClient("http://service.local:8080", () => "psk", "tester"));
    }

    [Fact]
    public void Empty_BaseUrl_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new LightHouseClient("", () => "psk", "tester"));
    }

    [Fact]
    public void Empty_userIdentity_throws_via_public_ctor()
    {
        Assert.Throws<ArgumentException>(() =>
            new LightHouseClient(BaseUrl, () => "psk", ""));
    }


    // ── Auth + X-User-Identity 헤더 자동 ─────────────────────────────────────

    [Fact]
    public async Task UploadCollectionAsync_attaches_Bearer_and_userIdentity()
    {
        var (client, handler) = MakeClient(psk: "psk-xyz", userIdentity: "alice@ex.com");
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id":"abc-123","storageRelPath":"Collections\\abc-123-Title\\"}""",
                                        Encoding.UTF8, "application/json"),
        };

        using var zip = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        var id = await client.UploadCollectionAsync("My Title", zip);

        Assert.Equal("abc-123", id);
        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal(BaseUrl + "collections", req.RequestUri!.ToString());
        Assert.NotNull(req.Headers.Authorization);
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("psk-xyz", req.Headers.Authorization.Parameter);
        Assert.Equal("alice@ex.com", req.Headers.GetValues("X-User-Identity").Single());

        // multipart body 안에 title + zip 필드 박제됐는지 — handler 가 SendAsync 시점에 미리 capture.
        var bodyStr = handler.RequestBodies.Single();
        Assert.Contains("name=title", bodyStr);
        Assert.Contains("My Title", bodyStr);
        Assert.Contains("name=zip", bodyStr);
    }

    [Fact]
    public async Task NewRequest_omits_Authorization_when_psk_null()
    {
        var (client, handler) = MakeClient(psk: null!);
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"schemaVersion":1,"collections":[]}""",
                                        Encoding.UTF8, "application/json"),
        };

        // pskProvider returns null → Authorization 헤더 생략 (server 가 401 응답 가정, client 는 raw forward)
        // 단 본 test 는 200 응답이라 GET 자체는 통과 — Authorization 미존재만 검증.
        _ = await client.ListCollectionsAsync();
        var req = handler.Requests.Single();
        Assert.Null(req.Headers.Authorization);
    }


    // ── List / Delete ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListCollectionsAsync_deserializes_response()
    {
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "schemaVersion": 1,
                  "collections": [
                    {"id":"id-1","displayName":"A","indexerVersion":"1.0.0","fileCount":3,"status":"idle","errorReason":null,"lastImportedAt":"2026-05-18T00:00:00Z"},
                    {"id":"id-2","displayName":"B","indexerVersion":"1.0.0","fileCount":0,"status":"error","errorReason":"oops","lastImportedAt":""}
                  ]
                }
                """, Encoding.UTF8, "application/json"),
        };

        var resp = await client.ListCollectionsAsync();
        Assert.Equal(1, resp.SchemaVersion);
        Assert.Equal(2, resp.Collections.Count);
        Assert.Equal("id-1", resp.Collections[0].Id);
        Assert.Equal("A", resp.Collections[0].DisplayName);
        Assert.Equal(3, resp.Collections[0].FileCount);
        Assert.Null(resp.Collections[0].ErrorReason);
        Assert.Equal("error", resp.Collections[1].Status);
        Assert.Equal("oops", resp.Collections[1].ErrorReason);
    }

    [Fact]
    public async Task DeleteCollectionAsync_URL_escapes_id()
    {
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        await client.DeleteCollectionAsync("550e8400-e29b-41d4-a716-446655440000");
        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.EndsWith("collections/550e8400-e29b-41d4-a716-446655440000", req.RequestUri!.ToString());
    }


    // ── Session ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSessionAsync_serializes_collectionIds_and_deserializes_response()
    {
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "token": "tok-abc",
                  "acceptedCollectionIds": ["id-1"],
                  "unknownIds": ["id-ghost"],
                  "unindexableIds": []
                }
                """, Encoding.UTF8, "application/json"),
        };

        var resp = await client.CreateSessionAsync(new[] { "id-1", "id-ghost" });
        Assert.Equal("tok-abc", resp.Token);
        Assert.Single(resp.AcceptedCollectionIds);
        Assert.Equal("id-1", resp.AcceptedCollectionIds[0]);
        Assert.Single(resp.UnknownIds);
        Assert.Empty(resp.UnindexableIds);

        // request body 의 collectionIds 직렬화 검증 — handler 가 SendAsync 시점에 미리 capture.
        var bodyStr = handler.RequestBodies.Single();
        using var doc = JsonDocument.Parse(bodyStr);
        var ids = doc.RootElement.GetProperty("collectionIds").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "id-1", "id-ghost" }, ids);
    }

    [Fact]
    public async Task DeleteSessionAsync_passes_token_in_path()
    {
        // 실제 token = `Guid.NewGuid().ToString("N")` (32 hex char) — escape 대상 char 0.
        // 본 시험은 escape 자체보다 token 이 path 에 박제되는지를 확인.
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        const string token = "abc123def456";
        await client.DeleteSessionAsync(token);
        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.EndsWith($"sessions/{token}", req.RequestUri!.ToString());
    }


    // ── Error 분기 (401 / 403 / 404 / 5xx) ───────────────────────────────────

    [Fact]
    public async Task Unauthorized_401_throws_LightHouseAuthException()
    {
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<LightHouseAuthException>(() => client.ListCollectionsAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task Forbidden_403_throws_LightHouseAuthException()
    {
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<LightHouseAuthException>(() => client.ListCollectionsAsync());
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [Fact]
    public async Task NotFound_404_throws_LightHouseProtocolException()
    {
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<LightHouseProtocolException>(() =>
            client.DeleteCollectionAsync("ghost"));
    }

    [Fact]
    public async Task ServerError_500_throws_LightHouseProtocolException()
    {
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<LightHouseProtocolException>(() => client.ListCollectionsAsync());
    }

    [Fact]
    public async Task ProtocolException_carries_StatusCode_for_caller_branching()
    {
        // review S5a-M2: 415 (IndexerVersion gate) 와 일반 500 의 사용자 안내 분기 위해 StatusCode 노출.
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType)
        {
            Content = new StringContent("""{"error":"indexerVersion too low","clientVersion":"0.9.0"}""",
                                        Encoding.UTF8, "application/json"),
        };
        using var zip = new MemoryStream(new byte[] { 1 });
        var ex = await Assert.ThrowsAsync<LightHouseProtocolException>(() =>
            client.UploadCollectionAsync("t", zip));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, ex.StatusCode);
    }


    // ── L3 자동 회복 hook ─────────────────────────────────────────────────────

    [Fact]
    public async Task RecoverSessionAsync_without_provider_throws()
    {
        var (client, _) = MakeClient(recover: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RecoverSessionAsync());
    }

    [Fact]
    public async Task RecoverSessionAsync_calls_CreateSession_with_provider_ids()
    {
        var (client, handler) = MakeClient(
            recover: () => new[] { "id-1", "id-2" });
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                { "token": "fresh-tok", "acceptedCollectionIds": ["id-1","id-2"], "unknownIds": [], "unindexableIds": [] }
                """, Encoding.UTF8, "application/json"),
        };

        var resp = await client.RecoverSessionAsync();
        Assert.Equal("fresh-tok", resp.Token);
        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.EndsWith("sessions", req.RequestUri!.ToString());
    }


    // ── Protocol 결함 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_response_missing_id_throws_protocol_exception()
    {
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"storageRelPath":"some-path"}""", Encoding.UTF8, "application/json"),
        };

        using var zip = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<LightHouseProtocolException>(() => client.UploadCollectionAsync("t", zip));
    }


    // ── Phase S5b — 재업로드 (POST /collections/{id}/payload, §3.9 / D5) ──────

    [Fact]
    public async Task ReuploadCollectionPayloadAsync_targets_id_path_with_bearer_and_userIdentity()
    {
        var (client, handler) = MakeClient(psk: "psk-r", userIdentity: "bob@ex.com");
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        using var zip = new MemoryStream(new byte[] { 9, 9 });
        await client.ReuploadCollectionPayloadAsync("550e8400-e29b-41d4-a716-446655440000", zip);

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal(BaseUrl + "collections/550e8400-e29b-41d4-a716-446655440000/payload",
            req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("psk-r", req.Headers.Authorization.Parameter);
        Assert.Equal("bob@ex.com", req.Headers.GetValues("X-User-Identity").Single());
        // multipart 안 zip part 박제 — title field 는 본 endpoint 에서 안 보냄.
        Assert.Contains("name=zip", handler.RequestBodies.Single());
        Assert.DoesNotContain("name=title", handler.RequestBodies.Single());
    }

    [Fact]
    public async Task ReuploadCollectionPayloadAsync_404_throws_protocol()
    {
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("collection not found", Encoding.UTF8, "text/plain"),
        };
        using var zip = new MemoryStream(new byte[] { 1 });
        var ex = await Assert.ThrowsAsync<LightHouseProtocolException>(
            () => client.ReuploadCollectionPayloadAsync("missing-id", zip));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task ReuploadCollectionPayloadAsync_empty_id_throws()
    {
        var (client, _) = MakeClient();
        using var zip = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.ReuploadCollectionPayloadAsync("", zip));
    }


    // ── P2-r3 (s6-r6): ExecuteWithSessionRetryAsync facade ──────────────────

    [Fact]
    public async Task ExecuteWithSessionRetry_without_provider_throws()
    {
        // recover provider 미설정 시 wrapper 자체 거부 — 의도된 retry 비활성 fail-loud.
        var (client, _) = MakeClient(recover: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ExecuteWithSessionRetryAsync<int>((_, _) => Task.FromResult(0)));
    }

    [Fact]
    public async Task ExecuteWithSessionRetry_first_call_succeeds_no_retry()
    {
        // operation 이 첫 token 으로 성공 → RecoverSession 1회 + operation 1회 = HTTP 1회.
        var (client, handler) = MakeClient(recover: () => new[] { "id-1" });
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"token":"tok-1","acceptedCollectionIds":["id-1"],"unknownIds":[],"unindexableIds":[]}""",
                Encoding.UTF8, "application/json"),
        };

        string capturedToken = "";
        var result = await client.ExecuteWithSessionRetryAsync(async (tok, _ct) =>
        {
            capturedToken = tok;
            return await Task.FromResult("ok");
        });

        Assert.Equal("ok", result);
        Assert.Equal("tok-1", capturedToken);
        // HTTP 호출 = RecoverSession 1회만 (operation lambda 는 in-process, HTTP 무관).
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ExecuteWithSessionRetry_on_401_recovers_and_retries_once()
    {
        // 첫 operation 호출 = 401 throw → wrapper 가 RecoverSession 재호출 → 새 token 으로 재시도 → 성공.
        var (client, handler) = MakeClient(recover: () => new[] { "id-1" });
        var sessionCallCount = 0;
        handler.Responder = _ =>
        {
            sessionCallCount++;
            var tok = sessionCallCount == 1 ? "tok-A" : "tok-B";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"token":"{{tok}}","acceptedCollectionIds":["id-1"],"unknownIds":[],"unindexableIds":[]}""",
                    Encoding.UTF8, "application/json"),
            };
        };

        var attempt = 0;
        var seenTokens = new List<string>();
        var result = await client.ExecuteWithSessionRetryAsync<string>(async (tok, _ct) =>
        {
            seenTokens.Add(tok);
            attempt++;
            if (attempt == 1)
                throw new LightHouseAuthException("simulated 401", HttpStatusCode.Unauthorized);
            return await Task.FromResult($"ok-on-{tok}");
        });

        Assert.Equal("ok-on-tok-B", result);
        Assert.Equal(2, attempt);
        Assert.Equal(new[] { "tok-A", "tok-B" }, seenTokens);
        Assert.Equal(2, sessionCallCount);  // RecoverSession 2회 (첫 + retry)
    }

    [Fact]
    public async Task ExecuteWithSessionRetry_OperationCanceled_skips_retry_and_propagates()
    {
        // 자가 검열 M2: operation 안에서 OCE throw → wrapper 가 retry 진입 차단 후 그대로 전파.
        // ct 는 None — RecoverSession 단계에서 OCE 가 먼저 발생하면 wrapper 본체 catch 검증 불가.
        var (client, handler) = MakeClient(recover: () => new[] { "id-1" });
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"token":"tok-1","acceptedCollectionIds":["id-1"],"unknownIds":[],"unindexableIds":[]}""",
                Encoding.UTF8, "application/json"),
        };

        var attempt = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            client.ExecuteWithSessionRetryAsync<string>((tok, _opCt) =>
            {
                attempt++;
                throw new OperationCanceledException("simulated cancel inside op");
            }, CancellationToken.None));

        // operation 첫 호출 1회만 — wrapper 가 LightHouseAuthException catch 만 처리, OCE 는 즉시 전파.
        Assert.Equal(1, attempt);
        // RecoverSession 1회 (첫 token 발급) 만 — retry 진입 안 함.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ExecuteWithSessionRetry_recover_step_failing_with_401_throws()
    {
        // 첫 op 401 → wrapper 가 RecoverSession 재호출 → RecoverSession 자체 401 (PSK 결함 의심) → wrapper throw.
        var (client, handler) = MakeClient(recover: () => new[] { "id-1" });
        var sessionCallCount = 0;
        handler.Responder = _ =>
        {
            sessionCallCount++;
            if (sessionCallCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"token":"tok-A","acceptedCollectionIds":["id-1"],"unknownIds":[],"unindexableIds":[]}""",
                        Encoding.UTF8, "application/json"),
                };
            }
            // 2nd RecoverSession 호출 = 401 응답 → EnsureSuccessOrThrow 가 LightHouseAuthException throw.
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("psk rejected", Encoding.UTF8, "text/plain"),
            };
        };

        var ex = await Assert.ThrowsAsync<LightHouseAuthException>(() =>
            client.ExecuteWithSessionRetryAsync<string>((tok, _ct) =>
                throw new LightHouseAuthException("simulated 401", HttpStatusCode.Unauthorized)));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("RecoverSession 자체 실패", ex.Message);
    }


    // ── D-S7-2b OpenEventsStreamAsync (SSE) ─────────────────────────────────────

    [Fact]
    public async Task OpenEventsStreamAsync_parses_data_lines_in_order()
    {
        var (client, handler) = MakeClient(psk: "psk-sse", userIdentity: "sse@ex.com");
        var ndjsonBody =
            "data: {\"event\":\"keepalive\",\"timestamp\":\"2026-05-19T00:00:00Z\"}\n\n" +
            "data: {\"event\":\"collection-added\",\"collectionId\":\"abc-123\",\"timestamp\":\"2026-05-19T00:00:01Z\"}\n\n" +
            "data: {\"event\":\"collection-deleted\",\"collectionId\":\"abc-123\",\"timestamp\":\"2026-05-19T00:00:02Z\"}\n\n";
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ndjsonBody, Encoding.UTF8, "text/event-stream"),
        };

        var received = new List<ServerEventDto>();
        await client.OpenEventsStreamAsync(evt => { received.Add(evt); return Task.CompletedTask; });

        Assert.Equal(3, received.Count);
        Assert.Equal(ServerEventNames.Keepalive, received[0].Event);
        Assert.Null(received[0].CollectionId);
        Assert.Equal(ServerEventNames.CollectionAdded, received[1].Event);
        Assert.Equal("abc-123", received[1].CollectionId);
        Assert.Equal(ServerEventNames.CollectionDeleted, received[2].Event);

        // 헤더 검증 — Bearer + X-User-Identity 자동 동봉.
        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal(BaseUrl + "events", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("psk-sse", req.Headers.Authorization.Parameter);
        Assert.Equal("sse@ex.com", req.Headers.GetValues("X-User-Identity").Single());
    }

    [Fact]
    public async Task OpenEventsStreamAsync_401_throws_LightHouseAuthException()
    {
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("psk rejected", Encoding.UTF8, "text/plain"),
        };

        var ex = await Assert.ThrowsAsync<LightHouseAuthException>(() =>
            client.OpenEventsStreamAsync(_ => Task.CompletedTask));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task OpenEventsStreamAsync_skips_malformed_json_line()
    {
        var (client, handler) = MakeClient();
        var ndjsonBody =
            "data: {malformed json\n\n" +  // 1줄 skip
            "data: {\"event\":\"collection-added\",\"collectionId\":\"x\",\"timestamp\":\"t\"}\n\n";
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ndjsonBody, Encoding.UTF8, "text/event-stream"),
        };

        var received = new List<ServerEventDto>();
        await client.OpenEventsStreamAsync(evt => { received.Add(evt); return Task.CompletedTask; });

        Assert.Single(received);
        Assert.Equal(ServerEventNames.CollectionAdded, received[0].Event);
    }

    [Fact]
    public async Task OpenEventsStreamAsync_null_callback_throws()
    {
        var (client, _) = MakeClient();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.OpenEventsStreamAsync(null!));
    }

    [Fact]
    public async Task OpenEventsStreamAsync_ignores_non_data_lines()
    {
        var (client, handler) = MakeClient();
        // SSE 표준상 comment (`:` prefix) / event: / id: / retry: 등 future-compat — `data:` 만 parse.
        var ndjsonBody =
            ": this is a comment\n" +
            "event: ignored\n" +
            "id: 12345\n" +
            "\n" +  // empty separator
            "data: {\"event\":\"keepalive\",\"timestamp\":\"t\"}\n\n";
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ndjsonBody, Encoding.UTF8, "text/event-stream"),
        };

        var received = new List<ServerEventDto>();
        await client.OpenEventsStreamAsync(evt => { received.Add(evt); return Task.CompletedTask; });

        Assert.Single(received);
        Assert.Equal(ServerEventNames.Keepalive, received[0].Event);
    }


    // ── D-S7-2c PublishCaptionProgressAsync ─────────────────────────────────────

    [Fact]
    public async Task PublishCaptionProgressAsync_sends_body_and_headers()
    {
        var (client, handler) = MakeClient(psk: "psk-cp", userIdentity: "cp@ex.com");
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        await client.PublishCaptionProgressAsync("col-xyz", 42, "5/120 image");

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal(BaseUrl + "events/caption-progress", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("psk-cp", req.Headers.Authorization.Parameter);
        Assert.Equal("cp@ex.com", req.Headers.GetValues("X-User-Identity").Single());

        var body = handler.RequestBodies.Single();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("col-xyz", doc.RootElement.GetProperty("collectionId").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("progress").GetInt32());
        Assert.Equal("5/120 image", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task PublishCaptionProgressAsync_omits_null_message()
    {
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        await client.PublishCaptionProgressAsync("c1", 0);

        var body = handler.RequestBodies.Single();
        using var doc = JsonDocument.Parse(body);
        // JsonOptions.DefaultIgnoreCondition = WhenWritingNull → message 필드 wire 누락.
        Assert.False(doc.RootElement.TryGetProperty("message", out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task PublishCaptionProgressAsync_progress_out_of_range_throws(int progress)
    {
        var (client, _) = MakeClient();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.PublishCaptionProgressAsync("c1", progress));
    }

    [Fact]
    public async Task PublishCaptionProgressAsync_empty_collectionId_throws()
    {
        var (client, _) = MakeClient();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.PublishCaptionProgressAsync("", 50));
    }

    [Fact]
    public async Task PublishCaptionProgressAsync_401_throws_LightHouseAuthException()
    {
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("psk rejected"),
        };

        var ex = await Assert.ThrowsAsync<LightHouseAuthException>(() =>
            client.PublishCaptionProgressAsync("c1", 50));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    // ── B5 D-S7-1 후속 (s6-r61) — client cert thumbprint normalize / X509Store lookup ───────────

    [Theory]
    [InlineData("aa:bb:cc:dd", "AABBCCDD")]
    [InlineData("AA BB CC", "AABBCC")]
    [InlineData("aa-bb-cc", "AABBCC")]
    [InlineData("  abcdef  ", "ABCDEF")]
    [InlineData("", "")]
    public void NormalizeThumbprint_strips_separators_and_uppercases(string input, string expected)
    {
        // B5: thumbprint normalize = hex 추출 + 대문자 (':' / 공백 / hyphen 제거). server-side Config.normalizeThumbprint 정합.
        Assert.Equal(expected, LightHouseClient.NormalizeThumbprint(input));
    }

    [Fact]
    public void LookupClientCert_invalid_length_throws()
    {
        // SHA-1=40 / SHA-256=64 외 길이 거부 (config 결함 fail-fast).
        Assert.Throws<InvalidOperationException>(() =>
            LightHouseClient.LookupClientCert("DEADBEEF"));
    }

    [Fact]
    public void LookupClientCert_nonexistent_thumbprint_throws_with_hint()
    {
        // 임의 thumbprint (40 hex, X509Store 에 존재하지 않을 가능성 매우 높음).
        var bogus = new string('A', 40);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LightHouseClient.LookupClientCert(bogus));
        // 사용자 안내 hint 박제 검증 (PowerShell Import-PfxCertificate / MMC).
        Assert.Contains("Import-PfxCertificate", ex.Message);
    }

    [Fact]
    public void LightHouseServiceConfig_ClientCertThumbprint_round_trip()
    {
        // B5: LlmConfig 의 새 필드 round-trip (JSON 직렬화 → 역직렬화 보존).
        var svc = new Promaker.LlmAgent.LightHouseServiceConfig
        {
            ServiceId = "test-svc-id",
            DisplayName = "test",
            BaseUrl = "https://example.local:8443",
            ClientCertThumbprint = "AA:BB:CC:DD:EE:FF:11:22:33:44:55:66:77:88:99:00:AA:BB:CC:DD",
        };
        var json = JsonSerializer.Serialize(svc);
        Assert.Contains("clientCertThumbprint", json);
        var restored = JsonSerializer.Deserialize<Promaker.LlmAgent.LightHouseServiceConfig>(json);
        Assert.NotNull(restored);
        Assert.Equal(svc.ClientCertThumbprint, restored!.ClientCertThumbprint);
    }

    [Fact]
    public void LightHouseServiceConfig_ClientCertThumbprint_null_omitted_in_json()
    {
        // null 시 JSON 출력에서 누락 (back-compat — legacy disk JSON 에 본 필드 부재).
        var svc = new Promaker.LlmAgent.LightHouseServiceConfig
        {
            ServiceId = "test-svc-id",
            DisplayName = "test",
            BaseUrl = "https://example.local:8443",
            ClientCertThumbprint = null,
        };
        var json = JsonSerializer.Serialize(svc,
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        Assert.DoesNotContain("clientCertThumbprint", json);
    }

    // ── D-S7-5 phase 2 — resumable upload client API (s6-r63) ─────────────────────

    [Fact]
    public async Task StartResumableUploadAsync_POST_uploads_rs_body_검증_uploadId_도출()
    {
        var (client, handler) = MakeClient();
        var responseBody = """{"uploadId":"abc123def456","offset":0,"totalBytes":1024}""";
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        };

        var result = await client.StartResumableUploadAsync("my-title", 1024L);

        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal($"{BaseUrl}uploads-rs", req.RequestUri!.ToString());
        var sentBody = handler.RequestBodies[0];
        Assert.Contains("\"title\":\"my-title\"", sentBody);
        Assert.Contains("\"totalBytes\":1024", sentBody);
        Assert.Equal("abc123def456", result.UploadId);
        Assert.Equal(1024L, result.TotalBytes);
    }

    [Fact]
    public async Task PatchResumableChunkAsync_Content_Range와_Content_Length_헤더_박제_body_bytes_정합()
    {
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"uploadId":"abc","offset":50,"totalBytes":100}""",
                Encoding.UTF8, "application/json"),
        };

        var chunk = new byte[50];
        for (int i = 0; i < 50; i++) chunk[i] = (byte)i;
        var status = await client.PatchResumableChunkAsync("abc", chunk, 0L, 49L, 100L);

        var req = handler.Requests[0];
        Assert.Equal(HttpMethod.Patch, req.Method);
        Assert.Equal($"{BaseUrl}uploads-rs/abc", req.RequestUri!.ToString());
        Assert.Equal(50L, req.Content!.Headers.ContentLength);
        Assert.Contains(req.Content.Headers, h => h.Key == "Content-Range" && h.Value.Contains("bytes 0-49/100"));
        Assert.Equal(50L, status.Offset);
    }

    [Fact]
    public async Task PatchResumableChunkAsync_chunk_length_불일치_ArgumentException()
    {
        var (client, _) = MakeClient();
        // chunk.Length=40 인데 endByte-startByte+1 = 50 → caller 측 검증 throw.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.PatchResumableChunkAsync("abc", new byte[40], 0L, 49L, 100L));
    }

    [Fact]
    public async Task FinalizeResumableUploadAsync_POST_finalize_collectionId_도출()
    {
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id":"550e8400-e29b-41d4-a716-446655440000","storageRelPath":"Collections\\foo"}""",
                Encoding.UTF8, "application/json"),
        };

        var resp = await client.FinalizeResumableUploadAsync("abc123");

        var req = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal($"{BaseUrl}uploads-rs/abc123/finalize", req.RequestUri!.ToString());
        Assert.Equal("550e8400-e29b-41d4-a716-446655440000", resp.Id);
        Assert.Equal("Collections\\foo", resp.StorageRelPath);
    }

    [Fact]
    public async Task UploadCollectionResumableAsync_wrapper_multi_chunk_round_trip()
    {
        var (client, handler) = MakeClient();
        // 100-byte zip stream + chunkSize=30 → start + 4 PATCH (30+30+30+10) + finalize = 6 request.
        var zipBytes = new byte[100];
        for (int i = 0; i < 100; i++) zipBytes[i] = (byte)i;
        long expectedOffset = 0;
        handler.Responder = req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/uploads-rs"))
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("""{"uploadId":"wrap-test","offset":0,"totalBytes":100}""",
                        Encoding.UTF8, "application/json"),
                };
            if (req.Method == HttpMethod.Patch)
            {
                var range = req.Content!.Headers.GetValues("Content-Range").First();
                // "bytes <start>-<end>/<total>" → <end> + 1 = newOffset
                var slash = range.IndexOf('/');
                var dash = range.IndexOf('-');
                var endStr = range.Substring(dash + 1, slash - dash - 1);
                expectedOffset = long.Parse(endStr) + 1L;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"uploadId":"wrap-test","offset":{{expectedOffset}},"totalBytes":100}""",
                        Encoding.UTF8, "application/json"),
                };
            }
            // finalize
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    """{"id":"new-coll-id","storageRelPath":"Collections\\wrap"}""",
                    Encoding.UTF8, "application/json"),
            };
        };

        using var stream = new MemoryStream(zipBytes);
        var collectionId = await client.UploadCollectionResumableAsync("wrap-test", stream, chunkSize: 30);

        Assert.Equal("new-coll-id", collectionId);
        // start (1) + PATCH (4) + finalize (1) = 6 request
        Assert.Equal(6, handler.Requests.Count);
        // 마지막 PATCH 의 Content-Range = bytes 90-99/100
        var lastPatch = handler.Requests.Where(r => r.Method == HttpMethod.Patch).Last();
        Assert.Contains(lastPatch.Content!.Headers,
            h => h.Key == "Content-Range" && h.Value.Contains("bytes 90-99/100"));
    }
}
