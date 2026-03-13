using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.UI.Core;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class PropertyPanelState
{
    [RelayCommand]
    private void ApplyCallTimeout()
    {
        if (!TryGetSelectedCall(out var selectedCall)) return;

        if (!_host.TryAction(
                () => Store.UpdateCallTimeoutMs(selectedCall.Id, CallTimeoutMs)))
            return;

        _originalCallTimeoutMs = CallTimeoutMs;
        IsCallTimeoutDirty = false;
        _host.SetStatusText("Call timeout updated.");
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
            _host.SetStatusText("Select a Device ApiDef first.");
            return;
        }

        if (!_host.TryFunc(
                () => Store.AddApiCallFromPanel(
                    selectedCall.Id,
                    selectedApiDefId,
                    "",
                    dialog.OutputAddress,
                    dialog.InputAddress,
                    dialog.OutTypeIndex, dialog.OutSpecText,
                    dialog.InTypeIndex, dialog.InSpecText),
                out Guid createdId,
                fallback: default))
            return;

        Refresh();
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
            _host.SetStatusText("Failed to update ApiCall spec.");
            return;
        }

        RefreshSingleCallApiCall(selectedCall.Id, item);
        _host.SetStatusText("ApiCall spec updated.");
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
        Refresh();
        if (selectedId is { } id)
            SelectedCallApiCall = CallApiCalls.FirstOrDefault(x => x.ApiCallId == id);

        _host.SetStatusText(failCount == 0
            ? $"{dirtyItems.Count} ApiCall(s) updated."
            : $"{dirtyItems.Count - failCount} updated, {failCount} failed.");
    }

    [RelayCommand]
    private void RemoveCallApiCall(CallApiCallItem? item)
    {
        if (!TryGetSelectedCall(out var selectedCall)) return;
        if (item is null) return;

        if (!_host.TryAction(
                () => Store.RemoveApiCallFromCall(selectedCall.Id, item.ApiCallId)))
            return;

        Refresh();
        _host.SetStatusText("ApiCall removed.");
    }
}
