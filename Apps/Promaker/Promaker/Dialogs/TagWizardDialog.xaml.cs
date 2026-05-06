using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class TagWizardDialog : Window, INotifyPropertyChanged
{
    private readonly DsStore _store;
    private readonly ObservableCollection<IoBatchRow> _ioRows;
    private readonly ObservableCollection<DummySignalRow> _dummyRows;
    private readonly ObservableCollection<UnmatchedSignalRow> _unmatchedRows;
    private readonly ObservableCollection<SystemBaseRow> _systemBaseRows;
    private readonly ObservableCollection<FlowBaseRow> _flowBaseRows;
    private readonly ObservableCollection<SignalPatternRow> _iwSignalRows;
    private readonly ObservableCollection<SignalPatternRow> _qwSignalRows;
    private readonly ObservableCollection<SignalPatternRow> _mwSignalRows;
    private readonly ObservableCollection<AuxPortRow> _auxPortRows;
    private readonly ObservableCollection<ErrorDisplayItem> _errorItems;

    // 신호 템플릿 그리드 콤보용 FB 타입 목록 (XGI_Template.xml 에서 로드)
    public System.Collections.Generic.IReadOnlyList<string> WizFBTypes { get; private set; }
        = System.Array.Empty<string>();

    // API 이름 콤보 데이터소스 — 현재 store 의 모든 ApiDef 이름 (distinct, 정렬)
    public System.Collections.ObjectModel.ObservableCollection<string> WizApiNames { get; }
        = new();

    // 시스템 주소 추가용 콤보 데이터소스 (이미 추가된 타입은 제외)
    public System.Collections.ObjectModel.ObservableCollection<string> AvailableSystemTypes { get; }
        = new();

    // 32점 단위 chunked 보기 (read-only). IndexedPatternRow 가 (Word.Bit 인덱스 + 패턴) 보유.
    private const int ChunkSize = 32;
    public System.Collections.ObjectModel.ObservableCollection<System.Collections.ObjectModel.ObservableCollection<IndexedPatternRow>> IwChunks { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<System.Collections.ObjectModel.ObservableCollection<IndexedPatternRow>> QwChunks { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<System.Collections.ObjectModel.ObservableCollection<IndexedPatternRow>> MwChunks { get; } = new();

    // ── Step 3 IoSignalPreviewGrid 컬럼 헤더 내장 필터 ───────────────
    private string _pvFlow = "", _pvDevice = "", _pvApi = "", _pvOutSym = "", _pvOutAddr = "", _pvInSym = "", _pvInAddr = "";
    public string PvFlowFilter       { get => _pvFlow;     set => SetPv(ref _pvFlow, value, nameof(PvFlowFilter)); }
    public string PvDeviceFilter     { get => _pvDevice;   set => SetPv(ref _pvDevice, value, nameof(PvDeviceFilter)); }
    public string PvApiFilter        { get => _pvApi;      set => SetPv(ref _pvApi, value, nameof(PvApiFilter)); }
    public string PvOutSymbolFilter  { get => _pvOutSym;   set => SetPv(ref _pvOutSym, value, nameof(PvOutSymbolFilter)); }
    public string PvOutAddressFilter { get => _pvOutAddr;  set => SetPv(ref _pvOutAddr, value, nameof(PvOutAddressFilter)); }
    public string PvInSymbolFilter   { get => _pvInSym;    set => SetPv(ref _pvInSym, value, nameof(PvInSymbolFilter)); }
    public string PvInAddressFilter  { get => _pvInAddr;   set => SetPv(ref _pvInAddr, value, nameof(PvInAddressFilter)); }
    /// <summary>150ms 디바운스 — 빠른 타이핑 시 마지막 입력 후 한 번만 _ioView.Refresh.</summary>
    private RowFilterDebouncer _pvDebounce = null!;

    private void SetPv(ref string field, string value, string name)
    {
        value ??= "";
        if (field == value) return;
        field = value;
        OnPropertyChanged(name);
        _pvDebounce.Bump();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private ICollectionView _ioView;
    private int _currentStep = 1;
    private int _successCount;
    private string _currentDeviceTemplateFile = "";

    /// <summary>LoadDeviceTemplate 진행 중 자동 저장 차단 — 행 setter 의 PropertyChanged 가
    /// 신구 SystemType 사이에 잘못된 preset 으로 persist 되는 race 방지.</summary>
    private bool _isLoadingTemplate;

    public TagWizardDialog(DsStore store)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ioRows = new ObservableCollection<IoBatchRow>();
        _dummyRows = new ObservableCollection<DummySignalRow>();
        _unmatchedRows = new ObservableCollection<UnmatchedSignalRow>();
        _systemBaseRows = new ObservableCollection<SystemBaseRow>();
        _flowBaseRows = new ObservableCollection<FlowBaseRow>();
        _iwSignalRows = new ObservableCollection<SignalPatternRow>();
        _qwSignalRows = new ObservableCollection<SignalPatternRow>();
        _mwSignalRows = new ObservableCollection<SignalPatternRow>();
        _auxPortRows = new ObservableCollection<AuxPortRow>();
        _errorItems = new ObservableCollection<ErrorDisplayItem>();

        // Setup CollectionView with filtering
        _ioView = CollectionViewSource.GetDefaultView(_ioRows);
        _ioView.Filter = FilterIoRow;
        IoSignalPreviewGrid.ItemsSource = _ioView;

        _pvDebounce = new RowFilterDebouncer(() => _ioView?.Refresh());

        DummySignalPreviewGrid.ItemsSource = _dummyRows;
        UnmatchedSignalGrid.ItemsSource = _unmatchedRows;
        ErrorsListBox.ItemsSource = _errorItems;

        // Bind DataGrids
        SystemBaseGrid.ItemsSource = _systemBaseRows;
        FlowBaseGrid.ItemsSource = _flowBaseRows;
        IwSignalGrid.ItemsSource = _iwSignalRows;
        QwSignalGrid.ItemsSource = _qwSignalRows;
        MwSignalGrid.ItemsSource = _mwSignalRows;
        if (AuxPortGrid != null)
            AuxPortGrid.ItemsSource = _auxPortRows;

        // FB 타입 목록 로드 (XGI_Template.xml 의 UserFB) — 콤보 바인딩 전에 선행
        WizFBTypes = FBPortCatalog.GetFBTypeNames();

        // API 이름 목록 로드 (store 전체 ApiDef 이름)
        ReloadWizApiNames();

        // 글로벌 FB 타입 콤보 초기화 — SystemType 당 1개 FB 선택
        if (GlobalFBTypeCombo != null)
            GlobalFBTypeCombo.ItemsSource = WizFBTypes;

        // Bind filter text boxes
        // 필터는 컬럼 헤더 내장 TextBox 가 DataContext.Pv*Filter 에 바인딩 → INPC setter 가 Refresh
        DataContext = this;

        InitializeStep1();
    }

    /// <summary>
    /// API 콤보박스 데이터 로드.
    ///   • 항상 Api_None 첫 번째.
    ///   • 현재 선택된 SystemType 의 프리셋 API 목록 (DevicePresets/Entries3).
    ///   • + store ApiDefs (사용자 정의 추가분).
    /// systemType 가 비어있으면 store ApiDefs 만 로드.
    /// </summary>
    private void ReloadWizApiNames(string? systemType = null)
    {
        var set = new System.Collections.Generic.SortedSet<string>(System.StringComparer.OrdinalIgnoreCase);

        // (1) SystemType 프리셋 APIs — Robot 등은 ADV/RET 가 아니라 여기서 옴.
        if (!string.IsNullOrWhiteSpace(systemType))
            foreach (var n in SystemTypePresetProvider.GetApiNames(systemType))
                if (!string.IsNullOrWhiteSpace(n)) set.Add(n);

        // (2) store ApiDefs (사용자 정의 추가분).
        foreach (var d in _store.ApiDefsReadOnly.Values)
            if (!string.IsNullOrWhiteSpace(d.Name)) set.Add(d.Name);

        WizApiNames.Clear();
        // Api_None — 글로벌 신호 sentinel ($(A) 미사용 패턴용).
        WizApiNames.Add(ApiNoneSentinel);
        foreach (var n in set) WizApiNames.Add(n);
    }

    /// <summary>"Api 사용 안 함" 표시용 sentinel — IoConstants 의 별칭(기존 호출부 호환).</summary>
    public const string ApiNoneSentinel = IoConstants.ApiNoneSentinel;

    /// <summary>새 행의 기본 ApiName 은 Api_None — 사용자가 명시적으로 실제 API 를 선택해야 한다.</summary>
    private string DefaultApiName() => ApiNoneSentinel;

    /// <summary>
    /// IW/QW/MW 신호 템플릿 행들의 ApiName 비어있는지 검사 — 예비(Spare) 행은 제외.
    /// 결과는 차단 없이 Step 4 요약 화면에 안내 메시지로 표시.
    /// </summary>
    public IReadOnlyList<string> SignalTemplateWarnings { get; private set; } = Array.Empty<string>();

    private bool ValidateSignalTemplates()
    {
        var missing = new List<string>();
        if (_iwSignalRows.Any(r => !r.IsSpare && string.IsNullOrWhiteSpace(r.ApiName))) missing.Add("IW");
        if (_qwSignalRows.Any(r => !r.IsSpare && string.IsNullOrWhiteSpace(r.ApiName))) missing.Add("QW");
        if (_mwSignalRows.Any(r => !r.IsSpare && string.IsNullOrWhiteSpace(r.ApiName))) missing.Add("MW");
        SignalTemplateWarnings = missing.Count == 0
            ? Array.Empty<string>()
            : new[] { $"API 이름 미지정 행 존재 — {string.Join(", ", missing)} 탭에서 API 선택 필요 (예비 행 제외)." };
        return true;  // 차단하지 않음 — 요약에서 알림.
    }

    // ── Step 4: 신호 매핑 ──────────────────────────────────────────────

    /// <summary>
    /// 글로벌 FB 타입 콤보 변경 시 — AuxPortRow 및 IW/QW/MW 신호 행의 TargetFBType 을 일괄 갱신해
    /// PortOptions 가 새 FB 의 포트로 바뀌게 하고, 현재 SystemType 의 Preset 에 즉시 저장(자동 persist).
    /// </summary>
    private void GlobalFBType_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var fbType = GlobalFBTypeCombo?.SelectedItem as string ?? "";
        foreach (var row in _auxPortRows)
            row.TargetFBType = fbType;
        foreach (var row in _iwSignalRows)
            row.TargetFBType = fbType;
        foreach (var row in _qwSignalRows)
            row.TargetFBType = fbType;
        foreach (var row in _mwSignalRows)
            row.TargetFBType = fbType;

        // 선택 즉시 Preset 에 persist — 사용자가 💾 저장을 누르지 않아도 다음 세션에 유지.
        var systemType = _currentDeviceTemplateFile;
        if (!string.IsNullOrWhiteSpace(systemType))
        {
            var presets = FBTagMapStore.LoadAll(_store);
            if (!presets.TryGetValue(systemType, out var presetDto))
                presetDto = new FBTagMapPresetDto();
            presetDto.FBTagMapName = fbType;
            FBTagMapStore.Save(_store, systemType, presetDto);

            if (DeviceTemplateStatusText != null)
                DeviceTemplateStatusText.Text = string.IsNullOrEmpty(fbType)
                    ? $"✓ '{systemType}' FB 지정 해제"
                    : $"✓ '{systemType}' FB → {fbType} 저장";
        }
    }

    private void ShowSummary()
    {
        var summary = WizardSummaryBuilder.Build(
            _store, _successCount, _ioRows.Count, _dummyRows.Count, SignalTemplateWarnings);
        SummarySignalStats.Text = summary.SignalStats;
        CompletionSummaryText.Text = summary.CompletionStatus;
        OpenMigrationButton.Visibility =
            summary.HasCallStructureViolations ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Step 4 '🔧 Call 구조 마이그레이션...' 버튼 — 위반 Call 분할/삭제 다이얼로그.</summary>
    private void OpenMigration_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new MultiDeviceCallMigrationDialog(_store) { Owner = this };
        dlg.ShowDialog();
        ShowSummary();
    }

    /// <summary>32점 단위 보기 토글 — IW/QW/MW 각 섹션 독립.</summary>
    private void ChunkedToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        var sec = AllSections().FirstOrDefault(s => s.ChunkedToggle == cb);
        if (sec != null) ApplyChunkedView(sec, cb.IsChecked == true);
    }

    /// <summary>SystemType 변경 후 — chunked 모드가 켜져있는 섹션만 chunks 재계산.</summary>
    private void RefreshChunkedViewsIfActive()
    {
        foreach (var sec in AllSections())
            if (sec.ChunkedToggle?.IsChecked == true)
                ApplyChunkedView(sec, true);
    }

    /// <summary>섹션 1개의 single/chunked 뷰 동기화 — 각 16행이 Word 1개, ChunkSize 비트씩 묶음.</summary>
    private static void ApplyChunkedView(SignalSectionInfo sec, bool chunked)
    {
        sec.Chunks.Clear();
        if (!chunked)
        {
            sec.Grid.Visibility = Visibility.Visible;
            sec.ChunkedView.Visibility = Visibility.Collapsed;
            return;
        }
        for (int start = 0; start < sec.Rows.Count; start += ChunkSize)
        {
            var slice = new ObservableCollection<IndexedPatternRow>();
            for (int i = start; i < sec.Rows.Count && i < start + ChunkSize; i++)
                slice.Add(new IndexedPatternRow(i / 16, i % 16, sec.Rows[i].Pattern ?? ""));
            sec.Chunks.Add(slice);
        }
        sec.Grid.Visibility = Visibility.Collapsed;
        sec.ChunkedView.Visibility = Visibility.Visible;
    }
}

