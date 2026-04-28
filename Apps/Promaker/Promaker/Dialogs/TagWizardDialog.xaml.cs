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
    private readonly IoListGeneratorService _generator;
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

    // ── Step 3 IoSignalPreviewGrid 컬럼 헤더 내장 필터 ───────────────
    private string _pvFlow = "", _pvDevice = "", _pvApi = "", _pvOutSym = "", _pvOutAddr = "", _pvInSym = "", _pvInAddr = "";
    public string PvFlowFilter       { get => _pvFlow;     set => SetPv(ref _pvFlow, value, nameof(PvFlowFilter)); }
    public string PvDeviceFilter     { get => _pvDevice;   set => SetPv(ref _pvDevice, value, nameof(PvDeviceFilter)); }
    public string PvApiFilter        { get => _pvApi;      set => SetPv(ref _pvApi, value, nameof(PvApiFilter)); }
    public string PvOutSymbolFilter  { get => _pvOutSym;   set => SetPv(ref _pvOutSym, value, nameof(PvOutSymbolFilter)); }
    public string PvOutAddressFilter { get => _pvOutAddr;  set => SetPv(ref _pvOutAddr, value, nameof(PvOutAddressFilter)); }
    public string PvInSymbolFilter   { get => _pvInSym;    set => SetPv(ref _pvInSym, value, nameof(PvInSymbolFilter)); }
    public string PvInAddressFilter  { get => _pvInAddr;   set => SetPv(ref _pvInAddr, value, nameof(PvInAddressFilter)); }
    /// <summary>
    /// 빠른 타이핑 중 매 키마다 전체 행 재평가 + 재렌더로 버벅이므로 150ms 디바운스.
    /// 마지막 키 입력 후 한 번만 Refresh 수행.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _pvDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(150) };

    private void SetPv(ref string field, string value, string name)
    {
        value ??= "";
        if (field == value) return;
        field = value;
        OnPropertyChanged(name);
        _pvDebounce.Stop();
        _pvDebounce.Start();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private ICollectionView _ioView;
    private int _currentStep = 1;
    private int _successCount;
    private string _currentDeviceTemplateFile = "";

    public TagWizardDialog(DsStore store)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _generator = new IoListGeneratorService();
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

        // 필터 디바운스 Tick — Refresh 1 회 후 자기 자신 stop.
        _pvDebounce.Tick += (_, _) =>
        {
            _pvDebounce.Stop();
            _ioView?.Refresh();
        };

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
    /// 현재 store 의 모든 ApiDef 이름을 distinct 정렬해 WizApiNames 에 로드.
    /// API 콤보박스의 원천 데이터.
    /// </summary>
    private void ReloadWizApiNames()
    {
        var names = _store.ApiDefsReadOnly.Values
            .Select(d => d.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();
        WizApiNames.Clear();
        // Api_None 은 미바인딩 sentinel — Step 2 에서 명시적으로 선택해야 한다.
        // 다운스트림에서 ApiName == "Api_None" 이면 IO 조회/Step 3 의 API 컬럼은 빈칸으로 표시.
        WizApiNames.Add(ApiNoneSentinel);
        foreach (var n in names) WizApiNames.Add(n);
    }

    /// <summary>"Api 사용 안 함" 표시용 sentinel.</summary>
    public const string ApiNoneSentinel = "Api_None";

    /// <summary>새 행의 기본 ApiName 은 Api_None — 사용자가 명시적으로 실제 API 를 선택해야 한다.</summary>
    private string DefaultApiName() => ApiNoneSentinel;

    /// <summary>
    /// IW/QW/MW 신호 템플릿 행들의 ApiName 비어있는지 검사.
    /// 하나라도 비어있으면 경고 후 false 반환 — 해당 그리드 탭/행으로 포커스 이동.
    /// </summary>
    private bool ValidateSignalTemplates()
    {
        var missing = new List<string>();
        if (_iwSignalRows.Any(r => string.IsNullOrWhiteSpace(r.ApiName))) missing.Add("IW");
        if (_qwSignalRows.Any(r => string.IsNullOrWhiteSpace(r.ApiName))) missing.Add("QW");
        if (_mwSignalRows.Any(r => string.IsNullOrWhiteSpace(r.ApiName))) missing.Add("MW");
        if (missing.Count == 0) return true;

        System.Windows.MessageBox.Show(
            $"API 이름이 비어있는 행이 있습니다: {string.Join(", ", missing)} 탭\n각 행의 API 콤보에서 값을 선택하세요.",
            "API 이름 누락",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
        return false;
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
            var presets = Promaker.Services.FBTagMapStore.LoadAll(_store);
            if (!presets.TryGetValue(systemType, out var presetDto))
                presetDto = new Promaker.Services.FBTagMapPresetDto();
            presetDto.FBTagMapName = fbType;
            Promaker.Services.FBTagMapStore.Save(_store, systemType, presetDto);

            if (DeviceTemplateStatusText != null)
                DeviceTemplateStatusText.Text = string.IsNullOrEmpty(fbType)
                    ? $"✓ '{systemType}' FB 지정 해제"
                    : $"✓ '{systemType}' FB → {fbType} 저장";
        }
    }

    private void ShowSummary()
    {
        var summary = WizardSummaryBuilder.Build(_store, _successCount, _ioRows.Count);
        SummarySignalStats.Text = summary.SignalStats;
        SummaryBindingStats.Text = summary.BindingStats;
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

    /// <summary>Step 4 '🤖 Robot 메타데이터' 버튼 — RobotMetadata 편집 다이얼로그.</summary>
    private void OpenRobotMetadata_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new RobotMetadataDialog(_store) { Owner = this };
        dlg.ShowDialog();
    }

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
            // Step 3 → Step 4: 요약 이동만 (ApiCall 자동 덮어쓰기 없음).
            // 실제 패턴 적용은 Step 3 의 "패턴 적용" 버튼 클릭으로만 수행.
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
    string Type
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

/// <summary>
/// 시스템 주소 설정 행 (DataGrid 바인딩용)
/// </summary>
public class SystemBaseRow : INotifyPropertyChanged
{
    private string _systemType = "";
    private bool _isEnabled = false;
    private string _iwBase = "";
    private string _qwBase = "";
    private string _mwBase = "";

    public string SystemType
    {
        get => _systemType;
        set { _systemType = value; OnPropertyChanged(nameof(SystemType)); }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; OnPropertyChanged(nameof(IsEnabled)); }
    }

    public string IW_Base
    {
        get => _iwBase;
        set { _iwBase = value; OnPropertyChanged(nameof(IW_Base)); }
    }

    public string QW_Base
    {
        get => _qwBase;
        set { _qwBase = value; OnPropertyChanged(nameof(QW_Base)); }
    }

    public string MW_Base
    {
        get => _mwBase;
        set { _mwBase = value; OnPropertyChanged(nameof(MW_Base)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Flow 주소 설정 행 (DataGrid 바인딩용)
/// </summary>
public class FlowBaseRow : INotifyPropertyChanged
{
    private string _flowName = "";
    private string _iwBase = "";
    private string _qwBase = "";
    private string _mwBase = "";

    public string FlowName
    {
        get => _flowName;
        set { _flowName = value; OnPropertyChanged(nameof(FlowName)); }
    }

    public string IW_Base
    {
        get => _iwBase;
        set { _iwBase = value; OnPropertyChanged(nameof(IW_Base)); }
    }

    public string QW_Base
    {
        get => _qwBase;
        set { _qwBase = value; OnPropertyChanged(nameof(QW_Base)); }
    }

    public string MW_Base
    {
        get => _mwBase;
        set { _mwBase = value; OnPropertyChanged(nameof(MW_Base)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// 신호 패턴 행 (IW/QW/MW 그리드 바인딩용)
/// </summary>
public class SignalPatternRow : INotifyPropertyChanged
{
    private string _apiName = "";
    private string _pattern = "";
    private string _targetFBType = "";
    private string _targetFBPort = "";

    public string ApiName
    {
        get => _apiName;
        set { _apiName = value; OnPropertyChanged(nameof(ApiName)); }
    }

    public string Pattern
    {
        get => _pattern;
        set { _pattern = value; OnPropertyChanged(nameof(Pattern)); }
    }

    /// 템플릿별 사용 FB 타입 (XGI_Template.xml 에서 선택)
    public string TargetFBType
    {
        get => _targetFBType;
        set
        {
            if (_targetFBType == value) return;
            _targetFBType = value ?? "";
            OnPropertyChanged(nameof(TargetFBType));
            OnPropertyChanged(nameof(PortOptions));
        }
    }

    /// API 이름별 매핑할 FB Local Label
    public string TargetFBPort
    {
        get => _targetFBPort;
        set { _targetFBPort = value ?? ""; OnPropertyChanged(nameof(TargetFBPort)); }
    }

    /// 선택된 FB 의 Local Label 목록 (콤보 데이터소스)
    public System.Collections.Generic.IReadOnlyList<string> PortOptions =>
        FbPortOptionsHelper.Get(_targetFBType);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// AUX 포트 매핑 행 (AUX 포트 탭 그리드 바인딩용).
/// 하나의 API 당 하나의 행 — AutoAux/ComAux 포트 이름을 FB 포트 드롭다운에서 선택.
/// SystemType 이 바뀌면 전체 행 리로드 및 TargetFBType 변경에 따라 PortOptions 갱신.
/// </summary>
public class AuxPortRow : INotifyPropertyChanged
{
    private string _apiName = "";
    private string _targetFBType = "";
    private string _autoAuxPort = "";
    private string _comAuxPort = "";

    public string ApiName
    {
        get => _apiName;
        set { _apiName = value ?? ""; OnPropertyChanged(nameof(ApiName)); }
    }

    /// 이 AUX 행이 참조하는 FB 타입 (동일 SystemType 의 TargetFBType 공유).
    /// SystemType 선택 시 일괄 주입.
    public string TargetFBType
    {
        get => _targetFBType;
        set
        {
            if (_targetFBType == value) return;
            _targetFBType = value ?? "";
            OnPropertyChanged(nameof(TargetFBType));
            OnPropertyChanged(nameof(PortOptions));
        }
    }

    /// FB 의 AutoAux 입력 포트 이름 (예: "M_Auto_Adv", "1ST_IN_OK")
    public string AutoAuxPort
    {
        get => _autoAuxPort;
        set { _autoAuxPort = value ?? ""; OnPropertyChanged(nameof(AutoAuxPort)); }
    }

    /// FB 의 ComAux 입력 포트 이름 (비워두면 ComAux coil 생성 안 함)
    public string ComAuxPort
    {
        get => _comAuxPort;
        set { _comAuxPort = value ?? ""; OnPropertyChanged(nameof(ComAuxPort)); }
    }

    /// 선택된 FB 의 Local Label 목록 (Auto/Com ComboBox 공유 데이터소스).
    /// AUX 포트는 선택 해제가 가능해야 하므로 맨 앞에 빈 문자열을 삽입해 "(없음)" 선택지로 제공.
    public System.Collections.Generic.IReadOnlyList<string> PortOptions
    {
        get
        {
            var labels = FbPortOptionsHelper.Get(_targetFBType);
            var result = new System.Collections.Generic.List<string>(labels.Count + 1) { "" };
            result.AddRange(labels);
            return result;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
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
    public static System.Collections.Generic.IReadOnlyList<string> Get(string? fbType) =>
        string.IsNullOrEmpty(fbType)
            ? System.Array.Empty<string>()
            : Promaker.Services.FBPortCatalog.GetLocalLabels(fbType!);
}
