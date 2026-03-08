using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Core;
using Promaker.Dialogs;
using Microsoft.FSharp.Core;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void ApplyCallTimeout()
    {
        if (!TryGetSelectedCall(out var selectedCall)) return;

        if (!TryEditorAction(
                () => _store.UpdateCallTimeoutMs(selectedCall.Id, ToOption(CallTimeoutMs))))
            return;

        _originalCallTimeoutMs = CallTimeoutMs;
        IsCallTimeoutDirty = false;
        StatusText = "Call timeout updated.";
    }

    [RelayCommand]
    private void AddCallApiCall()
    {
        if (!TryGetSelectedCall(out var selectedCall)) return;

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
                () => _store.AddApiCallFromPanel(
                    selectedCall.Id,
                    selectedApiDefId,
                    dialog.ApiCallName,
                    dialog.OutputAddress,
                    dialog.InputAddress,
                    dialog.OutTypeIndex, dialog.OutSpecText,
                    dialog.InTypeIndex, dialog.InSpecText),
                out var created,
                fallback: (FSharpOption<Guid>?)null))
            return;

        if (created?.Value is not { } createdId)
        {
            StatusText = "Failed to add ApiCall.";
            return;
        }

        RefreshPropertyPanel();
        SelectedCallApiCall = CallApiCalls.FirstOrDefault(x => x.ApiCallId == createdId);
        StatusText = "ApiCall added.";
    }

    private void RefreshSingleCallApiCall(Guid callId, CallApiCallItem item)
    {
        var idx = CallApiCalls.IndexOf(item);
        if (idx < 0)
            return;

        if (!TryEditorFunc(
                () => _store.TryGetCallApiCallForPanel(callId, item.ApiCallId),
                out var rowOpt,
                fallback: null))
            return;

        if (rowOpt?.Value is not { } row)
            return;

        var newItem = CallApiCallItem.FromPanel(row);
        CallApiCalls[idx] = newItem;
        SelectedCallApiCall = newItem;
    }

    /// ApiDefId 검증 + UpdateApiCallFromPanel 호출을 한 곳에서 처리
    private bool TryUpdateSingleApiCall(
        Guid callId, CallApiCallItem item,
        int outTypeIndex, string outSpecText, int inTypeIndex, string inSpecText,
        bool setMissingApiDefStatus)
    {
        if (item.ApiDefId is not Guid apiDefId || apiDefId == Guid.Empty)
        {
            if (setMissingApiDefStatus)
                StatusText = "Select a Device ApiDef first.";
            return false;
        }

        if (!TryEditorFunc(
                () => _store.UpdateApiCallFromPanel(
                    callId, item.ApiCallId, apiDefId,
                    item.Name, item.OutputAddress, item.InputAddress,
                    outTypeIndex, outSpecText, inTypeIndex, inSpecText),
                out var updated,
                fallback: false))
            return false;

        return updated;
    }

    [RelayCommand]
    private void EditCallApiCallSpec(CallApiCallItem? item)
    {
        if (!TryGetSelectedCall(out var selectedCall)) return;
        if (item is null) return;

        var dialog = new ApiCallSpecDialog(
            item.Name,
            item.ValueSpecText,
            item.OutputSpecTypeIndex,
            item.InputValueSpecText,
            item.InputSpecTypeIndex);
        if (!ShowOwnedDialog(dialog))
            return;

        if (!TryUpdateSingleApiCall(selectedCall.Id, item,
                dialog.OutSpecTypeIndex, dialog.OutSpecText,
                dialog.InSpecTypeIndex, dialog.InSpecText,
                setMissingApiDefStatus: true))
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
        if (!TryGetSelectedCall(out var selectedCall)) return;

        var dirtyItems = CallApiCalls.Where(x => x.IsDirty).ToList();
        if (dirtyItems.Count == 0) return;

        var failCount = dirtyItems.Count(dirty =>
            !TryUpdateSingleApiCall(selectedCall.Id, dirty,
                dirty.OutputSpecTypeIndex, dirty.ValueSpecText,
                dirty.InputSpecTypeIndex, dirty.InputValueSpecText,
                setMissingApiDefStatus: false));

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
        if (!TryGetSelectedCall(out var selectedCall)) return;
        if (item is null) return;

        if (!TryEditorAction(
                () => _store.RemoveApiCallFromCall(selectedCall.Id, item.ApiCallId)))
            return;

        RefreshPropertyPanel();
        StatusText = "ApiCall removed.";
    }

    [RelayCommand]
    private void AddCondition(CallConditionType type)
    {
        if (!TryGetSelectedCall(out var selectedCall)) return;

        if (!TryEditorAction(() => _store.AddCallCondition(selectedCall.Id, type)))
            return;

        RefreshCallPanel(selectedCall.Id);
    }

    [RelayCommand]
    private void RemoveCallCondition(CallConditionItem? item)
    {
        if (!TryGetSelectedCall(out var selectedCall)) return;
        if (item is null) return;

        if (!TryEditorAction(() => _store.RemoveCallCondition(selectedCall.Id, item.ConditionId)))
            return;

        RefreshCallPanel(selectedCall.Id);
    }

    [RelayCommand]
    private void AddConditionApiCall(CallConditionItem? item)
    {
        if (!TryGetSelectedCall(out var selectedCall)) return;
        if (item is null) return;

        if (!TryEditorRef(
                () => _store.GetAllApiCallsForPanel(),
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
                () => _store.AddApiCallsToConditionBatch(
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
        if (!TryGetSelectedCall(out var selectedCall)) return;
        if (row is null) return;

        if (!TryEditorAction(() => _store.RemoveApiCallFromCondition(selectedCall.Id, row.ConditionId, row.ApiCallId)))
            return;

        RefreshCallPanel(selectedCall.Id);
    }

    [RelayCommand]
    private void EditConditionApiCallSpec(ConditionApiCallRow? row)
    {
        if (!TryGetSelectedCall(out var selectedCall)) return;
        if (row is null) return;

        var dialog = new ValueSpecDialog(row.OutputSpecText, row.OutputSpecTypeIndex, "Edit Output ValueSpec");
        if (!ShowOwnedDialog(dialog))
            return;

        if (!TryEditorFunc(
                () => _store.UpdateConditionApiCallOutputSpec(
                    selectedCall.Id, row.ConditionId, row.ApiCallId,
                    row.OutputSpecTypeIndex, dialog.ValueSpecText),
                out var updated,
                fallback: false))
            return;

        if (!updated) return;
        RefreshCallPanel(selectedCall.Id);
    }

    private void ToggleConditionSetting(CallConditionItem? item, bool toggleIsOR)
    {
        if (!TryGetSelectedCall(out var selectedCall)) return;
        if (item is null) return;

        var newIsOR    = toggleIsOR ? !item.IsOR : item.IsOR;
        var newIsRising = toggleIsOR ? item.IsRising : !item.IsRising;

        if (!TryEditorFunc(
                () => _store.UpdateCallConditionSettings(selectedCall.Id, item.ConditionId, newIsOR, newIsRising),
                out var updated,
                fallback: false))
            return;

        if (!updated) return;
        RefreshCallPanel(selectedCall.Id);
    }

    [RelayCommand]
    private void ToggleConditionIsOR(CallConditionItem? item) => ToggleConditionSetting(item, toggleIsOR: true);

    [RelayCommand]
    private void ToggleConditionIsRising(CallConditionItem? item) => ToggleConditionSetting(item, toggleIsOR: false);

    private bool TryGetSelectedCall([NotNullWhen(true)] out EntityNode? selectedCall) =>
        TryGetSelectedNode(EntityKind.Call, out selectedCall);

    private void RefreshCallPanel(Guid callId)
    {
        var previousSelectionId = SelectedCallApiCall?.ApiCallId;

        if (!TryEditorRef(
                () => _store.GetDeviceApiDefOptionsForCall(callId),
                out var deviceOptions))
            return;

        if (!TryEditorRef(
                () => _store.GetCallApiCallsForPanel(callId),
                out var callRows))
            return;

        ReplaceAll(DeviceApiDefOptions,
            deviceOptions.Select(o => new DeviceApiDefOptionItem(o.Id, o.DeviceName, o.ApiDefName, o.DisplayName)));

        ReplaceAll(CallApiCalls, callRows.Select(CallApiCallItem.FromPanel));

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
                () => _store.GetCallConditionsForPanel(callId),
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
