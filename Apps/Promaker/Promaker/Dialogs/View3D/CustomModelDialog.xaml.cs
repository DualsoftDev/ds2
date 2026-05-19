using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Promaker.ViewModels;

namespace Promaker.Windows;

public partial class CustomModelDialog : Window
{
    private readonly CustomModelRegistry _registry;
    private readonly string _wwwrootPath;
    private readonly List<string> _projectSystemTypes;
    private readonly Dictionary<string, List<string>> _systemTypeApiDefs;

    private DispatcherTimer? _debounceTimer;

    /// <summary>"+ 새로 만들기"로 추가했지만 아직 등록(Save)되지 않은 항목의 ApiDef 메타데이터</summary>
    private readonly Dictionary<string, List<string>> _pendingNewApiDefs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>등록 완료 시 설정되는 결과</summary>
    public string? RegisteredSystemType { get; private set; }
    public string? RegisteredJson { get; private set; }

    /// <summary>편집 모드용 (기존 모델 수정)</summary>
    public string? EditingSystemType { get; set; }

    /// <param name="projectSystemTypes">프로젝트에 존재하는 SystemType 이름 목록</param>
    /// <param name="systemTypeApiDefs">SystemType → ApiDef 이름 목록 매핑 (dirs 템플릿 자동 생성용)</param>
    public CustomModelDialog(CustomModelRegistry registry, string wwwrootPath, Window owner,
                             IEnumerable<string>? projectSystemTypes = null,
                             Dictionary<string, List<string>>? systemTypeApiDefs = null)
    {
        _registry = registry;
        _wwwrootPath = wwwrootPath;
        _projectSystemTypes = projectSystemTypes?.OrderBy(s => s).ToList() ?? new List<string>();
        _systemTypeApiDefs = systemTypeApiDefs ?? new Dictionary<string, List<string>>();
        Owner = owner;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 디바운스 타이머 (JSON 변경 → 프리뷰 갱신 지연)
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            UpdatePreview();
        };

        // ComboBox에 프로젝트 SystemType 목록 채우기
        PopulateSystemTypeDropdown();

        // 편집 모드이면 기존 데이터 로드
        if (!string.IsNullOrEmpty(EditingSystemType))
        {
            if (_registry.Models.TryGetValue(EditingSystemType, out var json))
                JsonEditor.Text = json;
            Title = $"커스텀 3D 모델 편집 — {EditingSystemType}";
            RegisterButton.Content = "저장";
            TrySelectSystemType(EditingSystemType);
        }

        UpdateJsonEditorEnabled();
        await InitializePreviewWebView();
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        var systemType = GetSystemType();
        var json = JsonEditor.Text.Trim();

        // 최종 검증
        var (isValid, error) = CustomModelRegistry.Validate(json);
        if (!isValid)
        {
            ShowMessage(error, true);
            return;
        }

        // 이름이 비어있는지 체크
        if (string.IsNullOrEmpty(systemType))
        {
            ShowMessage("SystemType을 선택하세요.", true);
            return;
        }

        // 내장 모델명과 동일한 경우: 차단
        var builtinNames = Ds2.View3D.DevicePresets.KnownNames;
        if (builtinNames.Contains(systemType))
        {
            ShowMessage($"\"{systemType}\"은 내장 모델명입니다. 다른 이름을 사용하세요.\n(예: \"{systemType}_Custom\", \"My{systemType}\")", true);
            return;
        }

        // 다른 커스텀 모델과 중복 체크 (신규 등록 또는 이름 변경 시)
        var isNewName = string.IsNullOrEmpty(EditingSystemType) || !systemType.Equals(EditingSystemType, StringComparison.Ordinal);
        if (isNewName && _registry.Models.ContainsKey(systemType))
        {
            var result = MessageBox.Show(
                $"\"{systemType}\" 커스텀 모델이 이미 존재합니다.\n덮어쓰시겠습니까?",
                "모델 덮어쓰기", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;
        }

        // 편집 모드에서 이름이 변경된 경우: 이전 파일 삭제
        if (!string.IsNullOrEmpty(EditingSystemType) && !systemType.Equals(EditingSystemType, StringComparison.Ordinal))
        {
            _registry.Delete(EditingSystemType);
        }

        // JSON의 "name"과 SystemType 불일치 시 안내
        var jsonName = ExtractJsonName(json);
        if (!string.IsNullOrEmpty(jsonName) && jsonName != systemType)
        {
            var result = MessageBox.Show(
                $"JSON의 \"name\"이 \"{jsonName}\"이지만,\nSystemType은 \"{systemType}\"입니다.\n\nSystemType 기준으로 \"name\"을 \"{systemType}\"(으)로 변경하여 저장합니다.",
                "이름 자동 수정", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (result != MessageBoxResult.OK)
                return;
        }

        // JSON의 "name" 필드를 SystemType과 일치시킴
        json = PatchJsonName(json, systemType);

        // 저장
        _registry.Save(systemType, json);
        RegisteredSystemType = systemType;
        RegisteredJson = json;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ValidateForm()
    {
        var hasType = !string.IsNullOrEmpty(GetSystemType());
        var hasJson = !string.IsNullOrWhiteSpace(JsonEditor.Text);

        if (hasJson)
        {
            var (isValid, error) = CustomModelRegistry.Validate(JsonEditor.Text);
            if (!isValid)
            {
                ShowMessage(error, true);
                RegisterButton.IsEnabled = false;
                return;
            }
        }

        HideMessage();
        RegisterButton.IsEnabled = hasType && hasJson;
    }

    private void ShowMessage(string msg, bool isError)
    {
        MessageBar.Visibility = Visibility.Visible;
        MessageText.Text = msg;
        MessageText.Foreground = isError
            ? new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#f87171"))
            : new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10b981"));
    }

    private void HideMessage()
    {
        MessageBar.Visibility = Visibility.Collapsed;
    }
}
