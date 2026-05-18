using System;
using System.IO;
using Promaker.LlmAgent;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// Phase 2 후속 — LlmConfig (consent + provider 통합) 회귀 테스트.
/// 핵심: corrupt JSON → .bak 백업 + default 반환, DPAPI key 라운드트립, atomic write.
/// </summary>
public sealed class LlmConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Promaker.Tests",
        nameof(LlmConfigTests),
        Guid.NewGuid().ToString("N"));

    public LlmConfigTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ─── Corrupt JSON → fallback ─────────────────────────────────────────────

    [Fact]
    public void LoadFrom_returns_default_when_file_missing()
    {
        var path = Path.Combine(_root, "missing.json");
        var cfg = LlmConfig.LoadFrom(path);

        Assert.NotNull(cfg);
        Assert.False(cfg.DataEgressConsent);
        Assert.Equal("Claude", cfg.DefaultProvider);
    }

    [Fact]
    public void LoadFrom_corrupt_JSON_creates_bak_and_returns_default()
    {
        var path = Path.Combine(_root, "corrupt.json");
        File.WriteAllText(path, "{ this is not valid json ##");

        var cfg = LlmConfig.LoadFrom(path);

        // default 반환
        Assert.False(cfg.DataEgressConsent);
        Assert.Equal("Claude", cfg.DefaultProvider);

        // .bak 으로 corrupt 파일 보존
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void LoadFrom_valid_JSON_round_trips_all_fields()
    {
        var path = Path.Combine(_root, "valid.json");
        const string json = """
            {
              "dataEgressConsent": true,
              "consentTimestampUtc": "2026-05-07T12:00:00Z",
              "defaultProvider": "Codex",
              "encryptedKeys": { "anthropic": "BASE64HERE" },
              "anthropicModel": "claude-opus-4-7",
              "openAiModel": "gpt-5",
              "ollamaModel": "qwen2.5",
              "ollamaBaseUrl": "http://192.168.1.50:11434"
            }
            """;
        File.WriteAllText(path, json);

        var cfg = LlmConfig.LoadFrom(path);

        Assert.True(cfg.DataEgressConsent);
        Assert.Equal("2026-05-07T12:00:00Z", cfg.ConsentTimestampUtc);
        Assert.Equal("Codex", cfg.DefaultProvider);
        Assert.Equal("BASE64HERE", cfg.EncryptedKeys["anthropic"]);
        Assert.Equal("claude-opus-4-7", cfg.AnthropicModel);
        Assert.Equal("gpt-5", cfg.OpenAiModel);
        Assert.Equal("qwen2.5", cfg.OllamaModel);
        Assert.Equal("http://192.168.1.50:11434", cfg.OllamaBaseUrl);
    }

    [Fact]
    public void LoadFrom_unknown_extra_fields_are_ignored()
    {
        // Deserialize 에 PropertyNameCaseInsensitive=true 만 적용 — 알 수 없는 field 는 silently skip
        var path = Path.Combine(_root, "extra.json");
        File.WriteAllText(path, """{"dataEgressConsent":true,"unknownFutureField":42}""");

        var cfg = LlmConfig.LoadFrom(path);
        Assert.True(cfg.DataEgressConsent);
    }

    // ─── DPAPI key set/get round-trip ────────────────────────────────────────

    [Fact]
    public void SetApiKey_then_GetApiKey_returns_same_plaintext()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI Windows 전용

        var cfg = new LlmConfig();
        const string plain = "sk-ant-api03-fake-test-key-1234567890";

        cfg.SetApiKey("anthropic", plain);
        var got = cfg.GetApiKey("anthropic");

        Assert.Equal(plain, got);
        Assert.True(cfg.HasApiKey("anthropic"));

        // EncryptedKeys 안에는 base64 + 암호화 형태 — 평문 일치 X
        Assert.Single(cfg.EncryptedKeys);
        Assert.NotEqual(plain, cfg.EncryptedKeys["anthropic"]);
    }

    [Fact]
    public void SetApiKey_with_null_or_empty_removes_key()
    {
        if (!OperatingSystem.IsWindows()) return;

        var cfg = new LlmConfig();
        cfg.SetApiKey("openai", "sk-temp");
        Assert.True(cfg.HasApiKey("openai"));

        cfg.SetApiKey("openai", null);
        Assert.False(cfg.HasApiKey("openai"));
        Assert.False(cfg.EncryptedKeys.ContainsKey("openai"));
    }

    [Fact]
    public void GetApiKey_for_missing_provider_returns_null()
    {
        var cfg = new LlmConfig();
        Assert.Null(cfg.GetApiKey("nonexistent"));
        Assert.False(cfg.HasApiKey("nonexistent"));
    }

    // ─── Phase S5a — KbCollections + LightHouseService ───────────────────────

    [Fact]
    public void KbCollections_round_trips_through_save_load()
    {
        var path = Path.Combine(_root, "kb-roundtrip.json");
        const string json = """
            {
              "dataEgressConsent": true,
              "kbCollections": [
                {"collectionId":"550e8400-e29b-41d4-a716-446655440000","displayName":"라인A","active":true},
                {"collectionId":"77777777-e29b-41d4-a716-446655440000","displayName":"라인B","active":false}
              ]
            }
            """;
        File.WriteAllText(path, json);

        var cfg = LlmConfig.LoadFrom(path);
        Assert.Equal(2, cfg.KbCollections.Count);
        Assert.Equal("550e8400-e29b-41d4-a716-446655440000", cfg.KbCollections[0].CollectionId);
        Assert.Equal("라인A", cfg.KbCollections[0].DisplayName);
        Assert.True(cfg.KbCollections[0].Active);
        Assert.False(cfg.KbCollections[1].Active);
    }

    [Fact]
    public void LightHouseService_round_trips_through_save_load()
    {
        var path = Path.Combine(_root, "lhs-roundtrip.json");
        const string json = """
            {
              "lightHouseService": {
                "baseUrl": "https://service.test.local:8443",
                "apiKeyEncrypted": "ZmFrZS1lbmNyeXB0ZWQtYmFzZTY0"
              }
            }
            """;
        File.WriteAllText(path, json);

        var cfg = LlmConfig.LoadFrom(path);
        Assert.NotNull(cfg.LightHouseService);
        Assert.Equal("https://service.test.local:8443", cfg.LightHouseService!.BaseUrl);
        Assert.Equal("ZmFrZS1lbmNyeXB0ZWQtYmFzZTY0", cfg.LightHouseService.ApiKeyEncrypted);
    }

    [Fact]
    public void Default_LlmConfig_has_empty_KbCollections_and_null_LightHouseService()
    {
        var cfg = new LlmConfig();
        Assert.NotNull(cfg.KbCollections);
        Assert.Empty(cfg.KbCollections);
        Assert.Null(cfg.LightHouseService);
        Assert.False(cfg.HasLightHousePsk());
    }

    [Fact]
    public void SetLightHousePsk_then_GetLightHousePsk_returns_same_plaintext()
    {
        if (!OperatingSystem.IsWindows()) return;

        var cfg = new LlmConfig();
        const string plain = "lighthouse-psk-test-1234567890";

        cfg.SetLightHousePsk(plain);
        Assert.True(cfg.HasLightHousePsk());
        Assert.Equal(plain, cfg.GetLightHousePsk());
        Assert.NotNull(cfg.LightHouseService);
        Assert.NotEqual(plain, cfg.LightHouseService!.ApiKeyEncrypted);
    }

    [Fact]
    public void SetLightHousePsk_with_null_clears_encrypted()
    {
        if (!OperatingSystem.IsWindows()) return;

        var cfg = new LlmConfig();
        cfg.SetLightHousePsk("temp");
        Assert.True(cfg.HasLightHousePsk());

        cfg.SetLightHousePsk(null);
        Assert.False(cfg.HasLightHousePsk());
        Assert.Equal("", cfg.LightHouseService!.ApiKeyEncrypted);
    }

    [Fact]
    public void LightHousePsk_uses_distinct_entropy_from_LlmApi_keys()
    {
        // 다른 entropy 사용을 검증 — LlmApi key 로 LightHouse PSK 를 복호화 시도하면 실패.
        // 동일 평문을 두 entropy 로 암호화한 byte 가 다른지만 검증 (DPAPI 결정성 보장은 아니지만 entropy 차이는 확실).
        if (!OperatingSystem.IsWindows()) return;

        var cfg1 = new LlmConfig();
        cfg1.SetApiKey("anthropic", "same-plain");
        cfg1.SetLightHousePsk("same-plain");

        // 두 base64 가 동일하면 entropy 통합된 것 — 의도와 다름
        Assert.NotEqual(cfg1.EncryptedKeys["anthropic"], cfg1.LightHouseService!.ApiKeyEncrypted);
    }

    // ─── Phase 2 task D-iii / E-i (s6-r20) — VLM provider/model + VisionCostGate ─────────────

    [Fact]
    public void Vlm_defaults_anthropic_sonnet_4_6()
    {
        var cfg = new LlmConfig();
        Assert.Equal("anthropic", cfg.VlmProvider);
        Assert.Equal("claude-sonnet-4-6", cfg.VlmModel);
        Assert.NotNull(cfg.VisionCostGate);
        Assert.Equal(10_000, cfg.VisionCostGate.DailyTokenCap);
        Assert.Equal(0, cfg.VisionCostGate.TokensUsedToday);
    }

    [Fact]
    public void Vlm_fields_round_trip_through_save_load()
    {
        var path = Path.Combine(_root, "vlm-roundtrip.json");
        const string json = """
            {
              "vlmProvider": "none",
              "vlmModel": "claude-opus-4-7",
              "visionCostGate": {
                "dailyTokenCap": 50000,
                "lastResetUtc": "2026-05-18",
                "tokensUsedToday": 1234
              }
            }
            """;
        File.WriteAllText(path, json);

        var cfg = LlmConfig.LoadFrom(path);
        Assert.Equal("none", cfg.VlmProvider);
        Assert.Equal("claude-opus-4-7", cfg.VlmModel);
        Assert.Equal(50000, cfg.VisionCostGate.DailyTokenCap);
        Assert.Equal("2026-05-18", cfg.VisionCostGate.LastResetUtc);
        Assert.Equal(1234, cfg.VisionCostGate.TokensUsedToday);
    }

    [Fact]
    public void IsVlmEnabled_requires_provider_and_apikey()
    {
        if (!OperatingSystem.IsWindows()) return;

        var cfg = new LlmConfig();
        // no apikey → false
        Assert.False(cfg.IsVlmEnabled());

        cfg.SetApiKey("anthropic", "sk-vlm-fake");
        Assert.True(cfg.IsVlmEnabled());

        cfg.VlmProvider = "none";
        Assert.False(cfg.IsVlmEnabled());
    }

    [Fact]
    public void GetVlmApiKey_env_var_takes_precedence()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string envVar = "LIGHTHOUSE_VLM_API_KEY";
        var prior = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "env-override-key");
            var cfg = new LlmConfig();
            cfg.SetApiKey("anthropic", "config-key");
            Assert.Equal("env-override-key", cfg.GetVlmApiKey());

            // env clear → config 값 fallback
            Environment.SetEnvironmentVariable(envVar, null);
            Assert.Equal("config-key", cfg.GetVlmApiKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, prior);
        }
    }

    [Fact]
    public void VisionCostGate_CanAfford_respects_daily_cap()
    {
        var gate = new VisionCostGate { DailyTokenCap = 1000 };
        Assert.True(gate.CanAfford(500));
        gate.Consume(500);
        Assert.True(gate.CanAfford(500));   // 정확히 한도까지
        gate.Consume(500);
        Assert.False(gate.CanAfford(1));    // 초과
    }

    [Fact]
    public void VisionCostGate_Status_thresholds()
    {
        var gate = new VisionCostGate { DailyTokenCap = 1000 };
        Assert.Equal(VisionCostGateStatus.Normal, gate.Status);
        gate.Consume(799);
        Assert.Equal(VisionCostGateStatus.Normal, gate.Status);
        gate.Consume(1);    // 800 / 1000 = 80%
        Assert.Equal(VisionCostGateStatus.SoftWarning, gate.Status);
        gate.Consume(199);
        Assert.Equal(VisionCostGateStatus.SoftWarning, gate.Status);
        gate.Consume(1);    // 1000 도달
        Assert.Equal(VisionCostGateStatus.HardCap, gate.Status);
    }

    [Fact]
    public void VisionCostGate_RolloverIfNeeded_resets_on_new_day()
    {
        var gate = new VisionCostGate
        {
            DailyTokenCap = 1000,
            TokensUsedToday = 950,
            LastResetUtc = "2020-01-01",   // 과거 날짜
        };
        gate.RolloverIfNeeded();
        Assert.Equal(0, gate.TokensUsedToday);
        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), gate.LastResetUtc);
    }

    [Fact]
    public void VisionCostGate_EstimateTokens_uses_average_300_per_image()
    {
        Assert.Equal(0, VisionCostGate.EstimateTokens(0));
        Assert.Equal(300, VisionCostGate.EstimateTokens(1));
        Assert.Equal(3000, VisionCostGate.EstimateTokens(10));
    }
}
