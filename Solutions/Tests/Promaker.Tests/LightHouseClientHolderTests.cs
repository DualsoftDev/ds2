using System;
using System.Linq;
using System.Threading.Tasks;
using Promaker.Knowledge;
using Promaker.LlmAgent;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// Phase S5c → D-S7-3b — LightHouseClientHolder multi-instance lifecycle 시험
/// (done-lighthouse-kb-server.md §3.8 L2-2 + §3.16.7 D-S7-3b).
///
/// scope: holder 의 self-contained 로직만 — EnsureCreated / GetClient / Invalidate / RegisterSession /
/// UnregisterSession / LiveSessions / DisposeAllAsync. HTTP round-trip 은 LightHouseClientTests (mock).
/// server-side DELETE 는 service.Tests.
///
/// 본 suite 는 *static singleton-like* dictionary 를 다루므로 각 Fact 가 Invalidate 로 시작해 prior state 제거.
/// </summary>
public sealed class LightHouseClientHolderTests : IDisposable
{
    private const string TestBaseUrl = "https://service.test.local:8443";
    private const string TestPsk = "test-psk-abc";

    public LightHouseClientHolderTests() => LightHouseClientHolder.Invalidate();
    public void Dispose() => LightHouseClientHolder.Invalidate();

    private static LlmConfig MakeConfig(string? baseUrl = TestBaseUrl, string? psk = TestPsk)
    {
        // **D-S7-3a** — 새 multi-service path. EnsureActiveService 가 ServiceId 자동 발급 + Active=true.
        var c = new LlmConfig();
        if (!string.IsNullOrEmpty(baseUrl))
        {
            var svc = c.EnsureActiveService();
            svc.BaseUrl = baseUrl;
            if (!string.IsNullOrEmpty(psk)) c.SetLightHousePsk(psk);
        }
        return c;
    }

