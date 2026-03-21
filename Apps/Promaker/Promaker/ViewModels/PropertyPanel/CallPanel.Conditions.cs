using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class PropertyPanelState
{
    [RelayCommand]
    private void AddCondition(CallConditionType type) =>
        CallPanelAction(id => Store.AddCallCondition(id, type));

    [RelayCommand]
    private void RemoveCallCondition(CallConditionItem? item)
    {
        if (item is null) return;
        if (!_host.TryAction(
                () => Store.RemoveCallCondition(item.CallId, item.ConditionId)))
            return;
        RefreshCallPanel(item.CallId);
    }

    [RelayCommand]
    private void AddConditionApiCall(CallConditionItem? item)
    {
        if (item is null) return;

        var conditionId = item.ConditionId;
        var callId = item.CallId;

        var dialog = new ConditionDropDialog();
        dialog.ShowNonModal(
            droppedCallId =>
            {
                if (!_host.TryRef(
                        () => Store.GetCallApiCallsForPanel(droppedCallId),
                        out var rows))
                    return Array.Empty<ConditionApiCallChoice>();

                return rows
                    .Select(r => new ConditionApiCallChoice(
                        r.ApiCallId, $"{r.ApiDefDisplayName} / {r.Name}"))
                    .ToArray();
            },
            selectedApiCallIds =>
            {
                if (!_host.TryFunc(
                        () => Store.AddApiCallsToConditionBatch(
                            callId, conditionId, selectedApiCallIds),
                        out var added,
                        fallback: 0))
                    return;

                if (added > 0)
                    RefreshCallPanel(callId);

                _host.SetStatusText(added > 0
                    ? $"{added} ApiCall(s) added to condition."
                    : "No ApiCalls were added.");
            });
    }

    [RelayCommand]
    private void RemoveConditionApiCall(ConditionApiCallRow? row)
    {
        if (row is null) return;
        if (!_host.TryAction(
                () => Store.RemoveApiCallFromCondition(row.CallId, row.ConditionId, row.ApiCallId)))
            return;
        RefreshCallPanel(row.CallId);
    }

    [RelayCommand]
    private void EditConditionApiCallSpec(ConditionApiCallRow? row)
    {
        if (row is null) return;

        var dialog = new ValueSpecDialog(row.OutputSpecText, row.OutputSpecTypeIndex, "Edit Output ValueSpec");
        if (!ShowOwnedDialog(dialog))
            return;

        if (!_host.TryFunc(
                () => Store.UpdateConditionApiCallOutputSpec(
                    row.CallId, row.ConditionId, row.ApiCallId,
                    dialog.TypeIndex, dialog.ValueSpecText),
                out var updated,
                fallback: false))
            return;

        if (!updated) return;
        RefreshCallPanel(row.CallId);
    }

    private void ToggleConditionSetting(CallConditionItem? item, bool toggleIsOR)
    {
        if (item is null) return;

        var newIsOR = toggleIsOR ? !item.IsOR : item.IsOR;
        var newIsRising = toggleIsOR ? item.IsRising : !item.IsRising;

        if (!_host.TryFunc(
                () => Store.UpdateCallConditionSettings(item.CallId, item.ConditionId, newIsOR, newIsRising),
                out var updated,
                fallback: false))
            return;

        if (!updated) return;
        RefreshCallPanel(item.CallId);
    }

    [RelayCommand]
    private void NavigateConditionApiCall(ConditionApiCallRow? row)
    {
        if (row is null) return;

        if (!_host.TryRef(
                () => Store.FindCallsByApiCallId(row.ApiCallId),
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

    [RelayCommand]
    private void AddChildCondition(CallConditionItem? item)
    {
        if (item is null) return;
        if (!_host.TryAction(
                () => Store.AddChildCondition(item.CallId, item.ConditionId, isOR: false)))
            return;
        RefreshCallPanel(item.CallId);
    }

    [RelayCommand]
    private void ToggleConditionIsOR(CallConditionItem? item) => ToggleConditionSetting(item, toggleIsOR: true);

    [RelayCommand]
    private void ToggleConditionIsRising(CallConditionItem? item) => ToggleConditionSetting(item, toggleIsOR: false);

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
