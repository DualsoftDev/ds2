using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Frontend.Dialogs;

namespace Ds2.UI.Frontend.ViewModels;

public partial class MainViewModel
{
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
        if (!ShowOwnedDialog(dialog))
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

        if (created is null)
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
            row.ValueSpecText, row.InputValueSpecText,
            row.OutputSpecTypeIndex, row.InputSpecTypeIndex);
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

        var dialog = new ApiCallSpecDialog(item.Name, item.ValueSpecText, item.OutputSpecTypeIndex, item.InputValueSpecText, item.InputSpecTypeIndex);
        if (!ShowOwnedDialog(dialog))
            return;

        var updated = _editor.UpdateApiCallFromPanel(
            selectedCall.Id, item.ApiCallId, apiDefId,
            item.Name, item.OutputAddress, item.InputAddress,
            dialog.OutSpecTypeIndex, dialog.OutSpecText,
            dialog.InSpecTypeIndex, dialog.InSpecText);

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
                    dirty.OutputSpecTypeIndex, dirty.ValueSpecText,
                    dirty.InputSpecTypeIndex, dirty.InputValueSpecText))
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

    [RelayCommand]
    private void AddCondition(CallConditionType type)
    {
        if (RequireSelectedAs(EntityTypes.Call) is not { } selectedCall) return;
        if (!_editor.AddCallCondition(selectedCall.Id, type)) return;
        RefreshCallPanel(selectedCall.Id);
    }

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
        if (!ShowOwnedDialog(dialog) || dialog.SelectedApiCallIds.Count == 0)
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

        var dialog = new ValueSpecDialog(row.OutputSpecText, row.OutputSpecTypeIndex, "기대값 편집");
        if (!ShowOwnedDialog(dialog))
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
                    row.InputValueSpecText,
                    row.OutputSpecTypeIndex,
                    row.InputSpecTypeIndex));
        }

        if (previousSelectionId is { } selectedId)
            SelectedCallApiCall = CallApiCalls.FirstOrDefault(x => x.ApiCallId == selectedId);

        if (SelectedCallApiCall is null && CallApiCalls.Count > 0)
            SelectedCallApiCall = CallApiCalls[0];

        ReloadConditions(callId);
    }

    private void ReloadConditions(Guid callId)
    {
        ClearConditionSections();
        foreach (var cond in _editor.GetCallConditionsForPanel(callId))
        {
            var target = FindConditionSection(cond.ConditionType)
                         ?? FindConditionSection(CallConditionType.Common);
            target?.Conditions.Add(new CallConditionItem(cond));
        }
    }

    private void ClearConditionSections()
    {
        EnsureConditionSectionsInitialized();
        foreach (var section in ConditionSections)
            section.Conditions.Clear();
    }

    private ConditionSectionItem? FindConditionSection(CallConditionType type)
    {
        EnsureConditionSectionsInitialized();
        return ConditionSections.FirstOrDefault(s => s.ConditionType == type);
    }

    private void EnsureConditionSectionsInitialized()
    {
        if (ConditionSections.Count > 0) return;
        ConditionSections.Add(new ConditionSectionItem(CallConditionType.Active, "ActiveTrigger", "Add ActiveTrigger"));
        ConditionSections.Add(new ConditionSectionItem(CallConditionType.Auto, "AutoCondition", "Add AutoCondition"));
        ConditionSections.Add(new ConditionSectionItem(CallConditionType.Common, "CommonCondition", "Add CommonCondition"));
    }
}