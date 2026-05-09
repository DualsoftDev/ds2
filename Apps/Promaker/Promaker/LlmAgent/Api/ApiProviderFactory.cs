using System;
using System.ClientModel;
using System.Net.Http;
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
    private const string McpNonceHeader = "X-Promaker-Nonce";

    /// <summary>LlmApiConfig 의 EncryptedKeys dict 키 — Anthropic API key 슬롯.</summary>
    public const string AnthropicKey = "anthropic";
    /// <summary>LlmApiConfig 의 EncryptedKeys dict 키 — OpenAI API key 슬롯.</summary>
    public const string OpenAiKey = "openai";
    /// <summary>F-1 spike — Groq API key 슬롯 (env-var fallback only, DPAPI 미사용). F-4 cleanup 시 정식 schema 로 대체.</summary>
    public const string GroqKey = "groq";

    /// <summary>F-1 spike — Groq OpenAI 호환 endpoint. F-4 cleanup 시 ProviderCapabilities.DefaultEndpoint 로 흡수.</summary>
    private static readonly Uri GroqEndpoint = new("https://api.groq.com/openai/v1");

    public static Task<ApiChatProvider> CreateAnthropicAsync(
        string apiKey, string model, string systemPrompt, string mcpServerUrl, string mcpNonce)
    {
        var raw = new AnthropicClient { ApiKey = apiKey }.AsIChatClient(model);
        // Anthropic API 공식 한도 — 이미지 base64 inline 5MB / PDF document 32MB. Files API 경유 시 별도.
        var caps = Capabilities.ImagesAndPdf(5L * 1024L * 1024L, 32L * 1024L * 1024L);
        return CreateInternalAsync(
            raw, "Anthropic", model, systemPrompt, mcpServerUrl, mcpNonce,
            validate: () => !string.IsNullOrEmpty(apiKey),
            capabilities: caps);
    }

    public static Task<ApiChatProvider> CreateOpenAiAsync(
        string apiKey, string model, string systemPrompt, string mcpServerUrl, string mcpNonce)
    {
        var raw = new OpenAIClient(apiKey).GetChatClient(model).AsIChatClient();
        // OpenAI gpt-4o vision — 이미지 20MB. PDF 직접 지원 여부 = V-1 미해결 (S-4 spike) → 보수적으로 미지원.
        var caps = Capabilities.ImagesOnly(20L * 1024L * 1024L);
        return CreateInternalAsync(
            raw, "OpenAI", model, systemPrompt, mcpServerUrl, mcpNonce,
            validate: () => !string.IsNullOrEmpty(apiKey),
            capabilities: caps);
    }

    /// <summary>
    /// F-1 spike — Groq OpenAI 호환 endpoint. OpenAI SDK 의 <c>OpenAIClientOptions.Endpoint</c> override
    /// 로 baseUrl 만 갈아끼움. tool calling / streaming / 5종 LlmEvent 매핑은 OpenAI 어댑터 그대로 통과.
    /// 429/Retry-After backoff 위치는 spike 검증 항목 (provider layer / ApiChatProvider / Microsoft.Extensions.AI 미들웨어 4 후보).
    /// F-4 cleanup 시 본 메소드는 ProviderCapabilities + Endpoint 파라미터화된 일반 메소드로 흡수.
    /// </summary>
    public static Task<ApiChatProvider> CreateGroqAsync(
        string apiKey, string model, string systemPrompt, string mcpServerUrl, string mcpNonce)
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
            capabilities: Capabilities.TextOnly);
    }

    public static Task<ApiChatProvider> CreateOllamaAsync(
        string baseUrl, string model, string systemPrompt, string mcpServerUrl, string mcpNonce)
    {
        // OllamaApiClient 가 IChatClient 직접 구현. base URL + 기본 모델만 주면 됨. API key 없음 → 항상 valid.
        IChatClient raw = new OllamaApiClient(new Uri(baseUrl), defaultModel: model);
        // 모델 의존 — vision 모델 (llava/llama3.2-vision 등) 동적 갱신은 phase 3a UI commit 에서 `/api/show` 조회.
        // 현 단계 정적 placeholder = TextOnly.
        return CreateInternalAsync(
            raw, "Ollama", model, systemPrompt, mcpServerUrl, mcpNonce,
            validate: () => true,
            capabilities: Capabilities.TextOnly);
    }

    /// <summary>
    /// 3 provider 공통 구성: ChatClientBuilder + UseFunctionInvocation() 미들웨어 + McpClient HTTP self-call +
    /// ApiChatProvider 인스턴스화. 각 public 메소드는 raw IChatClient 만 만들어 위임.
    /// </summary>
    private static async Task<ApiChatProvider> CreateInternalAsync(
        IChatClient raw, string label, string model,
        string systemPrompt, string mcpServerUrl, string mcpNonce,
        Func<bool> validate, Capabilities capabilities)
    {
        var chatClient = new ChatClientBuilder(raw).UseFunctionInvocation().Build();
        var (mcpClient, http) = await CreateMcpClientAsync(mcpServerUrl, mcpNonce).ConfigureAwait(false);
        return new ApiChatProvider(chatClient, mcpClient, http, label, model, systemPrompt, validate, capabilities);
    }

    /// <summary>
    /// 자기 자신의 Promaker McpHostService 에 HTTP 로 connect. nonce 헤더 부착 + reusable HttpClient.
    /// HttpClient 은 ApiChatProvider 가 자기 lifecycle 동안 보유, DisposeAsync 시 반환.
    /// HttpClientTransport 가 외부 주입 HttpClient 를 dispose 하지 않는 동작이라 ApiChatProvider 측에서 별도 dispose.
    /// </summary>
    private static async Task<(McpClient client, HttpClient http)> CreateMcpClientAsync(
        string mcpServerUrl, string mcpNonce)
    {
        var http = new HttpClient { BaseAddress = new Uri(mcpServerUrl) };
        http.DefaultRequestHeaders.Add(McpNonceHeader, mcpNonce);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(mcpServerUrl) },
            httpClient: http);

        var client = await McpClient.CreateAsync(transport).ConfigureAwait(false);
        return (client, http);
    }
}
