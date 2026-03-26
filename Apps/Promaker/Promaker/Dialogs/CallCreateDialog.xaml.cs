using System.Windows;
using System.Windows.Controls;
using Ds2.Store;
using Ds2.Editor;

namespace Promaker.Dialogs;

public enum CallCreateMode { CallReplication, ApiCallReplication, ApiDefPicker }

public partial class CallCreateDialog : Window
{
    private readonly Func<string, IReadOnlyList<ApiDefMatch>> _findApiDefsByName;

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

    public CallCreateDialog(Func<string, IReadOnlyList<ApiDefMatch>> findApiDefsByName)
    {
        _findApiDefsByName = findApiDefsByName;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            BasicAliasTextBox.Focus();
            RefreshApiDefList();
        };
    }

    // ─── 프리셋 ───
    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BasicApiNameTextBox is null) return;

        if (PresetComboBox.SelectedItem is ComboBoxItem item)
        {
            var tag = item.Tag?.ToString();
            if (tag == "USER")
            {
                BasicApiNameTextBox.IsEnabled = true;
                BasicApiNameTextBox.Text = "";
                BasicApiNameTextBox.Focus();
            }
            else
            {
                BasicApiNameTextBox.IsEnabled = false;
                BasicApiNameTextBox.Text = tag ?? "";
            }
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
        var apiDefText = apiNameBox.Text.Trim();

        if (string.IsNullOrEmpty(alias))
        {
            DialogHelpers.Warn("DevicesAlias를 입력해주세요."); return null;
        }
        if (alias.Contains('.'))
        {
            DialogHelpers.Warn("DevicesAlias에는 '.'을 사용할 수 없습니다."); return null;
        }
        if (string.IsNullOrEmpty(apiDefText))
        {
            DialogHelpers.Warn("ApiName을 입력해주세요."); return null;
        }

        var apiNames = apiDefText.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        if (apiNames.Count == 0)
        {
            DialogHelpers.Warn("ApiName을 입력해주세요."); return null;
        }
        if (apiNames.Any(n => n.Contains('.')))
        {
            DialogHelpers.Warn("ApiName에는 '.'을 사용할 수 없습니다."); return null;
        }

        return (alias, apiNames);
    }

    private void CommitBasicTab()
    {
        var result = ValidateAliasAndApiNames(BasicAliasTextBox, BasicApiNameTextBox);
        if (result is null) return;

        var (alias, apiNames) = result.Value;

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

    private void CommitCallReplication(string alias, List<string> apiNames)
    {
        if (!int.TryParse(CallCountTextBox.Text.Trim(), out int count) || count < 1 || count > 100)
        {
            DialogHelpers.Warn("개수는 1~100 사이의 숫자를 입력해주세요."); return;
        }

        var deviceAliases = count == 1
            ? [alias]
            : Enumerable.Range(1, count).Select(i => $"{alias}{i}").ToList();

        var names = new List<string>();
        foreach (var dev in deviceAliases)
            foreach (var apiName in apiNames)
                names.Add($"{dev}.{apiName}");

        Mode = CallCreateMode.CallReplication;
        CallNames = names;
        DialogResult = true;
    }

    private void CommitApiCallReplication(string alias, List<string> apiNames)
    {
        if (!int.TryParse(ApiCallCountTextBox.Text.Trim(), out int count) || count < 1 || count > 100)
        {
            DialogHelpers.Warn("개수는 1~100 사이의 숫자를 입력해주세요."); return;
        }

        // ApiCall 복제: apiNames 각각에 대해 1개 Call 생성
        // 여러 ApiName이면 여러 Call이 생기되, 각 Call 안에 count개 ApiCall
        var deviceAliases = count == 1
            ? [alias]
            : Enumerable.Range(1, count).Select(i => $"{alias}{i}").ToList();

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
        if (string.IsNullOrEmpty(alias))
        {
            DialogHelpers.Warn("DevicesAlias를 입력해주세요."); return;
        }
        if (alias.Contains('.'))
        {
            DialogHelpers.Warn("DevicesAlias에는 '.'을 사용할 수 없습니다."); return;
        }

        var apiName = AdvApiNameFilterBox.Text.Trim();
        if (string.IsNullOrEmpty(apiName))
        {
            DialogHelpers.Warn("ApiName을 입력해주세요."); return;
        }
        if (apiName.Contains('.'))
        {
            DialogHelpers.Warn("ApiName에는 '.'을 사용할 수 없습니다."); return;
        }

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
