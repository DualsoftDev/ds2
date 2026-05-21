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
    /// <summary>현재 선택된 Owner 가 Work 인지 여부 — Condition 명령 dispatch 기준.</summary>
    private bool IsWorkConditionContext =>
        SelectedNode?.EntityType == EntityKind.Work;

    /// <summary>Owner 변경 후 panel 새로고침 — Work / Call 분기.</summary>
    private void RefreshConditionOwner(Guid ownerId)
    {
        if (IsWorkConditionContext) RefreshWorkPanel(ownerId);
        else RefreshCallPanel(ownerId);
    }

    [RelayCommand]
    private void AddCondition(ConditionType type)
    {
        if (IsWorkConditionContext)
            WorkPanelAction(id => Store.AddWorkCondition(id, type), "Work 조건 추가");
        else
            CallPanelAction(id => Store.AddCallCondition(id, type), "Call 조건 추가");
    }

    [RelayCommand]
    private void RemoveCallCondition(ConditionItem? item)
    {
        if (item is null) return;
        if (!GuardSimulationSemanticEdit("조건 삭제"))
            return;
        var ok =
            IsWorkConditionContext
                ? _host.TryAction(() => Store.RemoveWorkCondition(item.CallId, item.ConditionId))
                : _host.TryAction(() => Store.RemoveCallCondition(item.CallId, item.ConditionId));
        if (!ok) return;
        RefreshConditionOwner(item.CallId);
    }

    [RelayCommand]
    private void DropCallToConditionSection(ConditionDropInfo? info)
    {
        if (info is null || SelectedNode is null) return;
        if (!GuardSimulationSemanticEdit("조건 변경"))
            return;
        var ownerId = SelectedNode.Id;

        var executed = IsWorkConditionContext
            ? Controls.ConditionDropHelper.ExecuteWorkConditionDrop(
                Store, _host, ownerId, info.ConditionType, info.DroppedCallId)
            : Controls.ConditionDropHelper.ExecuteConditionDrop(
                Store, _host, ownerId, info.ConditionType, info.DroppedCallId);

        if (executed) RefreshConditionOwner(ownerId);
    }

    [RelayCommand]
    private void DropCallToConditionItem(ConditionItemDropInfo? info)
    {
        if (info is null || SelectedNode is null) return;
        if (!GuardSimulationSemanticEdit("조건 변경"))
            return;
        var ownerId = SelectedNode.Id;

        var executed = IsWorkConditionContext
            ? Controls.ConditionDropHelper.ExecuteAddApiCallsToWorkCondition(
                Store, _host, ownerId, info.ConditionId, info.DroppedCallId)
            : Controls.ConditionDropHelper.ExecuteAddApiCallsToCondition(
                Store, _host, ownerId, info.ConditionId, info.DroppedCallId);

        if (executed) RefreshConditionOwner(ownerId);
    }

    [RelayCommand]
    private void AddChildGroup(ConditionItem? item)
    {
        if (item is null || SelectedNode is null) return;
        if (!GuardSimulationSemanticEdit("하위 그룹 추가"))
            return;
        var ownerId = SelectedNode.Id;
        var ok = IsWorkConditionContext
            ? _host.TryAction(() => Store.AddWorkChildCondition(ownerId, item.ConditionId, false))
            : _host.TryAction(() => Store.AddChildCondition(ownerId, item.ConditionId, false));
        if (!ok) return;
        RefreshConditionOwner(ownerId);
    }

    [RelayCommand]
    private void EditConditions(ConditionSectionItem? section)
    {
        if (section is null || SelectedNode is null) return;
        if (!GuardSimulationSemanticEdit("조건 편집"))
            return;
        var ownerId = SelectedNode.Id;
        var ownerKind = IsWorkConditionContext ? EntityKind.Work : EntityKind.Call;

        var dialog = new ConditionEditDialog(Store, _host, ownerId, ownerKind, section.ConditionType);
        ShowOwnedDialog(dialog);
        RefreshConditionOwner(ownerId);
    }

    [RelayCommand]
    private void RemoveConditionApiCall(ConditionApiCallRow? row)
    {
        if (row is null || SelectedNode is null) return;
        if (!GuardSimulationSemanticEdit("ApiCall 제거"))
            return;
        var ownerId = SelectedNode.Id;
        var ok = IsWorkConditionContext
            ? _host.TryAction(() => Store.RemoveApiCallFromWorkCondition(ownerId, row.ConditionId, row.ApiCallId))
            : _host.TryAction(() => Store.RemoveApiCallFromCondition(ownerId, row.ConditionId, row.ApiCallId));
        if (!ok) return;
        RefreshConditionOwner(ownerId);
    }

    [RelayCommand]
    private void EditConditionApiCallSpec(ConditionApiCallRow? row)
    {
        if (row is null || SelectedNode is null) return;
        if (!GuardSimulationSemanticEdit("ValueSpec 편집"))
            return;
        var ownerId = SelectedNode.Id;

        var dialog = new ApiCallSpecDialog(
            row.ApiDefDisplayName,
            row.OutputSpecText, row.OutputSpecTypeIndex,
            row.InputSpecText,  row.InputSpecTypeIndex);
        ShowOwnedDialog(dialog);
        if (dialog.DialogResult != true) return;

        if (IsWorkConditionContext)
        {
            _host.TryAction(() =>
                Store.UpdateWorkConditionApiCallOutputSpec(
                    ownerId, row.ConditionId, row.ApiCallId,
                    dialog.OutSpecTypeIndex, dialog.OutSpecText));
            _host.TryAction(() =>
                Store.UpdateWorkConditionApiCallInputSpec(
                    ownerId, row.ConditionId, row.ApiCallId,
                    dialog.InSpecTypeIndex, dialog.InSpecText));
        }
        else
        {
            _host.TryAction(() =>
                Store.UpdateConditionApiCallOutputSpec(
                    ownerId, row.ConditionId, row.ApiCallId,
                    dialog.OutSpecTypeIndex, dialog.OutSpecText));
            _host.TryAction(() =>
                Store.UpdateConditionApiCallInputSpec(
                    ownerId, row.ConditionId, row.ApiCallId,
                    dialog.InSpecTypeIndex, dialog.InSpecText));
        }
        RefreshConditionOwner(ownerId);
    }

    [RelayCommand]
    private void NavigateConditionApiCall(ConditionApiCallRow? row)
    {
        if (row is null) return;

        // ApiCall을 직접 소유한 Call(소유자)을 찾아 그 캔버스로 이동.
        if (!_host.TryRef(
                () => ConditionQueries.FindOwnerCallByApiCallId(Store, row.ApiCallId),
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

        _host.OpenParentCanvasAndFocusNode(owners[0].Id, EntityKind.Call);
    }

    private IReadOnlyDictionary<Guid, string>? _lastIoSnapshot;

    /// <summary>Call 선택 시 호출 — Call 의 condition 트리 로드.</summary>
    private void ReloadConditions(Guid callId) =>
        ReloadConditionsCore(callId, () => Store.GetCallConditionsForPanel(callId));

    /// <summary>Work 선택 시 호출 — Work 의 condition 트리 로드 (SkipAction 만 의미).</summary>
    private void ReloadWorkConditions(Guid workId) =>
        ReloadConditionsCore(workId, () => Store.GetWorkConditionsForPanel(workId));

    private void ReloadConditionsCore(
        Guid ownerId,
        Func<Microsoft.FSharp.Collections.FSharpList<ConditionPanelItem>> fetch)
    {
        ClearConditionSections();

        if (!_host.TryRef(fetch, out var conditions))
            return;

        foreach (var cond in conditions)
        {
            var target = FindConditionSection(cond.ConditionType)
                         ?? FindConditionSection(ConditionType.ComAux);
            var item = new ConditionItem(ownerId, cond);
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

    private ConditionSectionItem? FindConditionSection(ConditionType type)
    {
        EnsureConditionSectionsInitialized();
        return ConditionSections.FirstOrDefault(s => s.ConditionType == type);
    }

    private void EnsureConditionSectionsInitialized()
    {
        if (ConditionSections.Count > 0) return;
        ConditionSections.Add(new ConditionSectionItem(ConditionType.SkipAction, "SkipAction"));
        ConditionSections.Add(new ConditionSectionItem(ConditionType.AutoAux, "AutoAux"));
        ConditionSections.Add(new ConditionSectionItem(ConditionType.ComAux, "ComAux"));
    }

    public void RefreshConditionRuntime(IReadOnlyDictionary<Guid, string>? ioValues)
    {
        _lastIoSnapshot = ioValues;
        foreach (var section in ConditionSections)
            foreach (var cond in section.Conditions)
                cond.RefreshRuntime(ioValues);
    }
}
