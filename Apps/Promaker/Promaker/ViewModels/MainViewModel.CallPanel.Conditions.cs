using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Core;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void AddCondition(CallConditionType type) =>
        CallPanelAction(id => _store.AddCallCondition(id, type));

    [RelayCommand]
    private void RemoveCallCondition(CallConditionItem? item)
    {
        if (item is null) return;
        if (!TryEditorAction(
                () => _store.RemoveCallCondition(item.CallId, item.ConditionId)))
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
                if (!TryEditorRef(
                        () => _store.GetCallApiCallsForPanel(droppedCallId),
                        out var rows))
                    return Array.Empty<ConditionApiCallChoice>();

                return rows
                    .Select(r => new ConditionApiCallChoice(
                        r.ApiCallId, $"{r.ApiDefDisplayName} / {r.Name}"))
                    .ToArray();
            },
            selectedApiCallIds =>
            {
                if (!TryEditorFunc(
                        () => _store.AddApiCallsToConditionBatch(
                            callId, conditionId, selectedApiCallIds),
                        out var added,
                        fallback: 0))
                    return;

                if (added > 0)
                    RefreshCallPanel(callId);

                StatusText = added > 0
                    ? $"{added} ApiCall(s) added to condition."
                    : "No ApiCalls were added.";
            });
    }

    [RelayCommand]
    private void RemoveConditionApiCall(ConditionApiCallRow? row)
    {
        if (row is null) return;
        if (!TryEditorAction(
                () => _store.RemoveApiCallFromCondition(row.CallId, row.ConditionId, row.ApiCallId)))
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

        if (!TryEditorFunc(
                () => _store.UpdateConditionApiCallOutputSpec(
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

        if (!TryEditorFunc(
                () => _store.UpdateCallConditionSettings(item.CallId, item.ConditionId, newIsOR, newIsRising),
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

        if (!TryEditorRef(
                () => _store.FindCallsByApiCallId(row.ApiCallId),
                out var callTuples))
            return;

        var calls = callTuples
            .Select(t => (Id: t.Item1, Name: t.Item2))
            .ToList();

        if (calls.Count == 0)
        {
            StatusText = "No Call references this ApiCall.";
            return;
        }

        if (calls.Count == 1)
        {
            OpenParentCanvasAndFocusNode(calls[0].Id, EntityKind.Call);
            return;
        }

        DialogHelpers.PickCallFromList(
            $"ApiCall을 참조하는 Call ({calls.Count}개)",
            calls,
            callId => OpenParentCanvasAndFocusNode(callId, EntityKind.Call));
    }

    [RelayCommand]
    private void AddChildCondition(CallConditionItem? item)
    {
        if (item is null) return;
        if (!TryEditorAction(
                () => _store.AddChildCondition(item.CallId, item.ConditionId, isOR: false)))
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

        if (!TryEditorRef(
                () => _store.GetCallConditionsForPanel(callId),
                out var conditions))
            return;

        foreach (var cond in conditions)
        {
            var target = FindConditionSection(cond.ConditionType)
                         ?? FindConditionSection(CallConditionType.Common);
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
        ConditionSections.Add(new ConditionSectionItem(CallConditionType.Active, "ActiveTrigger", "Add ActiveTrigger"));
        ConditionSections.Add(new ConditionSectionItem(CallConditionType.Auto, "AutoCondition", "Add AutoCondition"));
        ConditionSections.Add(new ConditionSectionItem(CallConditionType.Common, "CommonCondition", "Add CommonCondition"));
    }
}