    private static (LlmConfig cfg, string svcAId, string svcBId) MakeTwoActiveServices()
    {
        // **D-S7-3b** — 두 active service entry (A + B) 박제. ServiceId 자동 발급. PSK 평문 → DPAPI 암호화.
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

    [Fact]
    public void EnsureCreated_returns_empty_when_service_unset()
    {
        var cfg = new LlmConfig();
        Assert.Empty(LightHouseClientHolder.EnsureCreated(cfg));
    }

    [Fact]
    public void EnsureCreated_returns_same_instance_on_repeat_call()
    {
        var cfg = MakeConfig();
        var c1 = LightHouseClientHolder.EnsureCreated(cfg);
        var c2 = LightHouseClientHolder.EnsureCreated(cfg);
        Assert.Single(c1);
        Assert.Single(c2);
        Assert.Same(c1[0], c2[0]);
    }

    [Fact]
    public void EnsureCreated_recreates_after_baseUrl_change()
    {
        var cfg = MakeConfig();
        var c1 = LightHouseClientHolder.EnsureCreated(cfg);

        cfg.LightHouseServices[0].BaseUrl = "https://other.local:9443";
        var c2 = LightHouseClientHolder.EnsureCreated(cfg);
        Assert.Single(c1);
        Assert.Single(c2);
        Assert.NotSame(c1[0], c2[0]);
    }

    [Fact]
    public void EnsureCreated_recreates_after_psk_change()
    {
        var cfg = MakeConfig();
        var c1 = LightHouseClientHolder.EnsureCreated(cfg);

        cfg.SetLightHousePsk("rotated-psk");
        var c2 = LightHouseClientHolder.EnsureCreated(cfg);
        Assert.Single(c1);
        Assert.Single(c2);
        Assert.NotSame(c1[0], c2[0]);
    }

    [Fact]
    public void Invalidate_clears_all_entries()
    {
        var cfg = MakeConfig();
        Assert.Single(LightHouseClientHolder.EnsureCreated(cfg));
        LightHouseClientHolder.Invalidate();
        // GetClient 가 null 반환 — entry 모두 제거됨.
        Assert.Null(LightHouseClientHolder.GetClient(cfg.LightHouseServices[0].ServiceId));
    }

    [Fact]
    public void Session_tracking_register_and_unregister_per_service()
    {
        // **D-S7-3b** — RegisterSession / UnregisterSession 이 (serviceId, token) signature.
        // LiveSessions 가 service 별 dict.
        LightHouseClientHolder.RegisterSession("svc-A", "tok-1");
        LightHouseClientHolder.RegisterSession("svc-A", "tok-2");
        LightHouseClientHolder.RegisterSession("svc-B", "tok-3");

        var live = LightHouseClientHolder.LiveSessions;
        Assert.True(live.ContainsKey("svc-A"));
        Assert.True(live.ContainsKey("svc-B"));
        Assert.Contains("tok-1", live["svc-A"]);
        Assert.Contains("tok-2", live["svc-A"]);
        Assert.Contains("tok-3", live["svc-B"]);

        LightHouseClientHolder.UnregisterSession("svc-A", "tok-1");
        var live2 = LightHouseClientHolder.LiveSessions;
        Assert.DoesNotContain("tok-1", live2["svc-A"]);
        Assert.Contains("tok-2", live2["svc-A"]);

        // empty / null serviceId 또는 token 은 silent ignore.
        LightHouseClientHolder.RegisterSession("", "tok-x");
        LightHouseClientHolder.RegisterSession("svc-A", "");
        LightHouseClientHolder.RegisterSession(null!, "tok-y");
        var live3 = LightHouseClientHolder.LiveSessions;
        Assert.False(live3.ContainsKey(""));
        Assert.DoesNotContain("tok-x", live3.Values.SelectMany(v => v));
    }

    [Fact]
    public async Task DisposeAllAsync_clears_sessions_even_when_no_client()
    {
        LightHouseClientHolder.RegisterSession("svc-orphan", "tok-1");
        // entry 미생성 상태에서도 호출 안전 (best-effort) — LiveSessions 만 박제.
        await LightHouseClientHolder.DisposeAllAsync();
        Assert.Empty(LightHouseClientHolder.LiveSessions);
    }

    [Fact]
    public void EnsureCreated_skips_service_when_psk_missing()
    {
        // BaseUrl 만 있고 PSK 미설정. D-S7-3b — entry 가 skip 되어 결과 empty.
        var cfg = new LlmConfig();
        var svc = cfg.EnsureActiveService();
        svc.BaseUrl = TestBaseUrl;
        svc.ApiKeyEncrypted = "";
        Assert.Empty(LightHouseClientHolder.EnsureCreated(cfg));
    }

    [Fact]
    public void EnsureCreated_skips_service_when_baseUrl_invalid_https()
    {
        // plain HTTP — LightHouseClient ctor 가 ArgumentException → entry 가 skip.
        var cfg = new LlmConfig();
        var svc = cfg.EnsureActiveService();
        svc.BaseUrl = "http://insecure.local:8080";
        cfg.SetLightHousePsk(TestPsk);
        Assert.Empty(LightHouseClientHolder.EnsureCreated(cfg));
    }

    // ─── D-S7-3b (s6-r30) multi-instance ─────────────────────────────────────

    [Fact]
    public void EnsureCreated_creates_one_client_per_active_service()
    {
        // **D-S7-3b** — N 개 active service → N 개 client 생성. 각 client 가 별 instance (per-ServiceId).
        if (!OperatingSystem.IsWindows()) return;

        var (cfg, aId, bId) = MakeTwoActiveServices();
        var clients = LightHouseClientHolder.EnsureCreated(cfg);
        Assert.Equal(2, clients.Count);
        Assert.NotSame(clients[0], clients[1]);

        // GetClient(serviceId) 가 동일 instance 반환.
        var clientA = LightHouseClientHolder.GetClient(aId);
        var clientB = LightHouseClientHolder.GetClient(bId);
        Assert.NotNull(clientA);
        Assert.NotNull(clientB);
        Assert.Contains(clientA!, clients);
        Assert.Contains(clientB!, clients);
        Assert.NotSame(clientA, clientB);
    }

    [Fact]
    public void EnsureCreated_only_recreates_changed_entries()
    {
        // **D-S7-3b** — service A 만 BaseUrl 변경 시 A entry 만 dispose/recreate, B entry 는 그대로 재사용.
        if (!OperatingSystem.IsWindows()) return;

        var (cfg, aId, bId) = MakeTwoActiveServices();
        var clients1 = LightHouseClientHolder.EnsureCreated(cfg);
        var aBefore = LightHouseClientHolder.GetClient(aId);
        var bBefore = LightHouseClientHolder.GetClient(bId);

        // A 만 BaseUrl 변경.
        cfg.LightHouseServices.First(s => s.ServiceId == aId).BaseUrl = "https://a-new.test.local:8443";
        var clients2 = LightHouseClientHolder.EnsureCreated(cfg);

        var aAfter = LightHouseClientHolder.GetClient(aId);
        var bAfter = LightHouseClientHolder.GetClient(bId);

        Assert.NotSame(aBefore, aAfter);   // A 재생성
        Assert.Same(bBefore, bAfter);      // B 그대로
        Assert.Equal(2, clients2.Count);
    }

    [Fact]
    public void EnsureCreated_removes_entry_when_service_deactivated()
    {
        // **D-S7-3b** — config 에서 service B 가 비활성화되면 holder entry 도 제거.
        if (!OperatingSystem.IsWindows()) return;

        var (cfg, aId, bId) = MakeTwoActiveServices();
        LightHouseClientHolder.EnsureCreated(cfg);
        Assert.NotNull(LightHouseClientHolder.GetClient(bId));

        cfg.LightHouseServices.First(s => s.ServiceId == bId).Active = false;
        var clients = LightHouseClientHolder.EnsureCreated(cfg);

        Assert.Single(clients);
        Assert.NotNull(LightHouseClientHolder.GetClient(aId));
        Assert.Null(LightHouseClientHolder.GetClient(bId));
    }

    [Fact]
    public void GetClient_returns_null_for_unknown_serviceId()
    {
        var cfg = MakeConfig();
        LightHouseClientHolder.EnsureCreated(cfg);
        Assert.Null(LightHouseClientHolder.GetClient("nonexistent-id"));
        Assert.Null(LightHouseClientHolder.GetClient(""));
    }
}