/// <summary>32점 chunked 뷰 전용 read-only 행 — Word/Bit 인덱스 + 패턴 텍스트.</summary>
public sealed record IndexedPatternRow(int Word, int Bit, string Pattern)
{
    /// "[ 0.00]" 형식의 인덱스 라벨.
    public string IndexLabel => $"[{Word,2}.{Bit:D2}]";
}

public partial class TagWizardDialog
{
    // Step 4 는 이제 요약창이므로 신호 매핑 편집 관련 메서드는 모두 제거됨.
    // FB 바인딩은 Step 2 신호 템플릿(Phase 2 예정) 또는 I/O 일괄 편집에서 처리.

    internal ControlSystemProperties? GetOrCreateControlProps()
    {
        var opt = Queries.getOrCreatePrimaryControlProps(_store);
        return Microsoft.FSharp.Core.FSharpOption<ControlSystemProperties>.get_IsSome(opt) ? opt.Value : null;
    }

    private bool FilterIoRow(object obj)
    {
        if (obj is not IoBatchRow row) return false;
        static bool Match(string v, string f) =>
            string.IsNullOrEmpty(f) || (v != null && v.Contains(f, StringComparison.OrdinalIgnoreCase));
        return Match(row.Flow,       PvFlowFilter)
            && Match(row.Device,     PvDeviceFilter)
            && Match(row.Api,        PvApiFilter)
            && Match(row.OutSymbol,  PvOutSymbolFilter)
            && Match(row.OutAddress, PvOutAddressFilter)
            && Match(row.InSymbol,   PvInSymbolFilter)
            && Match(row.InAddress,  PvInAddressFilter);
    }

