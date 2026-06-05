using System;
using System.Threading.Tasks;
using Ds2.LlmAgent;
using Promaker.LlmAgent;
using Llm.Shared;
using Llm.Shared.Abstractions;
using Llm.Shared.Api;

namespace Promaker.ViewModels;

/// <summary>
/// LlmChatViewModel partial — HKMC H-Chat 전용 provider 생성. ENABLE_HKMC 미설정 환경에서는
/// <see cref="LlmChatViewModel.BuildAvailableProviders"/> 가 enum 값을 List 에서 제외하므로
/// ConfigureProviderAsync 의 switch 가 본 메서드로 진입할 일 자체가 없으나, disk JSON 에 우연히
/// HKMC 값이 박혀 있던 사용자가 ENABLE_HKMC off 로 재기동하는 시나리오 (todo §"enum / partial / 빌드")
/// 에 대비해 진입부에서 IsEnabled 가드 재확인 → declined throw.
///
/// 결정 정합 (todo §"결정 사항"):
///   #2 Personal Key only — Project ID 분기 없음, <c>Authorization: Bearer</c> 만 부착 (Factory 처리).
///   #3 모델 ID 빈 문자열 → declined — 본 메서드의 NullOrWhiteSpace 가드.
///   #7 EnsureApiCostConsent skip — 사내 부서 비용 청구 구조라 개인 과금 경고 부정합.
/// </summary>
public partial class LlmChatViewModel
{
    /// <summary>
    /// _config.HkmcHChat 이 null 또는 BaseUrl 빈 문자열이면 <see cref="HkmcHChatConfig"/> default 값으로 fallback.
    /// Settings 의 H-Chat panel 이 default 인스턴스를 표시값으로 사용하는 동선과 SSOT 정합 — UI 가 default URL 을
    /// 보여주는데 declined 메시지는 "BaseUrl 미설정" 으로 떠서 사용자가 혼동하는 UX 함정 차단.
    /// 모델 ID 는 결정 #3 에 따라 default 빈 문자열 강제이므로 여전히 declined.
    /// </summary>
    private async Task<ILlmProvider> CreateHkmcHChatClaudeProviderAsync()
    {
        if (!Promaker.HkmcFeature.IsEnabled)
            throw ProviderDeclined("HKMC H-Chat (Claude)", "ENABLE_HKMC 환경변수 미설정 (재시작 필요)");

        var cfg = _config.HkmcHChat ?? new HkmcHChatConfig();
        var baseUrl = string.IsNullOrWhiteSpace(cfg.BaseUrl) ? new HkmcHChatConfig().BaseUrl : cfg.BaseUrl;
        if (string.IsNullOrWhiteSpace(cfg.ClaudeModel))
            throw ProviderDeclined("HKMC H-Chat (Claude)", "Claude Model ID 미입력 — Settings 의 H-Chat 패널에서 입력 필요");

        var apiKey = _config.GetApiKey(HkmcApiProviderFactory.HkmcHChatKey)
                     ?? Environment.GetEnvironmentVariable("H_CHAT_API_KEY")
                     ?? "";
        return await HkmcApiProviderFactory.CreateHChatClaudeAsync(
            apiKey: apiKey,
            baseUrl: baseUrl,
            model: cfg.ClaudeModel,
            systemPrompt: CreateProviderSystemPrompt(),
            mcpServerUrl: _mcpHost.ServerUrl,
            mcpNonce: _mcpHost.HandshakeNonce).ConfigureAwait(true);
    }

    /// <summary>Claude 메서드와 동일 fallback 정책. <see cref="CreateHkmcHChatClaudeProviderAsync"/> 의 주석 참조.</summary>
    private async Task<ILlmProvider> CreateHkmcHChatOpenAiProviderAsync()
    {
        if (!Promaker.HkmcFeature.IsEnabled)
            throw ProviderDeclined("HKMC H-Chat (OpenAI)", "ENABLE_HKMC 환경변수 미설정 (재시작 필요)");

        var cfg = _config.HkmcHChat ?? new HkmcHChatConfig();
        var baseUrl = string.IsNullOrWhiteSpace(cfg.BaseUrl) ? new HkmcHChatConfig().BaseUrl : cfg.BaseUrl;
        if (string.IsNullOrWhiteSpace(cfg.OpenAiModel))
            throw ProviderDeclined("HKMC H-Chat (OpenAI)", "OpenAI Model ID 미입력 — Settings 의 H-Chat 패널에서 입력 필요");

        var apiKey = _config.GetApiKey(HkmcApiProviderFactory.HkmcHChatKey)
                     ?? Environment.GetEnvironmentVariable("H_CHAT_API_KEY")
                     ?? "";
        return await HkmcApiProviderFactory.CreateHChatOpenAiAsync(
            apiKey: apiKey,
            baseUrl: baseUrl,
            model: cfg.OpenAiModel,
            systemPrompt: CreateProviderSystemPrompt(),
            mcpServerUrl: _mcpHost.ServerUrl,
            mcpNonce: _mcpHost.HandshakeNonce).ConfigureAwait(true);
    }
}
