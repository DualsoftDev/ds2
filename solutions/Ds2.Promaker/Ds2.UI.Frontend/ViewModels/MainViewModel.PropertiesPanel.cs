using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Core;
using Ds2.UI.Frontend;
using Ds2.UI.Frontend.Dialogs;
using Microsoft.FSharp.Core;

namespace Ds2.UI.Frontend.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<CallApiCallItem> CallApiCalls { get; } = [];
    public ObservableCollection<DeviceApiDefOptionItem> DeviceApiDefOptions { get; } = [];
    public ObservableCollection<ApiDefPanelItem> SystemApiDefs { get; } = [];

    public string CallApiCallsHeader   => $"ApiCalls [{CallApiCalls.Count}]";
    public string SystemApiDefsHeader  => $"ApiDefs [{SystemApiDefs.Count}]";

    [ObservableProperty] private bool _isWorkSelected;
    [ObservableProperty] private bool _isCallSelected;
    [ObservableProperty] private bool _isSystemSelected;
    [ObservableProperty] private string _workDurationText = string.Empty;
    [ObservableProperty] private string _callTimeoutText = string.Empty;
    [ObservableProperty] private CallApiCallItem? _selectedCallApiCall;
    [ObservableProperty] private string _nameEditorText = string.Empty;
    [ObservableProperty] private bool _isNameDirty;
    [ObservableProperty] private bool _isWorkDurationDirty;
    [ObservableProperty] private bool _isCallTimeoutDirty;

    private string _originalWorkDurationText = string.Empty;
    private string _originalCallTimeoutText = string.Empty;

    partial void OnNameEditorTextChanged(string value) =>
        IsNameDirty = !string.Equals(value.Trim(), SelectedNode?.Name ?? string.Empty, StringComparison.Ordinal);

    partial void OnWorkDurationTextChanged(string value) =>
        IsWorkDurationDirty = value != _originalWorkDurationText;

    partial void OnCallTimeoutTextChanged(string value) =>
        IsCallTimeoutDirty = value != _originalCallTimeoutText;

    partial void OnSelectedNodeChanged(EntityNode? value) => RefreshPropertyPanel();

    private void InitializePropertyPanelState()
    {
        CallApiCalls.CollectionChanged  += (_, _) => OnPropertyChanged(nameof(CallApiCallsHeader));
        SystemApiDefs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SystemApiDefsHeader));
    }

    [RelayCommand]
    private void ApplyWorkDuration()
    {
        if (SelectedNode is not { } selectedWork || !EntityTypes.Is(selectedWork.EntityType, EntityTypes.Work)) return;

        if (!_editor.TryUpdateWorkDuration(selectedWork.Id, WorkDurationText))
        {
            StatusText = "Invalid duration. Use hh:mm:ss or leave empty.";
            return;
        }

        _originalWorkDurationText = WorkDurationText;
        IsWorkDurationDirty = false;
        StatusText = "Work duration updated.";
    }

    [RelayCommand]
    private void ApplyCallTimeout()
    {
        if (SelectedNode is not { } selectedCall || !EntityTypes.Is(selectedCall.EntityType, EntityTypes.Call)) return;

        if (!_editor.TryUpdateCallTimeout(selectedCall.Id, CallTimeoutText))
        {
            StatusText = "Invalid timeout. Enter a non-negative integer (ms) or leave empty.";
            return;
        }

        _originalCallTimeoutText = CallTimeoutText;
        IsCallTimeoutDirty = false;
        StatusText = "Call timeout updated.";
    }

    [RelayCommand]
    private void AddCallApiCall()
    {
        if (SelectedNode is not { } selectedCall || !EntityTypes.Is(selectedCall.EntityType, EntityTypes.Call)) return;

        var apiDefChoices = DeviceApiDefOptions
            .Select(x => new ApiCallCreateDialog.ApiDefChoice(x.Id, x.DisplayName))
            .ToList();
        var dialog = new ApiCallCreateDialog(apiDefChoices);
        if (GetOwnerWindow() is { } owner)
            dialog.Owner = owner;

        if (dialog.ShowDialog() != true)
            return;

        if (dialog.SelectedApiDefId is not Guid selectedApiDefId || selectedApiDefId == Guid.Empty)
        {
            StatusText = "Select a Device ApiDef first.";
            return;
        }

        var created = _editor.AddApiCallFromPanel(
            selectedCall.Id,
            selectedApiDefId,
            dialog.ApiCallName,
            dialog.OutputAddress,
            dialog.InputAddress,
            dialog.ValueSpecText,
            dialog.InValueSpecText);

        if (!FSharpOption<Guid>.get_IsSome(created))
        {
            StatusText = "Failed to add ApiCall.";
            return;
        }

        RefreshPropertyPanel();
        SelectedCallApiCall = CallApiCalls.FirstOrDefault(x => x.ApiCallId == created.Value);
        StatusText = "ApiCall added.";
    }

    [RelayCommand]
    private void EditCallApiCallSpec(CallApiCallItem? item)
    {
        if (item is null) return;

        var dialog = new ApiCallSpecDialog(item.Name, item.ValueSpecText, item.InputValueSpecText);
        if (GetOwnerWindow() is { } owner)
            dialog.Owner = owner;

        if (dialog.ShowDialog() != true)
            return;

        item.ValueSpecText      = dialog.OutSpecText;
        item.InputValueSpecText = dialog.InSpecText;
        StatusText = "Spec updated in row. Click v to apply.";
    }

    [RelayCommand]
    private void UpdateCallApiCall(CallApiCallItem? item)
    {
        if (SelectedNode is not { } selectedCall || !EntityTypes.Is(selectedCall.EntityType, EntityTypes.Call)) return;
        if (item is null || !item.IsDirty) return;
        if (item.ApiDefId is not Guid apiDefId || apiDefId == Guid.Empty)
        {
            StatusText = "Select a Device ApiDef first.";
            return;
        }

        var updated = _editor.UpdateApiCallFromPanel(
            selectedCall.Id,
            item.ApiCallId,
            apiDefId,
            item.Name,
            item.OutputAddress,
            item.InputAddress,
            item.ValueSpecText,
            item.InputValueSpecText);

        if (!updated)
        {
            StatusText = "Failed to update ApiCall.";
            return;
        }

        var selectedId = item.ApiCallId;
        RefreshPropertyPanel();
        SelectedCallApiCall = CallApiCalls.FirstOrDefault(x => x.ApiCallId == selectedId);
        StatusText = "ApiCall updated.";
    }

    [RelayCommand]
    private void RemoveCallApiCall(CallApiCallItem? item)
    {
        if (SelectedNode is not { } selectedCall || !EntityTypes.Is(selectedCall.EntityType, EntityTypes.Call)) return;
        if (item is null) return;

        _editor.RemoveApiCallFromCall(selectedCall.Id, item.ApiCallId);
        RefreshPropertyPanel();
        StatusText = "ApiCall removed.";
    }

    [RelayCommand]
    private void AddSystemApiDef()
    {
        if (SelectedNode is not { } systemNode || !EntityTypes.Is(systemNode.EntityType, EntityTypes.System)) return;

        var works = _editor.GetWorksForSystem(systemNode.Id).ToList();
        var dialog = new ApiDefEditDialog(works) { Owner = GetOwnerWindow() };
        if (dialog.ShowDialog() != true) return;

        var newApiDef = _editor.AddApiDef(dialog.ApiDefName, systemNode.Id);
        var props = BuildApiDefProperties(dialog);
        _editor.UpdateApiDefProperties(newApiDef.Id, props);

        RefreshSystemPanel(systemNode.Id);
        StatusText = $"ApiDef '{dialog.ApiDefName}' added.";
    }

    [RelayCommand]
    private void EditSystemApiDef(ApiDefPanelItem? item)
    {
        if (item is null || SelectedNode is not { } systemNode || !EntityTypes.Is(systemNode.EntityType, EntityTypes.System)) return;

        var works = _editor.GetWorksForSystem(systemNode.Id).ToList();
        var dialog = new ApiDefEditDialog(works, item) { Owner = GetOwnerWindow() };
        if (dialog.ShowDialog() != true) return;

        if (dialog.ApiDefName != item.Name)
            _editor.RenameEntity(item.Id, EntityTypes.ApiDef, dialog.ApiDefName);

        var props = BuildApiDefProperties(dialog);
        _editor.UpdateApiDefProperties(item.Id, props);

        RefreshSystemPanel(systemNode.Id);
        StatusText = $"ApiDef '{dialog.ApiDefName}' updated.";
    }

    [RelayCommand]
    private void DeleteSystemApiDef(ApiDefPanelItem? item)
    {
        if (item is null || SelectedNode is not { } systemNode || !EntityTypes.Is(systemNode.EntityType, EntityTypes.System)) return;

        _editor.RemoveApiDef(item.Id);
        RefreshSystemPanel(systemNode.Id);
        StatusText = $"ApiDef '{item.Name}' deleted.";
    }

    private static ApiDefProperties BuildApiDefProperties(ApiDefEditDialog dialog)
    {
        var props = new ApiDefProperties();
        props.IsPush   = dialog.IsPush;
        props.TxGuid   = dialog.TxWorkId.HasValue ? Microsoft.FSharp.Core.FSharpOption<Guid>.Some(dialog.TxWorkId.Value) : null;
        props.RxGuid   = dialog.RxWorkId.HasValue ? Microsoft.FSharp.Core.FSharpOption<Guid>.Some(dialog.RxWorkId.Value) : null;
        props.Duration = dialog.Duration;
        props.Memo     = !string.IsNullOrEmpty(dialog.Memo) ? Microsoft.FSharp.Core.FSharpOption<string>.Some(dialog.Memo) : null;
        return props;
    }

    private void RefreshPropertyPanel()
    {
        var selected = SelectedNode;
        NameEditorText = selected?.Name ?? string.Empty;
        IsWorkSelected   = EntityTypes.Is(selected?.EntityType, EntityTypes.Work);
        IsCallSelected   = EntityTypes.Is(selected?.EntityType, EntityTypes.Call);
        IsSystemSelected = EntityTypes.Is(selected?.EntityType, EntityTypes.System);

        if (IsWorkSelected && selected is not null)
        {
            _originalWorkDurationText = _editor.GetWorkDurationText(selected.Id);
            WorkDurationText = _originalWorkDurationText;
        }
        else
        {
            _originalWorkDurationText = string.Empty;
            WorkDurationText = string.Empty;
        }

        if (IsCallSelected && selected is not null)
        {
            _originalCallTimeoutText = _editor.GetCallTimeoutText(selected.Id);
            CallTimeoutText = _originalCallTimeoutText;
            RefreshCallPanel(selected.Id);
        }
        else
        {
            _originalCallTimeoutText = string.Empty;
            CallTimeoutText = string.Empty;
            CallApiCalls.Clear();
            DeviceApiDefOptions.Clear();
            SelectedCallApiCall = null;
        }

        if (IsSystemSelected && selected is not null)
        {
            RefreshSystemPanel(selected.Id);
        }
        else
        {
            SystemApiDefs.Clear();
        }
    }

    private void RefreshSystemPanel(Guid systemId)
    {
        SystemApiDefs.Clear();
        foreach (var item in _editor.GetApiDefsForSystem(systemId))
            SystemApiDefs.Add(item);
    }

    private void RefreshCallPanel(Guid callId)
    {
        var previousSelectionId = SelectedCallApiCall?.ApiCallId;

        DeviceApiDefOptions.Clear();
        foreach (var option in _editor.GetDeviceApiDefOptionsForCall(callId))
        {
            DeviceApiDefOptions.Add(
                new DeviceApiDefOptionItem(
                    option.Id,
                    option.DeviceName,
                    option.ApiDefName,
                    option.DisplayName));
        }

        CallApiCalls.Clear();
        foreach (var row in _editor.GetCallApiCallsForPanel(callId))
        {
            CallApiCalls.Add(
                new CallApiCallItem(
                    row.ApiCallId,
                    row.Name,
                    row.ApiDefId,
                    row.HasApiDef,
                    row.ApiDefDisplayName,
                    row.OutputAddress,
                    row.InputAddress,
                    row.ValueSpecText,
                    row.InputValueSpecText));
        }

        if (previousSelectionId is { } selectedId)
            SelectedCallApiCall = CallApiCalls.FirstOrDefault(x => x.ApiCallId == selectedId);

        if (SelectedCallApiCall is null && CallApiCalls.Count > 0)
            SelectedCallApiCall = CallApiCalls[0];
    }

    public void EditApiDefNode(Guid apiDefId)
    {
        var systemIdOpt = _editor.GetApiDefParentSystemId(apiDefId);
        if (!FSharpOption<Guid>.get_IsSome(systemIdOpt)) return;
        var systemId = systemIdOpt.Value;

        var existing = _editor.GetApiDefsForSystem(systemId).FirstOrDefault(x => x.Id == apiDefId);
        if (existing is null) return;

        var works = _editor.GetWorksForSystem(systemId).ToList();
        var dialog = new ApiDefEditDialog(works, existing) { Owner = GetOwnerWindow() };
        if (dialog.ShowDialog() != true) return;

        if (dialog.ApiDefName != existing.Name)
            _editor.RenameEntity(apiDefId, EntityTypes.ApiDef, dialog.ApiDefName);

        var props = BuildApiDefProperties(dialog);
        _editor.UpdateApiDefProperties(apiDefId, props);

        if (IsSystemSelected && SelectedNode?.Id == systemId)
            RefreshSystemPanel(systemId);

        StatusText = $"ApiDef '{dialog.ApiDefName}' updated.";
    }

    private static Window? GetOwnerWindow()
    {
        if (Application.Current is null)
            return null;

        return Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
    }
}