    /// <summary>
    /// Step 1 초기화
    /// </summary>
    private void InitializeStep1()
    {
        // 프로젝트 통계 조회
        var flows = Queries.allFlows(_store);
        var works = flows.SelectMany(f => Queries.worksOf(f.Id, _store)).ToArray();
        var calls = works.SelectMany(w => Queries.callsOf(w.Id, _store)).ToArray();
        var allApiCalls = Queries.allApiCalls(_store);

        FlowCountText.Text = $"{flows.Length}";
        WorkCountText.Text = $"{works.Length}";
        CallCountText.Text = $"{calls.Length}";
        DeviceCountText.Text = $"{allApiCalls.Length}";

        // Step 1 이 곧바로 선두 주소 설정 편집 화면이므로 Flow/System base 데이터를 즉시 로드
        // (내부적으로 LoadSystemBase + LoadFlowBase 를 수행)
        LoadTemplateFileList();
    }

    /// <summary>
    /// 다음 버튼 클릭
    /// </summary>
    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep == 1)
        {
            // Step 1 → Step 2: 템플릿 편집
            LoadTemplateFileList();
            MoveToStep(2);
        }
        else if (_currentStep == 2)
        {
            // Step 2 → Step 3: 신호 생성 전 템플릿 유효성 검증 (API 이름 필수)
            if (!ValidateSignalTemplates())
                return;

            if (!GenerateSignals())
                return;

            MoveToStep(3);
        }
        else if (_currentStep == 3)
        {
            // Step 3 → Step 4: 확인 다이얼로그 → 적용 → 요약 이동 (Basic 모드와 동일 UX).
            // 별도 "패턴 적용" 버튼을 눌러도 같은 흐름.
            if (!ConfirmAndApplyPatterns()) return;
            MoveToStep(4);
        }
    }

    /// <summary>
    /// 이전 버튼 클릭
    /// </summary>
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
        {
            MoveToStep(_currentStep - 1);
        }
    }

    /// <summary>
    /// 닫기 버튼 클릭
    /// </summary>
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 단계 이동
    /// </summary>
    private void MoveToStep(int step)
    {
        _currentStep = step;

        // 컨텐츠 표시
        Step1Content.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Content.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Content.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4Content.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;

        // Step 4 진입 시 요약 통계 계산
        if (step == 4) ShowSummary();

        // 단계 인디케이터 업데이트
        UpdateStepIndicators();

        // 버튼 상태 업데이트
        UpdateButtons();
    }

    /// <summary>
    /// 단계 인디케이터 업데이트
    /// </summary>
    private void UpdateStepIndicators()
    {
        var accentBrush = (Brush)FindResource("AccentBrush");
        var greenBrush = (Brush)FindResource("GreenAccentBrush");
        var secondaryBrush = (Brush)FindResource("SecondaryBackgroundBrush");
        var lightText = (Brush)FindResource("AlwaysLightTextBrush");
        var secondaryText = (Brush)FindResource("SecondaryTextBrush");

        ApplyStepStyle(Step1Bar, Step1Text, 1, accentBrush, greenBrush, secondaryBrush, lightText, secondaryText);
        ApplyStepStyle(Step2Bar, Step2Text, 2, accentBrush, greenBrush, secondaryBrush, lightText, secondaryText);
        ApplyStepStyle(Step3Bar, Step3Text, 3, accentBrush, greenBrush, secondaryBrush, lightText, secondaryText);
        ApplyStepStyle(Step4Bar, Step4Text, 4, accentBrush, greenBrush, secondaryBrush, lightText, secondaryText);
    }

    /// <summary>
    /// 개별 스텝 인디케이터 스타일 적용
    /// </summary>
    private void ApplyStepStyle(
        Border bar, TextBlock text, int stepNumber,
        Brush accentBrush, Brush greenBrush, Brush secondaryBrush,
        Brush lightText, Brush secondaryText)
    {
        if (_currentStep == stepNumber)
        {
            bar.Background = accentBrush;
            text.Foreground = lightText;
            text.FontWeight = FontWeights.SemiBold;
        }
        else if (_currentStep > stepNumber)
        {
            bar.Background = greenBrush;
            text.Foreground = lightText;
            text.FontWeight = FontWeights.Normal;
        }
        else
        {
            bar.Background = secondaryBrush;
            text.Foreground = secondaryText;
            text.FontWeight = FontWeights.Normal;
        }
    }

    /// <summary>
    /// 버튼 상태 업데이트
    /// </summary>
    private void UpdateButtons()
    {
        // 이전 버튼 (Step 4 신호 매핑에서도 뒤로 갈 수 있어야 함)
        BackButton.Visibility = _currentStep > 1 ? Visibility.Visible : Visibility.Collapsed;

        // 다음 버튼
        if (_currentStep < 4)
        {
            NextButton.Visibility = Visibility.Visible;
            if (_currentStep == 1)
                NextButton.Content = "다음 ▶";
            else if (_currentStep == 2)
                NextButton.Content = "신호 생성 ▶";
            else if (_currentStep == 3)
                NextButton.Content = "적용 ▶";
            CloseButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            NextButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
        }
    }
}

