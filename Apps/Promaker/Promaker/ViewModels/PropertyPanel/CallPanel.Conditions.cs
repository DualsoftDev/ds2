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
    private void NavigateConditionApiCall(ConditionApiCallRow? row)
    {
        if (row is null) return;

        if (!_host.TryRef(
                () => CallConditionQueries.FindCallsByApiCallId(Store, row.ApiCallId),
                out var callTuples))
            return;

        var calls = callTuples
            .Select(t => (Id: t.Item1, Name: t.Item2))
            .ToList();

        if (calls.Count == 0)
        {
            _host.SetStatusText("No Call references this ApiCall.");
            return;
        }

        if (calls.Count == 1)
        {
            _host.OpenParentCanvasAndFocusNode(calls[0].Id, EntityKind.Call);
            return;
        }

        DialogHelpers.PickCallFromList(
            $"ApiCall을 참조하는 Call ({calls.Count}개)",
            calls,
            callId => _host.OpenParentCanvasAndFocusNode(callId, EntityKind.Call));
    }

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
            target?.Conditions.Add(new CallConditionItem(callId, cond));
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
}
