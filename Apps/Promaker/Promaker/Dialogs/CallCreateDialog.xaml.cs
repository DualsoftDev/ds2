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

    // ─── 직접 입력 SystemType 자동 동기화 상태 ───
    /// <summary>사용자가 CustomSystemTypeComboBox 를 한 번이라도 직접 손댔는가.
    /// true 가 되면 DevicesAlias 변경에 따른 자동 채우기는 멈춘다 (엑셀 자동완성 패턴).</summary>
    private bool _customSystemTypeTouched;
    /// <summary>프로그램이 SystemType 텍스트를 자동 갱신할 때 임시로 set — touched 오탐 방지.</summary>
    private bool _suppressSystemTypeTouchDetection;

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

        // DevicesAlias → CustomSystemTypeComboBox 자동 동기화 (touched 전까지).
        BasicAliasTextBox.TextChanged += OnAliasChanged;

        // 편집 가능한 ComboBox 의 TextChanged 는 inner TextBox 에서 발생 — AddHandler 로 후킹.
        CustomSystemTypeComboBox.AddHandler(
            System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(OnCustomSystemTypeTextChanged));

        Loaded += (_, _) =>
        {
            BasicAliasTextBox.Focus();
            RefreshApiDefList();
            RefreshSignalCountsPanel();
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
    /// systemTypePreset.json 미존재 시 — Ds2.Core.Store.DevicePresets.Entries3 의
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

    /// <summary>
    /// systemTypePreset.json 에 (systemType, apiNames) 항목을 upsert.
    /// - 이미 같은 SystemType 이 있으면 ApiNames 를 합집합으로 보강 (기존 entry 보존)
    /// - 없으면 새 entry 추가
    /// 직접 입력 경로에서 새 Call 이 추가될 때마다 호출되어, 다음 IO 생성 시
    /// 해당 SystemType 의 IW/QW 패턴이 자동 합성되도록 보장한다.
    /// </summary>
    private static void UpsertSystemTypePreset(string systemType, IEnumerable<string> apiNames)
    {
        if (string.IsNullOrWhiteSpace(systemType)) return;

        try
        {
            // 기존 항목 로드 — "ApiList:SystemType" 형식.
            var current = LoadPresetsFromFile();
            var apisByType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var typeOrder = new List<string>();

            foreach (var s in current)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                var idx = s.LastIndexOf(':');
                if (idx <= 0) continue;
                var apis = s.Substring(0, idx)
                    .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(a => a.Trim())
                    .Where(a => !string.IsNullOrEmpty(a))
                    .ToList();
                var st = s.Substring(idx + 1).Trim();
                if (string.IsNullOrEmpty(st)) continue;

                if (!apisByType.ContainsKey(st))
                {
                    apisByType[st] = apis;
                    typeOrder.Add(st);
                }
                else
                {
                    foreach (var a in apis)
                        if (!apisByType[st].Contains(a, StringComparer.Ordinal))
                            apisByType[st].Add(a);
                }
            }

            // 대상 SystemType 보강/추가.
            if (!apisByType.TryGetValue(systemType, out var existing))
            {
                existing = new List<string>();
                apisByType[systemType] = existing;
                typeOrder.Add(systemType);
            }
            foreach (var a in apiNames)
            {
                var trimmed = a?.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (!existing.Contains(trimmed!, StringComparer.Ordinal))
                    existing.Add(trimmed!);
            }

            // 직렬화 후 파일 쓰기.
            var serialized = typeOrder
                .Select(st => $"{string.Join(";", apisByType[st])}:{st}")
                .ToArray();

            var dir = Path.GetDirectoryName(PresetFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(serialized,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PresetFilePath, json);
        }
        catch { /* 쓰기 실패해도 Call 생성 자체는 진행 — 진단 패널에서 재안내됨. */ }
    }

    private static void CreateDefaultPresetFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(PresetFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Cylinder_N (N=1,2,3,...) 은 모두 ADV;RET — 단일 템플릿 "Cylinder_#" 로 합친다.
            // ('#' 마커는 AddCall 다이얼로그에서 ApiCall 복제 카운트로 치환됨)
            var defaults = Ds2.Core.Store.DevicePresets.Entries3
                .Where(t => !string.IsNullOrEmpty(t.Item2))
                .Select(t => (modelType: NormalizeCylinder(t.Item1), apiList: t.Item2))
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

        // 프리셋 소스 우선순위:
        //  (1) systemTypePreset.json — 사용자 편집 가능. 없으면 디폴트로 자동 생성.
        //  (2) DevicePresets.Entries — 파일 읽기/생성 실패 시 in-memory fallback.
        // FBTagMapStore (AASX) 는 PLC 생성용으로 별도 — AddCall 콤보와 무관.
        var rawEntries = new List<(string modelType, string apiList)>();

        foreach (var preset in LoadPresetsFromFile())
        {
            var parts = preset.Split(':');
            if (parts.Length == 2) rawEntries.Add((parts[1], parts[0]));
        }

        if (rawEntries.Count == 0)
        {
            foreach (var t in Ds2.Core.Store.DevicePresets.Entries3)
                if (!string.IsNullOrEmpty(t.Item2))
                    rawEntries.Add((t.Item1, t.Item2));
        }

        foreach (var (modelType, apiList) in rawEntries)
        {
            PresetComboBox.Items.Add(new ComboBoxItem
            {
                Content = modelType,
                Tag = $"{apiList}|{modelType}",
            });
        }

        // 직접 입력 항목 추가 (Tag = null → CustomSystemTypeComboBox 로 SystemType 결정)
        PresetComboBox.Items.Add(new ComboBoxItem { Content = "직접 입력", Tag = null });

        // CustomSystemTypeComboBox 후보 — 등록된 SystemType 이름들 (템플릿 '#' 표기는 제외).
        // 여기에 표시되는 항목은 selectable history — 같은 그릇으로 합류시키고 싶을 때 사용.
        CustomSystemTypeComboBox.Items.Clear();
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (modelType, _) in rawEntries)
        {
            if (string.IsNullOrEmpty(modelType) || modelType.Contains('#')) continue;
            if (seenTypes.Add(modelType))
                CustomSystemTypeComboBox.Items.Add(modelType);
        }

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

            // 직접 입력 모드 진입 — SystemType 패널 표시 + DevicesAlias 와 동기화 시작.
            if (CustomSystemTypePanel is not null)
                CustomSystemTypePanel.Visibility = Visibility.Visible;
            _customSystemTypeTouched = false;
            SyncCustomSystemTypeFromAlias();
        }
        else if (PresetComboBox.SelectedItem is ComboBoxItem item)
        {
            var apiList   = ParseSysType(item.Tag?.ToString());
            var modelType = ParseModelType(item.Tag?.ToString());
            BasicApiNameTextBox.IsEnabled = false;
            BasicApiNameTextBox.Text = apiList ?? "";

            // 표준 프리셋 — 직접 입력 패널 숨김.
            if (CustomSystemTypePanel is not null)
                CustomSystemTypePanel.Visibility = Visibility.Collapsed;

            // 템플릿(#) 프리셋: ApiCall 복제 카운트로 SystemType 을 결정 — 패널 자동 펼침.
            if (IsTemplateModel(modelType))
            {
                if (RadioApiCallReplication is not null) RadioApiCallReplication.IsChecked = true;
                if (AdvancedExpander is not null) AdvancedExpander.IsExpanded = true;
            }
        }
        RefreshSignalCountsPanel();
    }

    // ─── DevicesAlias → CustomSystemType 자동 동기화 ───

    private void OnAliasChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsCustomInputSelected()) return;
        if (_customSystemTypeTouched) return;
        SyncCustomSystemTypeFromAlias();
    }

    private void SyncCustomSystemTypeFromAlias()
    {
        if (CustomSystemTypeComboBox is null || BasicAliasTextBox is null) return;
        var alias = BasicAliasTextBox.Text?.Trim() ?? "";
        // 프로그램 갱신 — touched 오탐 방지.
        _suppressSystemTypeTouchDetection = true;
        try { CustomSystemTypeComboBox.Text = alias; }
        finally { _suppressSystemTypeTouchDetection = false; }
    }

    // 사용자가 ComboBox 의 inner TextBox 에 직접 타이핑 → touched.
    private void OnCustomSystemTypeTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSystemTypeTouchDetection) return;
        _customSystemTypeTouched = true;
    }

    // 사용자가 드롭다운에서 기존 SystemType 을 선택 → touched + 텍스트 채움.
    private void OnCustomSystemTypeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSystemTypeTouchDetection) return;
        if (CustomSystemTypeComboBox.SelectedItem is string picked)
        {
            _customSystemTypeTouched = true;
            // SelectedItem 변경은 ComboBox 가 Text 도 같이 갱신하므로 별도 set 불필요.
            // 다만 선택 직후 inner TextBox 의 caret/selection 정리만 살짝.
            CustomSystemTypeComboBox.Text = picked;
        }
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
        SyncSignalCountInput();
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
        var aliasResult = InputValidation.validateDevicesAlias(alias);
        if (aliasResult.IsEmptyAlias) { DialogHelpers.Warn("DevicesAlias를 입력해주세요."); return null; }
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

        // 직접 입력 경로: 새 SystemType 이면 Api 집합과 함께 systemTypePreset.json 에 자동 등록.
        // 이미 있는 SystemType 이면 Api 들을 합집합으로 보강 — IO 생성 시 새 ApiCall 도 패턴 매칭됨.
        if (IsCustomInputSelected() && !string.IsNullOrWhiteSpace(SelectedSystemType))
            UpsertSystemTypePreset(SelectedSystemType!, apiNames);

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

        // 직접 입력 선택 시 → CustomSystemTypeComboBox 의 텍스트 사용.
        // 사용자가 비워뒀거나 만지지 않았으면 DevicesAlias 로 fallback.
        // 이렇게 하면 어떤 경우에도 의미 있는 SystemType 이 결정되어 Dummy 사각지대가 사라진다.
        if (IsCustomInputSelected())
        {
            var typed = CustomSystemTypeComboBox?.Text?.Trim();
            if (!string.IsNullOrEmpty(typed)) return typed;
            var alias = BasicAliasTextBox?.Text?.Trim();
            return string.IsNullOrEmpty(alias) ? null : alias;
        }

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
        if (aliasResult.IsEmptyAlias) { DialogHelpers.Warn("DevicesAlias를 입력해주세요."); return; }
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
