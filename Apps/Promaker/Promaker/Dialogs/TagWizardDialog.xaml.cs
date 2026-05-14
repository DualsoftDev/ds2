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

    // ── Step 4: 신호 매핑 ──────────────────────────────────────────────

    /// <summary>조건식 컬럼 셀 클릭 → 공용 식 편집기 열기.
    /// Pre-FB 조건 편집 — 현 SystemType 의 IW/QW/MW Pattern 을 변수 후보로 제공.</summary>
    private void EditPreFbCondition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SignalPatternRow row) return;
        var sysType = _currentDeviceTemplateFile;
        if (string.IsNullOrWhiteSpace(sysType))
        {
            DialogHelpers.ShowThemedMessageBox("SystemType 을 먼저 선택하세요.", "TAG Wizard",
                MessageBoxButton.OK, "⚠");
            return;
        }
        var provider = new Promaker.Controls.ExpressionEditor.Providers.SignalPatternSymbolProvider(_store, sysType);
        var initial = Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.FromCore(row.PreFbCondition);
        var dlg = new Promaker.Controls.ExpressionEditor.Views.ExpressionEditorWindow(initial, provider,
            $"Pre-FB 조건 편집 — {row.TargetFBPort}") { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            // Var 빈 노드만 있으면 조건 제거 (간소화)
            var node = dlg.Result;
            if (node.Kind == AAStoPLC.LadderEditor.Expression.ExprKind.Var
                && string.IsNullOrWhiteSpace(node.Symbol)
                && node.Children.Count == 0)
                row.PreFbCondition = null;
            else
                row.PreFbCondition = Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.ToCore(node);
            // 자동저장 — PreFbCondition setter 가 PropertyChanged 발화 → HookAutoSave 가 PersistCurrentPreset.
        }
    }

    /// <summary>AUX 포트 콤보의 입력/출력/전체 필터 라디오 변경 핸들러.</summary>
    /// <summary>AUX 포트 entry 의 조건식 편집 — 공용 식 편집기 열기.</summary>
    private void EditAuxPortCondition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not AuxPortRow row) return;
        var sysType = _currentDeviceTemplateFile;
        if (string.IsNullOrWhiteSpace(sysType))
        {
            DialogHelpers.ShowThemedMessageBox("SystemType 을 먼저 선택하세요.", "TAG Wizard",
                MessageBoxButton.OK, "⚠");
            return;
        }
        var provider = new Promaker.Controls.ExpressionEditor.Providers.SignalPatternSymbolProvider(_store, sysType);
        var initial = Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.FromCore(row.Condition);
        var dlg = new Promaker.Controls.ExpressionEditor.Views.ExpressionEditorWindow(initial, provider,
            $"AUX 조건식 — {row.ApiName} → {row.TargetFBPort}") { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            var node = dlg.Result;
            if (node.Kind == AAStoPLC.LadderEditor.Expression.ExprKind.Var
                && string.IsNullOrWhiteSpace(node.Symbol)
                && node.Children.Count == 0)
                row.Condition = null;
            else
                row.Condition = Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.ToCore(node);
        }
    }

    /// <summary>AUX 그리드 선택 트리거 셀 — 클릭 시 행 선택 (Ctrl/Shift 지원).</summary>
    private void AuxRowSelector_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not AuxPortRow row) return;
        if (AuxPortGrid == null) return;
        bool ctrl  = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift)   != 0;
        if (ctrl)
        {
            if (AuxPortGrid.SelectedItems.Contains(row)) AuxPortGrid.SelectedItems.Remove(row);
            else AuxPortGrid.SelectedItems.Add(row);
        }
        else if (shift && AuxPortGrid.SelectedItem is AuxPortRow anchor)
        {
            int a = _auxPortRows.IndexOf(anchor), b = _auxPortRows.IndexOf(row);
            if (a >= 0 && b >= 0)
            {
                int lo = System.Math.Min(a, b), hi = System.Math.Max(a, b);
                AuxPortGrid.SelectedItems.Clear();
                for (int i = lo; i <= hi; i++) AuxPortGrid.SelectedItems.Add(_auxPortRows[i]);
            }
        }
        else
        {
            AuxPortGrid.SelectedItems.Clear();
            AuxPortGrid.SelectedItems.Add(row);
        }
        AuxPortGrid.Focus();
        e.Handled = true;
    }

    /// <summary>탭 변경 — AUX 포트 탭 활성화 시 심볼 후보를 IW/QW/MW 의 최신 패턴으로 재스냅샷.</summary>
    private void SignalSectionTab_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.Source != SignalSectionTabControl) return;
        if (SignalSectionTabControl.SelectedItem is not System.Windows.Controls.TabItem tab) return;
        var header = tab.Header as string ?? "";
        if (!header.Contains("AUX")) return;
        var sysType = _currentDeviceTemplateFile;
        if (string.IsNullOrWhiteSpace(sysType)) return;
        var fresh = BuildAuxApiOptions(sysType);
        foreach (var row in _auxPortRows)
        {
            row.ApiOptions = fresh;
            row.RaisePropertyChanged(nameof(AuxPortRow.ApiOptions));
        }
    }

    /// <summary>AUX 포트 그리드에 빈 행 추가 — 사용자가 한 API 에 여러 포트 매핑 가능.</summary>
    private void AddAuxPortRow_Click(object sender, RoutedEventArgs e)
    {
        var fb = GlobalFBTypeCombo?.SelectedItem as string ?? "";
        var sysType = _currentDeviceTemplateFile;
        _auxPortRows.Add(HookAutoSave(new AuxPortRow
        {
            ApiName = "",
            TargetFBType = fb,
            TargetFBPort = "",
            Kind = "DirectFB",
            AuxKind = "AutoAux",
            ApiOptions = BuildAuxApiOptions(sysType),
        }));
        PersistCurrentPreset();
    }

    /// <summary>선택 행 삭제.</summary>
    private void RemoveAuxPortRow_Click(object sender, RoutedEventArgs e)
    {
        if (AuxPortGrid == null) return;
        var selected = AuxPortGrid.SelectedItems.Cast<AuxPortRow>().ToList();
        if (selected.Count == 0) return;
        foreach (var row in selected) _auxPortRows.Remove(row);
        PersistCurrentPreset();
    }

    private void MoveAuxRowUp_Click(object sender, RoutedEventArgs e) =>
        MoveSelected(AuxPortGrid, _auxPortRows, up: true);
    private void MoveAuxRowDown_Click(object sender, RoutedEventArgs e) =>
        MoveSelected(AuxPortGrid, _auxPortRows, up: false);

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

    private record ClipboardEnvelope(string Type, string Json);

    private void CopyGridSelection(System.Windows.Controls.DataGrid grid)
    {
        try
        {
            object[] selected = grid.SelectedItems.Cast<object>().ToArray();
            if (selected.Length == 0) return;

            string typeTag;
            string payload;
            if (selected[0] is SignalPatternRow)
            {
                typeTag = "SignalPatternRow";
                var items = selected.Cast<SignalPatternRow>()
                    .Select(r => new SignalRowClipboardItem(
                        r.ApiName ?? "", r.Pattern ?? "", r.TargetFBPort ?? "",
                        r.SkipAddressAlloc, r.IsSpare, r.UserDataType ?? "",
                        Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.FromCore(r.PreFbCondition)))
                    .ToList();
                payload = System.Text.Json.JsonSerializer.Serialize(items);
            }
            else if (selected[0] is AuxPortRow)
            {
                typeTag = "AuxPortRow";
                var items = selected.Cast<AuxPortRow>()
                    .Select(r => new AuxPortClipboardItem(
                        r.ApiName ?? "", r.TargetFBPort ?? "",
                        r.Kind ?? "DirectFB", r.AuxKind ?? "AutoAux",
                        Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.FromCore(r.Condition)))
                    .ToList();
                payload = System.Text.Json.JsonSerializer.Serialize(items);
            }
            else return;

            var envelope = new ClipboardEnvelope(typeTag, payload);
            System.Windows.Clipboard.SetText(System.Text.Json.JsonSerializer.Serialize(envelope));
        }
        catch { /* clipboard 실패 시 silent */ }
    }

    private void PasteGridSelection(System.Windows.Controls.DataGrid grid)
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) return;
            var raw = System.Windows.Clipboard.GetText();
            var env = System.Text.Json.JsonSerializer.Deserialize<ClipboardEnvelope>(raw);
            if (env == null) return;

            var fb = GlobalFBTypeCombo?.SelectedItem as string ?? "";
            var sysType = _currentDeviceTemplateFile;

            // SignalPatternRow → IW/QW/MW 그리드끼리 호환.
            if (env.Type == "SignalPatternRow"
                && (grid == IwSignalGrid || grid == QwSignalGrid || grid == MwSignalGrid))
            {
                var sec = AllSections().FirstOrDefault(s => s.Grid == grid);
                if (sec == null) return;
                var items = System.Text.Json.JsonSerializer.Deserialize<List<SignalRowClipboardItem>>(env.Json) ?? new();
                foreach (var it in items)
                {
                    sec.Rows.Add(HookAutoSave(new SignalPatternRow
                    {
                        ApiName          = it.ApiName,
                        Pattern          = it.Pattern,
                        TargetFBType     = fb,
                        TargetFBPort     = it.TargetFBPort,
                        SkipAddressAlloc = it.SkipAddressAlloc,
                        IsSpare          = it.IsSpare,
                        UserDataType     = it.UserDataType,
                        PreFbCondition   = Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.ToCore(it.PreFbCondition),
                    }));
                }
                PersistCurrentPreset();
            }
            // AuxPortRow → AUX 그리드만.
            else if (env.Type == "AuxPortRow" && grid == AuxPortGrid)
            {
                var items = System.Text.Json.JsonSerializer.Deserialize<List<AuxPortClipboardItem>>(env.Json) ?? new();
                var apiOpts = BuildAuxApiOptions(sysType);
                foreach (var it in items)
                {
                    _auxPortRows.Add(HookAutoSave(new AuxPortRow
                    {
                        ApiName      = it.ApiName,
                        TargetFBType = fb,
                        TargetFBPort = it.TargetFBPort,
                        Kind         = string.IsNullOrEmpty(it.Kind) ? "DirectFB" : it.Kind,
                        AuxKind      = string.IsNullOrEmpty(it.AuxKind) ? "AutoAux" : it.AuxKind,
                        Condition    = Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.ToCore(it.Condition),
                        ApiOptions   = apiOpts,
                    }));
                }
                PersistCurrentPreset();
            }
            // 타입 불일치 시 silent (서로 다른 그리드 type)
        }
        catch { }
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
            // Step 3 적용: ConfirmAndApplyPatterns 가 단일 통합 리포트까지 처리.
            if (!ConfirmAndApplyPatterns()) return;
            _appliedInStep3 = true;
            UpdateButtons();
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
        if (step != 3) _appliedInStep3 = false;

        // 컨텐츠 표시
        Step1Content.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Content.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Content.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

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
    /// 버튼 상태 업데이트 — 3단계 구조: Step 3 적용 후엔 닫기 버튼만 노출.
    /// </summary>
    private void UpdateButtons()
    {
        BackButton.Visibility = _currentStep > 1 ? Visibility.Visible : Visibility.Collapsed;

        var atFinalApplied = _currentStep == 3 && _appliedInStep3;
        if (atFinalApplied)
        {
            NextButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
        }
        else
        {
            NextButton.Visibility = Visibility.Visible;
            CloseButton.Visibility = Visibility.Collapsed;
            if (_currentStep == 1)      NextButton.Content = "다음 ▶";
            else if (_currentStep == 2) NextButton.Content = "신호 생성 ▶";
            else if (_currentStep == 3) NextButton.Content = "적용 ▶";
        }
    }

    private bool _appliedInStep3;
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
    Ds2.Core.FbInputExpr? _preFbCondition;

    /// <summary>Pre-FB 입력식 — null 이면 단일 변수 와이어, 비어있지 않으면 LD contact 트리로 FB 핀 와이어.</summary>
    public Ds2.Core.FbInputExpr? PreFbCondition
    {
        get => _preFbCondition;
        set
        {
            _preFbCondition = value;
            OnPropertyChanged(nameof(PreFbCondition));
            OnPropertyChanged(nameof(PreFbConditionSummary));
            OnPropertyChanged(nameof(HasPreFbCondition));
        }
    }

    /// <summary>그리드 셀 표시용 한 줄 요약 — ST 형태.</summary>
    public string PreFbConditionSummary
    {
        get
        {
            if (_preFbCondition == null) return "";
            var node = Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.FromCore(_preFbCondition);
            return AAStoPLC.LadderEditor.Expression.CoilConditionConverter.ToStPreview(node);
        }
    }

    public bool HasPreFbCondition => _preFbCondition != null;

    public string ApiName
    {
        get => _apiName;
        set => SetProperty(ref _apiName, value ?? "");
    }

    /// 패턴 setter — ApiName 은 사용자가 명시적으로 선택한 값 그대로 보존.
    /// (Pattern 에 $(A)/$(C) 가 없어도 ApiName 이 "7TH_IN_OK" 같이 의미 있는 값이면 유지.)
    public string Pattern
    {
        get => _pattern;
        set
        {
            if (!SetProperty(ref _pattern, value ?? "")) return;
            OnPropertyChanged(nameof(DataType));
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
            OnPropertyChanged(nameof(IsDataTypeUserEditable));
            OnPropertyChanged(nameof(IsDataTypeFromFb));
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
            OnPropertyChanged(nameof(IsDataTypeUserEditable));
            OnPropertyChanged(nameof(IsDataTypeFromFb));
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
            OnPropertyChanged(nameof(IsDataTypeUserEditable));
            OnPropertyChanged(nameof(IsDataTypeFromFb));
        }
    }

    /// IsSpare 의 반대 — UI 셀 IsEnabled 바인딩용.
    public bool IsEditable => !_isSpare;

    /// 사용자가 직접 선택한 데이터 타입 — FB Local Label 미설정 시에만 의미 있음.
    /// FB 가 설정되면 FB 포트 타입이 우선되어 무시됨.
    string _userDataType = "";
    public string UserDataType
    {
        get => _userDataType;
        set
        {
            if (!SetProperty(ref _userDataType, value ?? "")) return;
            OnPropertyChanged(nameof(DataType));
        }
    }

    /// 선택된 FB Local Label 의 IEC 데이터 타입.
    /// 우선순위: 시스템 플래그 → IsSpare → FB 포트 → 사용자 선택.
    public string DataType
    {
        get
        {
            var sysType = Plc.Xgi.XgiSystemFlags.tryGetTypeName(_pattern ?? "");
            if (Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(sysType)) return sysType.Value;
            if (_isSpare) return "BOOL";
            if (!string.IsNullOrEmpty(_targetFBType) && !string.IsNullOrEmpty(_targetFBPort))
                return FBPortCatalog.GetPortTypeMap(_targetFBType).TryGetValue(_targetFBPort, out var t) ? t : "";
            return _userDataType ?? "";
        }
        set
        {
            // FB 가 미설정일 때만 사용자 선택 의미 있음 — Cell 콤보 SelectedValue=DataType TwoWay 바인딩 호환.
            if (string.IsNullOrEmpty(_targetFBType) || string.IsNullOrEmpty(_targetFBPort))
                UserDataType = value ?? "";
        }
    }

    /// FB 가 미설정 = 사용자 선택 가능. FB 가 설정되면 read-only.
    public bool IsDataTypeUserEditable =>
        !_isSpare
        && (string.IsNullOrEmpty(_targetFBType) || string.IsNullOrEmpty(_targetFBPort));

    /// FB 가 설정되어 데이터타입이 FB 포트로부터 자동 결정됨 — IsEditable 콤보의 IsReadOnly 바인딩.
    public bool IsDataTypeFromFb => !IsDataTypeUserEditable;

    /// 사용자 선택 가능한 IEC 표준 데이터 타입 후보.
    public static System.Collections.Generic.IReadOnlyList<string> StandardDataTypes { get; }
        = new[] { "", "BOOL", "BYTE", "WORD", "DWORD", "LWORD", "SINT", "INT", "DINT", "LINT",
                  "USINT", "UINT", "UDINT", "ULINT", "REAL", "LREAL", "TIME", "STRING" };

    /// 선택된 FB 의 Local Label 목록 (콤보 데이터소스)
    public System.Collections.Generic.IReadOnlyList<string> PortOptions =>
        FbPortOptionsHelper.Get(_targetFBType);
}

