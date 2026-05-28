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
    private async Task<ILlmProvider> CreateHkmcHChatClaudeProviderAsync()
    {
        if (!Promaker.HkmcFeature.IsEnabled)
            throw ProviderDeclined("HKMC H-Chat (Claude)", "ENABLE_HKMC 환경변수 미설정 (재시작 필요)");

        var cfg = _config.HkmcHChat;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
            throw ProviderDeclined("HKMC H-Chat (Claude)", "BaseUrl 미설정 — Settings 의 H-Chat 패널에서 입력 필요");
        if (string.IsNullOrWhiteSpace(cfg.ClaudeModel))
            throw ProviderDeclined("HKMC H-Chat (Claude)", "모델 ID 미입력 — Settings 의 H-Chat 패널에서 입력 필요");

        var apiKey = _config.GetApiKey(HkmcApiProviderFactory.HkmcHChatKey)
                     ?? Environment.GetEnvironmentVariable("H_CHAT_API_KEY")
                     ?? "";
        return await HkmcApiProviderFactory.CreateHChatClaudeAsync(
            apiKey: apiKey,
            baseUrl: cfg.BaseUrl,
            model: cfg.ClaudeModel,
            systemPrompt: SystemPromptText.Phase1c(PromakerProfile.Instance),
            mcpServerUrl: _mcpHost.ServerUrl,
            mcpNonce: _mcpHost.HandshakeNonce).ConfigureAwait(true);
    }

    private async Task<ILlmProvider> CreateHkmcHChatOpenAiProviderAsync()
    {
        if (!Promaker.HkmcFeature.IsEnabled)
            throw ProviderDeclined("HKMC H-Chat (OpenAI)", "ENABLE_HKMC 환경변수 미설정 (재시작 필요)");

        var cfg = _config.HkmcHChat;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
            throw ProviderDeclined("HKMC H-Chat (OpenAI)", "BaseUrl 미설정 — Settings 의 H-Chat 패널에서 입력 필요");
        if (string.IsNullOrWhiteSpace(cfg.OpenAiModel))
            throw ProviderDeclined("HKMC H-Chat (OpenAI)", "모델 ID 미입력 — Settings 의 H-Chat 패널에서 입력 필요");

        var apiKey = _config.GetApiKey(HkmcApiProviderFactory.HkmcHChatKey)
                     ?? Environment.GetEnvironmentVariable("H_CHAT_API_KEY")
                     ?? "";
        return await HkmcApiProviderFactory.CreateHChatOpenAiAsync(
            apiKey: apiKey,
            baseUrl: cfg.BaseUrl,
            model: cfg.OpenAiModel,
            systemPrompt: SystemPromptText.Phase1c(PromakerProfile.Instance),
            mcpServerUrl: _mcpHost.ServerUrl,
            mcpNonce: _mcpHost.HandshakeNonce).ConfigureAwait(true);
    }
}
