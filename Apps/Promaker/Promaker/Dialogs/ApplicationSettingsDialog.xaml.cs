using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Promaker.LlmAgent;
using Promaker.LlmAgent.Api;
using Promaker.Presentation;
using Promaker.Services;

namespace Promaker.Dialogs;

/// <summary>
/// 환경(앱 전역) 설정 다이얼로그 — AASX / PLC / LLM / 프리셋. 프로젝트 무관.
/// 프로젝트 메타(이름/작성자/버전/설명)는 별도 <see cref="ProjectPropertiesDialog"/> 에서 편집한다.
/// </summary>
public partial class ApplicationSettingsDialog : Window
{
    private static readonly string PresetFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "systemTypePreset", "systemTypePreset.json");

    private static string SplitDeviceAasxSettingsPath        => Promaker.Services.SettingsPaths.SplitDeviceAasx;
    private static string IriPrefixSettingsPath              => Promaker.Services.SettingsPaths.IriPrefix;
    private static string CreateDefaultEntitiesSettingsPath  => Promaker.Services.SettingsPaths.CreateDefaultEntitiesOnEmptyAasx;

    private const string DefaultIriPrefix = "https://dualsoft.com/";

    public string ResultIriPrefix { get; private set; } = "https://dualsoft.com/";
    public bool ResultSplitDeviceAasx { get; private set; }
    public bool ResultCreateDefaultEntities { get; private set; }

    // 프리셋 SystemType 매핑 결과 (배열)
    public string[] ResultPresetSystemTypes { get; private set; } = Array.Empty<string>();

    private LlmConfig _llmConfig = LlmConfig.Load();
    /// <summary>OK 시 LlmConfig 변경 사항이 disk 에 저장되었는지. 호출자가 LlmChatVm.ReloadConfig() 호출 트리거로 사용.</summary>
    public bool LlmConfigChanged { get; private set; }

    /// <summary>모델 ComboBox (IsEditable=True) 의 후보 목록. 사용자는 선택 또는 직접 입력 가능.</summary>
    private static readonly string[] AnthropicModelCandidates =
    {
        "claude-opus-4-7",
        "claude-sonnet-4-6",
        "claude-haiku-4-5-20251001",
    };
    private static readonly string[] OpenAiModelCandidates =
    {
        "gpt-4o",
        "gpt-4o-mini",
        "gpt-4-turbo",
        "o1-mini",
    };
    private static readonly string[] OllamaModelCandidates =
    {
        "llama3.1",
        "llama3.2",
        "mistral-small",
        "qwen2.5",
    };

    public ApplicationSettingsDialog()
    {
        InitializeComponent();

        // 앱 설정에서 로드
        IriPrefixBox.Text = AppSettingStore.LoadStringOrDefault(IriPrefixSettingsPath, DefaultIriPrefix);
        SplitDeviceAasxBox.IsChecked = AppSettingStore.LoadBoolOrDefault(SplitDeviceAasxSettingsPath, false);
        CreateDefaultEntitiesBox.IsChecked = AppSettingStore.LoadBoolOrDefault(CreateDefaultEntitiesSettingsPath, false);

        // PLC 설정 로드 — 빈 값 금지. 항상 effective 값(디폴트 자동 채움)을 표시.
        var plcCfg = PlcConfig.Settings;
        PlcXgiTemplatePathBox.Text = plcCfg.EffectiveXgiTemplatePath;
        PlcXg5000ExePathBox.Text   = plcCfg.EffectiveXg5000ExePath;

        // 프리셋 SystemType 매핑 로드
        LoadPresetMappings();

        // LLM 탭 — 통합 LlmConfig 로 UI 채우기 (PR-B)
        LoadLlmTab();

        // 기본 값 — Motor 샘플 (프리셋 입력 형식 안내용). 실제 systemTypePreset.json 의
        // 디폴트 항목은 CallCreateDialog 가 없을 때 자동 생성하는 5종 (Unit/Cylinder_#/Robot*/Part).
        PresetTextBox.Text = "FWD;BWD";
    }

    // ── FBTagMap 디바이스 타입 설정은 제거됨 ────────────────────────────────
    //    IoList = 단일 진실원 (B안). FB 포트 바인딩은 IO Wizard 에서 편집합니다.

    // ── 프리셋 ───────────────────────────────────────────────────────────────

    private void LoadPresetMappings()
    {
        PresetMappingListBox.Items.Clear();

        var filePresets = LoadPresetsFromFile();
        var source = filePresets.Length > 0
            ? filePresets
            : SystemTypePresetProvider.BuildDefaultMappingStrings();

        foreach (var mapping in source)
            PresetMappingListBox.Items.Add(mapping);
    }

    private static string[] LoadPresetsFromFile()
    {
        try
        {
            if (!File.Exists(PresetFilePath)) return [];
            var json = File.ReadAllText(PresetFilePath);
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch { return []; }
    }

    private static void SavePresetsToFile(string[] presets)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PresetFilePath)!);
            File.WriteAllText(PresetFilePath,
                JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void PresetMappingListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetMappingListBox.SelectedItem is string selectedMapping)
        {
            var parts = selectedMapping.Split(':');
            if (parts.Length == 2)
            {
                PresetTextBox.Text = parts[0];
                SystemTypeTextBox.Text = parts[1];
            }
        }
    }

    private void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        var presetName = PresetTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(presetName))
        {
            DialogHelpers.Warn("프리셋을 입력해주세요.");
            return;
        }

        var systemType = SystemTypeTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(systemType))
        {
            DialogHelpers.Warn("SystemType을 입력해주세요.");
            return;
        }

        var mapping = $"{presetName}:{systemType}";

        var existingItems = PresetMappingListBox.Items
            .Cast<string>()
            .Where(item => item.StartsWith(presetName + ":"))
            .ToList();

        foreach (var item in existingItems)
            PresetMappingListBox.Items.Remove(item);

        PresetMappingListBox.Items.Add(mapping);
    }

    private void RemovePreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetMappingListBox.SelectedItem is string selectedMapping)
        {
            PresetMappingListBox.Items.Remove(selectedMapping);
        }
        else
        {
            DialogHelpers.Warn("제거할 항목을 선택해주세요.");
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        var selectedIndex = PresetMappingListBox.SelectedIndex;
        if (selectedIndex <= 0) return;

        var item = PresetMappingListBox.Items[selectedIndex];
        PresetMappingListBox.Items.RemoveAt(selectedIndex);
        PresetMappingListBox.Items.Insert(selectedIndex - 1, item);
        PresetMappingListBox.SelectedIndex = selectedIndex - 1;
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        var selectedIndex = PresetMappingListBox.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= PresetMappingListBox.Items.Count - 1) return;

        var item = PresetMappingListBox.Items[selectedIndex];
        PresetMappingListBox.Items.RemoveAt(selectedIndex);
        PresetMappingListBox.Items.Insert(selectedIndex + 1, item);
        PresetMappingListBox.SelectedIndex = selectedIndex + 1;
    }

    // ── LLM 탭 (PR-B) ────────────────────────────────────────────────────────

    private void LoadLlmTab()
    {
        // API keys — DPAPI 복호화한 값을 PasswordBox 에 표시 (사용자가 직접 보고 검증 가능)
        LlmAnthropicKeyBox.Password = _llmConfig.GetApiKey(ApiProviderFactory.AnthropicKey) ?? "";
        LlmOpenAiKeyBox.Password    = _llmConfig.GetApiKey(ApiProviderFactory.OpenAiKey) ?? "";

        // Models — TextBox + ▾ Button 패턴 (Hot-fix-8 v3, IsEditable ComboBox quirks 회피)
        LlmAnthropicModelBox.Text = _llmConfig.AnthropicModel;
        LlmOpenAiModelBox.Text    = _llmConfig.OpenAiModel;
        LlmOllamaModelBox.Text    = _llmConfig.OllamaModel;

        // Ollama base URL
        LlmOllamaBaseUrlBox.Text = _llmConfig.OllamaBaseUrl;

        // Consent 상태
        UpdateConsentStatus();
    }

    // ─── Hot-fix-8 v3: 후보 선택 ContextMenu (TextBox + ▾ Button 패턴) ─────────

    private void LlmAnthropicCandidates_Click(object sender, RoutedEventArgs e)
        => ShowCandidatesMenu(sender, LlmAnthropicModelBox, AnthropicModelCandidates);

    private void LlmOpenAiCandidates_Click(object sender, RoutedEventArgs e)
        => ShowCandidatesMenu(sender, LlmOpenAiModelBox, OpenAiModelCandidates);

    private void LlmOllamaCandidates_Click(object sender, RoutedEventArgs e)
        => ShowCandidatesMenu(sender, LlmOllamaModelBox, OllamaModelCandidates);

    private static void ShowCandidatesMenu(object sender, TextBox target, string[] candidates)
    {
        if (sender is not Button btn) return;
        var menu = new ContextMenu
        {
            PlacementTarget = btn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (var c in candidates)
        {
            var mi = new MenuItem { Header = c };
            mi.Click += (_, _) => target.Text = c;
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private void UpdateConsentStatus()
    {
        if (_llmConfig.IsConsentGranted())
        {
            var ts = _llmConfig.ConsentTimestampUtc ?? "(시간 정보 없음)";
            LlmConsentStatusText.Text = $"동의 완료 — {ts}";
            LlmRevokeConsentButton.IsEnabled = true;
        }
        else
        {
            LlmConsentStatusText.Text = "미동의 — LLM Chat 진입 시 다이얼로그 표시";
            LlmRevokeConsentButton.IsEnabled = false;
        }
    }

    private void LlmClearAnthropicKey_Click(object sender, RoutedEventArgs e) => LlmAnthropicKeyBox.Password = "";
    private void LlmClearOpenAiKey_Click(object sender, RoutedEventArgs e)    => LlmOpenAiKeyBox.Password = "";

    private void LlmRevokeConsent_Click(object sender, RoutedEventArgs e)
    {
        _llmConfig.DataEgressConsent = false;
        _llmConfig.ConsentTimestampUtc = null;
        _llmConfig.Save();
        LlmConfigChanged = true;
        UpdateConsentStatus();
        DialogHelpers.Warn("동의가 철회되었습니다. LLM Chat 패널이 열려 있다면 다음 진입 시 다시 동의 다이얼로그가 표시됩니다.");
    }

    private async void LlmTestOllama_Click(object sender, RoutedEventArgs e)
    {
        var url = (LlmOllamaBaseUrlBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(url))
        {
            LlmOllamaTestResult.Text = "❌ URL 이 비어있습니다.";
            return;
        }
        LlmOllamaTestResult.Text = "확인 중…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            // Ollama 의 `GET /api/version` 은 인증 불필요 + 가벼운 endpoint.
            var probe = url.TrimEnd('/') + "/api/version";
            using var resp = await http.GetAsync(probe).ConfigureAwait(true);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(true);
                LlmOllamaTestResult.Text = $"✅ 연결 성공 — {body.Trim()}";
            }
            else
            {
                LlmOllamaTestResult.Text = $"❌ 응답 status {(int)resp.StatusCode} {resp.ReasonPhrase}";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            // review (3): OOM/StackOverflow 등 시스템 예외는 흡수하지 않고 즉시 throw → 진단 용이성 보존.
            LlmOllamaTestResult.Text = $"❌ 연결 실패: {ex.Message}";
        }
    }

    private void SaveLlmTab()
    {
        // PR-D: Default provider 는 LLM Chat 패널의 ComboBox 마지막 선택이 자동 저장 — 본 다이얼로그에서 다루지 않음.
        //
        // review (2) — dirty 비교: DPAPI 재암호화는 매번 다른 ciphertext 라 EncryptedKeys 변경만으로는 dirty 판단 불가.
        // plaintext (PasswordBox.Password) + 모델 / URL 비교로 진짜 변경 여부 판단 → 불필요한 ReloadConfig (활성 provider
        // 재구성 비용) 회피. LlmRevokeConsent_Click 처럼 별도 핸들러는 자체 LlmConfigChanged=true 설정.
        var fallback = new LlmConfig();
        var newAnthropicKey = LlmAnthropicKeyBox.Password ?? "";
        var newOpenAiKey    = LlmOpenAiKeyBox.Password ?? "";
        var newAnthropicModel = string.IsNullOrWhiteSpace(LlmAnthropicModelBox.Text) ? fallback.AnthropicModel : LlmAnthropicModelBox.Text.Trim();
        var newOpenAiModel    = string.IsNullOrWhiteSpace(LlmOpenAiModelBox.Text)    ? fallback.OpenAiModel    : LlmOpenAiModelBox.Text.Trim();
        var newOllamaModel    = string.IsNullOrWhiteSpace(LlmOllamaModelBox.Text)    ? fallback.OllamaModel    : LlmOllamaModelBox.Text.Trim();
        var newOllamaBaseUrl  = string.IsNullOrWhiteSpace(LlmOllamaBaseUrlBox.Text)  ? fallback.OllamaBaseUrl  : LlmOllamaBaseUrlBox.Text.Trim();

        var dirty =
            newAnthropicModel != _llmConfig.AnthropicModel
            || newOpenAiModel    != _llmConfig.OpenAiModel
            || newOllamaModel    != _llmConfig.OllamaModel
            || newOllamaBaseUrl  != _llmConfig.OllamaBaseUrl
            || newAnthropicKey   != (_llmConfig.GetApiKey(ApiProviderFactory.AnthropicKey) ?? "")
            || newOpenAiKey      != (_llmConfig.GetApiKey(ApiProviderFactory.OpenAiKey)    ?? "");

        if (!dirty) return;

        _llmConfig.SetApiKey(ApiProviderFactory.AnthropicKey, newAnthropicKey);
        _llmConfig.SetApiKey(ApiProviderFactory.OpenAiKey,    newOpenAiKey);
        _llmConfig.AnthropicModel = newAnthropicModel;
        _llmConfig.OpenAiModel    = newOpenAiModel;
        _llmConfig.OllamaModel    = newOllamaModel;
        _llmConfig.OllamaBaseUrl  = newOllamaBaseUrl;

        _llmConfig.Save();
        LlmConfigChanged = true;
    }

    // ── OK 처리 ──────────────────────────────────────────────────────────────

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultIriPrefix = string.IsNullOrWhiteSpace(IriPrefixBox.Text) ? DefaultIriPrefix : IriPrefixBox.Text.Trim();
        AppSettingStore.SaveString(IriPrefixSettingsPath, ResultIriPrefix);

        ResultSplitDeviceAasx = SplitDeviceAasxBox.IsChecked == true;
        AppSettingStore.SaveBool(SplitDeviceAasxSettingsPath, ResultSplitDeviceAasx);

        ResultCreateDefaultEntities = CreateDefaultEntitiesBox.IsChecked == true;
        AppSettingStore.SaveBool(CreateDefaultEntitiesSettingsPath, ResultCreateDefaultEntities);

        ResultPresetSystemTypes = PresetMappingListBox.Items
            .Cast<string>()
            .ToArray();

        SavePresetsToFile(ResultPresetSystemTypes);

        PlcConfig.Save(
            PlcXgiTemplatePathBox.Text.Trim(),
            PlcXg5000ExePathBox.Text.Trim());

        // LLM 탭 저장 (PR-B). LlmConfigChanged=true 면 호출자가 LlmChatVm.ReloadConfig() 호출.
        SaveLlmTab();

        DialogResult = true;
    }

    // ── PLC 탭: 폴더/파일 선택 ───────────────────────────────────────────────

    private void BrowsePlcXgiTemplate_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title  = "XGI 템플릿 파일 선택",
            Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*",
            FileName = PlcXgiTemplatePathBox.Text
        };
        if (picker.ShowDialog(this) == true)
            PlcXgiTemplatePathBox.Text = picker.FileName;
    }

    private void BrowsePlcXg5000Exe_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title  = "XG5000.exe 선택",
            Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*",
            FileName = PlcXg5000ExePathBox.Text
        };
        if (picker.ShowDialog(this) == true)
            PlcXg5000ExePathBox.Text = picker.FileName;
    }
}