public sealed class CallApiCallItem : ObservableObject
{
    private readonly Guid? _originalApiDefId;
    private readonly string _originalName;
    private readonly string _originalOutputAddress;
    private readonly string _originalInputAddress;
    private readonly string _originalValueSpecText;
    private readonly string _originalInputValueSpecText;

    private Guid? _apiDefId;
    private string _name;
    private string _outputAddress;
    private string _inputAddress;
    private string _valueSpecText;
    private string _inputValueSpecText;
    private bool _isDirty;

    public CallApiCallItem(
        Guid apiCallId,
        string name,
        Guid apiDefId,
        bool hasApiDef,
        string apiDefDisplayName,
        string outputAddress,
        string inputAddress,
        string valueSpecText,
        string inputValueSpecText)
    {
        ApiCallId = apiCallId;
        ApiDefDisplayName = apiDefDisplayName;

        _apiDefId = hasApiDef && apiDefId != Guid.Empty ? apiDefId : null;
        _name = name;
        _outputAddress = outputAddress;
        _inputAddress = inputAddress;
        _valueSpecText = valueSpecText;
        _inputValueSpecText = inputValueSpecText;

        _originalApiDefId = _apiDefId;
        _originalName = _name;
        _originalOutputAddress = _outputAddress;
        _originalInputAddress = _inputAddress;
        _originalValueSpecText = _valueSpecText;
        _originalInputValueSpecText = _inputValueSpecText;
    }

