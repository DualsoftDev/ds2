using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Promaker.Presentation;
using Promaker.Services;

namespace Promaker.Dialogs;

/// <summary>
/// 환경(앱 전역) 설정 다이얼로그 — AASX / PLC / 프리셋. 프로젝트 무관.
/// 프로젝트 메타(이름/작성자/버전/설명)는 별도 <see cref="ProjectPropertiesDialog"/> 에서 편집한다.
/// </summary>
public partial class ApplicationSettingsDialog : Window
{
    private static readonly string PresetFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "systemTypePreset", "systemTypePreset.json");

    private static string SplitDeviceAasxSettingsPath        => SettingsPaths.SplitDeviceAasx;
    private static string IriPrefixSettingsPath              => SettingsPaths.IriPrefix;
    private static string CreateDefaultEntitiesSettingsPath  => SettingsPaths.CreateDefaultEntitiesOnEmptyAasx;

    private const string DefaultIriPrefix = "https://dualsoft.com/";

    public string ResultIriPrefix { get; private set; } = "https://dualsoft.com/";
    public bool ResultSplitDeviceAasx { get; private set; }
    public bool ResultCreateDefaultEntities { get; private set; }

    /// <summary>프리셋 SystemType 매핑 결과 (배열).</summary>
    public string[] ResultPresetSystemTypes { get; private set; } = Array.Empty<string>();

    /// <summary>탭 SSOT — XAML 의 TabItem 순서와 일치 (General=0, Aasx=1, Plc=2, Preset=3).</summary>
    public enum SettingsTab { General = 0, Aasx = 1, Plc = 2, Preset = 3 }

    /// <summary>
    /// <paramref name="initialTab"/> 으로 특정 탭을 선택해서 열 수 있음. 유효 범위 밖이면 ArgumentOutOfRangeException
    /// — silent fallback 안 함 (fail-fast, XAML 재배치 시 즉시 발견).
    /// </summary>
    public ApplicationSettingsDialog(SettingsTab initialTab) : this()
    {
        var idx = (int)initialTab;
        if (idx < 0 || idx >= SettingsTabControl.Items.Count)
            throw new ArgumentOutOfRangeException(nameof(initialTab), idx,
                $"SettingsTabControl.Items.Count={SettingsTabControl.Items.Count} — XAML TabItem 순서/개수 불일치?");
        SettingsTabControl.SelectedIndex = idx;
    }

    public ApplicationSettingsDialog()
    {
        InitializeComponent();

        IriPrefixBox.Text = AppSettingStore.LoadStringOrDefault(IriPrefixSettingsPath, DefaultIriPrefix);
        SplitDeviceAasxBox.IsChecked = AppSettingStore.LoadBoolOrDefault(SplitDeviceAasxSettingsPath, false);
        CreateDefaultEntitiesBox.IsChecked = AppSettingStore.LoadBoolOrDefault(CreateDefaultEntitiesSettingsPath, false);

        var plcCfg = PlcConfig.Settings;
        PlcXgiTemplatePathBox.Text = plcCfg.EffectiveXgiTemplatePath;
        PlcXg5000ExePathBox.Text   = plcCfg.EffectiveXg5000ExePath;

        LoadPresetMappings();

        PresetTextBox.Text = "FWD;BWD";

        // 일반 탭 — 테마 라디오 초기 선택 (ThemeManager.CurrentTheme 기준).
        // Checked 핸들러에서 _suppressThemeRadio 가드로 ApplyTheme 재호출 차단.
        _suppressThemeRadio = true;
        try
        {
            if (ThemeManager.CurrentTheme == AppTheme.Dark) ThemeDarkRadio.IsChecked = true;
            else ThemeLightRadio.IsChecked = true;
        }
        finally { _suppressThemeRadio = false; }
    }

    /// <summary>다이얼로그 열릴 때 테마 라디오 초기 선택이 ApplyTheme 를 트리거하는 것 차단.</summary>
    private bool _suppressThemeRadio;

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
            if (!File.Exists(PresetFilePath)) return Array.Empty<string>();
            var json = File.ReadAllText(PresetFilePath);
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
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
            PresetMappingListBox.Items.Remove(selectedMapping);
        else
            DialogHelpers.Warn("제거할 항목을 선택해주세요.");
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

        DialogResult = true;
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

    /// <summary>
    /// PR-A3 — dock 레이아웃 초기화. MainWindow 가 시작 시 캡쳐한 default 스냅샷으로 현재 layout 즉시 복원 (재시작 불요).
    /// 파일 삭제 방식은 Window_Closing 의 SaveLayout 가 다시 쓰기 때문에 무효 — 메모리 상의 layout 자체를 reset.
    /// 모든 알림은 DialogHelpers 의 themed message box 사용.
    /// </summary>
    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogHelpers.Confirm(this,
                "Dock 패널 배치를 기본 상태로 즉시 복원합니다.\n\n계속하시겠습니까?",
                "레이아웃 초기화"))
            return;

        if (Application.Current.MainWindow is MainWindow mw)
        {
            try
            {
                mw.ResetDockLayoutToDefault();
                DialogHelpers.Info(this,
                    "레이아웃이 기본 상태로 복원되었습니다.",
                    "레이아웃 초기화 완료");
            }
            catch (Exception ex)
            {
                DialogHelpers.Error(this,
                    $"레이아웃 복원에 실패했습니다.\n\n{ex.Message}",
                    "레이아웃 초기화 실패");
            }
        }
        else
        {
            DialogHelpers.Error(this,
                "MainWindow 를 찾을 수 없습니다.",
                "레이아웃 초기화 실패");
        }
    }

    /// <summary>
    /// 테마 라디오 선택 시 즉시 <see cref="ThemeManager.ApplyTheme"/> 호출. ctor 초기 set 은 _suppressThemeRadio 로 차단.
    /// </summary>
    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressThemeRadio) return;
        if (sender == ThemeDarkRadio && ThemeManager.CurrentTheme != AppTheme.Dark)
            ThemeManager.ApplyTheme(AppTheme.Dark);
        else if (sender == ThemeLightRadio && ThemeManager.CurrentTheme != AppTheme.Light)
            ThemeManager.ApplyTheme(AppTheme.Light);
    }
}
