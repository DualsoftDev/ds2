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
    public ObservableCollection<CallConditionItem> ActiveTriggers  { get; } = [];
    public ObservableCollection<CallConditionItem> AutoConditions  { get; } = [];
    public ObservableCollection<CallConditionItem> CommonConditions{ get; } = [];

    public string CallApiCallsHeader    => $"ApiCalls [{CallApiCalls.Count}]";
    public string SystemApiDefsHeader   => $"ApiDefs [{SystemApiDefs.Count}]";
    public string ActiveTriggersHeader  => $"ActiveTrigger [{ActiveTriggers.Count}]";
    public string AutoConditionsHeader  => $"AutoCondition [{AutoConditions.Count}]";
    public string CommonConditionsHeader=> $"CommonCondition [{CommonConditions.Count}]";

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
        CallApiCalls.CollectionChanged    += (_, _) => OnPropertyChanged(nameof(CallApiCallsHeader));
        SystemApiDefs.CollectionChanged   += (_, _) => OnPropertyChanged(nameof(SystemApiDefsHeader));
        ActiveTriggers.CollectionChanged  += (_, _) => OnPropertyChanged(nameof(ActiveTriggersHeader));
        AutoConditions.CollectionChanged  += (_, _) => OnPropertyChanged(nameof(AutoConditionsHeader));
        CommonConditions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CommonConditionsHeader));
    }

    [RelayCommand]
    private void ApplyWorkDuration()
    {
        if (RequireSelectedAs(EntityTypes.Work) is not { } selectedWork) return;

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
        if (RequireSelectedAs(EntityTypes.Call) is not { } selectedCall) return;

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
        if (RequireSelectedAs(EntityTypes.Call) is not { } selectedCall) return;

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

    // 단일 ApiCall 항목만 store에서 다시 읽어 교체 — 다른 항목의 dirty 상태 보존
    private void RefreshSingleCallApiCall(Guid callId, CallApiCallItem item)
    {
        var idx = CallApiCalls.IndexOf(item);
        var row = _editor.GetCallApiCallsForPanel(callId)
                         .FirstOrDefault(r => r.ApiCallId == item.ApiCallId);
        if (idx < 0 || row is null) return;
        var newItem = new CallApiCallItem(
            row.ApiCallId, row.Name, row.ApiDefId, row.HasApiDef,
            row.ApiDefDisplayName, row.OutputAddress, row.InputAddress,
            row.ValueSpecText, row.InputValueSpecText);
        CallApiCalls[idx] = newItem;
        SelectedCallApiCall = newItem;
    }

    [RelayCommand]
    private void EditCallApiCallSpec(CallApiCallItem? item)
    {
        if (RequireSelectedAs(EntityTypes.Call) is not { } selectedCall) return;
        if (item is null) return;
        if (item.ApiDefId is not Guid apiDefId || apiDefId == Guid.Empty)
        {
            StatusText = "Select a Device ApiDef first.";
            return;
        }

        var dialog = new ApiCallSpecDialog(item.Name, item.ValueSpecText, item.InputValueSpecText);
        if (GetOwnerWindow() is { } owner)
            dialog.Owner = owner;

        if (dialog.ShowDialog() != true)
            return;

        var updated = _editor.UpdateApiCallFromPanel(
            selectedCall.Id, item.ApiCallId, apiDefId,
            item.Name, item.OutputAddress, item.InputAddress,
            dialog.OutSpecText, dialog.InSpecText);

        if (!updated) { StatusText = "Failed to update ApiCall spec."; return; }
        RefreshSingleCallApiCall(selectedCall.Id, item);
        StatusText = "ApiCall spec updated.";
    }

    [RelayCommand]
    private void UpdateCallApiCall(CallApiCallItem? _)
    {
        if (RequireSelectedAs(EntityTypes.Call) is not { } selectedCall) return;

        var dirtyItems = CallApiCalls.Where(x => x.IsDirty).ToList();
        if (dirtyItems.Count == 0) return;

        var failCount = 0;
        foreach (var dirty in dirtyItems)
        {
            if (dirty.ApiDefId is not Guid apiDefId || apiDefId == Guid.Empty) { failCount++; continue; }
            if (!_editor.UpdateApiCallFromPanel(
                    selectedCall.Id, dirty.ApiCallId, apiDefId,
                    dirty.Name, dirty.OutputAddress, dirty.InputAddress,
                    dirty.ValueSpecText, dirty.InputValueSpecText))
                failCount++;
        }

        var selectedId = SelectedCallApiCall?.ApiCallId;
        RefreshPropertyPanel();
        if (selectedId is { } id)
            SelectedCallApiCall = CallApiCalls.FirstOrDefault(x => x.ApiCallId == id);
        StatusText = failCount == 0
            ? $"{dirtyItems.Count} ApiCall(s) updated."
            : $"{dirtyItems.Count - failCount} updated, {failCount} failed.";
    }

    [RelayCommand]
    private void RemoveCallApiCall(CallApiCallItem? item)
    {
        if (RequireSelectedAs(EntityTypes.Call) is not { } selectedCall) return;
        if (item is null) return;

        _editor.RemoveApiCallFromCall(selectedCall.Id, item.ApiCallId);
        RefreshPropertyPanel();
        StatusText = "ApiCall removed.";
    }

    private void AddCondition(CallConditionType type)
    {
        if (RequireSelectedAs(EntityTypes.Call) is not { } selectedCall) return;
        if (!_editor.AddCallCondition(selectedCall.Id, type)) return;
        RefreshCallPanel(selectedCall.Id);
    }

    [RelayCommand]
    private void AddActiveCondition() => AddCondition(CallConditionType.Active);

    [RelayCommand]
    private void AddAutoCondition() => AddCondition(CallConditionType.Auto);

    [RelayCommand]
    private void AddCommonCondition() => AddCondition(CallConditionType.Common);

    [RelayCommand]
    private void RemoveCallCondition(CallConditionItem? item)
    {
        if (RequireSelectedAs(EntityTypes.Call) is not { } selectedCall) return;
        if (item is null) return;
        if (!_editor.RemoveCallCondition(selectedCall.Id, item.ConditionId)) return;
        RefreshCallPanel(selectedCall.Id);
    }

    [RelayCommand]
    private void AddConditionApiCall(CallConditionItem? item)
    {
        if (RequireSelectedAs(EntityTypes.Call) is not { } selectedCall) return;
        if (item is null) return;

        var choices = _editor.GetAllApiCallsForPanel()
            .Select(x => new ConditionApiCallPickerDialog.ApiCallChoice(
                x.ApiCallId, $"{x.ApiDefDisplayName} / {x.Name}"))
            .ToList();

        if (choices.Count == 0)
        {
            StatusText = "프로젝트에 ApiCall이 없습니다.";
            return;
        }

        var dialog = new ConditionApiCallPickerDialog(choices);
        if (GetOwnerWindow() is { } owner)
            dialog.Owner = owner;

        if (dialog.ShowDialog() != true || dialog.SelectedApiCallIds.Count == 0)
            return;

        var added = _editor.AddApiCallsToConditionBatch(
            selectedCall.Id, item.ConditionId,
            dialog.SelectedApiCallIds.ToArray());

        RefreshCallPanel(selectedCall.Id);
        var failCount = dialog.SelectedApiCallIds.Count - added;
        if (failCount > 0)
            StatusText = $"{failCount} ApiCall(s) 추가 실패.";
    }

    [RelayCommand]
    private void RemoveConditionApiCall(ConditionApiCallRow? row)
    {
        if (RequireSelectedAs(EntityTypes.Call) is not { } selectedCall) return;
        if (row is null) return;
        if (!_editor.RemoveApiCallFromCondition(selectedCall.Id, row.ConditionId, row.ApiCallId)) return;
        RefreshCallPanel(selectedCall.Id);
    }

    [RelayCommand]
    private void EditConditionApiCallSpec(ConditionApiCallRow? row)
    {
        if (RequireSelectedAs(EntityTypes.Call) is not { } selectedCall) return;
        if (row is null) return;

        var dialog = new ValueSpecDialog(row.OutputSpecText, "기대값 편집");
        if (GetOwnerWindow() is { } owner)
            dialog.Owner = owner;

        if (dialog.ShowDialog() != true)
            return;

        if (!_editor.UpdateConditionApiCallOutputSpec(selectedCall.Id, row.ConditionId, row.ApiCallId, dialog.ValueSpecText)) return;
        RefreshCallPanel(selectedCall.Id);
    }

    [RelayCommand]
    private void ToggleConditionIsOR(CallConditionItem? item)
    {
        if (RequireSelectedAs(EntityTypes.Call) is not { } selectedCall) return;
        if (item is null) return;
        if (!_editor.UpdateCallConditionSettings(selectedCall.Id, item.ConditionId, !item.IsOR, item.IsRising)) return;
        RefreshCallPanel(selectedCall.Id);
    }

    [RelayCommand]
    private void ToggleConditionIsRising(CallConditionItem? item)
    {
        if (RequireSelectedAs(EntityTypes.Call) is not { } selectedCall) return;
        if (item is null) return;
        if (!_editor.UpdateCallConditionSettings(selectedCall.Id, item.ConditionId, item.IsOR, !item.IsRising)) return;
        RefreshCallPanel(selectedCall.Id);
    }

    [RelayCommand]
    private void AddSystemApiDef()
    {
        if (RequireSelectedAs(EntityTypes.System) is not { } systemNode) return;

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
        if (item is null || RequireSelectedAs(EntityTypes.System) is not { } systemNode) return;

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
        if (item is null || RequireSelectedAs(EntityTypes.System) is not { } systemNode) return;

        _editor.RemoveEntities(new[] { Tuple.Create(EntityTypes.ApiDef, item.Id) });
        RefreshSystemPanel(systemNode.Id);
        StatusText = $"ApiDef '{item.Name}' deleted.";
    }

    private static ApiDefProperties BuildApiDefProperties(ApiDefEditDialog dialog)
    {
        var props = new ApiDefProperties();
        props.IsPush = dialog.IsPush;
        props.TxGuid = dialog.TxWorkId.HasValue ? Microsoft.FSharp.Core.FSharpOption<Guid>.Some(dialog.TxWorkId.Value) : null;
        props.RxGuid = dialog.RxWorkId.HasValue ? Microsoft.FSharp.Core.FSharpOption<Guid>.Some(dialog.RxWorkId.Value) : null;
        props.Duration = dialog.Duration;
        props.Description = !string.IsNullOrEmpty(dialog.Description) ? Microsoft.FSharp.Core.FSharpOption<string>.Some(dialog.Description) : null;
        return props;
    }

    private void RefreshPropertyPanel()
    {
        var selected = SelectedNode;
        NameEditorText = selected?.Name ?? string.Empty;
        IsWorkSelected = EntityTypes.Is(selected?.EntityType, EntityTypes.Work);
        IsCallSelected = EntityTypes.Is(selected?.EntityType, EntityTypes.Call);
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
            ActiveTriggers.Clear();
            AutoConditions.Clear();
            CommonConditions.Clear();
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

        ActiveTriggers.Clear();
        AutoConditions.Clear();
        CommonConditions.Clear();
        foreach (var cond in _editor.GetCallConditionsForPanel(callId))
        {
            var target = cond.ConditionType switch
            {
                CallConditionType.Active => ActiveTriggers,
                CallConditionType.Auto   => AutoConditions,
                _                        => CommonConditions
            };
            target.Add(new CallConditionItem(cond));
        }
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

    private EntityNode? RequireSelectedAs(string entityType) =>
        SelectedNode is { } n && EntityTypes.Is(n.EntityType, entityType) ? n : null;

    private static Window? GetOwnerWindow()
    {
        if (Application.Current is null)
            return null;

        return Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
    }
}