    public Guid ApiCallId { get; }
    public string ApiDefDisplayName { get; }

    public Guid? ApiDefId
    {
        get => _apiDefId;
        set
        {
            if (SetProperty(ref _apiDefId, value))
                RefreshDirtyState();
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            var next = value ?? string.Empty;
            if (SetProperty(ref _name, next))
                RefreshDirtyState();
        }
    }

    public string OutputAddress
    {
        get => _outputAddress;
        set
        {
            var next = value ?? string.Empty;
            if (SetProperty(ref _outputAddress, next))
                RefreshDirtyState();
        }
    }

    public string InputAddress
    {
        get => _inputAddress;
        set
        {
            var next = value ?? string.Empty;
            if (SetProperty(ref _inputAddress, next))
                RefreshDirtyState();
        }
    }

    public string ValueSpecText
    {
        get => _valueSpecText;
        set
        {
            var next = value ?? string.Empty;
            if (SetProperty(ref _valueSpecText, next))
                RefreshDirtyState();
        }
    }

    public string InputValueSpecText
    {
        get => _inputValueSpecText;
        set
        {
            var next = value ?? string.Empty;
            if (SetProperty(ref _inputValueSpecText, next))
                RefreshDirtyState();
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    private void RefreshDirtyState()
    {
        IsDirty =
            _originalApiDefId != _apiDefId ||
            !String.Equals(_originalName, _name, StringComparison.Ordinal) ||
            !String.Equals(_originalOutputAddress, _outputAddress, StringComparison.Ordinal) ||
            !String.Equals(_originalInputAddress, _inputAddress, StringComparison.Ordinal) ||
            !String.Equals(_originalValueSpecText, _valueSpecText, StringComparison.Ordinal) ||
            !String.Equals(_originalInputValueSpecText, _inputValueSpecText, StringComparison.Ordinal);
    }
}

public sealed class DeviceApiDefOptionItem(
    Guid id,
    string deviceName,
    string apiDefName,
    string displayName)
{
    public Guid Id { get; } = id;
    public string DeviceName { get; } = deviceName;
    public string ApiDefName { get; } = apiDefName;
    public string DisplayName { get; } = displayName;
}
