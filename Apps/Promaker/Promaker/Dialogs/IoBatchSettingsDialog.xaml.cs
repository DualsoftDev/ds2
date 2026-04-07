using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Ds2.Core.Store;
using Ds2.Editor;
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
        WorkFilterBox.TextChanged += (_, _) => _view.Refresh();
        DeviceFilterBox.TextChanged += (_, _) => _view.Refresh();
        ApiFilterBox.TextChanged += (_, _) => _view.Refresh();
        RefreshApplyButtonState();
    }

    private bool FilterRow(object obj)
    {
        if (obj is not IoBatchRow row) return false;

        var flow = FlowFilterBox.Text;
        var work = WorkFilterBox.Text;
        var device = DeviceFilterBox.Text;
        var api = ApiFilterBox.Text;

        if (!string.IsNullOrEmpty(flow) && !row.Flow.Contains(flow, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(work) && !row.Work.Contains(work, StringComparison.OrdinalIgnoreCase))
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

        // 변경사항이 없으면 사용자에게 알림
        if (changed.Count == 0)
        {
            DialogHelpers.Info(this, "변경된 항목이 없습니다.", "I/O 일괄 편집");
            return;
        }

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
            // Reload rows from store to reflect wizard changes
            ReloadRowsFromStore();

            DialogHelpers.ShowThemedMessageBox(
                "TAG Wizard가 완료되었습니다.\n\n" +
                "IO 태그가 ApiCall에 자동으로 적용되었으며, 프로젝트에 저장되었습니다.\n\n" +
                "추가 편집이 필요하면 이 창에서 수정 후 '적용'을 클릭하세요.\n" +
                "편집이 필요없으면 '닫기'를 클릭하세요.",
                "TAG Wizard",
                MessageBoxButton.OK,
                "ℹ");
        }
    }

    private void ReloadRowsFromStore()
    {
        // Commit any pending edits
        IoGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        IoGrid.CommitEdit(DataGridEditingUnit.Row, true);

        // Unsubscribe from old rows
        foreach (var row in _rows)
            row.PropertyChanged -= Row_PropertyChanged;

        // Get fresh data from store using the extension method from Ds2.Editor
        var storeRows = _store.GetAllApiCallIORows();

        // Clear and repopulate
        _rows.Clear();
        foreach (var r in storeRows)
        {
            var row = new IoBatchRow(
                r.CallId, r.ApiCallId, r.FlowName, r.DeviceName, r.ApiName,
                r.InAddress, r.InSymbol, r.OutAddress, r.OutSymbol,
                r.OutDataType, r.InDataType);

            row.PropertyChanged += Row_PropertyChanged;
            _rows.Add(row);
        }

        // Refresh view
        _view.Refresh();
        BatchDialogHelper.UpdateSelectedCount(_rows, SelectedCountText);
        RefreshApplyButtonState();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RefreshApplyButtonState()
    {
        // 적용 버튼 항상 활성화 (변경사항이 없어도 재적용 가능)
        ApplyButton.IsEnabled = true;
    }
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
    public string Work { get; }
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
