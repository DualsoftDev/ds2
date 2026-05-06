using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Ds2.Core.Store;
using Microsoft.Win32;
using Promaker.Services;

namespace Promaker.Dialogs;

/// <summary>
/// I/O 조회 다이얼로그 — 읽기 전용. 편집은 TAG Wizard.
/// store 를 받아 IoQueryService 로 직접 행을 생성/새로고침하며, 표시·필터링·CSV 내보내기만 담당한다.
/// 호출부는 DsStore 만 들고 다이얼로그를 띄우면 된다.
/// </summary>
public partial class IoBatchSettingsDialog : Window
{
    private readonly DsStore _store;
    private readonly Action<string?>? _openFBTagMapEdit;
    private readonly ObservableCollection<IoBatchRow> _rows = new();
    private readonly ObservableCollection<DiagnosticItemViewModel> _diagnostics = new();
    private readonly ICollectionView _view;
    private readonly RowFilterDebouncer _filterDebouncer;

    private bool _showOnlyUnmatched;
    private int _unmatchedCount;
    private int _errorCount;
    private int _warningCount;

    // 그리드 헤더 내장 필터 — TextChanged 에서 Tag 키로 Map 업데이트 후 디바운스 Refresh.
    private readonly Dictionary<string, string> _filters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Flow"] = "", ["Work"] = "", ["Device"] = "", ["Api"] = "",
        ["InName"] = "", ["InType"] = "", ["InAddress"] = "",
        ["OutName"] = "", ["OutType"] = "", ["OutAddress"] = "",
    };

    /// <summary>
    /// store 1개만으로 다이얼로그 생성. 행은 IoQueryService 가 만든다.
    /// <paramref name="openFBTagMapEdit"/> 가 주어지면 진단 카드의 "FBTagMap 편집" 버튼이 활성화되어
    /// SystemType 식별자와 함께 호출자에게 전달한다 (TAG Wizard 진입 등). 호출 후 자동 새로고침된다.
    /// </summary>
    public IoBatchSettingsDialog(DsStore store, Action<string?>? openFBTagMapEdit = null)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _openFBTagMapEdit = openFBTagMapEdit;

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        IoGrid.ItemsSource = _view;
        DiagnosticsList.ItemsSource = _diagnostics;

        _filterDebouncer = new RowFilterDebouncer(() => _view.Refresh());

        DataContext = this;

        LoadFromStore();
    }

    // ── 데이터 로드 / 새로고침 ────────────────────────────────────────────

    private void LoadFromStore()
    {
        var prev = Cursor;
        try
        {
            Cursor = Cursors.Wait;

            var qr = IoQueryService.Generate(_store);

            // 행 갱신 — 기존 PropertyChanged 핸들러 정리 후 재구독.
            foreach (var r in _rows) r.PropertyChanged -= Row_PropertyChanged;
            _rows.Clear();
            foreach (var r in qr.Rows)
            {
                r.PropertyChanged += Row_PropertyChanged;
                _rows.Add(r);
            }

            _unmatchedCount = qr.Unmatched.Count;
            _errorCount = qr.ErrorCount;
            _warningCount = qr.WarningCount;
            ShowOnlyUnmatchedCheckBox.Visibility =
                _unmatchedCount > 0 ? Visibility.Visible : Visibility.Collapsed;

            ApplyDiagnostics(qr.Diagnostics);
            UpdateStatusChips();

            BatchDialogHelper.UpdateSelectedCount(_rows, SelectedCountText);
            _view.Refresh();
        }
        finally
        {
            Cursor = prev;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadFromStore();

    private void IoGrid_LayoutUpdated(object? sender, EventArgs e)
    {
        if (IoHeaderGrid.ColumnDefinitions.Count == 0 || IoGrid.Columns.Count == 0)
            return;

        var count = Math.Min(IoHeaderGrid.ColumnDefinitions.Count, IoGrid.Columns.Count);
        for (var i = 0; i < count; i++)
        {
            var actualWidth = IoGrid.Columns[i].ActualWidth;
            if (actualWidth <= 0d)
                continue;

            var currentWidth = IoHeaderGrid.ColumnDefinitions[i].Width;
            if (currentWidth.IsAbsolute && Math.Abs(currentWidth.Value - actualWidth) < 0.5d)
                continue;

            IoHeaderGrid.ColumnDefinitions[i].Width = new GridLength(actualWidth, GridUnitType.Pixel);
        }
    }

    // ── 진단(Diagnostics) ─────────────────────────────────────────────────

    /// <summary>
    /// QueryResult 의 진단을 ViewModel 로 옮기고, 영향 행에 HasError 플래그를 매핑.
    /// 진단이 없으면 패널을 자동으로 닫는다.
    /// </summary>
    private void ApplyDiagnostics(IReadOnlyList<DiagnosticItem> diagnostics)
    {
        _diagnostics.Clear();

        // 행별 인덱스 — (CallId, ApiCallId) → IoBatchRow
        var rowIndex = new Dictionary<(Guid, Guid), IoBatchRow>();
        foreach (var r in _rows)
        {
            r.HasError = false;
            rowIndex[(r.CallId, r.ApiCallId)] = r;
        }

        foreach (var d in diagnostics)
        {
            var matched = new List<IoBatchRow>();
            foreach (var key in d.AffectedRows)
            {
                if (rowIndex.TryGetValue((key.CallId, key.ApiCallId), out var row))
                {
                    matched.Add(row);
                    if (d.Severity == DiagnosticSeverity.Error)
                        row.HasError = true;
                }
            }

            _diagnostics.Add(new DiagnosticItemViewModel(d, matched, _openFBTagMapEdit != null));
        }

        // 진단 없으면 패널 닫기. 있으면 헤더 텍스트만 갱신 (사용자가 직접 칩 클릭으로 열도록 둠).
        if (_diagnostics.Count == 0)
        {
            DiagnosticsPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateDiagnosticsHeader();
        }
    }

    private void UpdateDiagnosticsHeader()
    {
        var parts = new List<string>();
        if (_errorCount > 0) parts.Add($"❌ 오류 {_errorCount}");
        if (_warningCount > 0) parts.Add($"⚠ 경고 {_warningCount}");
        DiagnosticsHeaderText.Text = parts.Count > 0
            ? $"진단 — {string.Join(" / ", parts)}"
            : "진단";
    }

    private void UpdateStatusChips()
    {
        TotalChip.Content = _view != null && _view.Cast<object>().Any()
            ? $"총 {_rows.Count}"
            : $"총 {_rows.Count}";

        if (_warningCount > 0)
        {
            WarningChip.Content = $"⚠ 경고 {_warningCount}";
            WarningChip.Visibility = Visibility.Visible;
        }
        else
        {
            WarningChip.Visibility = Visibility.Collapsed;
        }

        if (_errorCount > 0)
        {
            ErrorChip.Content = $"❌ 오류 {_errorCount}";
            ErrorChip.Visibility = Visibility.Visible;
        }
        else
        {
            ErrorChip.Visibility = Visibility.Collapsed;
        }
    }

    private void DiagnosticsChip_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnostics.Count == 0) return;

        // 칩 클릭은 토글 — 이미 열려있으면 닫고, 닫혀있으면 연다.
        DiagnosticsPanel.Visibility = DiagnosticsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void DiagnosticsClose_Click(object sender, RoutedEventArgs e) =>
        DiagnosticsPanel.Visibility = Visibility.Collapsed;

    /// <summary>
    /// "FBTagMap 편집" 버튼 — 호출자가 주입한 액션으로 SystemType 을 넘기고,
    /// 그 액션(보통 TAG Wizard 모달)이 닫히면 IO 조회를 자동 새로고침해 결과를 즉시 반영.
    /// </summary>
    private void DiagnosticOpenFBTagMap_Click(object sender, RoutedEventArgs e)
    {
        if (_openFBTagMapEdit == null) return;
        if (sender is not Button { DataContext: DiagnosticItemViewModel vm }) return;

        _openFBTagMapEdit.Invoke(vm.SystemType);
        LoadFromStore();
    }

    private void DiagnosticGoToRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DiagnosticItemViewModel vm }) return;
        if (vm.MatchedRows.Count == 0) return;

        var first = vm.MatchedRows[0];

        // 필터 때문에 안 보일 수 있으니 필터를 임시로 비우는 대신, 일단 SelectedItem 만 세팅.
        // (사용자가 필터를 켠 상태에서 점프하길 원할 가능성도 있어 자동 해제는 보류.)
        IoGrid.SelectedItem = first;
        IoGrid.ScrollIntoView(first);
        IoGrid.Focus();
    }

    // ── 필터링 ────────────────────────────────────────────────────────────

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not string key) return;
        _filters[key] = tb.Text ?? "";
        _filterDebouncer.Bump();
    }

    private bool FilterRow(object obj)
    {
        if (obj is not IoBatchRow row) return false;

        if (_showOnlyUnmatched && !row.IsUnmatched)
            return false;

        return Match(row.Flow,        F("Flow"))
            && Match(row.Work,        F("Work"))
            && Match(row.Device,      F("Device"))
            && Match(row.Api,         F("Api"))
            && Match(row.InSymbol,    F("InName"))
            && Match(row.InDataType,  F("InType"))
            && Match(row.InAddress,   F("InAddress"))
            && Match(row.OutSymbol,   F("OutName"))
            && Match(row.OutDataType, F("OutType"))
            && Match(row.OutAddress,  F("OutAddress"));
    }

    private string F(string key) => _filters.TryGetValue(key, out var v) ? v : "";

    private static bool Match(string value, string filter) =>
        string.IsNullOrEmpty(filter)
        || (value != null && value.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private void ShowOnlyUnmatched_Changed(object sender, RoutedEventArgs e)
    {
        _showOnlyUnmatched = ShowOnlyUnmatchedCheckBox.IsChecked == true;
        _view.Refresh();
    }

    // ── 선택/체크박스 ──────────────────────────────────────────────────────

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IoBatchRow.IsSelected))
            BatchDialogHelper.UpdateSelectedCount(_rows, SelectedCountText);
    }

    private void CheckSelected_Click(object sender, RoutedEventArgs e) =>
        BatchDialogHelper.CheckGridSelected<IoBatchRow>(IoGrid);

    private void UncheckSelected_Click(object sender, RoutedEventArgs e) =>
        BatchDialogHelper.UncheckGridSelected<IoBatchRow>(IoGrid);

    private void CheckAll_Click(object sender, RoutedEventArgs e) =>
        BatchDialogHelper.CheckAll(_rows);

    private void UncheckAll_Click(object sender, RoutedEventArgs e) =>
        BatchDialogHelper.UncheckAll(_rows);

    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        BatchDialogHelper.DeselectOnEmptyAreaClick(sender, e);

    private void RowCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: IoBatchRow row } cb) return;
        BatchDialogHelper.ApplyCheckStateToSelectedRows(IoGrid, row, cb.IsChecked == true);
    }

    // ── CSV 내보내기 ──────────────────────────────────────────────────────

    /// <summary>현재 화면(필터 적용 후)의 행을 UTF-8 BOM CSV 로 내보낸다.</summary>
    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var rows = _view.Cast<IoBatchRow>().ToList();
        if (rows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox("내보낼 행이 없습니다.",
                "CSV 내보내기", MessageBoxButton.OK, "ℹ");
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title      = "CSV 저장",
            Filter     = "CSV 파일 (*.csv)|*.csv|모든 파일 (*.*)|*.*",
            FileName   = $"IoList_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            DefaultExt = ".csv",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Flow,TagName,DataType,Address,Description");
            int emitted = 0;
            foreach (var r in rows)
            {
                emitted += AppendCsvLine(sb, r, isInput: true);
                emitted += AppendCsvLine(sb, r, isInput: false);
            }

            File.WriteAllText(dlg.FileName, sb.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var openResult = DialogHelpers.ShowThemedMessageBox(
                $"{emitted}개 태그를 저장했습니다.\n{dlg.FileName}\n\n파일을 바로 열어 볼까요?",
                "CSV 내보내기 완료", MessageBoxButton.YesNo, "✓");

            if (openResult == MessageBoxResult.Yes) TryOpenFile(dlg.FileName);
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox($"저장 실패:\n{ex.Message}",
                "CSV 내보내기 오류", MessageBoxButton.OK, "✖");
        }
    }

    private static int AppendCsvLine(StringBuilder sb, IoBatchRow r, bool isInput)
    {
        var symbol  = isInput ? r.InSymbol    : r.OutSymbol;
        var address = isInput ? r.InAddress   : r.OutAddress;
        var dtype   = isInput ? r.InDataType  : r.OutDataType;
        if (string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(address))
            return 0;

        var dir = isInput ? "In" : "Out";
        sb.Append(BatchDialogHelper.EscapeCsvField(r.Flow));    sb.Append(',');
        sb.Append(BatchDialogHelper.EscapeCsvField(symbol));    sb.Append(',');
        sb.Append(BatchDialogHelper.EscapeCsvField(dtype));     sb.Append(',');
        sb.Append(BatchDialogHelper.EscapeCsvField(address));   sb.Append(',');
        sb.Append(BatchDialogHelper.EscapeCsvField($"{r.Work}/{r.Device}/{r.Api} ({dir})"));
        sb.AppendLine();
        return 1;
    }

    private void TryOpenFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox($"파일 열기에 실패했습니다:\n{ex.Message}",
                "열기 실패", MessageBoxButton.OK, "⚠");
        }
    }
}