/// AUX 포트 콤보 옵션의 방향 필터 — 입력/출력/전체.
public enum AuxPortDirectionFilter { All = 0, Input = 1, Output = 2 }

/// <summary>
/// AUX 포트 매핑 행 (AUX 포트 탭 그리드 바인딩용).
/// 하나의 API 당 하나의 행 — AutoAux/ComAux 포트 이름을 FB 포트 드롭다운에서 선택.
/// SystemType 이 바뀌면 전체 행 리로드 및 TargetFBType 변경에 따라 PortOptions 갱신.
/// </summary>
public class AuxPortRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    /// 방향 필터 — 모든 행이 공유하는 정적 상태. 변경 시 NotifyAll() 로 PortOptions 갱신.
    public static AuxPortDirectionFilter DirectionFilter { get; private set; } = AuxPortDirectionFilter.All;

    private static event System.Action? FilterChanged;

    public static void SetDirectionFilter(AuxPortDirectionFilter mode)
    {
        if (DirectionFilter == mode) return;
        DirectionFilter = mode;
        FilterChanged?.Invoke();
    }

    string _apiName = "", _targetFBType = "", _targetFBPort = "", _kind = "DirectFB", _auxKind = "AutoAux";
    Ds2.Core.FbInputExpr? _condition;

    public AuxPortRow()
    {
        FilterChanged += () => OnPropertyChanged(nameof(PortOptions));
    }

    public string ApiName      { get => _apiName;      set => SetProperty(ref _apiName, value ?? ""); }
    /// API 이름 콤보 후보 — row 생성 시 스냅샷 주입.
    /// 동적 RelativeSource 바인딩 race (ItemsSource 평가 지연 → SelectedItem 매칭 실패 → TwoWay null 역기록) 회피.
    public System.Collections.Generic.IReadOnlyList<string> ApiOptions { get; set; } = System.Array.Empty<string>();

    /// 외부에서 ApiOptions 갱신 후 콤보 ItemsSource 재바인딩 트리거용.
    public void RaisePropertyChanged(string propertyName) => OnPropertyChanged(propertyName);
    /// FB 입력 포트 (FB Local Label).
    public string TargetFBPort { get => _targetFBPort; set => SetProperty(ref _targetFBPort, value ?? ""); }
    /// 와이어 종류 — "DirectFB" / "AuxCoil".
    public string Kind
    {
        get => _kind;
        set
        {
            if (!SetProperty(ref _kind, value ?? "DirectFB")) return;
            OnPropertyChanged(nameof(IsAuxCoil));
        }
    }
    /// AuxCoil 일 때 CallCondition 속성 매핑 — "AutoAux" / "ComAux".
    public string AuxKind { get => _auxKind; set => SetProperty(ref _auxKind, value ?? "AutoAux"); }
    /// XAML AuxKind 콤보 IsEnabled 바인딩.
    public bool IsAuxCoil =>
        string.Equals(_kind, "AuxCoil", System.StringComparison.OrdinalIgnoreCase);

    /// 사용자 정의 추가 수식 — 자동 합성 조건과 AND 결합.
    public Ds2.Core.FbInputExpr? Condition
    {
        get => _condition;
        set
        {
            _condition = value;
            OnPropertyChanged(nameof(Condition));
            OnPropertyChanged(nameof(ConditionSummary));
        }
    }
    /// 그리드 셀 표시용 ST 한 줄 요약.
    public string ConditionSummary
    {
        get
        {
            if (_condition == null) return "";
            var node = Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.FromCore(_condition);
            return AAStoPLC.LadderEditor.Expression.CoilConditionConverter.ToStPreview(node);
        }
    }

    /// Kind 콤보 후보.
    public static System.Collections.Generic.IReadOnlyList<string> KindOptions { get; }
        = new[] { "DirectFB", "AuxCoil" };
    /// AuxKind 콤보 후보 — None 은 CallCondition 미사용 (entry.Condition 만 사용).
    public static System.Collections.Generic.IReadOnlyList<string> AuxKindOptions { get; }
        = new[] { "AutoAux", "ComAux", "None" };

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

    /// AUX 콤보 옵션 — 방향 필터 적용. 빈 항목은 항상 첫 행 (선택 해제용).
    public System.Collections.Generic.IReadOnlyList<string> PortOptions =>
        FbPortOptionsHelper.GetByDirection(_targetFBType, DirectionFilter);
}

