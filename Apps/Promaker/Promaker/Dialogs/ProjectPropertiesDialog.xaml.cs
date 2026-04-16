using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.Win32;
using Promaker.Presentation;
using Promaker.Services;

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

    private static readonly string CreateDefaultEntitiesSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "createDefaultEntitiesOnEmptyAasx.txt");

    private const string DefaultIriPrefix = "https://dualsoft.com/";
    private readonly string _initialProjectName;
    private readonly DsStore _store;

    public string? ResultProjectName { get; private set; }
    public string ResultAuthor { get; private set; } = "";
    public DateTimeOffset ResultDateTime { get; private set; } = DateTimeOffset.Now;
    public string ResultVersion { get; private set; } = "1.0.0";
    public string ResultIriPrefix { get; private set; } = "https://dualsoft.com/";
    public bool ResultSplitDeviceAasx { get; private set; }
    public bool ResultCreateDefaultEntities { get; private set; }

    // 프리셋 SystemType 매핑 결과 (배열)
    public string[] ResultPresetSystemTypes { get; private set; } = Array.Empty<string>();

    public ProjectPropertiesDialog(string projectName, DsStore store)
    {
        InitializeComponent();

        _store              = store;
        _initialProjectName = string.IsNullOrWhiteSpace(projectName) ? "NewProject" : projectName.Trim();
        ProjectNameBox.Text = _initialProjectName;
        var projects = Queries.allProjects(store);
        var project  = projects.IsEmpty ? null : projects.Head;
        AuthorBox.Text        = project?.Author ?? "";
        VersionBox.Text       = project?.Version ?? "";
        DescriptionBox.Text   = "";

        // 앱 설정에서 로드
        IriPrefixBox.Text = AppSettingStore.LoadStringOrDefault(IriPrefixSettingsPath, DefaultIriPrefix);
        SplitDeviceAasxBox.IsChecked = AppSettingStore.LoadBoolOrDefault(SplitDeviceAasxSettingsPath, false);
        CreateDefaultEntitiesBox.IsChecked = AppSettingStore.LoadBoolOrDefault(CreateDefaultEntitiesSettingsPath, false);

        // PLC 설정 로드
        var plcCfg = PlcConfig.Settings;
        PlcIoTemplateDirBox.Text   = plcCfg.IoTemplateDirPath;
        PlcXgiTemplatePathBox.Text = plcCfg.XgiTemplatePath;
        PlcXg5000ExePathBox.Text   = plcCfg.Xg5000ExePath;

        // 프리셋 SystemType 매핑 로드 (FBTagMap 목록이 이에 의존함)
        LoadPresetMappings();

        // FBTagMap 디바이스 타입 목록 로드
        LoadFBTagMapDeviceTypes();

        // 기본 값 설정
        PresetTextBox.Text = Ds2.Core.Store.DevicePresets.Entries[0].Item2;

        Loaded += (_, _) => ProjectNameBox.Focus();
    }

    // ── FBTagMap 디바이스 타입 목록 ──────────────────────────────────────────

    private void LoadFBTagMapDeviceTypes()
    {
        FBTagMapDeviceTypeListBox.Items.Clear();

        var store = FBTagMapStore.LoadAll(_store);

        // 현재 프리셋 매핑에서 디바이스 타입(=SystemType) 추출
        var deviceTypes = PresetMappingListBox.Items
            .Cast<string>()
            .Select(m =>
            {
                var parts = m.Split(':');
                return parts.Length == 2 ? parts[1].Trim() : "";
            })
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

        foreach (var dt in deviceTypes)
        {
            var fbName = store.TryGetValue(dt, out var preset) ? preset.FBTagMapName : "";
            var portCount = store.TryGetValue(dt, out var p2) ? (p2.Ports?.Count ?? 0) : 0;
            var display = string.IsNullOrEmpty(fbName)
                ? $"{dt}  (미설정)"
                : $"{dt}  → {fbName} ({portCount}포트)";

            FBTagMapDeviceTypeListBox.Items.Add(new FBTagMapDeviceTypeItem(dt, display));
        }
    }

    private void EditFBTagMap_Click(object sender, RoutedEventArgs e)
    {
        if (FBTagMapDeviceTypeListBox.SelectedItem is not FBTagMapDeviceTypeItem item)
        {
            DialogHelpers.Warn("편집할 디바이스 타입을 선택해주세요.");
            return;
        }

        var xgiTemplatePath = string.IsNullOrWhiteSpace(PlcXgiTemplatePathBox.Text)
            ? PlcConfig.Settings.EffectiveXgiTemplatePath
            : PlcXgiTemplatePathBox.Text.Trim();

        var dlg = new FBTagMapEditorDialog(item.DeviceType, xgiTemplatePath, _store)
        {
            Owner = this
        };

        if (dlg.ShowDialog() == true)
            LoadFBTagMapDeviceTypes(); // 목록 새로고침
    }

    // ── 프리셋 ───────────────────────────────────────────────────────────────

    private void LoadPresetMappings()
    {
        PresetMappingListBox.Items.Clear();

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

        // 디바이스 타입 목록 갱신 (추가/제거로 디바이스 타입이 변할 수 있음)
        LoadFBTagMapDeviceTypes();
    }

    private void RemovePreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetMappingListBox.SelectedItem is string selectedMapping)
        {
            PresetMappingListBox.Items.Remove(selectedMapping);
            LoadFBTagMapDeviceTypes();
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

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultProjectName = string.IsNullOrWhiteSpace(ProjectNameBox.Text) ? _initialProjectName : ProjectNameBox.Text.Trim();

        ResultAuthor = AuthorBox.Text?.Trim() ?? "";
        ResultVersion = string.IsNullOrWhiteSpace(VersionBox.Text) ? "1.0.0" : VersionBox.Text.Trim();
        ResultDateTime = DateTimeOffset.Now;

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
            PlcIoTemplateDirBox.Text.Trim(),
            PlcXgiTemplatePathBox.Text.Trim(),
            PlcXg5000ExePathBox.Text.Trim());

        DialogResult = true;
    }

    // ── PLC 탭: 폴더/파일 선택 ───────────────────────────────────────────────

    private void BrowsePlcIoTemplateDir_Click(object sender, RoutedEventArgs e)
    {
        var result = BrowseFolder("IOList 템플릿 폴더 선택", PlcIoTemplateDirBox.Text);
        if (result != null) PlcIoTemplateDirBox.Text = result;
    }

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

    private static string? BrowseFolder(string title, string initialPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = Directory.Exists(initialPath) ? initialPath : string.Empty
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

/// <summary>FBTagMap 디바이스 타입 목록 항목 (ListBox 바인딩용)</summary>
public record FBTagMapDeviceTypeItem(string DeviceType, string DisplayText);
