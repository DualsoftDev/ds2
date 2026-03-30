using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Ds2.Store;
using Microsoft.FSharp.Core;

namespace Promaker.Dialogs;

public partial class IoBatchSettingsDialog : Window
{
    private readonly DsStore _store;
    private readonly ObservableCollection<IoBatchRow> _rows;
    private readonly ICollectionView _view;
    private readonly string? _currentFilePath;
    private readonly Func<IReadOnlyList<IoBatchRow>, bool>? _applyChanges;

    public IoBatchSettingsDialog(
        DsStore store,
        IReadOnlyList<IoBatchRow> rows,
        string? currentFilePath = null,
        Func<IReadOnlyList<IoBatchRow>, bool>? applyChanges = null)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _applyChanges = applyChanges;
        _currentFilePath = currentFilePath;

        _rows = new ObservableCollection<IoBatchRow>(rows);

        foreach (var row in _rows)
            row.PropertyChanged += Row_PropertyChanged;

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        IoGrid.ItemsSource = _view;
        BatchDialogHelper.UpdateSelectedCount(_rows, SelectedCountText);

        FlowFilterBox.TextChanged += (_, _) => _view.Refresh();
        DeviceFilterBox.TextChanged += (_, _) => _view.Refresh();
        ApiFilterBox.TextChanged += (_, _) => _view.Refresh();
        RefreshApplyButtonState();
    }

    private bool FilterRow(object obj)
    {
        if (obj is not IoBatchRow row) return false;

        var flow = FlowFilterBox.Text;
        var device = DeviceFilterBox.Text;
        var api = ApiFilterBox.Text;

        if (!string.IsNullOrEmpty(flow) && !row.Flow.Contains(flow, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(device) && !row.Device.Contains(device, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(api) && !row.Api.Contains(api, StringComparison.OrdinalIgnoreCase))
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

    private (string DeviceName, string ApiDefName) GetDeviceAndApiNames(Guid apiCallId)
    {
        if (apiCallId == Guid.Empty)
            return ("UNKNOWN", "UNKNOWN");

        try
        {
            var apiCallKvp = _store.ApiCalls.FirstOrDefault(ac => ac.Key == apiCallId);
            if (apiCallKvp.Key == Guid.Empty)
                return ("UNKNOWN", "UNKNOWN");

            var apiCall = apiCallKvp.Value;

            if (!FSharpOption<Guid>.get_IsSome(apiCall.ApiDefId))
                return ("UNKNOWN", "UNKNOWN");

            var apiDefId = apiCall.ApiDefId.Value;

            var apiDefKvp = _store.ApiDefs.FirstOrDefault(ad => ad.Key == apiDefId);
            if (apiDefKvp.Key == Guid.Empty)
                return ("UNKNOWN", "UNKNOWN");

            var apiDef = apiDefKvp.Value;
            var apiDefName = apiDef.Name;

            var passiveSystemKvp = _store.Systems.FirstOrDefault(s => s.Key == apiDef.ParentId);
            var deviceName = passiveSystemKvp.Key != Guid.Empty ? passiveSystemKvp.Value.Name : "UNKNOWN";

            return (deviceName, apiDefName);
        }
        catch
        {
            return ("UNKNOWN", "UNKNOWN");
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

        DialogResult = true;
    }

    private void TagWizard_Click(object sender, RoutedEventArgs e)
    {
        var wizardDialog = new TagWizardDialog(_store)
        {
            Owner = this
        };

        wizardDialog.ShowDialog();

        if (wizardDialog.DialogResult == true)
        {
            DialogHelpers.ShowThemedMessageBox(
                "TAG Wizard가 완료되었습니다.\n\n" +
                "IO 태그가 ApiCall에 자동으로 적용되었습니다.\n" +
                "필요한 경우 추가 편집 후 프로젝트를 저장하세요.",
                "TAG Wizard",
                MessageBoxButton.OK,
                "ℹ");
        }
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
    private string _outDataType;
    private string _inDataType;
    private string _originalInAddress;
    private string _originalInSymbol;
    private string _originalOutAddress;
    private string _originalOutSymbol;

    public IoBatchRow(Guid callId, Guid apiCallId, string flow, string device, string api,
                      string inAddress, string inSymbol, string outAddress, string outSymbol,
                      string outDataType = "BOOL", string inDataType = "BOOL")
    {
        CallId = callId;
        ApiCallId = apiCallId;
        Flow = flow;
        Device = device;
        Api = api;
        _inAddress = inAddress;
        _inSymbol = inSymbol;
        _outAddress = outAddress;
        _outSymbol = outSymbol;
        _outDataType = outDataType;
        _inDataType = inDataType;
        _originalInAddress = inAddress;
        _originalInSymbol = inSymbol;
        _originalOutAddress = outAddress;
        _originalOutSymbol = outSymbol;
    }

    public Guid CallId { get; }
    public Guid ApiCallId { get; }
    public string Flow { get; }
    public string Device { get; }
    public string Api { get; }

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

    public string OutDataType
    {
        get => _outDataType;
        set => SetField(ref _outDataType, value);
    }

    public string InDataType
    {
        get => _inDataType;
        set => SetField(ref _inDataType, value);
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
