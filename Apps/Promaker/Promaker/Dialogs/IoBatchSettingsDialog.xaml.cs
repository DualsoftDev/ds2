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

    public IoBatchSettingsDialog(IReadOnlyList<IoBatchRow> rows)
    {
        InitializeComponent();

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
    }

    private void ApplySelection_Click(object sender, RoutedEventArgs e)
    {
        var selectedRows = _rows.Where(row => row.IsSelected).ToList();
        if (selectedRows.Count == 0)
        {
            MessageBox.Show(this, "먼저 하나 이상의 행을 선택하세요.", "I/O 일괄 편집", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed class IoBatchRow : BatchRowBase
{
    private string _inAddress;
    private string _inSymbol;
    private string _outAddress;
    private string _outSymbol;
    private string _memo;

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
        OriginalInAddress = inAddress;
        OriginalInSymbol = inSymbol;
        OriginalOutAddress = outAddress;
        OriginalOutSymbol = outSymbol;
    }

    public Guid CallId { get; }
    public Guid ApiCallId { get; }
    public string Flow { get; }
    public string Work { get; }
    public string Call { get; }

    public string OriginalInAddress { get; }
    public string OriginalInSymbol { get; }
    public string OriginalOutAddress { get; }
    public string OriginalOutSymbol { get; }

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
}
