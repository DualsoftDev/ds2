using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Ds2.Core.Store;

namespace Promaker.Dialogs;

public partial class DurationBatchDialog : Window
{
    private readonly ObservableCollection<DurationRow> _workRows;
    private readonly ICollectionView _view;
    private readonly string? _currentFilePath;
    private bool _showOnlyUnmatched;

    public DurationBatchDialog(IReadOnlyList<DurationRow> rows, string? currentFilePath = null)
    {
        _currentFilePath = currentFilePath;
        InitializeComponent();

        _workRows = new ObservableCollection<DurationRow>(rows);

        foreach (var row in _workRows)
            row.PropertyChanged += Row_PropertyChanged;

        _view = CollectionViewSource.GetDefaultView(_workRows);
        _view.Filter = FilterRow;
        WorkDurationGrid.ItemsSource = _view;
        BatchDialogHelper.UpdateSelectedCount(_workRows, WorkSelectedCountText);

        WorkFlowFilterBox.TextChanged += (_, _) => _view.Refresh();
        WorkNameFilterBox.TextChanged += (_, _) => _view.Refresh();
        WorkDurationFilterBox.TextChanged += (_, _) => _view.Refresh();

        // 초기 컬럼 헤더 설정 (디바이스가 기본)
        UpdateCategoryColumnHeader();
    }

    public IReadOnlyList<DurationRow> ChangedRows =>
        _workRows.Where(r => r.IsChanged).ToList();

    private bool FilterRow(object obj)
    {
        if (obj is not DurationRow row) return false;

        if (_showOnlyUnmatched && !row.IsUnmatched)
            return false;

        // Work type filter
        // Control = Explorer Control tree (active system) work
        // Device = Explorer Device tree (passive system) work
        if (ShowControlWorkRadio.IsChecked == true && row.IsDeviceWork)
            return false;
        if (ShowDeviceWorkRadio.IsChecked == true && !row.IsDeviceWork)
            return false;

        var flow = WorkFlowFilterBox.Text;
        var work = WorkNameFilterBox.Text;
        var dur = WorkDurationFilterBox.Text;

        if (!string.IsNullOrEmpty(flow) && !row.FlowName.Contains(flow, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(work) && !row.WorkName.Contains(work, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(dur) && !MatchDurationFilter(row.Duration, dur))
            return false;

        return true;
    }

    private void WorkTypeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_view != null)
        {
            UpdateCategoryColumnHeader();
            _view.Refresh();
            BatchDialogHelper.UpdateSelectedCount(_workRows.Where(r => _view.Filter == null || _view.Filter(r)).ToList(), WorkSelectedCountText);
        }
    }

    private void UpdateCategoryColumnHeader()
    {
        // 디바이스 선택 시 "System", 컨트롤 선택 시 "Flow"
        CategoryColumn.Header = ShowDeviceWorkRadio.IsChecked == true ? "System" : "Flow";
    }

    private static bool MatchDurationFilter(string durationStr, string filter) =>
        Format.matchDurationFilter(durationStr, filter);

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
            DialogHelpers.Info(this, "먼저 하나 이상의 행을 선택하세요.", "Duration 일괄 편집");
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

    private void RowCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: DurationRow row } checkBox)
            return;

        BatchDialogHelper.ApplyCheckStateToSelectedRows(WorkDurationGrid, row, checkBox.IsChecked == true);
    }

    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        BatchDialogHelper.DeselectOnEmptyAreaClick(sender, e);

    private void ShowOnlyUnmatched_Changed(object sender, RoutedEventArgs e)
    {
        _showOnlyUnmatched = ShowOnlyUnmatchedCheckBox.IsChecked == true;
        _view.Refresh();
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed class DurationRow : BatchRowBase
{
    private string _duration;

    public DurationRow(Guid workId, string systemName, string flowName, string workName, string duration, bool isDeviceWork)
    {
        WorkId = workId;
        SystemName = systemName;
        FlowName = flowName;
        WorkName = workName;
        _duration = duration;
        OriginalDuration = duration;
        IsDeviceWork = isDeviceWork;
    }

    public Guid WorkId { get; }
    public string SystemName { get; }
    public string FlowName { get; }
    public string WorkName { get; }
    public string OriginalDuration { get; }
    public bool IsDeviceWork { get; }

    public string Duration
    {
        get => _duration;
        set => SetField(ref _duration, value);
    }

    public bool IsChanged => Duration != OriginalDuration;

    // UI first column (Device=System, Control=Flow)
    public string DisplayCategory => IsDeviceWork ? SystemName : FlowName;
}
