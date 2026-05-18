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
}
