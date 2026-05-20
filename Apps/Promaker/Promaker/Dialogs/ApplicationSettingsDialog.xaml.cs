using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Promaker.Knowledge;
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

    /// <summary>
    /// **D-S7-3c (s6-r31)** — DataGrid binding 의 working copy. _llmConfig.LightHouseServices 의 deep clone.
    /// LoadLlmTab 에서 채워지고, ApplyLlmTab 에서 commit. Cancel 시 _llmConfig 무변경.
    /// </summary>
    private readonly ObservableCollection<LightHouseServiceConfig> _lhServicesWorking = new();

    /// <summary>
    /// **D-S7-3c (s6-r31)** — PSK 평문 dialog 입력 임시 저장. key = ServiceId, value = 평문 PSK. ApplyLlmTab 시점에
    /// _llmConfig.SetLightHousePsk(serviceId, plain) 호출 + Save. dict 자체는 dialog modal lifetime 동안만 유지 →
    /// 평문 보존 risk 최소화.
    /// </summary>
    private readonly Dictionary<string, string> _pskChanges = new();

    // s5c-r1 — 연결/테스트 결과 색상 SSOT (사용자 보고 cosmetic). dark theme 에서 잘 보이는 light blue / red.
    // Freeze 로 GC + thread 부담 최소화.
    // s6-r20 --review M6 — WarningBrush 신설 (SoftWarning 의 Success(파랑) 매핑 anomaly 차단). 주황색.
    private static readonly Brush SuccessBrush = MakeFrozen(0x4F, 0xC3, 0xF7);   // light blue
    private static readonly Brush FailureBrush = MakeFrozen(0xEF, 0x53, 0x50);   // light red
    private static readonly Brush WarningBrush = MakeFrozen(0xFF, 0xA7, 0x26);   // amber/orange
    private static Brush MakeFrozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

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

    /// <summary>s6-r20 (D-iii): VLM 모델 후보 — Sonnet 4.6 default + Opus 4.7 (escalation Phase 4) + Haiku 4.5 (cost).</summary>
    private static readonly string[] VlmModelCandidates =
    {
        "claude-sonnet-4-6",
        "claude-opus-4-7",
        "claude-haiku-4-5-20251001",
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

        // **D-S7-3c (s6-r31) — LightHouse Services DataGrid binding**.
        // _llmConfig.LightHouseServices 의 deep clone → working copy. ApplyLlmTab 시점에 _pskChanges 와 함께 commit.
        // Cancel 시 _llmConfig 무변경 (DataGrid 의 add/remove/edit 는 working copy 만 mutate).
        _lhServicesWorking.Clear();
        foreach (var src in _llmConfig.LightHouseServices)
        {
            _lhServicesWorking.Add(new LightHouseServiceConfig
            {
                ServiceId = src.ServiceId,
                DisplayName = src.DisplayName,
                BaseUrl = src.BaseUrl,
                ApiKeyEncrypted = src.ApiKeyEncrypted,
                Active = src.Active,
                // s6-r38 P4-C.2 — Embedding nested 박제. null 보존 (= 미설정 = BM25-only fallback).
                Embedding = src.Embedding is null ? null : new EmbeddingProviderConfig
                {
                    Enabled = src.Embedding.Enabled,
                    BaseUrl = src.Embedding.BaseUrl,
                    Model = src.Embedding.Model,
                    Dimension = src.Embedding.Dimension,
                },
            });
        }
        _pskChanges.Clear();
        LhServicesGrid.ItemsSource = _lhServicesWorking;

        // s6-r38 P4-C.2 — Embedding UI Load. active service 1개 가정 (multi-service per-service UI 진입은 backlog).
        LoadEmbeddingUi();

        // VLM (Phase 2 task D / E, s6-r20)
        // --review M5 정합 (s6-r21): 미지원 provider (openai / ollama 등 향후 확장 후보) 가 JSON 에 박혀있으면
        // ComboBox 에 "unrecognized: <name>" 항목을 동적으로 추가하여 silent downgrade 차단.
        // 사용자가 명시적으로 anthropic/none 으로 변경하지 않는 한 disk 의 값 보존.
        //
        // 자가 검열 mn4 정합 — LoadLlmTab 재호출 시 이전에 추가된 unrecognized 항목 (`Tag is string`) 정리.
        // 정적 XAML 항목 (Tag null) 은 보존.
        for (int i = VlmProviderBox.Items.Count - 1; i >= 0; i--)
        {
            if (VlmProviderBox.Items[i] is ComboBoxItem item && item.Tag is string)
                VlmProviderBox.Items.RemoveAt(i);
        }
        var prov = (_llmConfig.VlmProvider ?? "anthropic").Trim().ToLowerInvariant();
        if (prov == "anthropic")
        {
            VlmProviderBox.SelectedIndex = 0;
        }
        else if (prov == "none" || prov == "off" || string.IsNullOrEmpty(prov))
        {
            VlmProviderBox.SelectedIndex = 1;
        }
        else
        {
            // 미지원 provider — disk 값 보존. ComboBox 끝에 동적 항목 추가 + 선택.
            var preserveItem = new ComboBoxItem { Content = $"unrecognized: {prov}", Tag = prov };
            VlmProviderBox.Items.Add(preserveItem);
            VlmProviderBox.SelectedItem = preserveItem;
        }
        VlmModelBox.Text = _llmConfig.VlmModel ?? "claude-sonnet-4-6";
        VlmDailyTokenCapBox.Text = _llmConfig.VisionCostGate.DailyTokenCap.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdateVlmCostGateStatus();

        // Consent 상태
        UpdateConsentStatus();
    }

    // ─── VLM (Phase 2 task D / E, s6-r20) ────────────────────────────────────

    private void VlmModelCandidates_Click(object sender, RoutedEventArgs e)
        => ShowCandidatesMenu(sender, VlmModelBox, VlmModelCandidates);

    // semantic SSOT (s6-r25, m9): 사용자 명시 강제 reset. day rollover 자동 reset (RolloverIfNeeded — lazy,
    // 매 Consume/Status 호출 시점) 과 다른 path. soft-warning 도달 후 즉시 cap 해제하고 색인 재개 use case.
    // LastResetUtc 를 오늘 날짜로 stamp → 다음 자동 rollover 는 익일 00:00 UTC 이후 첫 호출.
    private void VlmResetCostGate_Click(object sender, RoutedEventArgs e)
    {
        _llmConfig.VisionCostGate.TokensUsedToday = 0;
        _llmConfig.VisionCostGate.LastResetUtc = DateTime.UtcNow.ToString("yyyy-MM-dd");
        _llmConfig.Save();
        LlmConfigChanged = true;
        UpdateVlmCostGateStatus();
    }

    private void UpdateVlmCostGateStatus()
    {
        var gate = _llmConfig.VisionCostGate;
        gate.RolloverIfNeeded();
        var reset = string.IsNullOrEmpty(gate.LastResetUtc) ? "(reset 안 됨)" : gate.LastResetUtc;
        // m11 (s6-r25) — 0 입력 UI 안내 명확화: Disabled (cap ≤ 0) 시 "VLM caption 미생성" 명시.
        var statusLabel = gate.Status switch
        {
            VisionCostGateStatus.Disabled => "⚠ 비활성 (cap ≤ 0 — VLM caption 미생성, 색인 시 image 는 caption NULL 유지)",
            VisionCostGateStatus.Normal => "정상",
            VisionCostGateStatus.SoftWarning => "⚠ 80% 초과",
            VisionCostGateStatus.HardCap => "🔒 hard cap — caption skip 중",
            _ => gate.Status.ToString(),
        };
        VlmCostGateStatusText.Text =
            $"누적 {gate.TokensUsedToday:N0} / {gate.DailyTokenCap:N0} token (reset = {reset} UTC, 상태 = {statusLabel})";
        VlmCostGateStatusText.ClearValue(TextBlock.ForegroundProperty);
        // --review M6 정합 — SoftWarning 은 WarningBrush(주황), HardCap 은 FailureBrush(빨강).
        // 이전 SoftWarning 의 SuccessBrush(파랑) 매핑은 의미 충돌이라 정정. m10 UTC 표기도 동시 적용.
        // m11 (s6-r25): Disabled 도 WarningBrush — 사용자가 0 입력으로 cost gate 의도적 비활성화 명시.
        if (gate.Status == VisionCostGateStatus.SoftWarning) VlmCostGateStatusText.Foreground = WarningBrush;
        else if (gate.Status == VisionCostGateStatus.HardCap) VlmCostGateStatusText.Foreground = FailureBrush;
        else if (gate.Status == VisionCostGateStatus.Disabled) VlmCostGateStatusText.Foreground = WarningBrush;
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

    // ─── LightHouse Services (D-S7-3c, s6-r31) — multi-service DataGrid handlers ─────────

    /// <summary>**D-S7-3c (s6-r31)** — DataGrid 의 "+ Add Service" 버튼. 새 LightHouseServiceConfig (ServiceId 자동 발급, Active=true, DisplayName "새 Service") 를 working copy 에 추가.</summary>
    private void LhAddService_Click(object sender, RoutedEventArgs e)
    {
        var svc = new LightHouseServiceConfig
        {
            ServiceId = Guid.NewGuid().ToString(),
            DisplayName = "새 Service",
            BaseUrl = "https://",
            Active = true,
        };
        _lhServicesWorking.Add(svc);
        LhServicesGrid.Items.Refresh();
    }

    /// <summary>**D-S7-3c (s6-r31)** — DataGrid row 의 "제거" 버튼. Button.Tag = ServiceId. working entry 제거 + _pskChanges 정리.</summary>
    private void LhRemoveService_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string serviceId) return;
        var idx = _lhServicesWorking.ToList().FindIndex(s => s.ServiceId == serviceId);
        if (idx < 0) return;
        _lhServicesWorking.RemoveAt(idx);
        _pskChanges.Remove(serviceId);
        LhServicesGrid.Items.Refresh();
    }

    /// <summary>**D-S7-3c (s6-r31)** — DataGrid row 의 "PSK 설정..." 버튼. 별 dialog 로 평문 PSK 입력 (DataGrid cell 에 평문 비노출). OK 시 _pskChanges 에 임시 저장.</summary>
    private void LhEditPsk_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string serviceId) return;
        var svc = _lhServicesWorking.FirstOrDefault(s => s.ServiceId == serviceId);
        if (svc is null) return;
        var dlg = new PskEditDialog(string.IsNullOrEmpty(svc.DisplayName) ? "(unnamed)" : svc.DisplayName)
        {
            Owner = this,
        };
        if (dlg.ShowDialog() == true && dlg.Result is not null)
        {
            _pskChanges[serviceId] = dlg.Result;
            SetTestResult(LhTestResult,
                string.IsNullOrEmpty(dlg.Result)
                    ? $"ℹ️ [{svc.DisplayName}] PSK 제거 (저장 시 반영)"
                    : $"ℹ️ [{svc.DisplayName}] PSK 변경됨 ({dlg.Result.Length} 문자, 저장 시 반영)",
                success: null);
        }
    }

    /// <summary>
    /// **B5 phase 2 (s6-r64)** — DataGrid row 의 "Cert 선택..." 버튼. Tag = ServiceId. LocalMachine\My X509Store
    /// 의 cert 목록을 Windows 표준 picker (<see cref="X509Certificate2UI.SelectFromCollection"/>) 로 표시 +
    /// 사용자 선택 시 svc.ClientCertThumbprint 박제. mTLS 가 server 측 mode="required" 시 진입 의무 (D-S7-1).
    /// <para/>
    /// **cert 발급/관리 정책** (todo-lighthouse-kb-server.md §3.8.2 박제):
    ///   - 사내 CA 발급 .pfx → `Import-PfxCertificate -CertStoreLocation Cert:\LocalMachine\My` (관리자 필요)
    ///     또는 `certmgr.msc` snap-in 으로 LocalMachine 의 Personal 에 import.
    ///   - thumbprint 는 식별자 (평문 정합) — DPAPI 미적용. 실 private key 는 X509Store 가 OS ACL 로 protect.
    ///   - cert 만료/폐기 시 본 button 으로 재선택 또는 직접 thumbprint 직접 편집 (별 PR — DataGrid TextColumn).
    /// </summary>
    private void LhSelectCert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string serviceId) return;
        var svc = _lhServicesWorking.FirstOrDefault(s => s.ServiceId == serviceId);
        if (svc is null) return;

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        try
        {
            store.Open(OpenFlags.ReadOnly);
            if (store.Certificates.Count == 0)
            {
                MessageBox.Show(this,
                    "LocalMachine\\Personal 에 cert 가 없습니다. 사내 CA 발급 .pfx 를 Import-PfxCertificate 또는 certmgr.msc 로 import 후 재시도.",
                    "Client Cert 선택", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // 만료된 cert 도 표시 — 사용자가 명시적으로 인지하도록. validOnly=false.
            var selected = X509Certificate2UI.SelectFromCollection(
                store.Certificates,
                "Client Certificate 선택",
                $"[{svc.DisplayName}] LightHouse Service 에 mTLS 로 연결할 때 사용할 client cert.",
                X509SelectionFlag.SingleSelection);
            if (selected is null || selected.Count == 0) return;
            var cert = selected[0];
            svc.ClientCertThumbprint = cert.Thumbprint;
            LhServicesGrid.Items.Refresh();
            // **B5 phase 3 (s6-r68)** — CertValidator 호출. 만료/임박/유효 분기 즉시 안내.
            var diag = Promaker.Knowledge.CertValidator.Validate(cert.Thumbprint);
            var msg = Promaker.Knowledge.CertValidator.FormatMessage(diag);
            bool? success = diag.Result switch
            {
                Promaker.Knowledge.CertValidator.ValidationResult.Valid => null,
                Promaker.Knowledge.CertValidator.ValidationResult.ExpiringSoon => null,
                _ => false,
            };
            SetTestResult(LhTestResult,
                $"ℹ️ [{svc.DisplayName}] cert 선택됨 — {msg}",
                success: success);
        }
        catch (Exception ex)
        {
            SetTestResult(LhTestResult,
                $"❌ [{svc.DisplayName}] cert 선택 실패 — {ex.GetType().Name}: {ex.Message}",
                success: false);
        }
    }

    /// <summary>
    /// **B5 phase 3 (s6-r68)** — DataGrid row 의 "검증" 버튼. Button.Tag = ServiceId. 박제된 thumbprint 의 cert validity
    /// (LocalMachine\My / CurrentUser\My 일치 + NotAfter > now + 만료 임박 경고) 진단 + 사용자 표시.
    /// <para/>
    /// 본 button 은 X509Store 선택 dialog (LhSelectCert_Click) 와 분리 — 사용자가 thumbprint 직접 paste 편집 (column 안
    /// TextBox) 후 검증 trigger 또는 기존 thumbprint 의 만료 여부 점검 path.
    /// </summary>
    private void LhValidateCert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string serviceId) return;
        var svc = _lhServicesWorking.FirstOrDefault(s => s.ServiceId == serviceId);
        if (svc is null) return;
        var diag = Promaker.Knowledge.CertValidator.Validate(svc.ClientCertThumbprint);
        var msg = Promaker.Knowledge.CertValidator.FormatMessage(diag);
        bool? success = diag.Result switch
        {
            Promaker.Knowledge.CertValidator.ValidationResult.Valid => true,
            Promaker.Knowledge.CertValidator.ValidationResult.ExpiringSoon => null,
            _ => false,
        };
        var icon = diag.Result == Promaker.Knowledge.CertValidator.ValidationResult.Valid ? "✅"
                 : diag.Result == Promaker.Knowledge.CertValidator.ValidationResult.ExpiringSoon ? "⚠️"
                 : "❌";
        SetTestResult(LhTestResult, $"{icon} [{svc.DisplayName}] {msg}", success);
    }

    /// <summary>
    /// **D-S7-3c (s6-r31)** — DataGrid row 의 "테스트" 버튼. Button.Tag = ServiceId. 본 row 의 BaseUrl + (_pskChanges 의 평문 또는 기존 ciphertext 복호화) 로 LightHouseClient 생성 후 ListCollectionsAsync.
    /// </summary>
    private async void LhTestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string serviceId) return;
        var svc = _lhServicesWorking.FirstOrDefault(s => s.ServiceId == serviceId);
        if (svc is null) return;

        var url = (svc.BaseUrl ?? "").Trim();
        // PSK: _pskChanges 에 우선, 없으면 기존 ciphertext 복호화 (deep clone 한 ApiKeyEncrypted 로부터 _llmConfig 의 GetLightHousePsk 호출).
        string psk;
        if (_pskChanges.TryGetValue(serviceId, out var pendingPlain))
        {
            psk = pendingPlain;
        }
        else
        {
            // 기존 service 에 박제된 ApiKeyEncrypted 가 있으면 복호화 — disk 의 _llmConfig 의 같은 ServiceId entry 사용.
            psk = _llmConfig.GetLightHousePsk(serviceId) ?? "";
        }

        if (string.IsNullOrEmpty(url) || url == "https://")
        {
            SetTestResult(LhTestResult, $"❌ [{svc.DisplayName}] Base URL 이 비어있습니다.", success: false);
            return;
        }
        if (string.IsNullOrEmpty(psk))
        {
            SetTestResult(LhTestResult, $"❌ [{svc.DisplayName}] PSK 가 비어있습니다.", success: false);
            return;
        }
        SetTestResult(LhTestResult, $"[{svc.DisplayName}] 확인 중…", success: null);
        try
        {
            using var client = new LightHouseClient(url, () => psk, Environment.UserName);
            var resp = await client.ListCollectionsAsync().ConfigureAwait(true);
            SetTestResult(LhTestResult, $"✅ [{svc.DisplayName}] 연결 성공 — collection {resp.Collections.Count}건", success: true);
        }
        catch (LightHouseAuthException ex)
        {
            SetTestResult(LhTestResult, $"❌ [{svc.DisplayName}] 인증 실패 — PSK 확인 필요 ({(int)ex.StatusCode})", success: false);
        }
        catch (LightHouseProtocolException ex)
        {
            SetTestResult(LhTestResult, $"❌ [{svc.DisplayName}] protocol 오류 — {ex.Message}", success: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or ArgumentException)
        {
            SetTestResult(LhTestResult, $"❌ [{svc.DisplayName}] 연결 실패 — {ex.Message}", success: false);
        }
    }

    /// <summary>
    /// s5c-r1 — Test 결과 TextBlock 의 색상 + 텍스트 동시 갱신. success=null = 진행 중 (default 색상).
    /// LhTestResult / LlmOllamaTestResult 의 공통 핸들러.
    /// </summary>
    private static void SetTestResult(TextBlock target, string text, bool? success)
    {
        target.Text = text;
        target.ClearValue(TextBlock.ForegroundProperty);
        if (success is true) target.Foreground = SuccessBrush;
        else if (success is false) target.Foreground = FailureBrush;
        // null = default (DynamicResource SecondaryTextBrush) 복원
    }

    private async void LlmTestOllama_Click(object sender, RoutedEventArgs e)
    {
        var url = (LlmOllamaBaseUrlBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(url))
        {
            SetTestResult(LlmOllamaTestResult, "❌ URL 이 비어있습니다.", success: false);
            return;
        }
        SetTestResult(LlmOllamaTestResult, "확인 중…", success: null);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            // Ollama 의 `GET /api/version` 은 인증 불필요 + 가벼운 endpoint.
            var probe = url.TrimEnd('/') + "/api/version";
            using var resp = await http.GetAsync(probe).ConfigureAwait(true);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(true);
                SetTestResult(LlmOllamaTestResult, $"✅ 연결 성공 — {body.Trim()}", success: true);
            }
            else
            {
                SetTestResult(LlmOllamaTestResult, $"❌ 응답 status {(int)resp.StatusCode} {resp.ReasonPhrase}", success: false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            // review (3): OOM/StackOverflow 등 시스템 예외는 흡수하지 않고 즉시 throw → 진단 용이성 보존.
            SetTestResult(LlmOllamaTestResult, $"❌ 연결 실패: {ex.Message}", success: false);
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

        // **D-S7-3c (s6-r31)** — LightHouse Services dirty 비교 (working copy vs _llmConfig.LightHouseServices + _pskChanges).
        var lhDirty = LhWorkingCopyDirty();

        // s6-r20 (D-iii / E-i): VLM provider / model / daily token cap dirty 비교.
        // --review M5 정합: unrecognized 항목의 Content="unrecognized: <prov>" 일 때 Tag 의 원본 값 사용.
        var vlmSelectedItem = VlmProviderBox.SelectedItem as ComboBoxItem;
        var newVlmProvider =
            (vlmSelectedItem?.Tag as string)
            ?? (vlmSelectedItem?.Content?.ToString() ?? "anthropic").Trim();
        var newVlmModel = string.IsNullOrWhiteSpace(VlmModelBox.Text) ? fallback.VlmModel : VlmModelBox.Text.Trim();
        var oldVlmDailyCap = _llmConfig.VisionCostGate.DailyTokenCap;
        var newVlmDailyCap =
            int.TryParse(VlmDailyTokenCapBox.Text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(0, parsed) : oldVlmDailyCap;

        var dirty =
            newAnthropicModel != _llmConfig.AnthropicModel
            || newOpenAiModel    != _llmConfig.OpenAiModel
            || newOllamaModel    != _llmConfig.OllamaModel
            || newOllamaBaseUrl  != _llmConfig.OllamaBaseUrl
            || newAnthropicKey   != (_llmConfig.GetApiKey(ApiProviderFactory.AnthropicKey) ?? "")
            || newOpenAiKey      != (_llmConfig.GetApiKey(ApiProviderFactory.OpenAiKey)    ?? "")
            || lhDirty
            || newVlmProvider    != _llmConfig.VlmProvider
            || newVlmModel       != _llmConfig.VlmModel
            || newVlmDailyCap    != oldVlmDailyCap;

        if (!dirty) return;

        // **D-S7-3c (s6-r31)** — lh dirty 시 uniqueness 검증 먼저. 중복 displayName 있으면 dialog + 저장 차단.
        if (lhDirty && !ValidateLhUniqueness()) return;

        _llmConfig.SetApiKey(ApiProviderFactory.AnthropicKey, newAnthropicKey);
        _llmConfig.SetApiKey(ApiProviderFactory.OpenAiKey,    newOpenAiKey);
        _llmConfig.AnthropicModel = newAnthropicModel;
        _llmConfig.OpenAiModel    = newOpenAiModel;
        _llmConfig.OllamaModel    = newOllamaModel;
        _llmConfig.OllamaBaseUrl  = newOllamaBaseUrl;
        _llmConfig.VlmProvider = newVlmProvider;
        _llmConfig.VlmModel = newVlmModel;
        _llmConfig.VisionCostGate.DailyTokenCap = newVlmDailyCap;

        // **D-S7-3c (s6-r31)** — LightHouse Services commit: working copy → _llmConfig.LightHouseServices 일괄 대체 +
        // _pskChanges 의 평문 PSK 를 per-service entropy 로 암호화.
        if (lhDirty)
        {
            // s6-r38 P4-C.2 — Embedding UI Save 의무. lhDirty 기준 commit 안 묶음 (working copy 의 Embedding 도
            // _lhServicesWorking 안에 deep clone 박제 정합).
            SaveEmbeddingUiToWorking();

            _llmConfig.LightHouseServices.Clear();
            foreach (var src in _lhServicesWorking)
            {
                _llmConfig.LightHouseServices.Add(new LightHouseServiceConfig
                {
                    ServiceId = src.ServiceId,
                    DisplayName = (src.DisplayName ?? "").Trim(),
                    BaseUrl = (src.BaseUrl ?? "").Trim(),
                    ApiKeyEncrypted = src.ApiKeyEncrypted ?? "",
                    Active = src.Active,
                    Embedding = src.Embedding is null ? null : new EmbeddingProviderConfig
                    {
                        Enabled = src.Embedding.Enabled,
                        BaseUrl = (src.Embedding.BaseUrl ?? "").Trim(),
                        Model = (src.Embedding.Model ?? "").Trim(),
                        Dimension = src.Embedding.Dimension,
                    },
                });
            }
            foreach (var (sid, plain) in _pskChanges)
            {
                // _llmConfig.LightHouseServices 안에 sid 가 없으면 (remove 후 PSK 변경 이전) skip.
                if (_llmConfig.LightHouseServices.Any(s => s.ServiceId == sid))
                {
                    _llmConfig.SetLightHousePsk(sid, plain);
                }
            }
            _pskChanges.Clear();
        }

        _llmConfig.Save();
        LlmConfigChanged = true;
        // Phase S5c → D-S7-3c — LightHouse 변경 시 process holder 의 모든 entry 무효화 → 다음 chat 진입 시 N 개 재생성.
        if (lhDirty) LightHouseClientHolder.Invalidate();
    }

    /// <summary>
    /// **D-S7-3c (s6-r31)** — DataGrid working copy 와 _llmConfig.LightHouseServices 의 deep compare + _pskChanges 비어있지 않으면 dirty.
    /// <para/>
    /// **s6-r38 P4-C.2** — Embedding UI 변경도 dirty 박제. dirty check 진입 시 SaveEmbeddingUiToWorking 우선 호출 →
    /// working copy 의 Embedding 박제 후 deep compare 안 Embedding 도 박제.
    /// </summary>
    private bool LhWorkingCopyDirty()
    {
        // Embedding UI 변경 박제 위해 우선 working copy 의 active service 의 Embedding 갱신.
        SaveEmbeddingUiToWorking();

        if (_pskChanges.Count > 0) return true;
        var working = _lhServicesWorking;
        var current = _llmConfig.LightHouseServices;
        if (working.Count != current.Count) return true;
        for (int i = 0; i < working.Count; i++)
        {
            var w = working[i];
            var c = current[i];
            if (w.ServiceId != c.ServiceId
                || (w.DisplayName ?? "").Trim() != (c.DisplayName ?? "").Trim()
                || (w.BaseUrl ?? "").Trim() != (c.BaseUrl ?? "").Trim()
                || w.Active != c.Active
                || (w.ApiKeyEncrypted ?? "") != (c.ApiKeyEncrypted ?? ""))
                return true;
            if (!EmbeddingConfigEquals(w.Embedding, c.Embedding))
                return true;
        }
        return false;
    }

    /// <summary>
    /// **s6-r38 P4-C.2** — EmbeddingProviderConfig deep equality. 둘 다 null 시 equal, 한쪽만 null 시 unequal,
    /// 둘 다 non-null 시 4 필드 모두 일치 의무. BaseUrl/Model 은 양쪽 모두 Trim 후 비교 (s6-r40 자가 검열 Major-1
    /// 정정 — legacy disk JSON 의 untrimmed 값에 대한 false-negative dirty 회피).
    /// </summary>
    private static bool EmbeddingConfigEquals(EmbeddingProviderConfig? a, EmbeddingProviderConfig? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.Enabled == b.Enabled
            && (a.BaseUrl ?? "").Trim() == (b.BaseUrl ?? "").Trim()
            && (a.Model ?? "").Trim() == (b.Model ?? "").Trim()
            && a.Dimension == b.Dimension;
    }

    /// <summary>
    /// **D-S7-3c (s6-r31)** — Save 시점 displayName uniqueness 검증. <see cref="LightHouseServiceValidator"/> SSOT
    /// 호출 — drift 회피 (Promaker.Tests 와 동일 로직 검증).
    /// </summary>
    private bool ValidateLhUniqueness()
    {
        var (ok, reason) = LightHouseServiceValidator.ValidateDisplayNames(_lhServicesWorking);
        if (ok) return true;
        var msg = reason == "(빈 값)"
            ? "LightHouse Service 의 Display Name 이 비어 있습니다 — 이름을 입력 후 다시 저장하세요."
            : $"LightHouse Service 의 Display Name 중복: \"{reason}\" — 수정 후 다시 저장하세요.";
        MessageBox.Show(this, msg, "displayName 검증", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
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

    // ── s6-r38 P4-C.2 — Embedding UI helpers ───────────────────────────────────────────────────

    /// <summary>
    /// 활성 LightHouseService (working copy) 의 Embedding 박제를 UI 에 채움. active service 0건 / 다수 시점은
    /// 첫 active service 박제 (UI 단순화, multi-service per-service UI 진입은 backlog).
    /// </summary>
    private void LoadEmbeddingUi()
    {
        var activeService = _lhServicesWorking.FirstOrDefault(s => s.Active);
        var cfg = activeService?.Embedding;
        if (cfg is null)
        {
            EmbeddingEnabledCheck.IsChecked = false;
            EmbeddingBaseUrlBox.Text = "http://localhost:11434";
            EmbeddingModelBox.Text = "bge-m3";
            EmbeddingDimensionBox.Text = "1024";
        }
        else
        {
            EmbeddingEnabledCheck.IsChecked = cfg.Enabled;
            EmbeddingBaseUrlBox.Text = cfg.BaseUrl;
            EmbeddingModelBox.Text = cfg.Model;
            EmbeddingDimensionBox.Text = cfg.Dimension.ToString();
        }
    }

    /// <summary>
    /// UI 의 Embedding 박제를 활성 LightHouseService (working copy) 의 Embedding 에 commit. active service 0건
    /// 시점은 no-op (Embedding config 박제 위치 없음). dimension parse 실패 시 1024 fallback.
    /// </summary>
    private void SaveEmbeddingUiToWorking()
    {
        var activeService = _lhServicesWorking.FirstOrDefault(s => s.Active);
        if (activeService is null)
        {
            return; // active service 없음 — Embedding 박제 위치 없음.
        }
        if (!int.TryParse(EmbeddingDimensionBox.Text, out var dim) || dim <= 0)
        {
            dim = 1024; // parse 실패 시 default fallback (UI 강조 안 함, save 후 다음 Load 시 1024 표시).
        }
        activeService.Embedding = new EmbeddingProviderConfig
        {
            Enabled = EmbeddingEnabledCheck.IsChecked == true,
            BaseUrl = (EmbeddingBaseUrlBox.Text ?? "").Trim(),
            Model = (EmbeddingModelBox.Text ?? "").Trim(),
            Dimension = dim,
        };
    }
}

/// <summary>
/// **B5 phase 2 (s6-r64)** — cert thumbprint 40-hex 의 마지막 8 자리만 표시 ("…1A2B3C4D" 형식).
/// 빈/null thumbprint 시 "(없음)" 표시. OneWay 전용.
/// </summary>
public sealed class ThumbprintShortConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.IsNullOrWhiteSpace(s)) return "(없음)";
        var t = s.Trim();
        if (t.Length <= 8) return t;
        return "…" + t.Substring(t.Length - 8);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException(nameof(ThumbprintShortConverter) + " 는 OneWay 전용.");
}
