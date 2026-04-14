using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Promaker.ViewModels;

namespace Promaker.Windows;

public partial class CustomModelDialog : Window
{
    private const string VirtualHostName = "promaker-preview.app";
    private const string PreviewHtmlPage = "device-preview.html";

    private readonly CustomModelRegistry _registry;
    private readonly string _wwwrootPath;
    private readonly List<string> _projectSystemTypes;
    private readonly Dictionary<string, List<string>> _systemTypeApiDefs;

    private DispatcherTimer? _debounceTimer;
    private bool _previewReady;

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
            SystemTypeInput.Text = EditingSystemType;
            if (_registry.Models.TryGetValue(EditingSystemType, out var json))
                JsonEditor.Text = json;
            Title = $"커스텀 3D 모델 편집 — {EditingSystemType}";
            RegisterButton.Content = "저장";
        }

        UpdatePlaceholder();
        await InitializePreviewWebView();
    }

    private static System.Windows.Media.SolidColorBrush Brush(string hex)
        => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    private void PopulateSystemTypeDropdown()
    {
        SystemTypeInput.Items.Clear();

        var registered = _registry.Models.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var builtinNames = Ds2.View3D.DevicePresets.KnownNames;

        // 미등록 SystemType (커스텀 모델 필요한 항목) — 선택 가능
        var unregistered = _projectSystemTypes
            .Where(st => !registered.Contains(st) && !builtinNames.Contains(st))
            .ToList();

        if (unregistered.Count > 0)
        {
            SystemTypeInput.Items.Add(new ComboBoxItem
                { Content = "── 프로젝트 SystemType (미등록) ──", IsEnabled = false, Foreground = Brush("#f59e0b") });
            foreach (var st in unregistered)
                SystemTypeInput.Items.Add(st);
        }

        // 이미 커스텀 등록된 SystemType — 선택 가능 (편집용)
        var customRegistered = _projectSystemTypes.Where(st => registered.Contains(st)).ToList();
        // 프로젝트에 없지만 레지스트리에는 있는 커스텀 모델도 표시
        var extraCustom = _registry.ModelNames.Where(n => !_projectSystemTypes.Contains(n)).ToList();

        if (customRegistered.Count > 0 || extraCustom.Count > 0)
        {
            SystemTypeInput.Items.Add(new ComboBoxItem
                { Content = "── 커스텀 모델 등록됨 ──", IsEnabled = false, Foreground = Brush("#10b981") });
            foreach (var st in customRegistered.Concat(extraCustom))
                SystemTypeInput.Items.Add(st);
        }

        // 내장 모델 (참고용, 선택 불가)
        var builtinUsed = _projectSystemTypes.Where(st => builtinNames.Contains(st)).ToList();
        if (builtinUsed.Count > 0)
        {
            SystemTypeInput.Items.Add(new ComboBoxItem
                { Content = "── 내장 모델 (변경 불가) ──", IsEnabled = false, Foreground = Brush("#64748b") });
            foreach (var st in builtinUsed)
                SystemTypeInput.Items.Add(new ComboBoxItem
                    { Content = $"{st} (내장)", IsEnabled = false, Foreground = Brush("#64748b") });
        }

        // 아무것도 없을 때 안내
        if (SystemTypeInput.Items.Count == 0)
        {
            SystemTypeInput.Items.Add(new ComboBoxItem
                { Content = "프로젝트에 SystemType이 없습니다 — 직접 입력하세요", IsEnabled = false, Foreground = Brush("#64748b") });
        }
    }

    private async Task InitializePreviewWebView()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync();
            await PreviewWebView.EnsureCoreWebView2Async(env);

            PreviewWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHostName, _wwwrootPath,
                CoreWebView2HostResourceAccessKind.Allow);

            PreviewWebView.NavigationCompleted += (_, _) =>
            {
                _previewReady = true;
                UpdatePreview();
            };

            PreviewWebView.CoreWebView2.Navigate($"https://{VirtualHostName}/{PreviewHtmlPage}?embed");
        }
        catch (Exception ex)
        {
            ShowMessage($"프리뷰 초기화 실패: {ex.Message}", true);
        }
    }

    // ── 이벤트 핸들러 ─────────────────────────────────────────

    private void SystemTypeInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // ComboBox 선택 시 "✓" 접미사 제거
        var selectedText = SystemTypeInput.SelectedItem as string;
        if (selectedText != null && selectedText.EndsWith(" ✓"))
        {
            SystemTypeInput.Text = selectedText.Replace(" ✓", "");
        }

        // 선택된 SystemType의 ApiDef 정보로 JSON 템플릿 자동 생성
        var typeName = SystemTypeInput.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(typeName) && string.IsNullOrWhiteSpace(JsonEditor.Text))
        {
            GenerateJsonTemplate(typeName);
        }

        UpdatePlaceholder();
        ValidateForm();
    }

    private void SystemTypeInput_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        UpdatePlaceholder();
        ValidateForm();
    }

    private void UpdatePlaceholder()
    {
        if (SystemTypePlaceholder != null)
            SystemTypePlaceholder.Visibility = string.IsNullOrEmpty(SystemTypeInput.Text)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 선택된 SystemType의 ApiDef 이름을 기반으로 JSON 템플릿 자동 생성
    /// </summary>
    private void GenerateJsonTemplate(string systemType)
    {
        if (!_systemTypeApiDefs.TryGetValue(systemType, out var apiDefNames) || apiDefNames.Count == 0)
        {
            // ApiDef 정보 없음 → 기본 단일 애니메이션 템플릿
            JsonEditor.Text = $$"""
            {
              "name": "{{systemType}}",
              "height": 2.0,
              "parts": [
                {"id": "base", "shape": "box", "size": [1.2, 0.3, 1.2], "color": "#64748b"},
                {"id": "body", "shape": "box", "size": [0.8, 1.0, 0.8], "color": "#fbbf24", "glow": 0.3, "on": "base"}
              ],
              "animation": {
                "active": {"target": "body", "type": "move", "axis": "y", "min": 0.65, "max": 1.5}
              }
            }
            """;
            return;
        }

        if (apiDefNames.Count == 1)
        {
            // ApiDef 1개 → 단일 애니메이션
            JsonEditor.Text = $$"""
            {
              "name": "{{systemType}}",
              "height": 2.0,
              "parts": [
                {"id": "base", "shape": "box", "size": [1.2, 0.3, 1.2], "color": "#64748b"},
                {"id": "body", "shape": "box", "size": [0.8, 1.0, 0.8], "color": "#fbbf24", "glow": 0.3, "on": "base"}
              ],
              "animation": {
                "active": {"target": "body", "type": "move", "axis": "y", "min": 0.65, "max": 1.5}
              }
            }
            """;
        }
        else
        {
            // ApiDef 2개 이상 → dirs 템플릿 자동 생성
            var dirsEntries = string.Join(",\n    ",
                apiDefNames.Select(name =>
                    $"\"{name}\": {{\"target\": \"body\", \"type\": \"move\", \"axis\": \"x\", \"min\": 0, \"max\": 0.5}}"));

            var apiDefComment = string.Join(", ", apiDefNames.Select((n, i) => $"{n}(#{i})"));

            JsonEditor.Text = $$"""
            {
              "name": "{{systemType}}",
              "height": 2.0,
              "parts": [
                {"id": "base", "shape": "box", "size": [1.5, 0.3, 1.0], "color": "#64748b"},
                {"id": "body", "shape": "box", "size": [0.6, 0.4, 0.9], "color": "#fbbf24", "glow": 0.3, "on": "base"}
              ],
              "dirs": {
                {{dirsEntries}}
              }
            }
            """;

            ShowMessage($"ApiDef 감지: {apiDefComment} — dirs가 자동 생성되었습니다. 파트와 애니메이션을 수정하세요.", false);
        }
    }

    private void JsonEditor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ValidateForm();
        _debounceTimer?.Stop();
        _debounceTimer?.Start();
    }

    private void LoadFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Device JSON (*.device.json;*.json)|*.device.json;*.json|All Files (*.*)|*.*",
            Title = "JSON 디바이스 파일 선택"
        };

        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                var json = File.ReadAllText(dlg.FileName);
                JsonEditor.Text = json;

                // SystemType이 비어있으면 파일명에서 추출
                if (string.IsNullOrWhiteSpace(SystemTypeInput.Text))
                {
                    var name = Path.GetFileNameWithoutExtension(dlg.FileName)
                        .Replace(".device", "");
                    SystemTypeInput.Text = name;
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"파일 읽기 실패: {ex.Message}", true);
            }
        }
    }

    private void CopySpec_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var specPath = Path.Combine(_wwwrootPath, "DEVICE_JSON_SPEC.md");
            if (File.Exists(specPath))
            {
                var spec = File.ReadAllText(specPath);
                Clipboard.SetText(spec);
                ShowMessage("DEVICE_JSON_SPEC.md가 클립보드에 복사되었습니다. AI에게 붙여넣기하세요.", false);
            }
            else
            {
                ShowMessage("DEVICE_JSON_SPEC.md 파일을 찾을 수 없습니다.", true);
            }
        }
        catch (Exception ex)
        {
            ShowMessage($"클립보드 복사 실패: {ex.Message}", true);
        }
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        var systemType = SystemTypeInput.Text.Trim();
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
            ShowMessage("SystemType 이름을 입력하세요.", true);
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

    // ── 내부 로직 ─────────────────────────────────────────────

    private void ValidateForm()
    {
        var hasType = !string.IsNullOrWhiteSpace(SystemTypeInput.Text);
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

    private void UpdatePreview()
    {
        if (!_previewReady) return;
        var json = JsonEditor.Text?.Trim();
        if (string.IsNullOrEmpty(json)) return;

        try
        {
            // preview.html의 전역 함수 호출: JSON을 파싱하여 렌더링
            var escaped = json
                .Replace("\\", "\\\\")
                .Replace("`", "\\`")
                .Replace("${", "\\${");
            PreviewWebView.CoreWebView2.ExecuteScriptAsync(
                $"try {{ var _s = JSON.parse(`{escaped}`); buildDevice(_s); setState('R'); }} catch(e) {{ }}");
        }
        catch
        {
            // 프리뷰 갱신 실패는 무시 (JSON이 아직 불완전할 수 있음)
        }
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
