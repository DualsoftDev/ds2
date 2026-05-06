using System;
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

    public static Task<ApiChatProvider> CreateAnthropicAsync(
        string apiKey, string model, string systemPrompt, string mcpServerUrl, string mcpNonce)
    {
        var raw = new AnthropicClient { ApiKey = apiKey }.AsIChatClient(model);
        return CreateInternalAsync(
            raw, "Anthropic", model, systemPrompt, mcpServerUrl, mcpNonce,
            validate: () => !string.IsNullOrEmpty(apiKey));
    }

    public static Task<ApiChatProvider> CreateOpenAiAsync(
        string apiKey, string model, string systemPrompt, string mcpServerUrl, string mcpNonce)
    {
        var raw = new OpenAIClient(apiKey).GetChatClient(model).AsIChatClient();
        return CreateInternalAsync(
            raw, "OpenAI", model, systemPrompt, mcpServerUrl, mcpNonce,
            validate: () => !string.IsNullOrEmpty(apiKey));
    }

    public static Task<ApiChatProvider> CreateOllamaAsync(
        string baseUrl, string model, string systemPrompt, string mcpServerUrl, string mcpNonce)
    {
        // OllamaApiClient 가 IChatClient 직접 구현. base URL + 기본 모델만 주면 됨. API key 없음 → 항상 valid.
        IChatClient raw = new OllamaApiClient(new Uri(baseUrl), defaultModel: model);
        return CreateInternalAsync(
            raw, "Ollama", model, systemPrompt, mcpServerUrl, mcpNonce,
            validate: () => true);
    }

    /// <summary>
    /// 3 provider 공통 구성: ChatClientBuilder + UseFunctionInvocation() 미들웨어 + McpClient HTTP self-call +
    /// ApiChatProvider 인스턴스화. 각 public 메소드는 raw IChatClient 만 만들어 위임.
    /// </summary>
    private static async Task<ApiChatProvider> CreateInternalAsync(
        IChatClient raw, string label, string model,
        string systemPrompt, string mcpServerUrl, string mcpNonce, Func<bool> validate)
    {
        var chatClient = new ChatClientBuilder(raw).UseFunctionInvocation().Build();
        var (mcpClient, http) = await CreateMcpClientAsync(mcpServerUrl, mcpNonce).ConfigureAwait(false);
        return new ApiChatProvider(chatClient, mcpClient, http, label, model, systemPrompt, validate);
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