/// <summary>
/// Dummy 신호 행 (Preview용)
/// </summary>
public record DummySignalRow(
    string Flow,
    string Work,
    string Call,
    string Symbol,
    string Address,
    string Type,
    string DataType
);

/// <summary>
/// 매칭 실패 신호 행
/// </summary>
public record UnmatchedSignalRow(
    string Flow,
    string Device,
    string Api,
    string OutSymbol,
    string OutAddress,
    string InSymbol,
    string InAddress,
    string FailureReason
);

/// <summary>시스템 주소 설정 행 (DataGrid 바인딩용)</summary>
public class SystemBaseRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    string _systemType = "", _iwBase = "", _qwBase = "", _mwBase = "";
    bool _isEnabled = false;
    public string SystemType { get => _systemType; set => SetProperty(ref _systemType, value); }
    public bool   IsEnabled  { get => _isEnabled;  set => SetProperty(ref _isEnabled, value); }
    public string IW_Base    { get => _iwBase;     set => SetProperty(ref _iwBase, value); }
    public string QW_Base    { get => _qwBase;     set => SetProperty(ref _qwBase, value); }
    public string MW_Base    { get => _mwBase;     set => SetProperty(ref _mwBase, value); }
}

/// <summary>Flow 주소 설정 행 (DataGrid 바인딩용)</summary>
public class FlowBaseRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    string _flowName = "", _iwBase = "", _qwBase = "", _mwBase = "";
    public string FlowName { get => _flowName; set => SetProperty(ref _flowName, value); }
    public string IW_Base  { get => _iwBase;   set => SetProperty(ref _iwBase, value); }
    public string QW_Base  { get => _qwBase;   set => SetProperty(ref _qwBase, value); }
    public string MW_Base  { get => _mwBase;   set => SetProperty(ref _mwBase, value); }
}

