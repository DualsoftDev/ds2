using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using AAStoPLC.TagWizard;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class TagWizardDialog : Window, INotifyPropertyChanged
{
    /// <summary>
    /// 각 그리드/리스트 초기 로드.
    /// Step 1: 시스템/Flow 선두 주소 / Step 2: SystemType 템플릿 목록
    /// 외부 txt 파일 동기화는 수행하지 않음 (AASX 내 FBTagMapPresets 가 단일 진실원).
    /// </summary>
    private void LoadTemplateFileList()
    {
        LoadSystemBase();
        LoadFlowBase();
        LoadDeviceTemplateList();
    }

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

    // Dummy 신호 컬럼 필터
    private string _dmFlow = "", _dmWork = "", _dmCall = "", _dmSym = "", _dmAddr = "", _dmArea = "", _dmDt = "";
    public string DmFlowFilter     { get => _dmFlow; set => SetDm(ref _dmFlow, value, nameof(DmFlowFilter)); }
    public string DmWorkFilter     { get => _dmWork; set => SetDm(ref _dmWork, value, nameof(DmWorkFilter)); }
    public string DmCallFilter     { get => _dmCall; set => SetDm(ref _dmCall, value, nameof(DmCallFilter)); }
    public string DmSymbolFilter   { get => _dmSym;  set => SetDm(ref _dmSym,  value, nameof(DmSymbolFilter)); }
    public string DmAddressFilter  { get => _dmAddr; set => SetDm(ref _dmAddr, value, nameof(DmAddressFilter)); }
    public string DmAreaFilter     { get => _dmArea; set => SetDm(ref _dmArea, value, nameof(DmAreaFilter)); }
    public string DmDataTypeFilter { get => _dmDt;   set => SetDm(ref _dmDt,   value, nameof(DmDataTypeFilter)); }
    private RowFilterDebouncer _dmDebounce = null!;
    private void SetDm(ref string field, string value, string name)
    {
        value ??= "";
        if (field == value) return;
        field = value;
        OnPropertyChanged(name);
        _dmDebounce.Bump();
    }
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
    private ICollectionView _dummyView = null!;
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

        _dummyView = CollectionViewSource.GetDefaultView(_dummyRows);
        _dummyView.Filter = FilterDummyRow;
        _dmDebounce = new RowFilterDebouncer(() => _dummyView?.Refresh());
        DummySignalPreviewGrid.ItemsSource = _dummyView;
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
        if (EndPortGrid != null)
            EndPortGrid.ItemsSource = _endPortRows;

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
    /// 외부에서 FBTagMap 편집 화면 (Step 2 - 신호 템플릿) 으로 바로 점프.
    /// I/O 조회 진단의 "FBTagMap 편집" 바로가기에서 호출.
    /// systemType 가 주어지면 해당 SystemType 의 preset 을 미리 로드.
    /// </summary>
    public void OpenAtFBTagMapForSystemType(string? systemType)
    {
        MoveToStep(2);
        if (!string.IsNullOrWhiteSpace(systemType))
        {
            try { LoadDeviceTemplate(systemType); }
            catch { /* best-effort — 실패해도 wizard 자체는 열린 상태 유지 */ }
        }
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

        // 현 SystemType 프리셋 APIs 만 — 다른 SystemType 의 ApiDef 는 노출 안 함.
        if (!string.IsNullOrWhiteSpace(systemType))
            foreach (var n in SystemTypePresetProvider.GetApiNames(systemType))
                if (!string.IsNullOrWhiteSpace(n)) set.Add(n);

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


    // ── End 포트 (EndPortMap) — API → 완료 OUT 포트 매핑 ────────────────────
    private readonly ObservableCollection<EndPortRow> _endPortRows = new();

    private void AddEndPortRow_Click(object sender, RoutedEventArgs e)
    {
        var fb = GlobalFBTypeCombo?.SelectedItem as string ?? "";
        var sysType = _currentDeviceTemplateFile;
        _endPortRows.Add(HookAutoSave(new EndPortRow
        {
            ApiName = "",
            EndPort = "",
            TargetFBType = fb,
            ApiOptions = BuildAuxApiOptions(sysType),
        }));
        PersistCurrentPreset();
    }

    private void RemoveEndPortRow_Click(object sender, RoutedEventArgs e)
    {
        if (EndPortGrid == null) return;
        var selected = EndPortGrid.SelectedItems.Cast<EndPortRow>().ToList();
        if (selected.Count == 0) return;
        foreach (var row in selected) _endPortRows.Remove(row);
        PersistCurrentPreset();
    }

    /// <summary>Ctrl+C/V 키 이벤트 — 포커스된 그리드의 선택 행 클립보드 복사 / 붙여넣기.
    /// 그리드 타입(SignalPatternRow vs AuxPortRow)에 따라 호환성 검사.</summary>
    private void TagWizardKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
        var grid = FindFocusedGrid();
        if (grid == null) return;
        if (e.Key == System.Windows.Input.Key.C)
        {
            CopyGridSelection(grid);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.V)
        {
            PasteGridSelection(grid);
            e.Handled = true;
        }
    }

    private System.Windows.Controls.DataGrid? FindFocusedGrid()
    {
        foreach (var g in new[] { IwSignalGrid, QwSignalGrid, MwSignalGrid, AuxPortGrid })
            if (g != null && g.IsKeyboardFocusWithin) return g;
        return null;
    }

    private void AuxFilter_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        AuxPortDirectionFilter mode;
        if (AuxFilterInput?.IsChecked == true)       mode = AuxPortDirectionFilter.Input;
        else if (AuxFilterOutput?.IsChecked == true) mode = AuxPortDirectionFilter.Output;
        else                                          mode = AuxPortDirectionFilter.All;
        AuxPortRow.SetDirectionFilter(mode);
    }

    /// <summary>
    /// 글로벌 FB 타입 콤보 변경 시 — AuxPortRow 및 IW/QW/MW 신호 행의 TargetFBType 을 일괄 갱신해
    /// PortOptions 가 새 FB 의 포트로 바뀌게 하고, 현재 SystemType 의 Preset 에 즉시 저장(자동 persist).
    /// </summary>
    private void GlobalFBType_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var fbType = GlobalFBTypeCombo?.SelectedItem as string ?? "";
        foreach (var row in _auxPortRows)
            row.TargetFBType = fbType;
        foreach (var row in _endPortRows)
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
        // SkipAddressAlloc(주소제외) 행은 IO 슬롯을 소비하지 않으므로 chunked 뷰(주소 인덱스)에서 제외.
        // ARRAY 타입(포인터)도 cursor 미진전이라 IO 인덱스 자리를 차지하지 않으므로 chunked 뷰에서 제외.
        // 외부 정의 공용 변수 / IEC 리터럴 (_T1S, T#200MS 등) 이 IO 주소 자리를 차지하지 않게 함.
        static bool IsArrayType(string? dt) =>
            !string.IsNullOrEmpty(dt) &&
            dt.StartsWith("ARRAY", StringComparison.OrdinalIgnoreCase);
        var ioRows = sec.Rows.Where(r => !r.SkipAddressAlloc && !IsArrayType(r.DataType)).ToList();
        for (int start = 0; start < ioRows.Count; start += ChunkSize)
        {
            var slice = new ObservableCollection<IndexedPatternRow>();
            for (int i = start; i < ioRows.Count && i < start + ChunkSize; i++)
                slice.Add(new IndexedPatternRow(i / 16, i % 16, ioRows[i].Pattern ?? ""));
            sec.Chunks.Add(slice);
        }
        sec.Grid.Visibility = Visibility.Collapsed;
        sec.ChunkedView.Visibility = Visibility.Visible;
    }
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

    private bool FilterDummyRow(object obj)
    {
        if (obj is not DummySignalRow row) return false;
        static bool Match(string v, string f) =>
            string.IsNullOrEmpty(f) || (v != null && v.Contains(f, StringComparison.OrdinalIgnoreCase));
        return Match(row.Flow,     DmFlowFilter)
            && Match(row.Work,     DmWorkFilter)
            && Match(row.Call,     DmCallFilter)
            && Match(row.Symbol,   DmSymbolFilter)
            && Match(row.Address,  DmAddressFilter)
            && Match(row.Type,     DmAreaFilter)
            && Match(row.DataType, DmDataTypeFilter);
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

}
