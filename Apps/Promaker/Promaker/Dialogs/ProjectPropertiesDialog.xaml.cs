using System.Windows;
using System.Windows.Controls;
using Ds2.Core;
using Ds2.Editor;
using Microsoft.FSharp.Core;

namespace Promaker.Dialogs;

public partial class ProjectPropertiesDialog : Window
{
    private const string DefaultIriPrefix = "http://your-company.com/";
    private readonly string _initialProjectName;

    public string? ResultProjectName { get; private set; }
    public string? ResultIriPrefix { get; private set; }
    public string? ResultGlobalAssetId { get; private set; }
    public string? ResultAuthor { get; private set; }
    public string? ResultVersion { get; private set; }
    public string? ResultDescription { get; private set; }
    public bool ResultSplitDeviceAasx { get; private set; }

    // 프리셋 SystemType 매핑 결과 (배열)
    public string[] ResultPresetSystemTypes { get; private set; } = Array.Empty<string>();

    public ProjectPropertiesDialog(string projectName, ProjectProperties properties)
    {
        InitializeComponent();

        _initialProjectName = string.IsNullOrWhiteSpace(projectName) ? "NewProject" : projectName.Trim();
        ProjectNameBox.Text = _initialProjectName;
        IriPrefixBox.Text     = properties.IriPrefix?.Value     ?? DefaultIriPrefix;
        GlobalAssetIdBox.Text = properties.GlobalAssetId?.Value  ?? "";
        AuthorBox.Text        = properties.Author?.Value         ?? "";
        VersionBox.Text       = properties.Version?.Value        ?? "";
        DescriptionBox.Text   = properties.Description?.Value    ?? "";
        SplitDeviceAasxBox.IsChecked = properties.SplitDeviceAasx;

        // 프리셋 SystemType 매핑 로드
        LoadPresetMappings(properties);

        // 기본 값 설정
        PresetTextBox.Text = "ADV;RET";

        Loaded += (_, _) => ProjectNameBox.Focus();
    }

    private void LoadPresetMappings(ProjectProperties properties)
    {
        var presets = ProjectPropertiesHelper.getPresetSystemTypes(properties);
        PresetMappingListBox.Items.Clear();

        foreach (var preset in presets)
        {
            PresetMappingListBox.Items.Add(preset);
        }

        // 기본값이 없으면 기본 매핑 추가
        if (presets.Length == 0)
        {
            PresetMappingListBox.Items.Add("ADV;RET:Unit");
            PresetMappingListBox.Items.Add("UP;DOWN:UpDn");
            PresetMappingListBox.Items.Add("FWD;BWD:Motor");
        }
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

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultProjectName   = string.IsNullOrWhiteSpace(ProjectNameBox.Text) ? _initialProjectName : ProjectNameBox.Text.Trim();
        ResultIriPrefix     = IriPrefixBox.Text.Trim();
        ResultGlobalAssetId = GlobalAssetIdBox.Text.Trim();
        ResultAuthor        = AuthorBox.Text.Trim();
        ResultVersion       = VersionBox.Text.Trim();
        ResultDescription   = DescriptionBox.Text.Trim();
        ResultSplitDeviceAasx = SplitDeviceAasxBox.IsChecked == true;

        // 프리셋 SystemType 매핑 저장 (ListBox에서 가져오기)
        ResultPresetSystemTypes = PresetMappingListBox.Items
            .Cast<string>()
            .ToArray();

        DialogResult = true;
    }
}