/// AUX 매핑 클립보드 직렬화 단위.
public sealed record AuxPortClipboardItem(
    string ApiName,
    string TargetFBPort,
    string Kind,
    string AuxKind,
    AAStoPLC.LadderEditor.Expression.ExprNode? Condition);

/// <summary>
/// EndPortMap 행 — API 이름 → 완료 FB 출력 포트 매핑.
/// PLC 인과 자동 게이팅 (A 의 완료 → B 시작) 에 사용. preset 단위 1:1 정적 매핑.
/// </summary>
public class EndPortRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    string _apiName = "", _endPort = "", _targetFBType = "";

    public string ApiName { get => _apiName; set => SetProperty(ref _apiName, value ?? ""); }
    public string EndPort { get => _endPort; set => SetProperty(ref _endPort, value ?? ""); }

    public System.Collections.Generic.IReadOnlyList<string> ApiOptions { get; set; }
        = System.Array.Empty<string>();

    public string TargetFBType
    {
        get => _targetFBType;
        set
        {
            if (!SetProperty(ref _targetFBType, value ?? "")) return;
            OnPropertyChanged(nameof(PortOptions));
        }
    }

    /// 완료 OUT 포트 후보 — FB 의 출력 포트만.
    public System.Collections.Generic.IReadOnlyList<string> PortOptions =>
        FbPortOptionsHelper.GetByDirection(_targetFBType, AuxPortDirectionFilter.Output);
}

/// IW/QW/MW 신호 패턴 행 클립보드 직렬화 단위 — 동일 타입 그리드끼리 호환.
public sealed record SignalRowClipboardItem(
    string ApiName,
    string Pattern,
    string TargetFBPort,
    bool   SkipAddressAlloc,
    bool   IsSpare,
    string UserDataType,
    AAStoPLC.LadderEditor.Expression.ExprNode? PreFbCondition);

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

    /// <summary>방향(입력/출력/전체) 필터를 적용한 FB Local Label 콤보 옵션.</summary>
    public static System.Collections.Generic.IReadOnlyList<string> GetByDirection(string? fbType, AuxPortDirectionFilter mode)
    {
        if (string.IsNullOrEmpty(fbType)) return System.Array.Empty<string>();
        var (inputs, outputs) = FBPortCatalog.GetPortsByDirection(fbType!);
        var result = new System.Collections.Generic.List<string> { "" };
        switch (mode)
        {
            case AuxPortDirectionFilter.Input:  result.AddRange(inputs);  break;
            case AuxPortDirectionFilter.Output: result.AddRange(outputs); break;
            default: result.AddRange(inputs); result.AddRange(outputs); break;
        }
        return result;
    }
}
