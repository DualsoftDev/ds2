using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class PropertyPanelState
{
    private bool GuardSimulationSemanticEdit(string editName) =>
        _host.GuardSimulationSemanticEdit(editName);

    private bool TryGetSelectedCall([NotNullWhen(true)] out EntityNode? selectedCall) =>
        TryGetSelectedNode(EntityKind.Call, out selectedCall);

    private bool TryGetSelectedCallId(out Guid callId)
    {
        if (TryGetSelectedCall(out var selectedCall))
        {
            callId = selectedCall.Id;
            return true;
        }

        callId = Guid.Empty;
        return false;
    }

    private bool TryRunCallMutation(
        Action<Guid> mutation,
        string successText,
        string blockedEditName,
        Action<Guid>? afterSuccess = null)
    {
        if (!GuardSimulationSemanticEdit(blockedEditName))
            return false;

        if (!TryGetSelectedCallId(out var callId)) return false;
        if (!_host.TryAction(() => mutation(callId))) return false;

        afterSuccess?.Invoke(callId);
        _host.SetStatusText(successText);
        return true;
    }

    private bool TryRunCallQuery<T>(Func<Guid, T> query, out Guid callId, out T value, T fallback)
    {
        if (!TryGetSelectedCallId(out var resolvedCallId))
        {
            callId = Guid.Empty;
            value = fallback;
            return false;
        }

        callId = resolvedCallId;
        return _host.TryFunc(() => query(resolvedCallId), out value, fallback);
    }

    private void CallPanelAction(Action<Guid> storeAction, string blockedEditName)
    {
        if (!GuardSimulationSemanticEdit(blockedEditName))
            return;

        if (!TryGetSelectedCallId(out var callId)) return;
        if (!_host.TryAction(() => storeAction(callId))) return;
        RefreshCallPanel(callId);
    }

    // ── Work condition 분기용 helper (Condition 명령이 Work 컨텍스트일 때 사용) ───
    private bool TryGetSelectedWork([NotNullWhen(true)] out EntityNode? selectedWork) =>
        TryGetSelectedNode(EntityKind.Work, out selectedWork);

    private bool TryGetSelectedWorkId(out Guid workId)
    {
        if (TryGetSelectedWork(out var selected))
        {
            workId = selected.Id;
            return true;
        }
        workId = Guid.Empty;
        return false;
    }

    private void WorkPanelAction(Action<Guid> storeAction, string blockedEditName)
    {
        if (!GuardSimulationSemanticEdit(blockedEditName))
            return;

        if (!TryGetSelectedWorkId(out var workId)) return;
        if (!_host.TryAction(() => storeAction(workId))) return;
        RefreshWorkPanel(workId);
    }

    /// <summary>Work 조건 트리 재로드 (Work 패널은 ApiCalls 그리드 없음 — Conditions 만 갱신).</summary>
    private void RefreshWorkPanel(Guid workId)
    {
        ReloadWorkConditions(workId);
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
