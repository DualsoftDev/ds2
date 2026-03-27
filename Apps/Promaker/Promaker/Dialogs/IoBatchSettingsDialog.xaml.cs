using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Promaker.Dialogs;

public partial class IoBatchSettingsDialog : Window
{
    private readonly ObservableCollection<IoBatchRow> _rows;
    private readonly ICollectionView _view;
    private readonly Func<IReadOnlyList<IoBatchRow>, bool>? _applyChanges;

    public IoBatchSettingsDialog(
        IReadOnlyList<IoBatchRow> rows,
        Func<IReadOnlyList<IoBatchRow>, bool>? applyChanges = null)
    {
        InitializeComponent();
        _applyChanges = applyChanges;

        _rows = new ObservableCollection<IoBatchRow>(rows);

        foreach (var row in _rows)
            row.PropertyChanged += Row_PropertyChanged;

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        IoGrid.ItemsSource = _view;
        BatchDialogHelper.UpdateSelectedCount(_rows, SelectedCountText);

        FlowFilterBox.TextChanged += (_, _) => _view.Refresh();
        WorkFilterBox.TextChanged += (_, _) => _view.Refresh();
        CallFilterBox.TextChanged += (_, _) => _view.Refresh();
        RefreshApplyButtonState();
    }

    private bool FilterRow(object obj)
    {
        if (obj is not IoBatchRow row) return false;

        var flow = FlowFilterBox.Text;
        var work = WorkFilterBox.Text;
        var call = CallFilterBox.Text;

        if (!string.IsNullOrEmpty(flow) && !row.Flow.Contains(flow, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(work) && !row.Work.Contains(work, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(call) && !row.Call.Contains(call, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public IReadOnlyList<IoBatchRow> ChangedRows =>
        _rows.Where(r => r.IsChanged).ToList();

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IoBatchRow.IsSelected))
            BatchDialogHelper.UpdateSelectedCount(_rows, SelectedCountText);

        RefreshApplyButtonState();
    }

    private void ApplySelection_Click(object sender, RoutedEventArgs e)
    {
        var selectedRows = _rows.Where(row => row.IsSelected).ToList();
        if (selectedRows.Count == 0)
        {
            DialogHelpers.Info(this, "먼저 하나 이상의 행을 선택하세요.", "I/O 일괄 편집");
            return;
        }

        if (BatchFieldCombo.SelectedItem is not ComboBoxItem comboItem)
            return;

        var fieldName = comboItem.Content?.ToString();
        var value = BatchValueBox.Text;

        foreach (var row in selectedRows)
        {
            switch (fieldName)
            {
                case "Out tag":
                    row.OutSymbol = value;
                    break;
                case "Out Address":
                    row.OutAddress = value;
                    break;
                case "In tag":
                    row.InSymbol = value;
                    break;
                case "In Address":
                    row.InAddress = value;
                    break;
                case "Memo":
                    row.Memo = value;
                    break;
            }
        }
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

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        IoGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        IoGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var changed = ChangedRows;
        if (changed.Count == 0)
            return;

        if (_applyChanges is null)
        {
            DialogResult = true;
            return;
        }

        if (!_applyChanges(changed))
            return;

        foreach (var row in changed)
            row.AcceptChanges();

        RefreshApplyButtonState();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RefreshApplyButtonState() =>
        ApplyButton.IsEnabled = ChangedRows.Count > 0;
}

public sealed class IoBatchRow : BatchRowBase
{
    private string _inAddress;
    private string _inSymbol;
    private string _outAddress;
    private string _outSymbol;
    private string _memo;
    private string _originalInAddress;
    private string _originalInSymbol;
    private string _originalOutAddress;
    private string _originalOutSymbol;

    public IoBatchRow(Guid callId, Guid apiCallId, string flow, string work, string call,
                      string inAddress, string inSymbol, string outAddress, string outSymbol, string memo)
    {
        CallId = callId;
        ApiCallId = apiCallId;
        Flow = flow;
        Work = work;
        Call = call;
        _inAddress = inAddress;
        _inSymbol = inSymbol;
        _outAddress = outAddress;
        _outSymbol = outSymbol;
        _memo = memo;
        _originalInAddress = inAddress;
        _originalInSymbol = inSymbol;
        _originalOutAddress = outAddress;
        _originalOutSymbol = outSymbol;
    }

    public Guid CallId { get; }
    public Guid ApiCallId { get; }
    public string Flow { get; }
    public string Work { get; }
    public string Call { get; }

    public string OriginalInAddress => _originalInAddress;
    public string OriginalInSymbol => _originalInSymbol;
    public string OriginalOutAddress => _originalOutAddress;
    public string OriginalOutSymbol => _originalOutSymbol;

    public string InAddress
    {
        get => _inAddress;
        set => SetField(ref _inAddress, value);
    }

    public string InSymbol
    {
        get => _inSymbol;
        set => SetField(ref _inSymbol, value);
    }

    public string OutAddress
    {
        get => _outAddress;
        set => SetField(ref _outAddress, value);
    }

    public string OutSymbol
    {
        get => _outSymbol;
        set => SetField(ref _outSymbol, value);
    }

    public string Memo
    {
        get => _memo;
        set => SetField(ref _memo, value);
    }

    public bool IsChanged =>
        !string.Equals(InAddress, OriginalInAddress, StringComparison.Ordinal) ||
        !string.Equals(InSymbol, OriginalInSymbol, StringComparison.Ordinal) ||
        !string.Equals(OutAddress, OriginalOutAddress, StringComparison.Ordinal) ||
        !string.Equals(OutSymbol, OriginalOutSymbol, StringComparison.Ordinal);

    public void AcceptChanges()
    {
        _originalInAddress = InAddress;
        _originalInSymbol = InSymbol;
        _originalOutAddress = OutAddress;
        _originalOutSymbol = OutSymbol;
    }
}
