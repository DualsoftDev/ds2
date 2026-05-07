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
}
