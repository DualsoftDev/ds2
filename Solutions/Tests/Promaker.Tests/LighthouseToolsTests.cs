using System;
using System.Linq;
using System.Text.Json;
using Promaker.Knowledge;
using Promaker.LlmAgent;
using Promaker.LlmAgent.Tools;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// **B2 (2026-05-27)** — Plan B wrapper (LightHouseTools) 의 helper / merge / prefix routing 검증.
/// 본 suite 가 B1 (session pooling) / B3 (JsonNode refactor) 의 회귀 가드 SSOT — backlog 문서 §2.B2 의무.
/// <para/>
/// scope: fileId 파싱 / collection guid 추출 / N-service merge / fileId prefix 박제 / FindClientByPrefix routing.
/// LightHouseClientHolder 의 static state 를 다루는 case 는 Invalidate / IDisposable pattern 으로 격리.
/// <para/>
/// **B5/B6/B7 phase fix (2026-05-27)** — `[Collection("LightHouseClientHolder")]` 박제로 `LightHouseClientHolderTests`
/// 와 순차 실행 강제. 두 class 가 동일 static state (`LightHouseClientHolder._clients`) 박제 — xUnit default 병렬
/// 실행 시 cross-contamination 으로 `LighthouseList_no_active_service_시_hint_응답` 의 `clients.Count == 0` 가드 실패.
/// </summary>
[Collection("LightHouseClientHolder")]
public sealed class LighthouseToolsTests : IDisposable
{
    public LighthouseToolsTests() => LightHouseClientHolder.Invalidate();
    public void Dispose() => LightHouseClientHolder.Invalidate();

    // ─── TryParseFileId ────────────────────────────────────────────────────────

    [Fact]
    public void TryParseFileId_정상_형식_분리()
    {
        var ok = LightHouseTools.TryParseFileId("12345678:c725077d-aaaa-bbbb-cccc-ddddeeeeffff:doc-1",
            out var prefix, out var orig);
        Assert.True(ok);
        Assert.Equal("12345678", prefix);
        Assert.Equal("c725077d-aaaa-bbbb-cccc-ddddeeeeffff:doc-1", orig);
    }

    [Fact]
    public void TryParseFileId_콜론_없음_실패()
    {
        Assert.False(LightHouseTools.TryParseFileId("nocolonatall", out _, out _));
    }

    [Fact]
    public void TryParseFileId_첫_segment_길이_불일치_실패()
    {
        // 7자 prefix — ServiceIdPrefixLen=8 과 불일치.
        Assert.False(LightHouseTools.TryParseFileId("1234567:rest", out _, out _));
        // 9자 prefix.
        Assert.False(LightHouseTools.TryParseFileId("123456789:rest", out _, out _));
    }

    [Fact]
    public void TryParseFileId_빈_값_실패()
    {
        Assert.False(LightHouseTools.TryParseFileId("", out _, out _));
        Assert.False(LightHouseTools.TryParseFileId(null!, out _, out _));
    }

    [Fact]
    public void TryParseFileId_prefix_뒤_orig_비어있으면_실패()
    {
        // "12345678:" — orig 부분이 빈 문자열.
        Assert.False(LightHouseTools.TryParseFileId("12345678:", out _, out _));
    }

    // ─── MakePrefix ────────────────────────────────────────────────────────────

    [Fact]
    public void MakePrefix_Guid_v4_첫_8자_추출()
    {
        var guid = "c725077d-aaaa-bbbb-cccc-ddddeeeeffff";
        Assert.Equal("c725077d", LightHouseTools.MakePrefix(guid));
    }

    [Fact]
    public void MakePrefix_8자_미만이면_전체_반환()
    {
        Assert.Equal("abc", LightHouseTools.MakePrefix("abc"));
        Assert.Equal("", LightHouseTools.MakePrefix(""));
    }

    // ─── ExtractCollectionGuid ────────────────────────────────────────────────

    [Fact]
    public void ExtractCollectionGuid_콜론_앞부분_추출()
    {
        Assert.Equal("c725077d-aaaa-bbbb-cccc-ddddeeeeffff",
            LightHouseTools.ExtractCollectionGuid("c725077d-aaaa-bbbb-cccc-ddddeeeeffff:doc-1"));
    }