/// <summary>
/// I/O 조회용 데이터 행 — 표시 + 매칭 강조용 IsUnmatched/HasError 만 변경 가능.
/// IoQueryService.Generate 이 채워서 반환한다.
/// </summary>
public sealed class IoBatchRow : BatchRowBase
{
    public IoBatchRow(Guid callId, Guid apiCallId, string flow, string work, string device, string api,
                      string inAddress, string inSymbol, string outAddress, string outSymbol,
                      string outDataType = "BOOL", string inDataType = "BOOL")
    {
        CallId = callId;
        ApiCallId = apiCallId;
        Flow = flow;
        Work = work;
        Device = device;
        Api = api;
        InAddress = inAddress;
        InSymbol = inSymbol;
        OutAddress = outAddress;
        OutSymbol = outSymbol;
        OutDataType = outDataType;
        InDataType = inDataType;
    }

    public Guid CallId { get; }
    public Guid ApiCallId { get; }
    public string Flow { get; }
    public string Work { get; }
    public string Device { get; }
    public string Api { get; }

    public string InAddress   { get; }
    public string InSymbol    { get; }
    public string OutAddress  { get; }
    public string OutSymbol   { get; }
    public string OutDataType { get; }
    public string InDataType  { get; }
}

/// <summary>
/// 진단 패널 항목 표시용 ViewModel.
/// XAML 바인딩에 필요한 표시 속성(Icon/AccentBrush/HasRawMessage 등)을 노출하고,
/// "해당 행 보기" 클릭 시 점프할 행 목록을 들고 있다.
/// </summary>
public sealed class DiagnosticItemViewModel
{
    private static readonly Brush ErrorBrush   = MakeBrush(0xE1, 0x5B, 0x5B);
    private static readonly Brush WarningBrush = MakeBrush(0xF2, 0xB1, 0x34);
    private static readonly Brush InfoBrush    = MakeBrush(0x57, 0xC0, 0x6D);

