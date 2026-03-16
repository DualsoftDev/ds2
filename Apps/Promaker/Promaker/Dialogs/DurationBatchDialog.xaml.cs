using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Promaker.Dialogs;

public partial class DurationBatchDialog : Window
{
    private readonly ObservableCollection<DurationRow> _workRows;
    private readonly ICollectionView _view;

    public DurationBatchDialog()
    {
        InitializeComponent();

        _workRows =
        [
            new("S141", "S141_INDEX", "20000"),
            new("S141", "S141_RB1", "18000"),
            new("S141", "S141_RB2", "16000"),
            new("S141", "S141_SOL_R_FLR", "8500"),
            new("S141", "S141_SOL_L_FLR", "8200"),
            new("S141", "S141_WELD_1", "12000"),
            new("S141", "S141_WELD_2", "11500"),
            new("S141", "S141_SLIDE", "9000"),
            new("CARTYPE", "CAR_1ST_CT", "6000"),
            new("CARTYPE", "CAR_2ND_CT", "5000"),
            new("CARTYPE", "CAR_3RD_CT", "4500")
        ];

        foreach (var row in _workRows)
            row.PropertyChanged += Row_PropertyChanged;

        _view = CollectionViewSource.GetDefaultView(_workRows);
        _view.Filter = FilterRow;
        WorkDurationGrid.ItemsSource = _view;
        BatchDialogHelper.UpdateSelectedCount(_workRows, WorkSelectedCountText);

        WorkFlowFilterBox.TextChanged += (_, _) => _view.Refresh();
        WorkNameFilterBox.TextChanged += (_, _) => _view.Refresh();
        WorkDurationFilterBox.TextChanged += (_, _) => _view.Refresh();
    }

    private bool FilterRow(object obj)
    {
        if (obj is not DurationRow row) return false;

        var flow = WorkFlowFilterBox.Text;
        var work = WorkNameFilterBox.Text;
        var dur = WorkDurationFilterBox.Text;

        if (!string.IsNullOrEmpty(flow) && !row.Col1.Contains(flow, System.StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(work) && !row.Col2.Contains(work, System.StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(dur) && !MatchDurationFilter(row.Duration, dur))
            return false;

        return true;
    }

    private static bool MatchDurationFilter(string durationStr, string filter)
    {
        filter = filter.Trim();
        if (string.IsNullOrEmpty(filter)) return true;

        if (!int.TryParse(durationStr, out var value))
            return durationStr.Contains(filter, System.StringComparison.OrdinalIgnoreCase);

        if (filter.StartsWith(">=") && int.TryParse(filter[2..], out var gte)) return value >= gte;
        if (filter.StartsWith("<=") && int.TryParse(filter[2..], out var lte)) return value <= lte;
        if (filter.StartsWith('>') && int.TryParse(filter[1..], out var gt)) return value > gt;
        if (filter.StartsWith('<') && int.TryParse(filter[1..], out var lt)) return value < lt;
        if (filter.StartsWith('=') && int.TryParse(filter[1..], out var eq)) return value == eq;
        if (int.TryParse(filter, out var exact)) return value == exact;

        return durationStr.Contains(filter, System.StringComparison.OrdinalIgnoreCase);
    }

    private void BatchValueBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, @"^\d+$");
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DurationRow.IsSelected))
            BatchDialogHelper.UpdateSelectedCount(_workRows, WorkSelectedCountText);
    }

    private void WorkApplySelection_Click(object sender, RoutedEventArgs e)
    {
        var selected = _workRows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "먼저 하나 이상의 행을 선택하세요.", "Duration 일괄 편집", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var row in selected)
            row.Duration = WorkBatchValueBox.Text;
    }

    private void CheckSelected_Click(object sender, RoutedEventArgs e) =>
        BatchDialogHelper.CheckGridSelected<DurationRow>(WorkDurationGrid);

    private void UncheckSelected_Click(object sender, RoutedEventArgs e) =>
        BatchDialogHelper.UncheckGridSelected<DurationRow>(WorkDurationGrid);

    private void CheckAll_Click(object sender, RoutedEventArgs e) =>
        BatchDialogHelper.CheckAll(_workRows);

    private void UncheckAll_Click(object sender, RoutedEventArgs e) =>
        BatchDialogHelper.UncheckAll(_workRows);

    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        BatchDialogHelper.DeselectOnEmptyAreaClick(sender, e);

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal sealed class DurationRow : BatchRowBase
    {
        private string _duration;

        public DurationRow(string col1, string col2, string duration)
        {
            Col1 = col1;
            Col2 = col2;
            _duration = duration;
        }

        public string Col1 { get; }
        public string Col2 { get; }

        public string Duration
        {
            get => _duration;
            set => SetField(ref _duration, value);
        }
    }
}
