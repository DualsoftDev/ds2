using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Frontend.Dialogs;
using Microsoft.FSharp.Core;

namespace Ds2.UI.Frontend.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void ApplyCallTimeout()
    {
        if (!TryGetSelectedNode(EntityTypes.Call, out var selectedCall)) return;

        if (!TryEditorFunc(
                "TryUpdateCallTimeout",
                () => _editor.TryUpdateCallTimeout(selectedCall.Id, CallTimeoutText),
                out var updated,
                fallback: false))
            return;

        if (!updated)
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
        if (!TryGetSelectedNode(EntityTypes.Call, out var selectedCall)) return;

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

        if (!TryEditorFunc(
                "AddApiCallFromPanel",
                () => _editor.AddApiCallFromPanel(
                    selectedCall.Id,
                    selectedApiDefId,
                    dialog.ApiCallName,
                    dialog.OutputAddress,
                    dialog.InputAddress,
                    dialog.ValueSpecText,
                    dialog.InValueSpecText),
                out var created,
                fallback: (FSharpOption<Guid>?)null))
            return;

        if (!FSharpOption<Guid>.get_IsSome(created))
        {
            StatusText = "Failed to add ApiCall.";
            return;
        }

        RefreshPropertyPanel();
        SelectedCallApiCall = CallApiCalls.FirstOrDefault(x => x.ApiCallId == created!.Value);
        StatusText = "ApiCall added.";
    }

    private void RefreshSingleCallApiCall(Guid callId, CallApiCallItem item)
    {
        var idx = CallApiCalls.IndexOf(item);
        if (idx < 0)
            return;

        if (!TryEditorRef(
                "GetCallApiCallsForPanel",
                () => _editor.GetCallApiCallsForPanel(callId),
                out var rows))
            return;

        var row = rows.FirstOrDefault(r => r.ApiCallId == item.ApiCallId);
        if (row is null)
            return;

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
        if (!TryGetSelectedNode(EntityTypes.Call, out var selectedCall)) return;
        if (item is null) return;
        if (item.ApiDefId is not Guid apiDefId || apiDefId == Guid.Empty)
        {
            StatusText = "Select a Device ApiDef first.";
            return;
        }

        var dialog = new ApiCallSpecDialog(
            item.Name,
            item.ValueSpecText,
            item.OutputSpecTypeIndex,
            item.InputValueSpecText,
            item.InputSpecTypeIndex);
        if (!ShowOwnedDialog(dialog))
            return;

        if (!TryEditorFunc(
                "UpdateApiCallFromPanel",
                () => _editor.UpdateApiCallFromPanel(
                    selectedCall.Id, item.ApiCallId, apiDefId,
                    item.Name, item.OutputAddress, item.InputAddress,
                    dialog.OutSpecTypeIndex, dialog.OutSpecText,
                    dialog.InSpecTypeIndex, dialog.InSpecText),
                out var updated,
                fallback: false))
            return;

        if (!updated)
        {
            StatusText = "Failed to update ApiCall spec.";
            return;
        }

        RefreshSingleCallApiCall(selectedCall.Id, item);
        StatusText = "ApiCall spec updated.";
    }

    [RelayCommand]
    private void UpdateCallApiCall(CallApiCallItem? _)
    {
        if (!TryGetSelectedNode(EntityTypes.Call, out var selectedCall)) return;

        var dirtyItems = CallApiCalls.Where(x => x.IsDirty).ToList();
        if (dirtyItems.Count == 0) return;

        var failCount = 0;
        foreach (var dirty in dirtyItems)
        {
            if (dirty.ApiDefId is not Guid apiDefId || apiDefId == Guid.Empty)
            {
                failCount++;
                continue;
            }

            if (!TryEditorFunc(
                    "UpdateApiCallFromPanel",
                    () => _editor.UpdateApiCallFromPanel(
                        selectedCall.Id, dirty.ApiCallId, apiDefId,
                        dirty.Name, dirty.OutputAddress, dirty.InputAddress,
                        dirty.OutputSpecTypeIndex, dirty.ValueSpecText,
                        dirty.InputSpecTypeIndex, dirty.InputValueSpecText),
                    out var updated,
                    fallback: false))
            {
                failCount++;
                continue;
            }

            if (!updated)
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
        if (!TryGetSelectedNode(EntityTypes.Call, out var selectedCall)) return;
        if (item is null) return;

        if (!TryEditorAction(
                "RemoveApiCallFromCall",
                () => _editor.RemoveApiCallFromCall(selectedCall.Id, item.ApiCallId)))
            return;

        RefreshPropertyPanel();
        StatusText = "ApiCall removed.";
    }

    [RelayCommand]
    private void AddCondition(CallConditionType type)
    {
        if (!TryGetSelectedNode(EntityTypes.Call, out var selectedCall)) return;

        if (!TryEditorFunc(
                "AddCallCondition",
                () => _editor.AddCallCondition(selectedCall.Id, type),
                out var added,
                fallback: false))
            return;

        if (!added) return;
        RefreshCallPanel(selectedCall.Id);
    }

    [RelayCommand]
    private void RemoveCallCondition(CallConditionItem? item)
    {
        if (!TryGetSelectedNode(EntityTypes.Call, out var selectedCall)) return;
        if (item is null) return;

        if (!TryEditorFunc(
                "RemoveCallCondition",
                () => _editor.RemoveCallCondition(selectedCall.Id, item.ConditionId),
                out var removed,
                fallback: false))
            return;

        if (!removed) return;
        RefreshCallPanel(selectedCall.Id);
    }

    [RelayCommand]
    private void AddConditionApiCall(CallConditionItem? item)
    {
        if (!TryGetSelectedNode(EntityTypes.Call, out var selectedCall)) return;
        if (item is null) return;

        if (!TryEditorRef(
                "GetAllApiCallsForPanel",
                () => _editor.GetAllApiCallsForPanel(),
                out var allApiCalls))
            return;

        var choices = allApiCalls
            .Select(x => new ConditionApiCallPickerDialog.ApiCallChoice(
                x.ApiCallId, $"{x.ApiDefDisplayName} / {x.Name}"))
            .ToList();

        if (choices.Count == 0)
        {
            StatusText = "No ApiCall is available in this project.";
            return;
        }

        var dialog = new ConditionApiCallPickerDialog(choices);
        if (!ShowOwnedDialog(dialog) || dialog.SelectedApiCallIds.Count == 0)
            return;

        if (!TryEditorFunc(
                "AddApiCallsToConditionBatch",
                () => _editor.AddApiCallsToConditionBatch(
                    selectedCall.Id, item.ConditionId,
                    dialog.SelectedApiCallIds.ToArray()),
                out var added,
                fallback: 0))
            return;

        RefreshCallPanel(selectedCall.Id);
        var failCount = dialog.SelectedApiCallIds.Count - added;
        if (failCount > 0)
            StatusText = $"{failCount} ApiCall(s) failed to add to condition.";
    }

    [RelayCommand]
    private void RemoveConditionApiCall(ConditionApiCallRow? row)
    {
        if (!TryGetSelectedNode(EntityTypes.Call, out var selectedCall)) return;
        if (row is null) return;

        if (!TryEditorFunc(
                "RemoveApiCallFromCondition",
                () => _editor.RemoveApiCallFromCondition(selectedCall.Id, row.ConditionId, row.ApiCallId),
                out var removed,
                fallback: false))
            return;

        if (!removed) return;
        RefreshCallPanel(selectedCall.Id);
    }

    [RelayCommand]
    private void EditConditionApiCallSpec(ConditionApiCallRow? row)
    {
        if (!TryGetSelectedNode(EntityTypes.Call, out var selectedCall)) return;
        if (row is null) return;

        var dialog = new ValueSpecDialog(row.OutputSpecText, row.OutputSpecTypeIndex, "Edit Output ValueSpec");
        if (!ShowOwnedDialog(dialog))
            return;

        if (!TryEditorFunc(
                "UpdateConditionApiCallOutputSpec",
                () => _editor.UpdateConditionApiCallOutputSpec(selectedCall.Id, row.ConditionId, row.ApiCallId, dialog.ValueSpecText),
                out var updated,
                fallback: false))
            return;

        if (!updated) return;
        RefreshCallPanel(selectedCall.Id);
    }

    [RelayCommand]
    private void ToggleConditionIsOR(CallConditionItem? item)
    {
        if (!TryGetSelectedNode(EntityTypes.Call, out var selectedCall)) return;
        if (item is null) return;

        if (!TryEditorFunc(
                "UpdateCallConditionSettings",
                () => _editor.UpdateCallConditionSettings(selectedCall.Id, item.ConditionId, !item.IsOR, item.IsRising),
                out var updated,
                fallback: false))
            return;

        if (!updated) return;
        RefreshCallPanel(selectedCall.Id);
    }

    [RelayCommand]
    private void ToggleConditionIsRising(CallConditionItem? item)
    {
        if (!TryGetSelectedNode(EntityTypes.Call, out var selectedCall)) return;
        if (item is null) return;

        if (!TryEditorFunc(
                "UpdateCallConditionSettings",
                () => _editor.UpdateCallConditionSettings(selectedCall.Id, item.ConditionId, item.IsOR, !item.IsRising),
                out var updated,
                fallback: false))
            return;

        if (!updated) return;
        RefreshCallPanel(selectedCall.Id);
    }

    private void RefreshCallPanel(Guid callId)
    {
        var previousSelectionId = SelectedCallApiCall?.ApiCallId;

        if (!TryEditorRef(
                "GetDeviceApiDefOptionsForCall",
                () => _editor.GetDeviceApiDefOptionsForCall(callId),
                out var deviceOptions))
            return;

        if (!TryEditorRef(
                "GetCallApiCallsForPanel",
                () => _editor.GetCallApiCallsForPanel(callId),
                out var callRows))
            return;

        DeviceApiDefOptions.Clear();
        foreach (var option in deviceOptions)
        {
            DeviceApiDefOptions.Add(
                new DeviceApiDefOptionItem(
                    option.Id,
                    option.DeviceName,
                    option.ApiDefName,
                    option.DisplayName));
        }

        CallApiCalls.Clear();
        foreach (var row in callRows)
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

        if (!TryEditorRef(
                "GetCallConditionsForPanel",
                () => _editor.GetCallConditionsForPanel(callId),
                out var conditions))
            return;

        foreach (var cond in conditions)
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
