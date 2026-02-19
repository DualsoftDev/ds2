using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ds2.Core;
using Ds2.UI.Core;

namespace Ds2.UI.Frontend.Dialogs;

public partial class CallCreateDialog : Window
{
    private readonly DsStore _store;

    // Device 모드 출력
    public bool IsDeviceMode { get; private set; }
    public IReadOnlyList<string> CallNames { get; private set; } = [];

    // ApiDef 연결 모드 출력
    public IReadOnlyList<ApiDefMatch> SelectedApiDefs { get; private set; } = [];
    public string DevicesAlias { get; private set; } = string.Empty;
    public string ApiName { get; private set; } = string.Empty;

    public CallCreateDialog(DsStore store)
    {
        _store = store;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            DeviceAliasTextBox.Focus();
            RefreshApiDefList();
        };
    }

    private void OnRadioChanged(object sender, RoutedEventArgs e)
    {
        if (DeviceModePanel is null || ApiDefPickerPanel is null) return;

        bool deviceMode = RadioDeviceMode.IsChecked == true;
        DeviceModePanel.Visibility = deviceMode ? Visibility.Visible : Visibility.Collapsed;
        ApiDefPickerPanel.Visibility = deviceMode ? Visibility.Collapsed : Visibility.Visible;

        if (deviceMode)
            DeviceAliasTextBox.Focus();
        else
            ApiNameFilterBox.Focus();
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceApiNameTextBox is null) return;

        if (PresetComboBox.SelectedItem is ComboBoxItem item)
        {
            var tag = item.Tag?.ToString();
            if (tag == "USER")
            {
                DeviceApiNameTextBox.IsEnabled = true;
                DeviceApiNameTextBox.Text = "";
                DeviceApiNameTextBox.Focus();
            }
            else
            {
                DeviceApiNameTextBox.IsEnabled = false;
                DeviceApiNameTextBox.Text = tag ?? "";
            }
        }
    }

    private void OnCountPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !int.TryParse(e.Text, out _);
    }

    private void OnApiNameFilterChanged(object sender, TextChangedEventArgs e)
    {
        RefreshApiDefList();
    }

    private void RefreshApiDefList()
    {
        var apiNameFilter = ApiNameFilterBox?.Text?.Trim() ?? string.Empty;
        var matches = EntityHierarchyQueries.findApiDefsByName(_store, apiNameFilter);
        ApiDefListBox.ItemsSource = matches;
        if (matches.Length > 0)
            ApiDefListBox.SelectedIndex = 0;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (RadioDeviceMode.IsChecked == true)
            CommitDeviceMode();
        else
            CommitApiDefPickerMode();
    }

    private void CommitDeviceMode()
    {
        var alias = DeviceAliasTextBox.Text.Trim();
        var apiDefText = DeviceApiNameTextBox.Text.Trim();

        if (string.IsNullOrEmpty(alias))
        {
            Warn("DevicesAlias를 입력해주세요."); return;
        }
        if (alias.Contains('.'))
        {
            Warn("DevicesAlias에는 '.'을 사용할 수 없습니다."); return;
        }
        if (string.IsNullOrEmpty(apiDefText))
        {
            Warn("ApiName을 입력해주세요."); return;
        }

        var apiNames = apiDefText.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        if (apiNames.Count == 0)
        {
            Warn("ApiName을 입력해주세요."); return;
        }
        if (apiNames.Any(n => n.Contains('.')))
        {
            Warn("ApiName에는 '.'을 사용할 수 없습니다."); return;
        }
        if (!int.TryParse(DeviceCountTextBox.Text.Trim(), out int count) || count < 1 || count > 100)
        {
            Warn("개수는 1~100 사이의 숫자를 입력해주세요."); return;
        }

        var deviceAliases = count == 1
            ? [alias]
            : Enumerable.Range(1, count).Select(i => $"{alias}_{i}").ToList();

        var names = new List<string>();
        foreach (var dev in deviceAliases)
            foreach (var apiName in apiNames)
                names.Add($"{dev}.{apiName}");

        IsDeviceMode = true;
        CallNames = names;
        DialogResult = true;
    }

    private void CommitApiDefPickerMode()
    {
        var alias = AliasFilterBox.Text.Trim();
        if (string.IsNullOrEmpty(alias))
        {
            Warn("DevicesAlias를 입력해주세요."); return;
        }
        if (alias.Contains('.'))
        {
            Warn("DevicesAlias에는 '.'을 사용할 수 없습니다."); return;
        }

        var apiName = ApiNameFilterBox.Text.Trim();
        if (string.IsNullOrEmpty(apiName))
        {
            Warn("ApiName을 입력해주세요."); return;
        }
        if (apiName.Contains('.'))
        {
            Warn("ApiName에는 '.'을 사용할 수 없습니다."); return;
        }

        var selected = ApiDefListBox.SelectedItems.OfType<ApiDefMatch>().ToList();
        if (selected.Count == 0)
        {
            Warn("ApiDef를 선택해주세요.\n\nDevice System이 없으면 '프리셋 모드'로 먼저 생성해주세요."); return;
        }

        IsDeviceMode = false;
        SelectedApiDefs = selected;
        DevicesAlias = alias;
        ApiName = apiName;
        CallNames = [$"{alias}.{apiName}"];
        DialogResult = true;
    }

    private void Warn(string message) =>
        MessageBox.Show(message, "입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
}
