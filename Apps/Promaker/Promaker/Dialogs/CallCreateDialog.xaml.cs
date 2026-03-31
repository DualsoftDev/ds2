using System.Windows;
using System.Windows.Controls;
using Microsoft.FSharp.Collections;
using Ds2.Core;
using Ds2.Store;
using Ds2.Store.DsQuery;
using Ds2.Editor;

namespace Promaker.Dialogs;

public enum CallCreateMode { CallReplication, ApiCallReplication, ApiDefPicker }

public partial class CallCreateDialog : Window
{
    private readonly Func<string, IReadOnlyList<ApiDefMatch>> _findApiDefsByName;
    private readonly ProjectProperties? _projectProperties;

    // ─── 공통 출력 ───
    public CallCreateMode Mode { get; private set; }

    // ─── 기본 탭: Call 복제 출력 ───
    public IReadOnlyList<string> CallNames { get; private set; } = [];

    // ─── 기본 탭: ApiCall 복제 출력 ───
    public string CallDevicesAlias { get; private set; } = string.Empty;
    public string CallApiName { get; private set; } = string.Empty;
    public IReadOnlyList<string> DeviceAliases { get; private set; } = [];

    // ─── 고급 탭: ApiDef 연결 출력 ───
    public IReadOnlyList<ApiDefMatch> SelectedApiDefs { get; private set; } = [];
    public string DevicesAlias { get; private set; } = string.Empty;
    public string ApiName { get; private set; } = string.Empty;

    // ─── SystemType 출력 ───
    public string? SelectedSystemType { get; private set; } = null;

    public CallCreateDialog(Func<string, IReadOnlyList<ApiDefMatch>> findApiDefsByName, ProjectProperties? projectProperties = null)
    {
        _findApiDefsByName = findApiDefsByName;
        _projectProperties = projectProperties;
        InitializeComponent();
        LoadPresets();
        Loaded += (_, _) =>
        {
            BasicAliasTextBox.Focus();
            RefreshApiDefList();
        };
    }

    private void LoadPresets()
    {
        PresetComboBox.Items.Clear();

        if (_projectProperties != null)
        {
            var presets = ProjectPropertiesHelper.getPresetSystemTypes(_projectProperties);
            foreach (var preset in presets)
            {
                var parts = preset.Split(':');
                if (parts.Length == 2)
                {
                    var sysType  = parts[0];  // "ADV;RET" — ApiName 템플릿
                    var modelType = parts[1]; // "Unit"    — SystemType으로 저장될 값
                    PresetComboBox.Items.Add(new ComboBoxItem
                    {
                        Content = modelType,
                        Tag = $"{sysType}|{modelType}"
                    });
                }
            }
        }

        // 프리셋이 없으면 기본값 추가 (DevicePresets.Entries 단일 정의 참조)
        if (PresetComboBox.Items.Count == 0)
        {
            foreach (var (modelType, sysType) in Ds2.Store.DevicePresets.Entries)
            {
                if (string.IsNullOrEmpty(sysType)) continue;
                PresetComboBox.Items.Add(new ComboBoxItem
                {
                    Content = modelType,
                    Tag = $"{sysType}|{modelType}"
                });
            }
        }

        // 직접 입력 항목 추가 (Tag = null → Dummy SystemType)
        PresetComboBox.Items.Add(new ComboBoxItem
        {
            Content = "직접 입력",
            Tag = null
        });

        // 첫 번째 항목 선택
        if (PresetComboBox.Items.Count > 0)
            PresetComboBox.SelectedIndex = 0;
    }

    private bool IsCustomInputSelected() =>
        PresetComboBox.SelectedItem is ComboBoxItem { Tag: null };

