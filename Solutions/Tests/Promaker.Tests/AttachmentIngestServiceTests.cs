using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ds2.LightHouse;
using Microsoft.FSharp.Core;
using Promaker.Knowledge;
using Promaker.LlmAgent;
using Promaker.LlmAgent.Api;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// Phase 2 task E (s6-r23 묶음 4) — <see cref="AttachmentIngestService.BuildCaptionGenStatic"/> 의 cost gate /
/// VLM 분기 회귀 차단. (s6-r20 backlog "cost gate hard cap captionGen mock SkippedCaption Fact" 해소.)
///
/// scope: 단일 captionGen closure 호출의 결과 + cost gate 누적 부수 효과 검증.
/// HttpClient = 임의 mock handler 주입. CaptionGenerator.callAnthropic 의 wire 까지 모킹.
/// </summary>
public sealed class AttachmentIngestServiceTests
{
    /// <summary>응답 미리 set 후 호출 횟수 기록하는 mock handler. CaptionGenerator.callAnthropic 가
    /// `HttpClient.Send` (sync) 호출하므로 sync override 의무.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public int CallCount;
        public Func<HttpRequestMessage, HttpResponseMessage> Responder = _ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"content":[{"type":"text","text":"mock caption"}]}""",
                    Encoding.UTF8, "application/json"),
            };

        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Responder(request);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Responder(request));
        }
    }

    private static LlmConfig MakeVlmEnabledConfig(
        string apiKey = "mock-api-key", int dailyCap = 1000, int tokensUsedToday = 0)
    {
        var cfg = new LlmConfig
        {
            VlmProvider = "anthropic",
            VlmModel = "claude-sonnet-4-6",
        };
        // VLM apiKey = anthropic provider 의 EncryptedKeys (DPAPI per-user) 재활용 (D-2-5 정합).
        cfg.SetApiKey(ApiProviderFactory.AnthropicKey, apiKey);
        cfg.VisionCostGate.DailyTokenCap = dailyCap;
        cfg.VisionCostGate.TokensUsedToday = tokensUsedToday;
        cfg.VisionCostGate.LastResetUtc = DateTime.UtcNow.ToString("yyyy-MM-dd");
        return cfg;
    }

    private static CaptionResult Invoke(
        FSharpFunc<byte[], FSharpFunc<ImageFormat, CaptionResult>> captionGen,
        byte[] bytes, ImageFormat fmt)
        => captionGen.Invoke(bytes).Invoke(fmt);

    private static byte[] SampleBytes => new byte[]
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
    };


    [Fact]
    public void BuildCaptionGen_VLM_disabled_returns_noop_closure()
    {
        var cfg = new LlmConfig { VlmProvider = "none" };
        var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var gen = AttachmentIngestService.BuildCaptionGenStatic(cfg, http, CancellationToken.None);

        // CaptionGenerator.noop = SkippedCaption("noop"). HTTP 호출 0회.
        var result = Invoke(gen, SampleBytes, ImageFormat.Png);
        Assert.IsType<CaptionResult.SkippedCaption>(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void BuildCaptionGen_VLM_enabled_but_no_api_key_returns_noop()
    {
        var cfg = MakeVlmEnabledConfig(apiKey: "");
        var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var gen = AttachmentIngestService.BuildCaptionGenStatic(cfg, http, CancellationToken.None);

        var result = Invoke(gen, SampleBytes, ImageFormat.Png);
        Assert.IsType<CaptionResult.SkippedCaption>(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void BuildCaptionGen_cost_gate_hard_cap_returns_SkippedCaption_no_http()
    {
        // dailyCap=300, tokensUsedToday=300 → CanAfford(300) = false (HardCap).
        var cfg = MakeVlmEnabledConfig(dailyCap: 300, tokensUsedToday: 300);
        var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var gen = AttachmentIngestService.BuildCaptionGenStatic(cfg, http, CancellationToken.None);

        var result = Invoke(gen, SampleBytes, ImageFormat.Png);
        var skipped = Assert.IsType<CaptionResult.SkippedCaption>(result);
        Assert.Contains("cost gate hard cap", skipped.reason);
        // HardCap 분기 — http stub 미호출 (cost gate 가 callAnthropic 진입 차단).
        Assert.Equal(0, handler.CallCount);
        // Consume 미호출 — TokensUsedToday 그대로.
        Assert.Equal(300, cfg.VisionCostGate.TokensUsedToday);
    }

    [Fact]
    public void BuildCaptionGen_normal_path_Captioned_then_Consume()
    {
        var cfg = MakeVlmEnabledConfig(dailyCap: 100000, tokensUsedToday: 0);
        var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var gen = AttachmentIngestService.BuildCaptionGenStatic(cfg, http, CancellationToken.None);

        var result = Invoke(gen, SampleBytes, ImageFormat.Png);
        // 진단 messaging — Captioned 가 아니면 어떤 분기인지 표시 (FailedCaption.reason 노출).
        if (result is CaptionResult.FailedCaption fc) Assert.Fail($"Expected Captioned but got FailedCaption: {fc.reason}");
        if (result is CaptionResult.SkippedCaption sc) Assert.Fail($"Expected Captioned but got SkippedCaption: {sc.reason}");
        var captioned = Assert.IsType<CaptionResult.Captioned>(result);
        Assert.Equal("mock caption", captioned.text);
        Assert.Equal("claude-sonnet-4-6", captioned.model);
        Assert.Equal(1, handler.CallCount);
        // Captioned → Consume(perImageTokens). AverageTokensPerImage 만큼 누적.
        Assert.Equal(VisionCostGate.AverageTokensPerImage, cfg.VisionCostGate.TokensUsedToday);
    }

    [Fact]
    public void BuildCaptionGen_http_failure_FailedCaption_no_Consume()
    {
        var cfg = MakeVlmEnabledConfig(dailyCap: 100000, tokensUsedToday: 0);
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"invalid api key"}""",
                    Encoding.UTF8, "application/json"),
            },
        };
        using var http = new HttpClient(handler);
        var gen = AttachmentIngestService.BuildCaptionGenStatic(cfg, http, CancellationToken.None);

        var result = Invoke(gen, SampleBytes, ImageFormat.Png);
        Assert.IsType<CaptionResult.FailedCaption>(result);
        Assert.Equal(1, handler.CallCount);
        // FailedCaption → Consume 미호출 (정책 SSOT s6-r20 M2 정합).
        Assert.Equal(0, cfg.VisionCostGate.TokensUsedToday);
    }
}
