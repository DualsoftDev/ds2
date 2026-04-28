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

namespace Promaker.Dialogs;

public partial class IoBatchSettingsDialog : Window
{
    private readonly ObservableCollection<IoBatchRow> _rows;
    private readonly ICollectionView _view;
    private bool _showOnlyUnmatched;

    // ── 그리드 헤더 내장 필터 ─────────────────────────────────────────────
    // XAML 바인딩 대신 TextChanged 이벤트 + Tag 기반으로 필드 직접 갱신 →
    // DataGrid/DataTemplate 바인딩 tick 지연 완전 회피.
    private readonly Dictionary<string, string> _filters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Flow"] = "", ["Work"] = "", ["Device"] = "", ["Api"] = "",
        ["InName"] = "", ["InType"] = "", ["InAddress"] = "",
        ["OutName"] = "", ["OutType"] = "", ["OutAddress"] = "",
    };

    private string F(string key) => _filters.TryGetValue(key, out var v) ? v : "";

    /// <summary>
    /// 빠른 타이핑 중 매 키마다 전체 행 재평가하면 버벅이므로
    /// 150ms 디바운스 — 마지막 키입력 후 150ms 지난 뒤 한 번만 Refresh.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _filterDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(150) };

    /// <summary>필터 TextBox 의 TextChanged — Tag 에서 키를 읽어 딕셔너리 업데이트 후 디바운스 Refresh.</summary>
    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not string key) return;
        _filters[key] = tb.Text ?? "";
        // 기존 타이머를 리셋해 연속 입력 중에는 한 번만 Refresh.
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private static bool MatchFilter(string value, string filter) =>
        string.IsNullOrEmpty(filter) || (value != null && value.Contains(filter, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// I/O 조회 전용 다이얼로그. 편집은 TAG Wizard 에서만 수행.
    /// </summary>
    public IoBatchSettingsDialog(DsStore store, IReadOnlyList<IoBatchRow> rows)
    {
        InitializeComponent();
        // store 는 더 이상 필요하지 않으므로 null 검증만 수행하고 보관하지 않음
        _ = store ?? throw new ArgumentNullException(nameof(store));

        _rows = new ObservableCollection<IoBatchRow>(rows);

        foreach (var row in _rows)
            row.PropertyChanged += Row_PropertyChanged;

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        IoGrid.ItemsSource = _view;
        BatchDialogHelper.UpdateSelectedCount(_rows, SelectedCountText);

        // 디바운스 타이머 — 발화 시 Refresh 1회 실행 후 자기 자신 stop.
        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            _view?.Refresh();
        };

        DataContext = this;
    }

    private bool FilterRow(object obj)
    {
        if (obj is not IoBatchRow row) return false;

        if (_showOnlyUnmatched && !row.IsUnmatched)
            return false;

        return MatchFilter(row.Flow,        F("Flow"))
            && MatchFilter(row.Work,        F("Work"))
            && MatchFilter(row.Device,      F("Device"))
            && MatchFilter(row.Api,         F("Api"))
            && MatchFilter(row.InSymbol,    F("InName"))
            && MatchFilter(row.InDataType,  F("InType"))
            && MatchFilter(row.InAddress,   F("InAddress"))
            && MatchFilter(row.OutSymbol,   F("OutName"))
            && MatchFilter(row.OutDataType, F("OutType"))
            && MatchFilter(row.OutAddress,  F("OutAddress"));
    }

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
        if (sender is not CheckBox { DataContext: IoBatchRow row } checkBox)
            return;

        BatchDialogHelper.ApplyCheckStateToSelectedRows(IoGrid, row, checkBox.IsChecked == true);
    }

    private void ShowOnlyUnmatched_Changed(object sender, RoutedEventArgs e)
    {
        _showOnlyUnmatched = ShowOnlyUnmatchedCheckBox.IsChecked == true;
        _view.Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>현재 화면(필터 적용 후)의 행들을 UTF-8 CSV(BOM) 로 내보냄.</summary>
    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var rows = _view.Cast<IoBatchRow>().ToList();
        if (rows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                "내보낼 행이 없습니다.",
                "CSV 내보내기",
                MessageBoxButton.OK,
                "ℹ");
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
                // 입력 태그 (값이 있을 때만 한 줄)
                if (!string.IsNullOrWhiteSpace(r.InSymbol) || !string.IsNullOrWhiteSpace(r.InAddress))
                {
                    sb.Append(CsvEscape(r.Flow));       sb.Append(',');
                    sb.Append(CsvEscape(r.InSymbol));   sb.Append(',');
                    sb.Append(CsvEscape(r.InDataType)); sb.Append(',');
                    sb.Append(CsvEscape(r.InAddress));  sb.Append(',');
                    sb.Append(CsvEscape($"{r.Work}/{r.Device}/{r.Api} (In)"));
                    sb.AppendLine();
                    emitted++;
                }

                // 출력 태그
                if (!string.IsNullOrWhiteSpace(r.OutSymbol) || !string.IsNullOrWhiteSpace(r.OutAddress))
                {
                    sb.Append(CsvEscape(r.Flow));        sb.Append(',');
                    sb.Append(CsvEscape(r.OutSymbol));   sb.Append(',');
                    sb.Append(CsvEscape(r.OutDataType)); sb.Append(',');
                    sb.Append(CsvEscape(r.OutAddress));  sb.Append(',');
                    sb.Append(CsvEscape($"{r.Work}/{r.Device}/{r.Api} (Out)"));
                    sb.AppendLine();
                    emitted++;
                }
            }

            // UTF-8 BOM — Excel 한글 깨짐 방지
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            // 저장 후 바로 열기 확인
            var openResult = DialogHelpers.ShowThemedMessageBox(
                $"{emitted}개 태그를 저장했습니다.\n{dlg.FileName}\n\n파일을 바로 열어 볼까요?",
                "CSV 내보내기 완료",
                MessageBoxButton.YesNo,
                "✓");

            if (openResult == MessageBoxResult.Yes)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName        = dlg.FileName,
                        UseShellExecute = true,  // 기본 프로그램(Excel 등)으로 열기
                    });
                }
                catch (Exception openEx)
                {
                    DialogHelpers.ShowThemedMessageBox(
                        $"파일 열기에 실패했습니다:\n{openEx.Message}",
                        "열기 실패",
                        MessageBoxButton.OK,
                        "⚠");
                }
            }
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"저장 실패:\n{ex.Message}",
                "CSV 내보내기 오류",
                MessageBoxButton.OK,
                "✖");
        }
    }

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // 쉼표, 쌍따옴표, 줄바꿈 포함 시 쌍따옴표로 감싸고 내부 " 는 "" 로 escape.
        bool needsQuote = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        var s = value.Replace("\"", "\"\"");
        return needsQuote ? "\"" + s + "\"" : s;
    }
}

/// <summary>
/// I/O 조회용 데이터 행 — 조회 전용이므로 편집 추적 필드 없음.
/// 값은 생성 시 주입되고 그대로 표시만 한다. TagWizard 가 저장한 값을 DsStore 에서 직접 읽어 바인딩.
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