/// <summary>
/// 신호 패턴 행 (IW/QW/MW 그리드 바인딩용)
/// </summary>
public class SignalPatternRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private const string ApiNoneSentinel = IoConstants.ApiNoneSentinel;

    string _apiName = "", _pattern = "", _targetFBType = "", _targetFBPort = "";
    bool _skipAddressAlloc, _isSpare;

    public string ApiName
    {
        get => _apiName;
        set => SetProperty(ref _apiName, value ?? "");
    }

    /// 패턴에 $(A)/$(C) 둘 다 없으면 글로벌 신호 → ApiName 자동으로 Api_None 으로 강제.
    /// $(C) 는 SignalCounts[ApiName] 을 참조하므로 ApiName 이 의미를 가짐 → 자동 강제 대상 아님.
    /// 예외: IsSpare(예비) 또는 패턴이 비어있는 경우는 의도된 미바인딩이므로 보존.
    public string Pattern
    {
        get => _pattern;
        set
        {
            if (!SetProperty(ref _pattern, value ?? "")) return;
            OnPropertyChanged(nameof(DataType));
            if (!_isSpare && !string.IsNullOrEmpty(_pattern)
                && !_pattern.Contains("$(A)") && !_pattern.Contains("$(C)")
                && _apiName != ApiNoneSentinel)
                ApiName = ApiNoneSentinel;
        }
    }

    /// 템플릿별 사용 FB 타입 (XGI_Template.xml 에서 선택)
    public string TargetFBType
    {
        get => _targetFBType;
        set
        {
            if (!SetProperty(ref _targetFBType, value ?? "")) return;
            OnPropertyChanged(nameof(PortOptions));
            OnPropertyChanged(nameof(DataType));
        }
    }

    /// API 이름별 매핑할 FB Local Label
    public string TargetFBPort
    {
        get => _targetFBPort;
        set
        {
            if (!SetProperty(ref _targetFBPort, value ?? "")) return;
            OnPropertyChanged(nameof(DataType));
        }
    }

    /// 주소 할당 미진행 (true) — IO 슬롯 미소비, Address 빈값. 예: _T1S, T#200MS.
    public bool SkipAddressAlloc
    {
        get => _skipAddressAlloc;
        set => SetProperty(ref _skipAddressAlloc, value);
    }

    /// 예비(Spare) 슬롯 (true) — 주소 1비트 예약, 신호 미생성. 셀 값 무시 (UI 비활성, 기존 내용 보존).
    public bool IsSpare
    {
        get => _isSpare;
        set
        {
            if (!SetProperty(ref _isSpare, value)) return;
            OnPropertyChanged(nameof(IsEditable));
            OnPropertyChanged(nameof(DataType));
        }
    }

    /// IsSpare 의 반대 — UI 셀 IsEnabled 바인딩용.
    public bool IsEditable => !_isSpare;

    /// 선택된 FB Local Label 의 IEC 데이터 타입 (read-only).
    /// 우선순위: 시스템 플래그 → IsSpare → FB 포트 → 빈값.
    public string DataType
    {
        get
        {
            var sysType = Plc.Xgi.XgiSystemFlags.tryGetTypeName(_pattern ?? "");
            if (Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(sysType)) return sysType.Value;
            if (_isSpare) return "BOOL";
            if (string.IsNullOrEmpty(_targetFBType) || string.IsNullOrEmpty(_targetFBPort)) return "";
            return FBPortCatalog.GetPortTypeMap(_targetFBType).TryGetValue(_targetFBPort, out var t) ? t : "";
        }
    }

    /// 선택된 FB 의 Local Label 목록 (콤보 데이터소스)
    public System.Collections.Generic.IReadOnlyList<string> PortOptions =>
        FbPortOptionsHelper.Get(_targetFBType);
}