    [Fact]
    public void ExtractCollectionGuid_콜론_없으면_전체_반환()
    {
        Assert.Equal("only-segment", LightHouseTools.ExtractCollectionGuid("only-segment"));
    }

    [Fact]
    public void ExtractCollectionGuid_빈_값()
    {
        Assert.Equal("", LightHouseTools.ExtractCollectionGuid(""));
        Assert.Equal("", LightHouseTools.ExtractCollectionGuid(null!));
    }

    // ─── MergeListResults ─────────────────────────────────────────────────────

    [Fact]
    public void MergeListResults_N_service_concat_및_totalCount_정합()
    {
        var perService = new[]
        {
            ("sid-a", "ServiceA", """[{"fileId":"aaaaaaaa:f1","fileName":"a1.md"},{"fileId":"aaaaaaaa:f2","fileName":"a2.md"}]"""),
            ("sid-b", "ServiceB", """[{"fileId":"bbbbbbbb:f3","fileName":"b1.md"}]"""),
        };
        var merged = LightHouseTools.MergeListResults(perService);
        using var doc = JsonDocument.Parse(merged);
        Assert.Equal(3, doc.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("activeServiceCount").GetInt32());
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void MergeListResults_빈_raw_안전_처리()
    {
        var perService = new[]
        {
            ("sid-a", "ServiceA", ""),
            ("sid-b", "ServiceB", "[]"),
        };
        var merged = LightHouseTools.MergeListResults(perService);
        using var doc = JsonDocument.Parse(merged);
        Assert.Equal(0, doc.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("activeServiceCount").GetInt32());
    }

    [Fact]
    public void MergeListResults_non_array_root_skip()
    {
        var perService = new[]
        {
            ("sid-a", "ServiceA", """{"not":"an array"}"""),
            ("sid-b", "ServiceB", """[{"fileId":"bbbbbbbb:f1"}]"""),
        };
        var merged = LightHouseTools.MergeListResults(perService);
        using var doc = JsonDocument.Parse(merged);
        // A 는 object 라 skip, B 의 1건만 잡힘.
        Assert.Equal(1, doc.RootElement.GetProperty("totalCount").GetInt32());
    }

    // ─── MergeSearchResultsRoundRobin ─────────────────────────────────────────

    [Fact]
    public void MergeSearchResultsRoundRobin_각_server_1건씩_interleave()
    {
        // server A: [A1, A2, A3], server B: [B1, B2] → 결과 [A1, B1, A2, B2, A3].
        var rawA = """{"results":[{"fileId":"aaaaaaaa:f1","score":0.9},{"fileId":"aaaaaaaa:f2","score":0.8},{"fileId":"aaaaaaaa:f3","score":0.7}]}""";
        var rawB = """{"results":[{"fileId":"bbbbbbbb:g1","score":0.95},{"fileId":"bbbbbbbb:g2","score":0.85}]}""";
        var merged = LightHouseTools.MergeSearchResultsRoundRobin(new[]
        {
            ("sid-a", "ServiceA", rawA),
            ("sid-b", "ServiceB", rawB),
        });
        using var doc = JsonDocument.Parse(merged);
        var hits = doc.RootElement.GetProperty("results").EnumerateArray()
            .Select(h => h.GetProperty("fileId").GetString()).ToList();
        Assert.Equal(new[] { "aaaaaaaa:f1", "bbbbbbbb:g1", "aaaaaaaa:f2", "bbbbbbbb:g2", "aaaaaaaa:f3" }, hits);
    }

    [Fact]
    public void MergeSearchResultsRoundRobin_perServerCounts_정합()
    {
        var rawA = """{"results":[{"fileId":"aaaaaaaa:f1"}]}""";
        var rawB = """{"results":[{"fileId":"bbbbbbbb:g1"},{"fileId":"bbbbbbbb:g2"}]}""";
        var merged = LightHouseTools.MergeSearchResultsRoundRobin(new[]
        {
            ("sid-a", "ServiceA", rawA),
            ("sid-b", "ServiceB", rawB),
        });
        using var doc = JsonDocument.Parse(merged);
        var counts = doc.RootElement.GetProperty("perServerCounts");
        Assert.Equal(1, counts.GetProperty("ServiceA").GetInt32());
        Assert.Equal(2, counts.GetProperty("ServiceB").GetInt32());
    }

    [Fact]
    public void MergeSearchResultsRoundRobin_hint_연결()
    {
        var rawA = """{"results":[],"hint":"empty"}""";
        var rawB = """{"results":[{"fileId":"bbbbbbbb:g1"}]}""";
        var merged = LightHouseTools.MergeSearchResultsRoundRobin(new[]
        {
            ("sid-a", "ServiceA", rawA),
            ("sid-b", "ServiceB", rawB),
        });
        using var doc = JsonDocument.Parse(merged);
        var hint = doc.RootElement.GetProperty("hint").GetString();
        Assert.NotNull(hint);
        Assert.Contains("[ServiceA] empty", hint);
    }

    // ─── PrefixFileIdsInListResponse ──────────────────────────────────────────

    [Fact]
    public void PrefixFileIdsInListResponse_fileId_prefix_박제_및_serviceName_추가()
    {
        var raw = """[{"fileId":"f1","fileName":"a.md","pageCount":3}]""";
        var serviceId = "12345678-aaaa-bbbb-cccc-ddddeeeeffff";
        var transformed = LightHouseTools.PrefixFileIdsInListResponse(serviceId, "MyService", raw);
        using var doc = JsonDocument.Parse(transformed);
        var first = doc.RootElement[0];
        Assert.Equal("12345678:f1", first.GetProperty("fileId").GetString());
        Assert.Equal("MyService", first.GetProperty("serviceName").GetString());
        Assert.Equal("a.md", first.GetProperty("fileName").GetString());
        Assert.Equal(3, first.GetProperty("pageCount").GetInt32());
    }

    [Fact]
    public void PrefixFileIdsInListResponse_빈_입력_그대로_반환()
    {
        Assert.Equal("", LightHouseTools.PrefixFileIdsInListResponse("12345678-x", "n", ""));
    }

    [Fact]
    public void PrefixFileIdsInListResponse_non_array_root_그대로_반환()
    {
        // root 가 object 면 transform 없이 원본 그대로 반환 (현재 contract).
        var raw = """{"not":"array"}""";
        var transformed = LightHouseTools.PrefixFileIdsInListResponse("12345678-x", "n", raw);
        Assert.Equal(raw, transformed);
    }

    // ─── PrefixFileIdsInSearchResponse ────────────────────────────────────────

    [Fact]
    public void PrefixFileIdsInSearchResponse_fileId_prefix_serviceName_박제()
    {
        var raw = """{"results":[{"fileId":"f1","score":0.9,"excerpt":"snip"}],"perServerCounts":{"x":1}}""";
        var serviceId = "12345678-aaaa-bbbb-cccc-ddddeeeeffff";
        var transformed = LightHouseTools.PrefixFileIdsInSearchResponse(serviceId, "MyService", raw);
        using var doc = JsonDocument.Parse(transformed);
        var hit = doc.RootElement.GetProperty("results")[0];
        Assert.Equal("12345678:f1", hit.GetProperty("fileId").GetString());
        Assert.Equal("MyService", hit.GetProperty("serviceName").GetString());
        Assert.Equal("snip", hit.GetProperty("excerpt").GetString());
        // 다른 root property 보존.
        Assert.True(doc.RootElement.TryGetProperty("perServerCounts", out _));
    }

    /// <summary>**메타리뷰 M1 회귀 가드 (2026-05-27)** — 인자 순서 (serviceId, serviceName, raw) 일관.
    /// list / search 두 변환 모두 동일 signature 면 본 test 컴파일 통과 자체가 가드.</summary>
    [Fact]
    public void PrefixFileIds_두_변환_인자_순서_동일_signature()
    {
        // compile-time guard — 두 메서드 모두 (string, string, string) → string.
        Func<string, string, string, string> a = LightHouseTools.PrefixFileIdsInListResponse;
        Func<string, string, string, string> b = LightHouseTools.PrefixFileIdsInSearchResponse;
        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    // ─── FindClientByPrefix (LightHouseClientHolder 의존) ─────────────────────

    [SkippableFact]
    public void FindClientByPrefix_매칭_성공_시_tuple_반환()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only (DPAPI / LightHouseClient ctor).");

        var (cfg, aId, _) = MakeTwoActiveServices();
        LightHouseClientHolder.EnsureCreated(cfg);

        var prefix = LightHouseTools.MakePrefix(aId);
        var match = LightHouseTools.FindClientByPrefix(prefix);
        Assert.NotNull(match);
        Assert.Equal(aId, match!.Value.ServiceId);
        Assert.Equal("A", match.Value.DisplayName);
    }

    [SkippableFact]
    public void FindClientByPrefix_미매칭_시_null()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only (DPAPI / LightHouseClient ctor).");

        var (cfg, _, _) = MakeTwoActiveServices();
        LightHouseClientHolder.EnsureCreated(cfg);

        Assert.Null(LightHouseTools.FindClientByPrefix("ffffffff"));
        Assert.Null(LightHouseTools.FindClientByPrefix(""));
    }

