using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Ds2.Core;
using Ds2.UI.Core;

namespace Promaker.ViewModels;

public partial class PropertyPanelState
{
    private bool TryGetSelectedCall([NotNullWhen(true)] out EntityNode? selectedCall) =>
        TryGetSelectedNode(EntityKind.Call, out selectedCall);

    private void CallPanelAction(Action<Guid> storeAction)
    {
        if (!TryGetSelectedCall(out var selectedCall)) return;
        if (!_host.TryAction(() => storeAction(selectedCall.Id))) return;
        RefreshCallPanel(selectedCall.Id);
    }

    private void RefreshCallPanel(Guid callId)
    {
        var previousSelectionId = SelectedCallApiCall?.ApiCallId;

        if (!_host.TryRef(
                () => Store.GetDeviceApiDefOptionsForCall(callId),
                out var deviceOptions))
            return;

        if (!_host.TryRef(
                () => Store.GetCallApiCallsForPanel(callId),
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
}
