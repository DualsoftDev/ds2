using AAStoPLC.TagWizard;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.FSharp.Collections;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.Dialogs;

public enum CallCreateMode { CallReplication, ApiCallReplication, ApiDefPicker }

public partial class CallCreateDialog : Window
{
    private static readonly string PresetFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "systemTypePreset", "systemTypePreset.json");

    /// <summary>
    /// 마지막으로 선택한 SystemType modelType — 프로세스 메모리에만 유지 (재시작 시 초기화).
    /// 다이얼로그 재오픈 시 자동 선택용.
    /// </summary>
    private static string? _lastSelectedPreset;

    public sealed record ApiCountSpec(string ApiName, int MaxCount, int DefaultCount);

    private readonly Func<string, IReadOnlyList<ApiDefMatch>> _findApiDefsByName;
    private readonly Func<string, IReadOnlyList<ApiCountSpec>>? _apiCountSpecsForSysType;
    private readonly Project? _project;

    /// <summary>다이얼로그 오픈 직후 잔여 Enter 키 입력으로 IsDefault 추가 버튼이 즉시 트리거되는 것 방지.
    /// Loaded 후 짧은 grace period 동안 commit 무시.</summary>
    private System.DateTime _readyAt = System.DateTime.MaxValue;
    private static readonly System.TimeSpan _commitGrace = System.TimeSpan.FromMilliseconds(400);

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

    /// <summary>true=Call+ApiCall+passive device 생성 (기본). false=Call만 생성 (ApiName 미입력 → Api_None).</summary>
    public bool CreateDeviceSystem { get; private set; } = true;

    /// <summary>
    /// 신호 수량 — SignalPatternEntry.RepeatCountFromCallProp 키 → 사용자 입력 카운트.
    /// 빈 dict 이면 기본값 (Pipeline ENUM011 경고).
    /// </summary>
    public Dictionary<string, int> SignalCounts { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public CallCreateDialog(
        Func<string, IReadOnlyList<ApiDefMatch>> findApiDefsByName,
        Project? project = null,
        Func<string, IReadOnlyList<ApiCountSpec>>? apiCountSpecsForSysType = null)
    {
        _findApiDefsByName = findApiDefsByName;
        _apiCountSpecsForSysType = apiCountSpecsForSysType;
        _project = project;
        InitializeComponent();
        LoadPresets();

        Loaded += (_, _) =>
        {
            BasicAliasTextBox.Focus();
            RefreshApiDefList();
            RefreshSignalCountsPanel();
            _readyAt = System.DateTime.UtcNow;
        };
    }

    /// <summary>
    /// systemTypePreset.json 을 읽는다. 파일이 없으면 DevicePresets.Entries 의 디폴트로
    /// 새 파일을 생성한 뒤 그 내용을 반환 — 사용자는 이후 이 파일만 편집하면 됨.
    /// </summary>
    private static string[] LoadPresetsFromFile()
    {
        try
        {
            if (!File.Exists(PresetFilePath))
                CreateDefaultPresetFile();

            var json = File.ReadAllText(PresetFilePath);
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch { return []; }
    }

    /// <summary>
    /// systemTypePreset.json 미존재 시 — Ds2.Core.Store.DevicePresets.Entries() 의
    /// (modelType, apiList) 를 "apiList:modelType" 형식으로 직렬화해 파일 생성.
    /// 디폴트 항목의 단일 출처는 F# 의 DevicePresets — 여기서 중복 정의하지 않는다.
    /// (Motor 샘플은 파일이 아닌 다이얼로그 SystemType 입력란 placeholder 로 별도 노출.)
    /// </summary>
    /// <summary>"Cylinder_1".."Cylinder_10" → "Cylinder_#" 로 정규화. 그 외는 원본 유지.</summary>
    private static string NormalizeCylinder(string modelType)
    {
        const string prefix = "Cylinder_";
        if (modelType.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(modelType.AsSpan(prefix.Length), out _))
        {
            return prefix + "#";
        }
        return modelType;
    }

    private static void CreateDefaultPresetFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(PresetFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Cylinder_N (N=1,2,3,...) 은 모두 ADV;RET — 단일 템플릿 "Cylinder_#" 로 합친다.
            // ('#' 마커는 AddCall 다이얼로그에서 ApiCall 복제 카운트로 치환됨)
            // ApiList 가 비어도 entry 자체는 보존 (예: ModeStn — Operation Mode FB).
            var defaults = Ds2.Core.Store.DevicePresets.Entries()
                .Select(t => (modelType: NormalizeCylinder(t.Item1), apiList: t.Item2 ?? ""))
                .GroupBy(x => x.modelType, StringComparer.Ordinal)
                .Select(g => $"{g.First().apiList}:{g.Key}")
                .ToArray();

            var json = JsonSerializer.Serialize(defaults,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PresetFilePath, json);
        }
        catch { /* 디렉토리 생성/쓰기 실패는 무시 — fallback 으로 진행 */ }
    }

    /// <summary>
    /// '#' 을 포함한 SystemType 은 ApiCall 개수로 치환되는 템플릿 — Add 시점에 동적 결정.
    /// 예: "Cylinder_#", "abcd#" → ApiCall 개수 N 일 때 "Cylinder_N", "abcdN".
    /// </summary>
    private static bool IsTemplateModel(string? modelType) =>
        !string.IsNullOrEmpty(modelType) && modelType.Contains('#');

    private void LoadPresets()
    {
        PresetComboBox.Items.Clear();

        // 프리셋 소스 (병합 모드 — 자동 동기화):
        //  (1) systemTypePreset.json — 사용자 편집/추가분 보존
        //  (2) DevicePresets.Entries() — 새로 등록된 SystemType 자동 합류 (Robot*/ModeStn 등)
        // 같은 modelType 이 양쪽에 있으면 파일 (사용자 편집) 우선.
        var byModelType = new Dictionary<string, string>(StringComparer.Ordinal);
        var orderedTypes = new List<string>();

        foreach (var preset in LoadPresetsFromFile())
        {
            var parts = preset.Split(':');
            if (parts.Length == 2)
            {
                if (!byModelType.ContainsKey(parts[1]))
                    orderedTypes.Add(parts[1]);
                byModelType[parts[1]] = parts[0];
            }
        }

        // DevicePresets.Entries() 의 미등록 항목 자동 추가 (ApiList 비어도 표시 — ModeStn 등).
        foreach (var t in Ds2.Core.Store.DevicePresets.Entries())
        {
            var modelType = NormalizeCylinder(t.Item1);
            if (!byModelType.ContainsKey(modelType))
            {
                orderedTypes.Add(modelType);
                byModelType[modelType] = t.Item2 ?? "";
            }
        }

        var rawEntries = orderedTypes
            .Select(mt => (modelType: mt, apiList: byModelType[mt]))
            .ToList();

        foreach (var (modelType, apiList) in rawEntries)
        {
            PresetComboBox.Items.Add(new ComboBoxItem
            {
                Content = modelType,
                Tag = $"{apiList}|{modelType}",
            });
        }

        // 직접 입력 항목 추가 (Tag = null → Dummy SystemType)
        PresetComboBox.Items.Add(new ComboBoxItem { Content = "직접 입력", Tag = null });

        // 마지막 선택 복원 — 일치 항목 없으면 첫 번째.
        var last = _lastSelectedPreset;
        var matchedIndex = -1;
        if (!string.IsNullOrEmpty(last))
        {
            for (int i = 0; i < PresetComboBox.Items.Count; i++)
            {
                if (PresetComboBox.Items[i] is ComboBoxItem cbi
                    && string.Equals(cbi.Content as string, last, StringComparison.Ordinal))
                {
                    matchedIndex = i;
                    break;
                }
            }
        }
        PresetComboBox.SelectedIndex = matchedIndex >= 0 ? matchedIndex : 0;
    }

    private bool IsCustomInputSelected() =>
        PresetComboBox.SelectedItem is ComboBoxItem { Tag: null };

    /// <summary>현재 SystemType 의 ApiName spec 캐시 — 콤보 선택 변경 시 lookup 용.</summary>
    private IReadOnlyList<ApiCountSpec> _currentApiSpecs = Array.Empty<ApiCountSpec>();
    private bool _suppressSignalCountEvents;

    /// <summary>BasicApiNameTextBox 의 ApiName 목록 (';' 구분).</summary>
    private IReadOnlyList<string> ParseApiNamesFromInput()
    {
        var raw = BasicApiNameTextBox?.Text ?? "";
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => s.Trim())
                  .Where(s => s.Length > 0
                              && !string.Equals(s, "Api_None", StringComparison.OrdinalIgnoreCase))
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .ToList();
    }

    /// <summary>현재 SystemType 의 ApiName 콤보 + 수량 입력 재구성. ApiCall 복제 우측 영역.</summary>
    private void RefreshSignalCountsPanel()
    {
        if (SignalCountApiCombo is null) return;
        SignalCountsContainer.Visibility = Visibility.Visible;

        // ApiName 1차 소스 = 다이얼로그 입력 (사용자가 만들 Call 의 정의).
        // preset 의 RepeatCountFromCallProp/FB 매칭은 max 산출에만 사용.
        var apiNames = ParseApiNamesFromInput();
        var sysType = GetSystemTypeForCurrentPreset() ?? "";
        var presetSpecs = _apiCountSpecsForSysType is null || string.IsNullOrEmpty(sysType)
            ? Array.Empty<ApiCountSpec>()
            : (_apiCountSpecsForSysType(sysType) ?? Array.Empty<ApiCountSpec>());
        var presetByName = presetSpecs.ToDictionary(s => s.ApiName, StringComparer.OrdinalIgnoreCase);

        // 입력 ApiName 으로 spec 합성. preset 매칭이 있으면 그 max 사용, 없으면 max=1.
        var specs = (apiNames.Count > 0 ? apiNames : presetSpecs.Select(s => s.ApiName).ToList())
            .Select(api => presetByName.TryGetValue(api, out var s)
                ? s : new ApiCountSpec(api, 1, 1))
            .ToList();

        if (specs.Count == 0)
        {
            // ApiName 도 preset 도 비어있는 매우 드문 경우 — 패널 비활성.
            _currentApiSpecs = Array.Empty<ApiCountSpec>();
            _suppressSignalCountEvents = true;
            SignalCountApiCombo.ItemsSource = new[] { "(ApiName 입력 필요)" };
            SignalCountApiCombo.SelectedIndex = 0;
            SignalCountApiCombo.IsEnabled = false;
            SignalCountValueBox.IsEnabled = false;
            SignalCountValueBox.Text = "0";
            SignalCountMaxLabel.Text = "";
            _suppressSignalCountEvents = false;
            return;
        }
        SignalCountApiCombo.IsEnabled = true;
        SignalCountValueBox.IsEnabled = true;
        _currentApiSpecs = specs;

        // 모든 API 의 default 값으로 SignalCounts 초기화 (사용자 기존 입력은 보존).
        foreach (var s in specs)
        {
            if (!SignalCounts.ContainsKey(s.ApiName))
                SignalCounts[s.ApiName] = s.DefaultCount;
            if (s.MaxCount > 0 && SignalCounts[s.ApiName] > s.MaxCount)
                SignalCounts[s.ApiName] = s.MaxCount;
        }

        _suppressSignalCountEvents = true;
        SignalCountApiCombo.ItemsSource = specs.Select(s => s.ApiName).ToList();
        SignalCountApiCombo.SelectedIndex = 0;
        _suppressSignalCountEvents = false;
        SyncSignalCountInput();
    }

    /// <summary>ApiCall 복제 개수 — 신호 수량 effective max 에 곱셈으로 반영.</summary>
    private int CurrentApiCallReplicationFactor()
    {
        if (RadioApiCallReplication?.IsChecked == true
            && int.TryParse(ApiCallCountTextBox?.Text?.Trim(), out var n) && n >= 1)
            return n;
        return 1;
    }

    private int EffectiveMaxFor(ApiCountSpec spec)
    {
        // 신호 수량 max = ApiCall 복제 수 (FB 용량 무시).
        return CurrentApiCallReplicationFactor();
    }

    private void SyncSignalCountInput()
    {
        if (SignalCountApiCombo?.SelectedItem is not string api) return;
        var spec = _currentApiSpecs.FirstOrDefault(s => string.Equals(s.ApiName, api, StringComparison.OrdinalIgnoreCase));
        if (spec is null) return;
        var effMax = EffectiveMaxFor(spec);
        var v = SignalCounts.TryGetValue(api, out var n) ? n : spec.DefaultCount;
        if (effMax > 0 && v > effMax) { v = effMax; SignalCounts[api] = v; }
        _suppressSignalCountEvents = true;
        SignalCountValueBox.Text = v.ToString();
        SignalCountMaxLabel.Text = effMax > 0 ? $"(max {effMax})" : "(max ∞)";
        _suppressSignalCountEvents = false;
    }

    private void OnBasicApiNameChanged(object sender, TextChangedEventArgs e)
    {
        RefreshSignalCountsPanel();
    }

    private void OnSignalCountApiChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSignalCountEvents) return;
        SyncSignalCountInput();
    }

    private void OnSignalCountValueChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSignalCountEvents) return;
        if (SignalCountApiCombo?.SelectedItem is not string api) return;
        var spec = _currentApiSpecs.FirstOrDefault(s => string.Equals(s.ApiName, api, StringComparison.OrdinalIgnoreCase));
        if (spec is null) return;
        if (!int.TryParse(SignalCountValueBox.Text.Trim(), out var n) || n < 0) return;
        var effMax = EffectiveMaxFor(spec);
        if (effMax > 0 && n > effMax)
        {
            n = effMax;
            _suppressSignalCountEvents = true;
            SignalCountValueBox.Text = n.ToString();
            SignalCountValueBox.CaretIndex = SignalCountValueBox.Text.Length;
            _suppressSignalCountEvents = false;
        }
        SignalCounts[api] = n;
    }

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
            var apiList   = ParseSysType(item.Tag?.ToString());
            var modelType = ParseModelType(item.Tag?.ToString());
            BasicApiNameTextBox.IsEnabled = false;
            BasicApiNameTextBox.Text = apiList ?? "";

            // 템플릿(#) 프리셋: ApiCall 복제 카운트로 SystemType 을 결정 — 패널 자동 펼침.
            if (IsTemplateModel(modelType))
            {
                if (RadioApiCallReplication is not null) RadioApiCallReplication.IsChecked = true;
                if (AdvancedExpander is not null) AdvancedExpander.IsExpanded = true;
            }
        }
        RefreshSignalCountsPanel();
    }

    // ─── 복제 모드 라디오 ───
    private void OnReplicationRadioChanged(object sender, RoutedEventArgs e)
    {
        if (CallReplicationPanel is null || ApiCallReplicationPanel is null) return;

        bool isCallMode = RadioCallReplication.IsChecked == true;
        CallReplicationPanel.IsEnabled = isCallMode;
        ApiCallReplicationPanel.IsEnabled = !isCallMode;
        SyncSignalCountInput();
    }

    // 좌/우 카운트 TextBox — 입력 시 해당 라디오 자동 선택 (UX 버그 방지).
    private void OnCallCountFocus(object sender, RoutedEventArgs e)
    {
        if (RadioCallReplication is { } r && r.IsChecked != true) r.IsChecked = true;
    }
    private void OnCallCountChanged(object sender, TextChangedEventArgs e) => OnCallCountFocus(sender, e);

    private void OnApiCallCountFocus(object sender, RoutedEventArgs e)
    {
        if (RadioApiCallReplication is { } r && r.IsChecked != true) r.IsChecked = true;
    }
    private void OnApiCallCountChanged(object sender, TextChangedEventArgs e)
    {
        OnApiCallCountFocus(sender, e);
        // ApiCall 복제 수 변경 시 모든 API 의 신호 수량을 effective max (= ApiCall 복제 수) 로 설정.
        BumpAllSignalCountsToMax();
        SyncSignalCountInput();
    }

    /// <summary>
    /// ApiCall 복제 수가 바뀌면 모든 ApiCountSpec 의 SignalCounts 를 effective max 로 일괄 설정.
    /// 사용자가 콤보로 보지 않은 API 도 같이 갱신.
    /// </summary>
    private void BumpAllSignalCountsToMax()
    {
        foreach (var spec in _currentApiSpecs)
        {
            var effMax = EffectiveMaxFor(spec);
            if (effMax > 0)
                SignalCounts[spec.ApiName] = effMax;
        }
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
        // 다이얼로그 오픈 직후 외부에서 눌린 Enter 가 IsDefault 추가 버튼을 트리거하는 경우 무시.
        if (System.DateTime.UtcNow - _readyAt < _commitGrace) return;

        if (ModeTabControl.SelectedIndex == 0)
            CommitBasicTab();
        else
            CommitAdvancedTab();
    }

    private (string alias, List<string> apiNames)? ValidateAliasAndApiNames(TextBox aliasBox, TextBox apiNameBox)
    {
        var alias = aliasBox.Text.Trim();
        var aliasResult = InputValidation.validateDevicesAlias(alias);
        if (aliasResult.IsEmptyAlias) { return null; }
        if (aliasResult.IsAliasDotForbidden) { DialogHelpers.Warn("DevicesAlias에는 '.'을 사용할 수 없습니다."); return null; }

        var apiResult = InputValidation.validateApiNames(apiNameBox.Text);
        if (apiResult.IsEmptyInput || apiResult.IsEmptyAfterParse)
        {
            CreateDeviceSystem = false;
            return (alias, new List<string> { "Api_None" });
        }
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

        // 마지막 선택 프리셋 저장 (메모리) — 다음 다이얼로그 오픈 시 자동 선택용.
        if (PresetComboBox.SelectedItem is ComboBoxItem { Content: string label })
            _lastSelectedPreset = label;

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

        var modelType = ParseModelType(item.Tag?.ToString());

        // ApiCall 개수 N 추출 (Advanced + ApiCallReplication 모드일 때만).
        int GetApiCallCount()
        {
            if (AdvancedExpander?.IsExpanded == true
                && RadioApiCallReplication?.IsChecked == true
                && int.TryParse(ApiCallCountTextBox?.Text?.Trim(), out var parsed)
                && parsed >= 1)
                return parsed;
            return 1;
        }

        // '#' 템플릿 → ApiCall 개수로 치환.
        if (IsTemplateModel(modelType))
            return modelType!.Replace("#", GetApiCallCount().ToString());

        return modelType;
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
        if (aliasResult.IsEmptyAlias) { return; }
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