    public DiagnosticItemViewModel(
        DiagnosticItem source,
        IReadOnlyList<IoBatchRow> matchedRows,
        bool fbTagMapEditAvailable = false)
    {
        Source = source;
        MatchedRows = matchedRows;
        FBTagMapEditAvailable = fbTagMapEditAvailable;
    }

    public DiagnosticItem Source { get; }
    public IReadOnlyList<IoBatchRow> MatchedRows { get; }
    public bool FBTagMapEditAvailable { get; }
    public string? SystemType => Source.SystemType;

    public string Icon => Source.Severity switch
    {
        DiagnosticSeverity.Error   => "❌",
        DiagnosticSeverity.Warning => "⚠",
        _                          => "ℹ",
    };

    public Brush AccentBrush => Source.Severity switch
    {
        DiagnosticSeverity.Error   => ErrorBrush,
        DiagnosticSeverity.Warning => WarningBrush,
        _                          => InfoBrush,
    };

    public string Title       => Source.Title;
    public string Detail      => Source.Detail;
    public string? RawMessage => Source.RawMessage;

    public bool HasRawMessage    => !string.IsNullOrEmpty(Source.RawMessage);
    public bool HasAffectedRows  => MatchedRows.Count > 0;

    /// <summary>SystemType 이 식별되고 호출자가 편집 액션을 제공했을 때만 버튼 노출.</summary>
    public bool CanOpenFBTagMap  => FBTagMapEditAvailable && !string.IsNullOrEmpty(SystemType);

    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
