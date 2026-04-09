using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Ds2.Core;
using Ds2.Editor;
using Promaker.Presentation;

namespace Promaker.Dialogs;

public partial class ProjectPropertiesDialog : Window
{
    private static readonly string PresetFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "systemTypePreset", "systemTypePreset.json");

    private static readonly string SplitDeviceAasxSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "splitDeviceAasx.txt");

    private static readonly string IriPrefixSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "iriPrefix.txt");

    private const string DefaultIriPrefix = "https://dualsoft.com/";
    private readonly string _initialProjectName;

    public string? ResultProjectName { get; private set; }
    public string ResultAuthor { get; private set; } = "";
    public DateTimeOffset ResultDateTime { get; private set; } = DateTimeOffset.Now;
    public string ResultVersion { get; private set; } = "1.0.0";
    public string ResultIriPrefix { get; private set; } = "https://dualsoft.com/";  // 앱 설정으로 저장됨
    public bool ResultSplitDeviceAasx { get; private set; }

    // 프리셋 SystemType 매핑 결과 (배열)
    public string[] ResultPresetSystemTypes { get; private set; } = Array.Empty<string>();

    public ProjectPropertiesDialog(string projectName, Project project)
    {
        InitializeComponent();

        _initialProjectName = string.IsNullOrWhiteSpace(projectName) ? "NewProject" : projectName.Trim();
        ProjectNameBox.Text = _initialProjectName;
        AuthorBox.Text        = project.Author ?? "";
        VersionBox.Text       = project.Version ?? "";
        DescriptionBox.Text   = "";  // Description removed from Project

        // 앱 설정에서 로드
        IriPrefixBox.Text = AppSettingStore.LoadStringOrDefault(IriPrefixSettingsPath, DefaultIriPrefix);
        SplitDeviceAasxBox.IsChecked = AppSettingStore.LoadBoolOrDefault(SplitDeviceAasxSettingsPath, false);

        // 프리셋 SystemType 매핑 로드
        LoadPresetMappings();

        // 기본 값 설정
        PresetTextBox.Text = Ds2.Core.Store.DevicePresets.Entries[0].Item2;

        Loaded += (_, _) => ProjectNameBox.Focus();
    }

    private void LoadPresetMappings()
    {
        PresetMappingListBox.Items.Clear();

        // 파일에서 로드 → 없으면 기본값 사용
        var filePresets = LoadPresetsFromFile();
        var source = filePresets.Length > 0
            ? filePresets
            : Ds2.Core.Store.DevicePresets.DefaultMappingStrings;

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
        // 프리셋 가져오기
        var presetName = PresetTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(presetName))
        {
            DialogHelpers.Warn("프리셋을 입력해주세요.");
            return;
        }

        // SystemType 가져오기
        var systemType = SystemTypeTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(systemType))
        {
            DialogHelpers.Warn("SystemType을 입력해주세요.");
            return;
        }

        // 매핑 문자열 생성
        var mapping = $"{presetName}:{systemType}";

        // 기존에 같은 프리셋이 있으면 제거
        var existingItems = PresetMappingListBox.Items
            .Cast<string>()
            .Where(item => item.StartsWith(presetName + ":"))
            .ToList();

        foreach (var item in existingItems)
        {
            PresetMappingListBox.Items.Remove(item);
        }

        // 새 매핑 추가
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
        if (selectedIndex <= 0)
        {
            // 선택 안 됨 또는 이미 맨 위
            return;
        }

        var item = PresetMappingListBox.Items[selectedIndex];
        PresetMappingListBox.Items.RemoveAt(selectedIndex);
        PresetMappingListBox.Items.Insert(selectedIndex - 1, item);
        PresetMappingListBox.SelectedIndex = selectedIndex - 1;
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        var selectedIndex = PresetMappingListBox.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= PresetMappingListBox.Items.Count - 1)
        {
            // 선택 안 됨 또는 이미 맨 아래
            return;
        }

        var item = PresetMappingListBox.Items[selectedIndex];
        PresetMappingListBox.Items.RemoveAt(selectedIndex);
        PresetMappingListBox.Items.Insert(selectedIndex + 1, item);
        PresetMappingListBox.SelectedIndex = selectedIndex + 1;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultProjectName = string.IsNullOrWhiteSpace(ProjectNameBox.Text) ? _initialProjectName : ProjectNameBox.Text.Trim();

        ResultAuthor = AuthorBox.Text?.Trim() ?? "";
        ResultVersion = string.IsNullOrWhiteSpace(VersionBox.Text) ? "1.0.0" : VersionBox.Text.Trim();
        ResultDateTime = DateTimeOffset.Now;  // Not editable in this dialog, use current time

        // 앱 설정으로 저장
        ResultIriPrefix = string.IsNullOrWhiteSpace(IriPrefixBox.Text) ? DefaultIriPrefix : IriPrefixBox.Text.Trim();
        AppSettingStore.SaveString(IriPrefixSettingsPath, ResultIriPrefix);

        ResultSplitDeviceAasx = SplitDeviceAasxBox.IsChecked == true;
        AppSettingStore.SaveBool(SplitDeviceAasxSettingsPath, ResultSplitDeviceAasx);

        // 프리셋 SystemType 매핑 저장 (ListBox에서 가져오기)
        ResultPresetSystemTypes = PresetMappingListBox.Items
            .Cast<string>()
            .ToArray();

        // 글로벌 설정 파일에 저장
        SavePresetsToFile(ResultPresetSystemTypes);

        DialogResult = true;
    }
}
