using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ds2.LightHouse.Protocol;
using Promaker.Knowledge;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// **PR-F (todo-lighthouse-index-summary.md §5.1)** — KbProfileExtractor 단위 시험.
///
/// scope: SSE event 분류 + acceptedIds filter + 단일 service fetch (mock HttpMessageHandler).
/// ViewModel 의 multi-service loop / cache / debounce 는 KbProfileExtractor 호출만 wrap — 본 suite 는
/// helper 의 pure 책임만 검증.
/// </summary>
public sealed class KbProfileExtractorTests
{
    private const string BaseUrl = "https://service.test.local:8443/";

    private sealed class StubHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Responder(request));
        }
    }

    private static (LightHouseClient client, StubHandler handler) MakeClient(string psk = "test-psk")
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var client = new LightHouseClient(
            http,
            () => psk,
            "tester@example.com",
            activeCollectionIdsProvider: null,
            ownsHttp: true);
        return (client, handler);
    }

    // ── IsKbProfileEvent — SSE event 분류 ────────────────────────────────────

    [Theory]
    [InlineData(ServerEventNames.CollectionAdded)]
    [InlineData(ServerEventNames.CollectionUpdated)]
    [InlineData(ServerEventNames.CollectionDeleted)]
    public void IsKbProfileEvent_collection_변경_event_true(string evtName)
    {
        // PR-F SSE event 분류 — 본 3종은 cache invalidate trigger.
        var dto = new ServerEventDto { Event = evtName, CollectionId = "abc" };
        Assert.True(KbProfileExtractor.IsKbProfileEvent(dto));
    }

    [Theory]
    [InlineData(ServerEventNames.Keepalive)]
    [InlineData(ServerEventNames.CaptionProgress)]
    [InlineData(ServerEventNames.UploadProgress)]
    [InlineData("")]
    [InlineData("unknown-future-event")]
    public void IsKbProfileEvent_progress_및_미정의_event_false(string evtName)
    {
        // PR-F — progress event 는 burst 차단 (debounce schedule 안 함). 미정의 event 도 false (보수적).
        var dto = new ServerEventDto { Event = evtName };
        Assert.False(KbProfileExtractor.IsKbProfileEvent(dto));
    }

    [Fact]
    public void IsKbProfileEvent_null_event_false()
    {
        Assert.False(KbProfileExtractor.IsKbProfileEvent(null));
    }

    // ── FilterAccepted — server response 의 collection 셋 필터링 ──────────────

    [Fact]
    public void FilterAccepted_accepted_셋만_결과_박제()
    {
        // PR-F — server response 3개 중 accepted 셋 2개만 결과. unknown/unindexable id 자연 제외.
        var collections = new List<CollectionInfo>
        {
            new() { Id = "id-1", DisplayName = "A" },
            new() { Id = "id-2", DisplayName = "B" },
            new() { Id = "id-3", DisplayName = "C" },
        };
        var accepted = new[] { "id-1", "id-3" };

        var result = KbProfileExtractor.FilterAccepted(collections, accepted);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "id-1", "id-3" }, result.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void FilterAccepted_빈_accepted_빈_결과()
    {
        // PR-F — session 응답에 accepted 가 0개면 KB digest 자체 비활성 정합 — 빈 list 반환.
        var collections = new List<CollectionInfo>
        {
            new() { Id = "id-1" },
            new() { Id = "id-2" },
        };
        Assert.Empty(KbProfileExtractor.FilterAccepted(collections, Array.Empty<string>()));
    }

    // ── FetchForServiceAsync — 단일 service round-trip ───────────────────────

    [Fact]
    public async Task FetchForServiceAsync_성공시_HTTP_호출_후_filter_적용()
    {
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "schemaVersion": 1,
                  "collections": [
                    {"id":"id-keep","displayName":"K","indexerVersion":"2.2.0","fileCount":1,"status":"idle","lastImportedAt":"","description":"d","keywords":["k1"]},
                    {"id":"id-drop","displayName":"D","indexerVersion":"2.2.0","fileCount":0,"status":"idle","lastImportedAt":""}
                  ]
                }
                """, Encoding.UTF8, "application/json"),
        };

        var result = await KbProfileExtractor.FetchForServiceAsync(client, new[] { "id-keep" });

        Assert.Single(result);
        Assert.Equal("id-keep", result[0].Id);
        Assert.Equal("d", result[0].Description);
        Assert.Equal(new[] { "k1" }, result[0].Keywords);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FetchForServiceAsync_401_시_빈_list_propagate_차단()
    {
        // PR-F (§5.1 service 실패 ≠ 전체 차단) — service 가 401 응답해도 다른 service 진행 위해 빈 list 반환.
        var (client, handler) = MakeClient();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var result = await KbProfileExtractor.FetchForServiceAsync(client, new[] { "id-keep" });

        Assert.Empty(result);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FetchForServiceAsync_빈_acceptedIds_HTTP_skip()
    {
        // acceptedIds 빈 = filter 결과 자명히 빈 → HTTP 호출 회피 (단순 efficiency).
        var (client, handler) = MakeClient();
        var result = await KbProfileExtractor.FetchForServiceAsync(client, Array.Empty<string>());

        Assert.Empty(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task FetchForServiceAsync_pre_cancelled_token_OCE_전파()
    {
        // **review m-7** — cancellation 경로 우선 — 다른 exception 흡수와 분리. pre-cancelled token 박제 시
        // HttpClient.SendAsync 가 TaskCanceledException throw → catch (Exception) 광역 흡수 우회 → caller 로 전파.
        var (client, _) = MakeClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            KbProfileExtractor.FetchForServiceAsync(client, new[] { "id-1" }, cts.Token));
    }
}
