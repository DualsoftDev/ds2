using System;
using System.ClientModel;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Ds2.LlmAgent;
using log4net;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OllamaSharp;
using OpenAI;

namespace Promaker.LlmAgent.Api;

/// <summary>
/// API 기반 ApiChatProvider 팩토리.
///
/// 각 provider 의 SDK → IChatClient 어댑터 변환 + UseFunctionInvocation() 미들웨어 + McpClient 결선.
/// LlmChatViewModel.CreateXxxApiProvider 가 본 팩토리만 호출 → SDK strong-typed 의존을 한 곳에 집중.
/// </summary>
public static class ApiProviderFactory
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ApiProviderFactory));

    private const string McpNonceHeader = "X-Promaker-Nonce";

    /// <summary>Ollama vision 모델용 단일 이미지 cap. base64 inline — OpenAI 한도와 동일하게 보수.</summary>
    private const long OllamaVisionImageCap = 20L * 1024L * 1024L;

    /// <summary>LlmApiConfig 의 EncryptedKeys dict 키 — Anthropic API key 슬롯.</summary>
    public const string AnthropicKey = "anthropic";
    /// <summary>LlmApiConfig 의 EncryptedKeys dict 키 — OpenAI API key 슬롯.</summary>
    public const string OpenAiKey = "openai";
    /// <summary>F-1 spike — Groq API key 슬롯 (env-var fallback only, DPAPI 미사용). F-4 cleanup 시 정식 schema 로 대체.</summary>
    public const string GroqKey = "groq";

    /// <summary>F-1 spike — Groq OpenAI 호환 endpoint. F-4 cleanup 시 ProviderCapabilities.DefaultEndpoint 로 흡수.</summary>
    private static readonly Uri GroqEndpoint = new("https://api.groq.com/openai/v1");

    public static Task<ApiChatProvider> CreateAnthropicAsync(
        string apiKey, string model, string systemPrompt, string mcpServerUrl, string mcpNonce,
        CancellationToken cancellationToken = default)
    {
        var raw = new AnthropicClient { ApiKey = apiKey }.AsIChatClient(model);
        // Anthropic API 공식 한도 — 이미지 base64 inline 5MB / PDF document 32MB. SSOT = CapabilityPresets.AnthropicWire.
        return CreateInternalAsync(
            raw, "Anthropic", model, systemPrompt, mcpServerUrl, mcpNonce,
            validate: () => !string.IsNullOrEmpty(apiKey),
            capabilities: CapabilityPresets.AnthropicWire,
            cancellationToken: cancellationToken);
    }

    public static Task<ApiChatProvider> CreateOpenAiAsync(
        string apiKey, string model, string systemPrompt, string mcpServerUrl, string mcpNonce,
        CancellationToken cancellationToken = default)
    {
        var raw = new OpenAIClient(apiKey).GetChatClient(model).AsIChatClient();
        // OpenAI gpt-4o vision — SSOT = CapabilityPresets.OpenAiApiWire (V-1 PDF 미해결, S-4 spike 결과 의존).
        return CreateInternalAsync(
            raw, "OpenAI", model, systemPrompt, mcpServerUrl, mcpNonce,
            validate: () => !string.IsNullOrEmpty(apiKey),
            capabilities: CapabilityPresets.OpenAiApiWire,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// F-1 spike — Groq OpenAI 호환 endpoint. OpenAI SDK 의 <c>OpenAIClientOptions.Endpoint</c> override
    /// 로 baseUrl 만 갈아끼움. tool calling / streaming / 5종 LlmEvent 매핑은 OpenAI 어댑터 그대로 통과.
    /// 429/Retry-After backoff 위치는 spike 검증 항목 (provider layer / ApiChatProvider / Microsoft.Extensions.AI 미들웨어 4 후보).
    /// F-4 cleanup 시 본 메소드는 ProviderCapabilities + Endpoint 파라미터화된 일반 메소드로 흡수.
    /// </summary>
    public static Task<ApiChatProvider> CreateGroqAsync(
        string apiKey, string model, string systemPrompt, string mcpServerUrl, string mcpNonce,
        CancellationToken cancellationToken = default)
    {
        // ApiKeyCredential ctor 는 empty string 에 throw — 빈 키일 때도 ConfigureProviderAsync 가
        // EnsureCli().IsValid=false 로 graceful 차단되도록 placeholder 로 대체. validate 가 실 빈 키 검증.
        var keyForCtor = string.IsNullOrEmpty(apiKey) ? "missing" : apiKey;
        var options = new OpenAIClientOptions { Endpoint = GroqEndpoint };
        var raw = new OpenAIClient(new ApiKeyCredential(keyForCtor), options).GetChatClient(model).AsIChatClient();
        // F-1 spike Llama 4 Scout = 텍스트 only tier (vision 별도 모델). 보수적으로 TextOnly.
        return CreateInternalAsync(
            raw, "Groq", model, systemPrompt, mcpServerUrl, mcpNonce,
            validate: () => !string.IsNullOrEmpty(apiKey),
            capabilities: Capabilities.TextOnly,
            cancellationToken: cancellationToken);
    }

    public static async Task<ApiChatProvider> CreateOllamaAsync(
        string baseUrl, string model, string systemPrompt, string mcpServerUrl, string mcpNonce,
        CancellationToken cancellationToken = default)
    {
        // OllamaApiClient 가 IChatClient 직접 구현. base URL + 기본 모델만 주면 됨. API key 없음 → 항상 valid.
        var ollama = new OllamaApiClient(new Uri(baseUrl), defaultModel: model);
        IChatClient raw = ollama;
        // 정책 7 — Ollama 만 모델별 동적 capability. /api/show 의 capabilities 배열에 "vision" 포함 시
        // 이미지 첨부 활성, 아니면 TextOnly. probe 실패 (Ollama 미실행 / 모델 미설치 / 네트워크) 시 보수적 fallback.
        var caps = await ProbeOllamaCapabilitiesAsync(ollama, model, cancellationToken).ConfigureAwait(false);
        return await CreateInternalAsync(
            raw, "Ollama", model, systemPrompt, mcpServerUrl, mcpNonce,
            validate: () => true,
            capabilities: caps,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ollama 의 <c>/api/show</c> 응답에서 <c>capabilities</c> 배열을 검사 — "vision" 포함 시 이미지 첨부 활성.
    /// PDF 는 Ollama 미지원 → <see cref="Capabilities.ImagesOnly"/> 만 반환. probe 실패는 TextOnly fallback.
    /// CT 가 cancel 되면 OperationCanceledException 그대로 전파 (TextOnly fallback 회피 — 호출자가 stale switch 처리).
    /// </summary>
    private static async Task<Capabilities> ProbeOllamaCapabilitiesAsync(
        OllamaApiClient client, string model, CancellationToken cancellationToken)
    {
        try
        {
            var resp = await client.ShowModelAsync(model, cancellationToken).ConfigureAwait(false);
            var modelCaps = resp?.Capabilities;
            if (modelCaps != null)
            {
                foreach (var c in modelCaps)
                {
                    if (string.Equals(c, "vision", StringComparison.OrdinalIgnoreCase))
                        return Capabilities.ImagesOnly(OllamaVisionImageCap);
                }
            }
            return Capabilities.TextOnly;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // cancel 은 fallback 대상이 아님 — caller 의 stale 처리에 위임.
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Ollama /api/show 실패 ({model}) — TextOnly fallback", ex);
            return Capabilities.TextOnly;
        }
    }

    /// <summary>
    /// 3 provider 공통 구성: ChatClientBuilder + UseFunctionInvocation() 미들웨어 + McpClient HTTP self-call +
    /// ApiChatProvider 인스턴스화. 각 public 메소드는 raw IChatClient 만 만들어 위임.
    /// </summary>
    private static async Task<ApiChatProvider> CreateInternalAsync(
        IChatClient raw, string label, string model,
        string systemPrompt, string mcpServerUrl, string mcpNonce,
        Func<bool> validate, Capabilities capabilities,
        CancellationToken cancellationToken)
    {
        var chatClient = new ChatClientBuilder(raw).UseFunctionInvocation().Build();
        var (mcpClient, http) = await CreateMcpClientAsync(mcpServerUrl, mcpNonce, cancellationToken).ConfigureAwait(false);
        return new ApiChatProvider(chatClient, mcpClient, http, label, model, systemPrompt, validate, capabilities);
    }

    /// <summary>
    /// 자기 자신의 Promaker McpHostService 에 HTTP 로 connect. nonce 헤더 부착 + reusable HttpClient.
    /// HttpClient 은 ApiChatProvider 가 자기 lifecycle 동안 보유, DisposeAsync 시 반환.
    /// HttpClientTransport 가 외부 주입 HttpClient 를 dispose 하지 않는 동작이라 ApiChatProvider 측에서 별도 dispose.
    /// </summary>
    private static async Task<(McpClient client, HttpClient http)> CreateMcpClientAsync(
        string mcpServerUrl, string mcpNonce, CancellationToken cancellationToken)
    {
        var http = new HttpClient { BaseAddress = new Uri(mcpServerUrl) };
        http.DefaultRequestHeaders.Add(McpNonceHeader, mcpNonce);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(mcpServerUrl) },
            httpClient: http);

        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
        return (client, http);
    }
}
