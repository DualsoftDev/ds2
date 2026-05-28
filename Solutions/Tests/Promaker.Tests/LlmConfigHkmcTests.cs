using System.Text.Json;
using Promaker.LlmAgent;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// Phase 5 (HKMC H-Chat) — <see cref="HkmcHChatConfig"/> JSON round-trip + default 정합.
/// process-static <c>HkmcFeature.IsEnabled</c> 의 한계로 AvailableProviders cardinality 는
/// 본 fixture 가 검증하지 않음 (Phase 6 매뉴얼 검증으로 박제).
/// </summary>
public sealed class LlmConfigHkmcTests
{
    [Fact]
    public void LlmConfig_HkmcHChat_RoundTrip()
    {
        var original = new LlmConfig
        {
            HkmcHChat = new HkmcHChatConfig
            {
                BaseUrl = "https://test/api",
                ClaudeModel = "test-model-c",
                OpenAiModel = "test-model-o",
            },
        };

        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<LlmConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.HkmcHChat);
        Assert.Equal("https://test/api", loaded.HkmcHChat!.BaseUrl);
        Assert.Equal("test-model-c", loaded.HkmcHChat.ClaudeModel);
        Assert.Equal("test-model-o", loaded.HkmcHChat.OpenAiModel);
    }

    [Fact]
    public void LlmConfig_HkmcHChat_Null_OmittedFromJson()
    {
        // 결정 #10 / 결정 #5 박제 — JsonIgnore(WhenWritingNull) 로 ENABLE_HKMC off 사용자의 disk JSON 에
        // hkmcHChat 키가 흔적도 남지 않음.
        var cfg = new LlmConfig { HkmcHChat = null };

        var json = JsonSerializer.Serialize(cfg);

        Assert.DoesNotContain("hkmcHChat", json);
    }

    [Fact]
    public void HkmcHChatConfig_Defaults()
    {
        // 결정 #3 정정 (ComboBox + 편집창 도입) — default 가 빈 문자열에서 H-Chat docs 의 latest stable 명시로 변경.
        // Settings panel 의 ▾ dropdown 첫 항목과 SSOT 정합.
        var cfg = new HkmcHChatConfig();

        Assert.Equal("***REDACTED-INTERNAL-URL***", cfg.BaseUrl);
        Assert.Equal("claude-sonnet-4-6", cfg.ClaudeModel);
        Assert.Equal("gpt-5.4", cfg.OpenAiModel);
    }
}
