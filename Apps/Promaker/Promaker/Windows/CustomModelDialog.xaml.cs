using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    private string _lastGeneratedTemplate = "";

    private void SystemTypeInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // ComboBox 선택 시 — SelectedItem이 string이면 텍스트 설정
        if (SystemTypeInput.SelectedItem is string selected)
        {
            SystemTypeInput.Text = selected.Replace(" ✓", "").Trim();
        }

        // 선택된 SystemType의 ApiDef 정보로 JSON 템플릿 생성
        var typeName = SystemTypeInput.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(typeName))
        {
            // JSON이 비어있거나 이전 자동 생성 템플릿이면 교체
            var currentJson = JsonEditor.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(currentJson) || currentJson == _lastGeneratedTemplate.Trim())
            {
                GenerateJsonTemplate(typeName);
            }
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

        UpdateJsonEditorEnabled();
    }

    /// <summary>
    /// SystemType 이름이 비어있으면 JSON 입력 / 파일 불러오기를 차단.
    /// (이름이 없는 상태에서 JSON만 편집되는 것을 방지)
    /// </summary>
    private void UpdateJsonEditorEnabled()
    {
        if (JsonEditor == null) return;
        var hasType = !string.IsNullOrWhiteSpace(SystemTypeInput.Text);
        JsonEditor.IsEnabled = hasType;
        if (LoadFileButton != null) LoadFileButton.IsEnabled = hasType;
        if (JsonEditorHint != null)
            JsonEditorHint.Visibility = (!hasType && string.IsNullOrEmpty(JsonEditor.Text))
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
            // dirs는 기본 loop="restart" (전진만 반복, 시작점 스냅) — 상반 방향이 명확히 구분됨
            var dirsEntries = string.Join(",\n    ",
                apiDefNames.Select(name =>
                    $"\"{name}\": {{\"target\": \"body\", \"type\": \"move\", \"axis\": \"x\", \"min\": 0, \"max\": 0.5, \"loop\": \"restart\"}}"));

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

        _lastGeneratedTemplate = JsonEditor.Text ?? "";
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

    private void CopyAIPrompt_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var specPath = Path.Combine(_wwwrootPath, "DEVICE_JSON_SPEC.md");
            if (!File.Exists(specPath))
            {
                ShowMessage("DEVICE_JSON_SPEC.md 파일을 찾을 수 없습니다.", true);
                return;
            }

            var spec = File.ReadAllText(specPath);
            var systemType = SystemTypeInput.Text?.Trim() ?? "";

            // 장비 맥락 정보 생성
            var context = BuildDeviceContext(systemType);

            var prompt = $"""
            {spec}

            ---

            {context}
            """;

            Clipboard.SetText(prompt.Trim());

            var msg = string.IsNullOrEmpty(systemType)
                ? "AI 프롬프트가 복사되었습니다. AI에게 붙여넣기 후 장비를 설명하세요."
                : $"AI 프롬프트가 복사되었습니다 ({systemType} 맥락 포함). AI에게 붙여넣기 후 원하는 모양을 설명하세요.";
            ShowMessage(msg, false);
        }
        catch (Exception ex)
        {
            ShowMessage($"클립보드 복사 실패: {ex.Message}", true);
        }
    }

    private string BuildDeviceContext(string systemType)
    {
        if (string.IsNullOrEmpty(systemType))
        {
            return "## 요청\n" +
                   "위 스펙에 따라 산업 장비의 3D 모델 JSON을 생성해주세요.\n" +
                   "장비 설명: [여기에 원하는 장비를 설명하세요]";
        }

        _systemTypeApiDefs.TryGetValue(systemType, out var apiDefNames);

        if (apiDefNames == null || apiDefNames.Count <= 1)
        {
            var apiNote = apiDefNames?.Count == 1
                ? $"\n- ApiDef: \"{apiDefNames[0]}\" (1개) → \"animation\" 사용"
                : "";
            return "## 요청\n" +
                   "다음 장비의 3D 모델 JSON을 생성해주세요.\n\n" +
                   $"- SystemType 이름: \"{systemType}\"\n" +
                   $"- 출력 JSON의 \"name\"은 반드시 \"{systemType}\"으로 설정" +
                   apiNote + "\n\n" +
                   "장비 설명: [여기에 원하는 장비를 설명하세요]";
        }

        // ApiDef 2개 이상 → dirs 필수
        var apiDefList = string.Join("\n",
            apiDefNames.Select((n, i) => $"  #{i}: \"{n}\""));

        var dirsLines = string.Join(",\n      ",
            apiDefNames.Select(n =>
                $"\"{n}\": {{\"target\": \"...\", \"type\": \"move\", \"axis\": \"...\", \"min\": 0, \"max\": 0.5, \"loop\": \"restart\"}}"));

        return "## 요청\n" +
               "다음 장비의 3D 모델 JSON을 생성해주세요.\n\n" +
               $"- SystemType 이름: \"{systemType}\"\n" +
               $"- 출력 JSON의 \"name\"은 반드시 \"{systemType}\"으로 설정\n" +
               "- ApiDef 목록 (이 순서와 이름을 반드시 유지):\n" +
               apiDefList + "\n" +
               "- ApiDef가 2개 이상이므로 \"animation\" 대신 \"dirs\"를 사용:\n" +
               "  ```\n" +
               "  \"dirs\": {\n" +
               "      " + dirsLines + "\n" +
               "  }\n" +
               "  ```\n" +
               "- 각 dir의 애니메이션은 해당 ApiDef의 물리적 동작에 맞게 설정\n" +
               "- `loop` 필드로 진행 방식 지정 (상반 방향 구분에 중요):\n" +
               "  - \"restart\" (dirs 기본): 전진만 반복하고 끝나면 시작점으로 스냅 — 피스톤/컨베이어 등\n" +
               "  - \"once\": 한번 진행 후 정지, Idle 되면 원위치 복귀 — 리프터/도어 등\n" +
               "  - \"pingpong\": min↔max 왕복 (방향성 없음)\n\n" +
               "장비 설명: [여기에 원하는 장비를 설명하세요]";
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

    private static string? ExtractJsonName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("name", out var prop) ? prop.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// JSON의 "name" 필드를 지정된 systemType으로 교체.
    /// AI가 생성한 JSON의 name이 달라도 SystemType에 맞게 강제 통일.
    /// </summary>
    private static string PatchJsonName(string json, string systemType)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // "name" 필드가 이미 일치하면 그대로 반환
            if (root.TryGetProperty("name", out var nameProp) &&
                nameProp.GetString() == systemType)
                return json;

            // JSON을 수정하여 "name"을 systemType으로 교체
            using var ms = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                // "name"을 먼저 쓰고
                writer.WriteString("name", systemType);
                // 나머지 프로퍼티를 복사 ("name" 제외)
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "name") continue;
                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return json; // 패치 실패 시 원본 반환
        }
    }
}
