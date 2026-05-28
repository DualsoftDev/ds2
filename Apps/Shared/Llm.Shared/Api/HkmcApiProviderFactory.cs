using System;
using System.ClientModel;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Azure.AI.OpenAI;
using Ds2.LlmAgent;
using log4net;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Llm.Shared.Api;

/// <summary>
/// HKMC (현대/기아) H-Chat 게이트웨이 전용 ApiChatProvider 팩토리.
///
/// H-Chat 은 사내망 폐쇄형 LLM 게이트웨이로 Anthropic Messages API / Azure OpenAI deployment path 와
/// wire 호환. 인증은 Anthropic 의 <c>x-api-key</c> 대신 <c>Authorization: Bearer</c> 사용 →
/// Anthropic SDK 의 <see cref="AnthropicClient.AuthToken"/> 1급 지원으로 DelegatingHandler 불필요.
/// OpenAI 측은 <c>/v3/openai/deployments/{model}/chat/completions</c> 의 Azure deployment 라우팅 →
/// <see cref="AzureOpenAIClient"/> 사용.
///
/// 본 factory 는 ENABLE_HKMC 평가를 일절 하지 않는다. 호출자 (LlmChatViewModel) 가
/// <c>Promaker.HkmcFeature.IsEnabled</c> 가드로 진입 제어한다.
/// </summary>
public static class HkmcApiProviderFactory
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(HkmcApiProviderFactory));

    private const string McpNonceHeader = "X-Promaker-Nonce";

    /// <summary><see cref="Promaker.LlmAgent.LlmConfig.EncryptedKeys"/> dict 의 키 — H-Chat API key 슬롯.
    /// 기존 "anthropic" / "openai" / "groq" 와 겹치지 않도록 별도 슬롯.</summary>
    public const string HkmcHChatKey = "hkmc-hchat";

    /// <summary>logging / status 라벨 — Claude 어댑터.</summary>
    public const string HChatClaudeProviderLabel = "HKMC-Claude";
    /// <summary>logging / status 라벨 — OpenAI (Azure deployment) 어댑터.</summary>
    public const string HChatOpenAiProviderLabel = "HKMC-OpenAI";

    /// <summary>
    /// H-Chat 의 Claude endpoint (<c>{baseUrl}/v3/claude/messages</c>) 로 라우팅되는 Anthropic 어댑터 생성.
    /// 모델 ID 빈 문자열은 사용자 입력 강제 — 즉시 throw.
    /// </summary>
    public static Task<ApiChatProvider> CreateHChatClaudeAsync(
        string apiKey, string baseUrl, string model, string systemPrompt, string mcpServerUrl, string mcpNonce,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(model))
            throw new ArgumentException("model is required for HKMC H-Chat Claude", nameof(model));

        var normalized = string.IsNullOrEmpty(baseUrl) ? string.Empty : baseUrl.TrimEnd('/');
        // TODO (Phase 6 라이브 검증) — Anthropic SDK 12.20.0 이 BaseUrl 뒤에 `/v1/messages` 를 append.
        // 결과 URL = `{baseUrl}/v3/claude/v1/messages` 가 되어 H-Chat 의 `.../v3/claude/messages` 와 불일치.
        // 라이브 검증에서 404 확인 시 hotfix 두 후보:
        //   (a) DelegatingHandler 로 `/v1/messages` → `/messages` rewrite (BaseUrl 은 `/v3/claude` 유지).
        //   (b) BaseUrl 을 `{baseUrl}` 또는 `{baseUrl}/v3/claude/messages` 직전까지로 잘라내고 SDK 동작 재확인.
        var claudeBase = normalized + "/v3/claude";

        // Phase 0 spike 박제 (sdk issue #47):
        //  - AuthToken = apiKey   → Authorization: Bearer <key>
        //  - ApiKey = null        → env var ANTHROPIC_API_KEY 누수 차단 (x-api-key 미부착)
        //  - BaseUrl override     → /v3/claude/messages 라우팅 (SDK 가 BaseUrl 을 string 으로 노출)
        // AnthropicClient 자체가 BaseUrl/ApiKey/AuthToken property 1급 노출 → object initializer 로 직접 설정.
        var raw = new AnthropicClient
        {
            BaseUrl = claudeBase,
            ApiKey = null,
            AuthToken = apiKey,
        }.AsIChatClient(model);

        return CreateInternalAsync(
            raw, HChatClaudeProviderLabel, model, systemPrompt, mcpServerUrl, mcpNonce,
            validate: () => !string.IsNullOrEmpty(apiKey),
            capabilities: CapabilityPresets.AnthropicWire,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// H-Chat 의 OpenAI endpoint (<c>{baseUrl}/v3/openai/deployments/{model}/chat/completions</c>) 로 라우팅되는
    /// Azure OpenAI 어댑터 생성. 모델 ID 가 곧 Azure deployment 이름. 빈 문자열은 throw.
    /// </summary>
    public static Task<ApiChatProvider> CreateHChatOpenAiAsync(
        string apiKey, string baseUrl, string model, string systemPrompt, string mcpServerUrl, string mcpNonce,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(model))
            throw new ArgumentException("model is required for HKMC H-Chat OpenAI", nameof(model));

        var normalized = string.IsNullOrEmpty(baseUrl) ? string.Empty : baseUrl.TrimEnd('/');
        var openAiBase = new Uri(normalized + "/v3");

        // ApiKeyCredential 은 빈 문자열에 throw → ConfigureProviderAsync 가 validate=false 로 graceful 차단되도록
        // placeholder. 실 빈 키는 validate 람다에서 검증. (Groq 패턴 — ApiProviderFactory.cs line 84~85 참조.)
        var keyForCtor = string.IsNullOrEmpty(apiKey) ? "missing" : apiKey;
        var raw = new AzureOpenAIClient(openAiBase, new ApiKeyCredential(keyForCtor))
            .GetChatClient(model)
            .AsIChatClient();

        return CreateInternalAsync(
            raw, HChatOpenAiProviderLabel, model, systemPrompt, mcpServerUrl, mcpNonce,
            validate: () => !string.IsNullOrEmpty(apiKey),
            capabilities: CapabilityPresets.OpenAiApiWire,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// <see cref="ApiProviderFactory"/> 의 private <c>CreateInternalAsync</c> 와 동일 로직 — todo 금지사항 (본체 수정 금지)
    /// 정합 차원에서 본 factory 안에 별도 복제. ChatClientBuilder + UseFunctionInvocation + ApiChatProvider 결선.
    /// </summary>
    private static async Task<ApiChatProvider> CreateInternalAsync(
        IChatClient raw, string label, string model,
        string systemPrompt, string mcpServerUrl, string mcpNonce,
        Func<bool> validate, Capabilities capabilities,
        CancellationToken cancellationToken)
    {
        Log.Info($"HKMC provider 생성 — {label} (model={model})");
        var chatClient = new ChatClientBuilder(raw).UseFunctionInvocation().Build();
        var (mcpClient, http) = await CreateMcpClientAsync(mcpServerUrl, mcpNonce, cancellationToken).ConfigureAwait(false);
        return new ApiChatProvider(chatClient, mcpClient, http, label, model, systemPrompt, validate, capabilities);
    }

    /// <summary>
    /// Promaker McpHostService 에 HTTP self-call. nonce 헤더 부착 + reusable HttpClient.
    /// (<see cref="ApiProviderFactory"/> 의 private 복제 — 본체 수정 금지 정합.)
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
