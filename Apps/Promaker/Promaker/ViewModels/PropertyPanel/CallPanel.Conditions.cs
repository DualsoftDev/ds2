using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class PropertyPanelState
{
    [RelayCommand]
    private void AddCondition(CallConditionType type) =>
        CallPanelAction(id => Store.AddCallCondition(id, type), "Call 조건 추가");

    [RelayCommand]
    private void RemoveCallCondition(CallConditionItem? item)
    {
        if (item is null) return;
        if (!GuardSimulationSemanticEdit("Call 조건 삭제"))
            return;
        if (!_host.TryAction(
                () => Store.RemoveCallCondition(item.CallId, item.ConditionId)))
            return;
        RefreshCallPanel(item.CallId);
    }

    [RelayCommand]
    private void DropCallToConditionSection(ConditionDropInfo? info)
    {
        if (info is null || SelectedNode is null) return;
        if (!GuardSimulationSemanticEdit("Call 조건 변경"))
            return;
        var callId = SelectedNode.Id;

        if (Controls.ConditionDropHelper.ExecuteConditionDrop(
                Store, _host, callId, info.ConditionType, info.DroppedCallId))
            RefreshCallPanel(callId);
    }

    [RelayCommand]
    private void DropCallToConditionItem(ConditionItemDropInfo? info)
    {
        if (info is null || SelectedNode is null) return;
        if (!GuardSimulationSemanticEdit("Call 조건 변경"))
            return;
        var callId = SelectedNode.Id;

        if (Controls.ConditionDropHelper.ExecuteAddApiCallsToCondition(
                Store, _host, callId, info.ConditionId, info.DroppedCallId))
            RefreshCallPanel(callId);
    }

    [RelayCommand]
    private void AddChildGroup(CallConditionItem? item)
    {
        if (item is null || SelectedNode is null) return;
        if (!GuardSimulationSemanticEdit("하위 그룹 추가"))
            return;
        var callId = SelectedNode.Id;
        if (!_host.TryAction(() => Store.AddChildCondition(callId, item.ConditionId, false)))
            return;
        RefreshCallPanel(callId);
    }

    [RelayCommand]
    private void EditConditions(ConditionSectionItem? section)
    {
        if (section is null || SelectedNode is null) return;
        if (!GuardSimulationSemanticEdit("Call 조건 편집"))
            return;
        var callId = SelectedNode.Id;

        var dialog = new ConditionEditDialog(Store, _host, callId, section.ConditionType);
        ShowOwnedDialog(dialog);
        RefreshCallPanel(callId);
    }

    [RelayCommand]
    private void RemoveConditionApiCall(ConditionApiCallRow? row)
    {
        if (row is null || SelectedNode is null) return;
        if (!GuardSimulationSemanticEdit("ApiCall 제거"))
            return;
        var callId = SelectedNode.Id;
        if (!_host.TryAction(() =>
                Store.RemoveApiCallFromCondition(callId, row.ConditionId, row.ApiCallId)))
            return;
        RefreshCallPanel(callId);
    }

    [RelayCommand]
    private void EditConditionApiCallSpec(ConditionApiCallRow? row)
    {
        if (row is null || SelectedNode is null) return;
        if (!GuardSimulationSemanticEdit("ValueSpec 편집"))
            return;
        var callId = SelectedNode.Id;

        var dialog = new ApiCallSpecDialog(
            row.ApiDefDisplayName,
            row.OutputSpecText, row.OutputSpecTypeIndex,
            row.InputSpecText,  row.InputSpecTypeIndex,
            row.UseInputSensor);
        ShowOwnedDialog(dialog);
        if (dialog.DialogResult != true) return;

        _host.TryAction(() =>
            Store.UpdateConditionApiCallOutputSpec(
                callId, row.ConditionId, row.ApiCallId,
                dialog.OutSpecTypeIndex, dialog.OutSpecText));
        _host.TryAction(() =>
            Store.UpdateConditionApiCallInputSpec(
                callId, row.ConditionId, row.ApiCallId,
                dialog.InSpecTypeIndex, dialog.InSpecText));
        _host.TryAction(() =>
            Store.UpdateConditionApiCallUseInputSensor(
                callId, row.ConditionId, row.ApiCallId,
                dialog.UseInputSensor));
        RefreshCallPanel(callId);
    }

    [RelayCommand]
    private void NavigateConditionApiCall(ConditionApiCallRow? row)
    {
        if (row is null) return;

        // ApiCall을 직접 소유한 Call(소유자)을 찾아 그 캔버스로 이동.
        // condition formula에서 참조된 ApiCall을 클릭하면 그 ApiCall의 원래 위치(소유 Call)로 점프.
        if (!_host.TryRef(
                () => CallConditionQueries.FindOwnerCallByApiCallId(Store, row.ApiCallId),
                out var ownerTuples))
            return;

        var owners = ownerTuples
            .Select(t => (Id: t.Item1, Name: t.Item2))
            .ToList();

        if (owners.Count == 0)
        {
            _host.SetStatusText("이 ApiCall의 소유 Call을 찾을 수 없습니다.");
            return;
        }

        // ApiCall은 정확히 1개의 Call에 소속됨 — 첫 번째 결과로 직접 이동
        _host.OpenParentCanvasAndFocusNode(owners[0].Id, EntityKind.Call);
    }

    private IReadOnlyDictionary<Guid, string>? _lastIoSnapshot;

    private void ReloadConditions(Guid callId)
    {
        ClearConditionSections();

        if (!_host.TryRef(
                () => Store.GetCallConditionsForPanel(callId),
                out var conditions))
            return;

        foreach (var cond in conditions)
        {
            var target = FindConditionSection(cond.ConditionType)
                         ?? FindConditionSection(CallConditionType.ComAux);
            var item = new CallConditionItem(callId, cond);
            // 시뮬 동작 중 Call 노드를 새로 선택해 reload 한 경우, 직전 IO 스냅샷으로 즉시 표시.
            if (_lastIoSnapshot is not null) item.RefreshRuntime(_lastIoSnapshot);
            target?.Conditions.Add(item);
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
        ConditionSections.Add(new ConditionSectionItem(CallConditionType.SkipUnmatch, "SkipUnmatch", "Add SkipUnmatch"));
        ConditionSections.Add(new ConditionSectionItem(CallConditionType.AutoAux, "AutoAux", "Add AutoAux"));
        ConditionSections.Add(new ConditionSectionItem(CallConditionType.ComAux, "ComAux", "Add ComAux"));
    }

    /// <summary>
    /// 시뮬 IO 값 dictionary 로 현재 표시 중인 모든 조건 row 의 런타임 표시를 갱신.
    /// ioValues 가 null 이면 표시 비움 (시뮬 종료). 시뮬 이벤트 hook 에서 호출.
    /// </summary>
    public void RefreshConditionRuntime(IReadOnlyDictionary<Guid, string>? ioValues)
    {
        _lastIoSnapshot = ioValues;
        foreach (var section in ConditionSections)
            foreach (var cond in section.Conditions)
                cond.RefreshRuntime(ioValues);
    }
}
