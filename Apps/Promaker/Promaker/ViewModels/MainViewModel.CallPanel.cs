using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
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

    /// ApiDefId 검증 + UpdateApiCallFromPanel 호출을 한 곳에서 처리
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

    [RelayCommand]
    private void AddCondition(CallConditionType type) =>
        CallPanelAction(id => _store.AddCallCondition(id, type));

    [RelayCommand]
    private void RemoveCallCondition(CallConditionItem? item)
    {
        if (item is null) return;
        CallPanelAction(id => _store.RemoveCallCondition(id, item.ConditionId));
    }

    [RelayCommand]
    private void AddConditionApiCall(CallConditionItem? item)
    {
        if (!TryGetSelectedCall(out var selectedCall)) return;
        if (item is null) return;

        var conditionId = item.ConditionId;
        var callId = selectedCall.Id;

        var dialog = new ConditionDropDialog();
        dialog.ShowNonModal(results =>
        {
            var totalAdded = 0;
            foreach (var result in results)
            {
                if (!TryEditorRef(
                        () => _store.GetCallApiCallsForPanel(result.CallId),
                        out var apiCallRows))
                    continue;

                var apiCallIds = apiCallRows.Select(r => r.ApiCallId).ToList();
                if (apiCallIds.Count == 0) continue;

                if (!TryEditorFunc(
                        () => _store.AddApiCallsToConditionBatch(
                            callId, conditionId, apiCallIds),
                        out var added,
                        fallback: 0))
                    continue;

                totalAdded += added;

                // 다이얼로그에서 입력한 ValueSpec을 각 ApiCall에 적용
                if (!string.IsNullOrEmpty(result.SpecText))
                {
                    foreach (var apiCallId in apiCallIds)
                        TryEditorFunc(
                            () => _store.UpdateConditionApiCallOutputSpec(
                                callId, conditionId, apiCallId,
                                result.SpecTypeIndex, result.SpecText),
                            out _,
                            fallback: false);
                }
            }

            if (totalAdded > 0)
                RefreshCallPanel(callId);

            StatusText = totalAdded > 0
                ? $"{totalAdded} ApiCall(s) added to condition."
                : "No ApiCalls were added.";
        });
    }

    [RelayCommand]
    private void RemoveConditionApiCall(ConditionApiCallRow? row)
    {
        if (row is null) return;
        CallPanelAction(id => _store.RemoveApiCallFromCondition(id, row.ConditionId, row.ApiCallId));
    }

    [RelayCommand]
    private void EditConditionApiCallSpec(ConditionApiCallRow? row)
    {
        if (!TryGetSelectedCall(out var selectedCall)) return;
        if (row is null) return;

        var dialog = new ValueSpecDialog(row.OutputSpecText, row.OutputSpecTypeIndex, "Edit Output ValueSpec");
        if (!ShowOwnedDialog(dialog))
            return;

        if (!TryEditorFunc(
                () => _store.UpdateConditionApiCallOutputSpec(
                    selectedCall.Id, row.ConditionId, row.ApiCallId,
                    row.OutputSpecTypeIndex, dialog.ValueSpecText),
                out var updated,
                fallback: false))
            return;

        if (!updated) return;
        RefreshCallPanel(selectedCall.Id);
    }

    private void ToggleConditionSetting(CallConditionItem? item, bool toggleIsOR)
    {
        if (!TryGetSelectedCall(out var selectedCall)) return;
        if (item is null) return;

        var newIsOR    = toggleIsOR ? !item.IsOR : item.IsOR;
        var newIsRising = toggleIsOR ? item.IsRising : !item.IsRising;

        if (!TryEditorFunc(
                () => _store.UpdateCallConditionSettings(selectedCall.Id, item.ConditionId, newIsOR, newIsRising),
                out var updated,
                fallback: false))
            return;

        if (!updated) return;
        RefreshCallPanel(selectedCall.Id);
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
    private void ToggleConditionIsOR(CallConditionItem? item) => ToggleConditionSetting(item, toggleIsOR: true);

    [RelayCommand]
    private void ToggleConditionIsRising(CallConditionItem? item) => ToggleConditionSetting(item, toggleIsOR: false);

    private bool TryGetSelectedCall([NotNullWhen(true)] out EntityNode? selectedCall) =>
        TryGetSelectedNode(EntityKind.Call, out selectedCall);

    /// TryGetSelectedCall → TryEditorAction → RefreshCallPanel 공통 패턴
    private void CallPanelAction(Action<Guid> storeAction)
    {
        if (!TryGetSelectedCall(out var selectedCall)) return;
        if (!TryEditorAction(() => storeAction(selectedCall.Id))) return;
        RefreshCallPanel(selectedCall.Id);
    }

    private void RefreshCallPanel(Guid callId)
    {
        var previousSelectionId = SelectedCallApiCall?.ApiCallId;

        if (!TryEditorRef(
                () => _store.GetDeviceApiDefOptionsForCall(callId),
                out var deviceOptions))
            return;

        if (!TryEditorRef(
                () => _store.GetCallApiCallsForPanel(callId),
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
            target?.Conditions.Add(new CallConditionItem(cond));
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
