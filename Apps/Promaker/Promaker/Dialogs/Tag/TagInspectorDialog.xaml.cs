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
using AAStoPLC.TagWizard;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.Win32;
using Promaker.Services;

namespace Promaker.Dialogs;

/// <summary>
/// IO·태그 확인 다이얼로그 — 읽기 전용 검증 도구. 편집은 TAG Wizard / PropertyPanel.
/// store 를 받아 IoQueryService 로 직접 행을 생성/새로고침하며, 표시·필터링·CSV 내보내기·진단만 담당한다.
/// 탭: IO 신호 / Dummy 신호 / 사용자 태그.
/// </summary>
public partial class TagInspectorDialog : Window
{
    private readonly DsStore _store;
    private readonly Action<string?>? _openFBTagMapEdit;
    private readonly ObservableCollection<IoBatchRow> _rows = new();
    private readonly ObservableCollection<DummySignalRow> _dummyRows = new();
    private readonly ObservableCollection<DiagnosticItemViewModel> _diagnostics = new();
    private readonly ObservableCollection<ProjectUserTagRow> _userTagRows = new();
    private readonly ICollectionView _view;
    private readonly ICollectionView _userTagView;
    private readonly RowFilterDebouncer _filterDebouncer;
    private string _userTagSearch = string.Empty;

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
    public TagInspectorDialog(DsStore store, Action<string?>? openFBTagMapEdit = null)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _openFBTagMapEdit = openFBTagMapEdit;

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        IoGrid.ItemsSource = _view;
        DummyGrid.ItemsSource = _dummyRows;
        DiagnosticsList.ItemsSource = _diagnostics;

        _userTagView = CollectionViewSource.GetDefaultView(_userTagRows);
        _userTagView.Filter = FilterUserTagRow;
        UserTagsGrid.ItemsSource = _userTagView;

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

            // Dummy 신호 행 갱신.
            _dummyRows.Clear();
            foreach (var d in qr.DummyRows) _dummyRows.Add(d);

            // UserTags 행 갱신 — 프로젝트 전체 평탄화.
            _userTagRows.Clear();
            foreach (var u in _store.GetAllUserTagsForProject())
                _userTagRows.Add(u);

            // 탭 헤더에 카운트 부착.
            IoTab.Header       = $"IO 신호 ({_rows.Count})";
            DummyTab.Header    = $"Dummy 신호 ({_dummyRows.Count})";
            UserTagsTab.Header = $"사용자 태그 ({_userTagRows.Count})";

            _unmatchedCount = qr.Unmatched.Count;
            _errorCount = qr.ErrorCount;
            _warningCount = qr.WarningCount;
            ShowOnlyUnmatchedCheckBox.Visibility =
                _unmatchedCount > 0 ? Visibility.Visible : Visibility.Collapsed;

            ApplyDiagnostics(qr.Diagnostics);
            UpdateStatusChips();

            BatchDialogHelper.UpdateSelectedCount(_rows, SelectedCountText);
            _view.Refresh();
            _userTagView.Refresh();
        }
        finally
        {
            Cursor = prev;
        }
    }

    // ── UserTags 탭 ───────────────────────────────────────────────────────

    private bool FilterUserTagRow(object obj)
    {
        if (string.IsNullOrWhiteSpace(_userTagSearch)) return true;
        if (obj is not ProjectUserTagRow r) return false;
        var q = _userTagSearch;
        return (r.SystemName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (r.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (r.TagAddress?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (r.LogLevel?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (r.ValueType?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void UserTagSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        _userTagSearch = tb.Text?.Trim() ?? string.Empty;
        _userTagView.Refresh();
    }

    private void UserTagCsvExport_Click(object sender, RoutedEventArgs e)
    {
        if (_userTagRows.Count == 0)
        {
            MessageBox.Show(this, "내보낼 사용자 태그가 없습니다.", "CSV 내보내기",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "사용자 태그 CSV 내보내기",
            FileName = "UserTags.csv",
            Filter = "CSV 파일 (*.csv)|*.csv|모든 파일 (*.*)|*.*",
            DefaultExt = "csv"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("System,이름,로그 레벨,태그 주소,값 타입");
            foreach (var r in _userTagRows)
                sb.AppendLine($"{CsvEscape(r.SystemName)},{CsvEscape(r.Name)},{CsvEscape(r.LogLevel)},{CsvEscape(r.TagAddress)},{CsvEscape(r.ValueType)}");

            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"CSV 내보내기 실패: {ex.Message}", "오류",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string CsvEscape(string? value)
    {
        var v = value ?? string.Empty;
        return v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? $"\"{v.Replace("\"", "\"\"")}\""
            : v;
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

}
