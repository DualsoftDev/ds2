using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.UI.Core;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void ApplyCallTimeout()
    {
        if (!TryGetSelectedCall(out var selectedCall)) return;

        if (!TryEditorAction(
                () => _store.UpdateCallTimeoutMs(selectedCall.Id, CallTimeoutMs)))
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

        if (dialog.SelectedApiDefId is not Guid selectedApiDefId)
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
                out Guid createdId,
                fallback: default))
            return;

        RefreshPropertyPanel();
        SelectedCallApiCall = CallApiCalls.FirstOrDefault(x => x.ApiCallId == createdId);
        StatusText = "ApiCall added.";
    }

    private void RefreshSingleCallApiCall(Guid callId, CallApiCallItem item)
    {
        var idx = CallApiCalls.IndexOf(item);
        if (idx < 0)
            return;

        if (!TryEditorRef(
                () => _store.TryGetCallApiCallForPanelOrNull(callId, item.ApiCallId),
                out var row))
            return;

        var newItem = CallApiCallItem.FromPanel(row);
        CallApiCalls[idx] = newItem;
        SelectedCallApiCall = newItem;
    }

    private bool TryUpdateSingleApiCall(
        Guid callId, CallApiCallItem item,
        int outTypeIndex, string outSpecText, int inTypeIndex, string inSpecText,
        bool setMissingApiDefStatus)
    {
        if (item.ApiDefId is not Guid apiDefId)
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
}
