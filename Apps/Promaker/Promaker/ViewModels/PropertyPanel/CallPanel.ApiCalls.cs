using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Store;
using Ds2.Editor;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class PropertyPanelState
{
    [RelayCommand]
    private void ApplyCallTimeout()
    {
        if (!TryRunCallMutation(
                callId => Store.UpdateCallTimeoutMs(callId, CallTimeoutMs),
                "Call timeout updated.",
                _ =>
                {
                    _originalCallTimeoutMs = CallTimeoutMs;
                    IsCallTimeoutDirty = false;
                }))
            return;
    }

    [RelayCommand]
    private void AddCallApiCall()
    {
        var apiDefChoices = DeviceApiDefOptions
            .Select(x => new ApiCallCreateDialog.ApiDefChoice(x.Id, x.DisplayName))
            .ToList();
        var dialog = new ApiCallCreateDialog(apiDefChoices);
        if (!ShowOwnedDialog(dialog))
            return;

        if (dialog.SelectedApiDefId is not Guid selectedApiDefId)
        {
            _host.SetStatusText("Select a Device ApiDef first.");
            return;
        }

        if (!TryRunCallQuery(
                selectedCallId => Store.AddApiCallFromPanel(
                    selectedCallId,
                    selectedApiDefId,
                    "", dialog.OutputAddress,
                    "", dialog.InputAddress,
                    dialog.OutTypeIndex, dialog.OutSpecText,
                    dialog.InTypeIndex, dialog.InSpecText),
                out var callId,
                out Guid createdId,
                fallback: default))
            return;

        RefreshCallPanel(callId);
        SelectedCallApiCall = CallApiCalls.FirstOrDefault(x => x.ApiCallId == createdId);
        _host.SetStatusText("ApiCall added.");
    }

    private void RefreshSingleCallApiCall(Guid callId, CallApiCallItem item)
    {
        var idx = CallApiCalls.IndexOf(item);
        if (idx < 0)
            return;

        if (!_host.TryRef(
                () => Store.TryGetCallApiCallForPanelOrNull(callId, item.ApiCallId),
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
                _host.SetStatusText("Select a Device ApiDef first.");
            return false;
        }

        if (!_host.TryFunc(
                () => Store.UpdateApiCallFromPanel(
                    callId, item.ApiCallId, apiDefId,
                    item.Name,
                    item.OutputTagName, item.OutputAddress,
                    item.InputTagName, item.InputAddress,
                    outTypeIndex, outSpecText, inTypeIndex, inSpecText),
                out var updated,
                fallback: false))
            return false;

        return updated;
    }

    [RelayCommand]
    private void EditCallApiCallSpec(CallApiCallItem? item)
    {
        if (item is null) return;

        var dialog = new ApiCallSpecDialog(
            item.Name,
            item.ValueSpecText,
            item.OutputSpecTypeIndex,
            item.InputValueSpecText,
            item.InputSpecTypeIndex);
        if (!ShowOwnedDialog(dialog))
            return;

        if (!TryRunCallQuery(
                selectedCallId => TryUpdateSingleApiCall(
                    selectedCallId, item,
                    dialog.OutSpecTypeIndex, dialog.OutSpecText,
                    dialog.InSpecTypeIndex, dialog.InSpecText,
                    setMissingApiDefStatus: true),
                out var callId,
                out bool updated,
                fallback: false))
            return;

        if (!updated)
        {
            _host.SetStatusText("Failed to update ApiCall spec.");
            return;
        }

        RefreshSingleCallApiCall(callId, item);
        _host.SetStatusText("ApiCall spec updated.");
    }

    [RelayCommand]
    private void UpdateCallApiCall(CallApiCallItem? _)
    {
        Guid ignoredCallId;

        if (!TryRunCallQuery(
                callId =>
                {
                    var dirtyItems = CallApiCalls.Where(x => x.IsDirty).ToList();
                    if (dirtyItems.Count == 0)
                        return (DirtyCount: 0, FailCount: 0, SelectedId: SelectedCallApiCall?.ApiCallId);

                    var failCount = dirtyItems.Count(dirty =>
                        !TryUpdateSingleApiCall(callId, dirty,
                            dirty.OutputSpecTypeIndex, dirty.ValueSpecText,
                            dirty.InputSpecTypeIndex, dirty.InputValueSpecText,
                            setMissingApiDefStatus: false));

                    return (DirtyCount: dirtyItems.Count, FailCount: failCount, SelectedId: SelectedCallApiCall?.ApiCallId);
                },
                out ignoredCallId,
                out var result,
                fallback: default))
            return;

        if (result.DirtyCount == 0)
            return;

        Refresh();
        if (result.SelectedId is { } id)
            SelectedCallApiCall = CallApiCalls.FirstOrDefault(x => x.ApiCallId == id);

        _host.SetStatusText(result.FailCount == 0
            ? $"{result.DirtyCount} ApiCall(s) updated."
            : $"{result.DirtyCount - result.FailCount} updated, {result.FailCount} failed.");
    }

    [RelayCommand]
    private void RemoveCallApiCall(CallApiCallItem? item)
    {
        if (item is null) return;

        if (!TryRunCallMutation(
                callId => Store.RemoveApiCallFromCall(callId, item.ApiCallId),
                "ApiCall removed.",
                RefreshCallPanel))
            return;
    }
}
