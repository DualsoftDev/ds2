using System.Windows;
using System.Windows.Controls;
using Llm.Shared.Api;
using Promaker.LlmAgent;

namespace Promaker.Dialogs;

/// <summary>
/// HKMC H-Chat 게이트웨이 전용 설정 panel. ApplicationSettingsDialog 의 LLM 탭에 ContentPresenter 로 부착되며,
/// <see cref="Promaker.HkmcFeature.IsEnabled"/> 가 true 일 때에만 visible.
/// <para/>
/// 책임: Base URL / API Key (DPAPI) / Claude Model / OpenAI Model 입력 UI + <see cref="Save"/> 호출 시
/// <see cref="LlmConfig"/> 갱신 + DPAPI 슬롯 (<see cref="HkmcApiProviderFactory.HkmcHChatKey"/>) 박제. Save 자체는
/// caller (ApplicationSettingsDialog.Ok_Click) 가 일괄 <see cref="LlmConfig.Save"/> 시점에 호출.
/// <para/>
/// Personal API Key 모드 (결정 #2 정합) — Project ID 입력 칸 없음.
/// 모델 ID 는 default 빈 문자열 (결정 #3) — 사용자 입력 강제.
/// </summary>
public partial class HkmcHChatSettingsPanel : UserControl
{
    private readonly LlmConfig _config;

    /// <summary>
    /// Save() 호출 시 true 로 박제 — caller 가 LlmConfigChanged 갱신용으로 읽음 (provider 재구성 trigger).
    /// </summary>
    public bool Dirty { get; private set; }

    public HkmcHChatSettingsPanel(LlmConfig config)
    {
        _config = config;
        InitializeComponent();
        Load();
    }

    private void Load()
    {
        // HkmcHChat null 이면 default 인스턴스 생성 — Save 시점에 _config 에 반영 (사용자가 한 번이라도 저장하면 disk 박제).
        var cfg = _config.HkmcHChat ?? new HkmcHChatConfig();
        HkmcBaseUrlBox.Text = cfg.BaseUrl;
        HkmcClaudeModelBox.Text = cfg.ClaudeModel;
        HkmcOpenAiModelBox.Text = cfg.OpenAiModel;
        // DPAPI 복호화한 평문 표시 — 다른 provider (Anthropic / OpenAI) 의 LoadLlmTab 패턴 정합.
        HkmcApiKeyBox.Password = _config.GetApiKey(HkmcApiProviderFactory.HkmcHChatKey) ?? "";
    }

    private void HkmcClearKey_Click(object sender, RoutedEventArgs e) => HkmcApiKeyBox.Password = "";

    /// <summary>
    /// caller (ApplicationSettingsDialog.Ok_Click → SaveLlmTab 직후) 가 호출. _config 만 mutate — disk Save 는 caller 책임.
    /// dirty 비교는 단순 — 사용자가 본 panel 을 열었고 ENABLE_HKMC 가 켜져 있으면 LlmConfigChanged=true 로 박제 안전 (provider
    /// 재구성 비용은 활성 provider 가 HKMC 일 때만 의미).
    /// </summary>
    public void Save()
    {
        var baseUrl = (HkmcBaseUrlBox.Text ?? "").Trim();
        var claudeModel = (HkmcClaudeModelBox.Text ?? "").Trim();
        var openAiModel = (HkmcOpenAiModelBox.Text ?? "").Trim();
        var apiKey = HkmcApiKeyBox.Password ?? "";

        // default fallback — BaseUrl 빈 값 입력 시 default 운영 URL 로 복원 (결정 #9).
        if (string.IsNullOrEmpty(baseUrl))
            baseUrl = new HkmcHChatConfig().BaseUrl;

        var oldCfg = _config.HkmcHChat;
        var oldKey = _config.GetApiKey(HkmcApiProviderFactory.HkmcHChatKey) ?? "";

        Dirty =
            oldCfg is null
            || oldCfg.BaseUrl != baseUrl
            || oldCfg.ClaudeModel != claudeModel
            || oldCfg.OpenAiModel != openAiModel
            || oldKey != apiKey;

        if (!Dirty) return;

        _config.HkmcHChat = new HkmcHChatConfig
        {
            BaseUrl = baseUrl,
            ClaudeModel = claudeModel,
            OpenAiModel = openAiModel,
        };
        _config.SetApiKey(HkmcApiProviderFactory.HkmcHChatKey, apiKey);
    }
}