    // ─── 공개 API early-return path (active service 0) ────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task LighthouseList_no_active_service_시_hint_응답()
    {
        var raw = await LightHouseTools.LighthouseList();
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(0, doc.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("activeServiceCount").GetInt32());
        Assert.Contains("no active LightHouse services", doc.RootElement.GetProperty("hint").GetString());
    }

    [Fact]
    public async System.Threading.Tasks.Task LighthouseSearch_empty_query_시_hint_응답()
    {
        var raw = await LightHouseTools.LighthouseSearch("   ");
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("query is empty", doc.RootElement.GetProperty("hint").GetString());
        Assert.Empty(doc.RootElement.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async System.Threading.Tasks.Task LighthouseOutline_invalid_fileId_시_error_응답()
    {
        var raw = await LightHouseTools.LighthouseOutline("nocolon");
        using var doc = JsonDocument.Parse(raw);
        Assert.Contains("invalid fileId", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async System.Threading.Tasks.Task LighthouseSummary_invalid_fileId_시_error_응답()
    {
        var raw = await LightHouseTools.LighthouseSummary("nocolon");
        using var doc = JsonDocument.Parse(raw);
        Assert.Contains("invalid fileId", doc.RootElement.GetProperty("error").GetString());
    }

    [SkippableFact]
    public async System.Threading.Tasks.Task LighthouseSummary_prefix_미매칭_시_error_응답()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only (DPAPI / LightHouseClient ctor).");

        // active service 박제 후 다른 prefix 로 호출.
        var (cfg, _, _) = MakeTwoActiveServices();
        LightHouseClientHolder.EnsureCreated(cfg);
        var raw = await LightHouseTools.LighthouseSummary("ffffffff:collection-guid:doc-1");
        using var doc = JsonDocument.Parse(raw);
        Assert.Contains("no active LightHouse service matches", doc.RootElement.GetProperty("error").GetString());
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static (LlmConfig cfg, string svcAId, string svcBId) MakeTwoActiveServices()
    {
        // LightHouseClientHolderTests 와 동일 pattern — 두 active service (A, B) + DPAPI PSK.
        var c = new LlmConfig();
        var a = new LightHouseServiceConfig
        {
            ServiceId = Guid.NewGuid().ToString(),
            DisplayName = "A",
            BaseUrl = "https://a.test.local:8443",
            Active = true,
        };
        var b = new LightHouseServiceConfig
        {
            ServiceId = Guid.NewGuid().ToString(),
            DisplayName = "B",
            BaseUrl = "https://b.test.local:8443",
            Active = true,
        };
        c.LightHouseServices.Add(a);
        c.LightHouseServices.Add(b);
        c.SetLightHousePsk(a.ServiceId, "psk-a");
        c.SetLightHousePsk(b.ServiceId, "psk-b");
        return (c, a.ServiceId, b.ServiceId);
    }
}