    // ─── SystemType 선택 ───
    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BasicApiNameTextBox is null) return;

        if (IsCustomInputSelected())
        {
            BasicApiNameTextBox.IsEnabled = true;
            BasicApiNameTextBox.Text = "";
            BasicApiNameTextBox.Focus();
        }
        else if (PresetComboBox.SelectedItem is ComboBoxItem item)
        {
            var sysType = ParseSysType(item.Tag?.ToString());
            BasicApiNameTextBox.IsEnabled = false;
            BasicApiNameTextBox.Text = sysType ?? "";
        }
    }

    // ─── 복제 모드 라디오 ───
    private void OnReplicationRadioChanged(object sender, RoutedEventArgs e)
    {
        if (CallReplicationPanel is null || ApiCallReplicationPanel is null) return;

        bool isCallMode = RadioCallReplication.IsChecked == true;
        CallReplicationPanel.IsEnabled = isCallMode;
        ApiCallReplicationPanel.IsEnabled = !isCallMode;
    }

    // ─── 고급 탭: ApiDef 검색 ───
    private void OnApiNameFilterChanged(object sender, TextChangedEventArgs e)
    {
        RefreshApiDefList();
    }

    private void RefreshApiDefList()
    {
        var apiNameFilter = AdvApiNameFilterBox?.Text?.Trim() ?? string.Empty;
        var matches = _findApiDefsByName(apiNameFilter);
        ApiDefListBox.ItemsSource = matches;
        if (matches.Count > 0)
            ApiDefListBox.SelectedIndex = 0;
    }

    // ─── 추가 버튼 ───
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (ModeTabControl.SelectedIndex == 0)
            CommitBasicTab();
        else
            CommitAdvancedTab();
    }

    private (string alias, List<string> apiNames)? ValidateAliasAndApiNames(TextBox aliasBox, TextBox apiNameBox)
    {
        var alias = aliasBox.Text.Trim();
        var aliasResult = InputValidation.validateDevicesAlias(alias);
        if (aliasResult.IsEmptyAlias) { DialogHelpers.Warn("DevicesAlias를 입력해주세요."); return null; }
        if (aliasResult.IsAliasDotForbidden) { DialogHelpers.Warn("DevicesAlias에는 '.'을 사용할 수 없습니다."); return null; }

        var apiResult = InputValidation.validateApiNames(apiNameBox.Text);
        if (apiResult.IsEmptyInput || apiResult.IsEmptyAfterParse) { DialogHelpers.Warn("ApiName을 입력해주세요."); return null; }
        if (apiResult.IsApiNameDotForbidden) { DialogHelpers.Warn("ApiName에는 '.'을 사용할 수 없습니다."); return null; }

        var apiNames = ((InputValidation.ApiNameValidationResult.Valid)apiResult).Item.ToList();
        return (alias, apiNames);
    }

    private void CommitBasicTab()
    {
        var result = ValidateAliasAndApiNames(BasicAliasTextBox, BasicApiNameTextBox);
        if (result is null) return;

        var (alias, apiNames) = result.Value;

        // SystemType 가져오기 - 프리셋에 따라 고급 탭에서 설정한 값 사용
        SelectedSystemType = GetSystemTypeForCurrentPreset();

        // 추가 기능이 펼쳐진 경우 복수 생성
        if (AdvancedExpander.IsExpanded)
        {
            if (RadioCallReplication.IsChecked == true)
                CommitCallReplication(alias, apiNames);
            else
                CommitApiCallReplication(alias, apiNames);
            return;
        }

        // 단일 생성
        var names = apiNames.Select(n => $"{alias}.{n}").ToList();
        Mode = CallCreateMode.CallReplication;
        CallNames = names;
        DialogResult = true;
    }

    // Tag 형식: "ADV;RET|Unit"  (sysType|modelType)
    private static string? ParseSysType(string? tag) =>
        tag?.Split('|') is [var s, ..] ? s : tag;

    private static string? ParseModelType(string? tag) =>
        tag?.Split('|') is [_, var m] ? m : null;

    private string? GetSystemTypeForCurrentPreset()
    {
        if (PresetComboBox.SelectedItem is not ComboBoxItem item)
            return null;
        // 직접 입력 선택 시 → Dummy (Tag=null)
        if (IsCustomInputSelected()) return "Dummy";
        // ModelType("Unit")을 SystemType으로 저장 — inferModelType이 직접 인식
        return ParseModelType(item.Tag?.ToString());
    }

    private void CommitCallReplication(string alias, List<string> apiNames)
    {
        if (!int.TryParse(CallCountTextBox.Text.Trim(), out int count) || count < 1 || count > 100)
        {
            DialogHelpers.Warn("개수는 1~100 사이의 숫자를 입력해주세요."); return;
        }

        var deviceAliases = Device.generateDeviceAliases(alias, count);
        var names = Device.generateCallNames(
            ListModule.OfSeq(deviceAliases),
            ListModule.OfSeq(apiNames));

        Mode = CallCreateMode.CallReplication;
        CallNames = names.ToList();
        DialogResult = true;
    }

    private void CommitApiCallReplication(string alias, List<string> apiNames)
    {
        if (!int.TryParse(ApiCallCountTextBox.Text.Trim(), out int count) || count < 1 || count > 100)
        {
            DialogHelpers.Warn("개수는 1~100 사이의 숫자를 입력해주세요."); return;
        }

        var deviceAliases = Device.generateDeviceAliases(alias, count);

        // 편의상 첫 번째 apiName 기준. 여러 apiName → 여러 Call.
        // 각 Call별로 DeviceAliases를 동일하게 사용.
        if (apiNames.Count == 1)
        {
            Mode = CallCreateMode.ApiCallReplication;
            CallDevicesAlias = alias;
            CallApiName = apiNames[0];
            DeviceAliases = deviceAliases;
            DialogResult = true;
        }
        else
        {
            // 여러 ApiName + ApiCall 복제: apiName별로 별도 Call
            // CallNames로 모든 조합을 전달하되, DeviceAliases도 함께 전달
            Mode = CallCreateMode.ApiCallReplication;
            CallDevicesAlias = alias;
            CallApiName = apiNames[0]; // 첫 번째 (multi는 별도 처리)
            DeviceAliases = deviceAliases;

            // multi-apiName은 CallNames에도 기록 (NodeCommands에서 분기)
            var names = apiNames.Select(n => $"{alias}.{n}").ToList();
            CallNames = names;
            DialogResult = true;
        }
    }

    private void CommitAdvancedTab()
    {
        var alias = AdvAliasFilterBox.Text.Trim();
        var aliasResult = InputValidation.validateDevicesAlias(alias);
        if (aliasResult.IsEmptyAlias) { DialogHelpers.Warn("DevicesAlias를 입력해주세요."); return; }
        if (aliasResult.IsAliasDotForbidden) { DialogHelpers.Warn("DevicesAlias에는 '.'을 사용할 수 없습니다."); return; }

        var apiName = AdvApiNameFilterBox.Text.Trim();
        var apiResult = InputValidation.validateApiNames(apiName);
        if (apiResult.IsEmptyInput || apiResult.IsEmptyAfterParse) { DialogHelpers.Warn("ApiName을 입력해주세요."); return; }
        if (apiResult.IsApiNameDotForbidden) { DialogHelpers.Warn("ApiName에는 '.'을 사용할 수 없습니다."); return; }

        var selected = ApiDefListBox.SelectedItems.OfType<ApiDefMatch>().ToList();
        if (selected.Count == 0)
        {
            DialogHelpers.Warn("ApiDef를 선택해주세요.\n\nDevice System이 없으면 '기본' 탭에서 먼저 생성해주세요."); return;
        }

        Mode = CallCreateMode.ApiDefPicker;
        SelectedApiDefs = selected;
        DevicesAlias = alias;
        ApiName = apiName;
        CallNames = [$"{alias}.{apiName}"];
        DialogResult = true;
    }
}