/// <summary>
/// AUX 포트 매핑 행 (AUX 포트 탭 그리드 바인딩용).
/// 하나의 API 당 하나의 행 — AutoAux/ComAux 포트 이름을 FB 포트 드롭다운에서 선택.
/// SystemType 이 바뀌면 전체 행 리로드 및 TargetFBType 변경에 따라 PortOptions 갱신.
/// </summary>
public class AuxPortRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    string _apiName = "", _targetFBType = "", _autoAuxPort = "", _comAuxPort = "";

    public string ApiName     { get => _apiName;     set => SetProperty(ref _apiName, value ?? ""); }
    public string AutoAuxPort { get => _autoAuxPort; set => SetProperty(ref _autoAuxPort, value ?? ""); }
    public string ComAuxPort  { get => _comAuxPort;  set => SetProperty(ref _comAuxPort, value ?? ""); }

    /// SystemType 선택 시 일괄 주입. PortOptions 는 동일 콤보 데이터소스로 의존.
    public string TargetFBType
    {
        get => _targetFBType;
        set
        {
            if (!SetProperty(ref _targetFBType, value ?? "")) return;
            OnPropertyChanged(nameof(PortOptions));
        }
    }

    /// AUX 콤보 옵션 — FbPortOptionsHelper 가 이미 빈 항목 제공 (선택 해제용).
    public System.Collections.Generic.IReadOnlyList<string> PortOptions =>
        FbPortOptionsHelper.Get(_targetFBType);
}

/// <summary>
/// 오류 표시 항목 (ListBox 바인딩용)
/// </summary>
public class ErrorDisplayItem
{
    public string ErrorType { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>
/// FB Local Label 목록 조회 공용 헬퍼 — SignalPatternRow/AuxPortRow 의 ComboBox ItemsSource.
/// fbType 이 비어있으면 빈 배열 반환.
/// </summary>
internal static class FbPortOptionsHelper
{
    /// <summary>FB Local Label 콤보 옵션. 첫 항목 "" 은 미바인딩 선택용 — 사용자가 라벨을 비울 수 있게 한다.</summary>
    public static System.Collections.Generic.IReadOnlyList<string> Get(string? fbType)
    {
        if (string.IsNullOrEmpty(fbType)) return System.Array.Empty<string>();
        var labels = FBPortCatalog.GetLocalLabels(fbType!);
        var result = new System.Collections.Generic.List<string>(labels.Count + 1) { "" };
        result.AddRange(labels);
        return result;
    }
}
