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
    private readonly ObservableCollection<IoBatchRow> _rows = new();
    private readonly ICollectionView _view;
    private readonly RowFilterDebouncer _filterDebouncer;

    private bool _showOnlyUnmatched;
    private int _unmatchedCount;

    // 그리드 헤더 내장 필터 — TextChanged 에서 Tag 키로 Map 업데이트 후 디바운스 Refresh.
    private readonly Dictionary<string, string> _filters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Flow"] = "", ["Work"] = "", ["Device"] = "", ["Api"] = "",
        ["InName"] = "", ["InType"] = "", ["InAddress"] = "",
        ["OutName"] = "", ["OutType"] = "", ["OutAddress"] = "",
    };

    /// <summary>
    /// store 1개만으로 다이얼로그 생성. 행은 IoQueryService 가 만든다.
    /// 호출부에서 행을 미리 만들어 넘기던 옛 시그니처는 제거 — 단일 책임.
    /// </summary>
    public IoBatchSettingsDialog(DsStore store)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        IoGrid.ItemsSource = _view;

        _filterDebouncer = new RowFilterDebouncer(() => _view.Refresh());

        DataContext = this;

        // 자동 1회 생성 — 사용자 입력 없이 진입 즉시 결과 표시.
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
            ShowOnlyUnmatchedCheckBox.Visibility =
                _unmatchedCount > 0 ? Visibility.Visible : Visibility.Collapsed;

            BatchDialogHelper.UpdateSelectedCount(_rows, SelectedCountText);
            UpdateStatus(qr);
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

    private void UpdateStatus(IoQueryService.QueryResult qr)
    {
        var sb = new StringBuilder();
        sb.Append($"총 {_rows.Count}개");
        if (_unmatchedCount > 0)
            sb.Append($" / 매칭 실패 {_unmatchedCount}개");
        if (qr.HasError)
            sb.Append($" / 오류 {qr.ErrorMessages.Count}건");
        StatusText.Text = sb.ToString();
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
/// I/O 조회용 데이터 행 — 표시 + 매칭 강조용 IsUnmatched 만 변경 가능.
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
