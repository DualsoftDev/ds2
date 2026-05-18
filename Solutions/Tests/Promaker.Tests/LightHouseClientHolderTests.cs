using System;
using System.Linq;
using System.Threading.Tasks;
using Promaker.Knowledge;
using Promaker.LlmAgent;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// Phase S5c — LightHouseClientHolder singleton + session tracking 의 lifecycle 시험
/// (todo-lighthouse-kb-server.md §3.8 L2-2 / s5b 잔여 우려 1 통일 결정).
///
/// scope: holder 의 self-contained 로직만 — EnsureCreated/Invalidate/RegisterSession/UnregisterSession/LiveSessions.
/// 실제 HTTP round-trip 은 LightHouseClientTests (mock). server-side DELETE 동작은 service.Tests.
///
/// 본 suite 는 *static singleton* 을 다루므로 각 Fact 가 Invalidate 로 시작해 prior state 제거 (격리).
/// </summary>
public sealed class LightHouseClientHolderTests : IDisposable
{
    private const string TestBaseUrl = "https://service.test.local:8443";
    private const string TestPsk = "test-psk-abc";

    public LightHouseClientHolderTests() => LightHouseClientHolder.Invalidate();
    public void Dispose() => LightHouseClientHolder.Invalidate();

    private static LlmConfig MakeConfig(string? baseUrl = TestBaseUrl, string? psk = TestPsk)
    {
        var c = new LlmConfig();
        if (!string.IsNullOrEmpty(baseUrl))
        {
            c.LightHouseService = new LightHouseServiceConfig { BaseUrl = baseUrl };
            if (!string.IsNullOrEmpty(psk)) c.SetLightHousePsk(psk);
        }
        return c;
    }

    [Fact]
    public void EnsureCreated_returns_null_when_service_unset()
    {
        var cfg = new LlmConfig();
        Assert.Null(LightHouseClientHolder.EnsureCreated(cfg));
        Assert.Null(LightHouseClientHolder.Current);
    }

    [Fact]
    public void EnsureCreated_returns_same_instance_on_repeat_call()
    {
        var cfg = MakeConfig();
        var c1 = LightHouseClientHolder.EnsureCreated(cfg);
        var c2 = LightHouseClientHolder.EnsureCreated(cfg);
        Assert.NotNull(c1);
        Assert.Same(c1, c2);
    }

    [Fact]
    public void EnsureCreated_recreates_after_baseUrl_change()
    {
        var cfg = MakeConfig();
        var c1 = LightHouseClientHolder.EnsureCreated(cfg);

        cfg.LightHouseService!.BaseUrl = "https://other.local:9443";
        var c2 = LightHouseClientHolder.EnsureCreated(cfg);
        Assert.NotNull(c1);
        Assert.NotNull(c2);
        Assert.NotSame(c1, c2);
    }

    [Fact]
    public void EnsureCreated_recreates_after_psk_change()
    {
        var cfg = MakeConfig();
        var c1 = LightHouseClientHolder.EnsureCreated(cfg);

        cfg.SetLightHousePsk("rotated-psk");
        var c2 = LightHouseClientHolder.EnsureCreated(cfg);
        Assert.NotNull(c1);
        Assert.NotNull(c2);
        Assert.NotSame(c1, c2);
    }

    [Fact]
    public void Invalidate_clears_current()
    {
        var cfg = MakeConfig();
        Assert.NotNull(LightHouseClientHolder.EnsureCreated(cfg));
        LightHouseClientHolder.Invalidate();
        Assert.Null(LightHouseClientHolder.Current);
    }

    [Fact]
    public void Session_tracking_register_and_unregister()
    {
        LightHouseClientHolder.RegisterSession("tok-1");
        LightHouseClientHolder.RegisterSession("tok-2");
        Assert.Contains("tok-1", LightHouseClientHolder.LiveSessions);
        Assert.Contains("tok-2", LightHouseClientHolder.LiveSessions);

        LightHouseClientHolder.UnregisterSession("tok-1");
        Assert.DoesNotContain("tok-1", LightHouseClientHolder.LiveSessions);
        Assert.Contains("tok-2", LightHouseClientHolder.LiveSessions);

        // empty / null 토큰은 silent ignore
        LightHouseClientHolder.RegisterSession("");
        LightHouseClientHolder.RegisterSession(null!);
        Assert.Single(LightHouseClientHolder.LiveSessions);
    }

    [Fact]
    public async Task DisposeAllAsync_clears_sessions_even_when_no_client()
    {
        LightHouseClientHolder.RegisterSession("orphan-1");
        // client 미생성 상태에서도 호출 안전 (best-effort)
        await LightHouseClientHolder.DisposeAllAsync();
        Assert.Empty(LightHouseClientHolder.LiveSessions);
        Assert.Null(LightHouseClientHolder.Current);
    }

    [Fact]
    public void EnsureCreated_returns_null_when_psk_missing()
    {
        // BaseUrl 만 있고 PSK 미설정
        var cfg = new LlmConfig
        {
            LightHouseService = new LightHouseServiceConfig { BaseUrl = TestBaseUrl, ApiKeyEncrypted = "" },
        };
        Assert.Null(LightHouseClientHolder.EnsureCreated(cfg));
    }

    [Fact]
    public void EnsureCreated_returns_null_when_baseUrl_invalid_https_check()
    {
        // plain HTTP — LightHouseClient ctor 가 ArgumentException → holder 가 catch 후 null.
        var cfg = new LlmConfig
        {
            LightHouseService = new LightHouseServiceConfig { BaseUrl = "http://insecure.local:8080" },
        };
        cfg.SetLightHousePsk(TestPsk);
        Assert.Null(LightHouseClientHolder.EnsureCreated(cfg));
    }
}
