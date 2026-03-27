using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanAddSystem))]
    private void AddSystem()
    {
        if (!GuardSimulationSemanticEdit("System 추가"))
            return;

        var name = _dialogService.PromptName(Resources.Strings.NewSystem, "NewSystem");
        if (name is null) return;
        var (selType, selId, tabKind, tabRoot) = SnapshotContext();
        if (TryEditorAction(() => _store.AddSystemResolved(
                name, Selection.ActiveTreePane == TreePaneKind.Control,
                selType, selId, tabKind, tabRoot)))
            StatusText = $"System '{name}' added.";
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void AddFlow()
    {
        if (!GuardSimulationSemanticEdit("Flow 추가"))
            return;

        var existingFlows = DsQuery.allFlows(_store);
        var defaultName = GetUniqueName("NewFlow", existingFlows.Select(f => f.Name));

        var name = _dialogService.PromptName(Resources.Strings.NewFlow, defaultName);
        if (name is null) return;

        if (existingFlows.Any(f => f.Name == name))
        {
            _dialogService.ShowWarning($"'{name}' 이름을 가진 Flow가 이미 존재합니다.\n다른 이름을 사용해주세요.");
            return;
        }

        var (selType, selId, tabKind, tabRoot) = SnapshotContext();
        if (TryEditorAction(() => _store.AddFlowResolved(
                name, selType, selId, tabKind, tabRoot)))
            StatusText = $"Flow '{name}' added.";
    }

    [RelayCommand(CanExecute = nameof(CanAddWork))]
    private void AddWork()
    {
        if (!GuardSimulationSemanticEdit("Work 추가"))
            return;

        var flowId = ResolveTargetId(EntityKind.Flow, TabKind.Flow)
                     ?? ResolveFirstFlowInSystemTab();
        if (flowId is not { } id)
        {
            StatusText = "Select a Flow or open a System tab that contains a Flow.";
            return;
        }

        var existingWorks = DsQuery.worksOf(id, _store);
        var defaultName = GetUniqueName("NewWork", existingWorks.Select(w => w.LocalName));

        var name = _dialogService.PromptName(Resources.Strings.NewWork, defaultName);
        if (name is null) return;

        if (existingWorks.Any(w => w.LocalName == name))
        {
            _dialogService.ShowWarning($"'{name}' 이름을 가진 Work가 이미 존재합니다.\n다른 이름을 사용해주세요.");
            return;
        }

        var basePos = ConsumeAddPosition();
        var siblings = GetSiblingSnapshot(TabKind.Flow, id);
        if (TryCreateSingleWithCascade(() => _store.AddWork(name, id), basePos, siblings.Positions))
            StatusText = $"Work '{name}' added.";
    }

    [RelayCommand(CanExecute = nameof(CanAddCall))]
    private void AddCall()
    {
        if (!GuardSimulationSemanticEdit("Call 추가"))
            return;

        var workId = ResolveTargetId(EntityKind.Work, TabKind.Work);
        if (workId is not { } targetWorkId)
        {
            StatusText = "Select a Work to add a Call.";
            return;
        }

        var projectProperties = HasProject
            ? DsQuery.allProjects(_store).Head.Properties
            : null;

        var dialog = new CallCreateDialog(
            apiNameFilter =>
            {
                if (!TryEditorRef(
                        () => StoreHierarchyQueries.FindApiDefsByName(_store, apiNameFilter),
                        out var matches))
                    return [];

                return matches;
            },
            projectProperties)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true)
            return;

        var rawPos = ConsumeAddPosition();
        var siblings = GetSiblingSnapshot(TabKind.Work, targetWorkId);

        var systemTypeOption = string.IsNullOrEmpty(dialog.SelectedSystemType)
            ? FSharpOption<string>.None
            : FSharpOption<string>.Some(dialog.SelectedSystemType);

        switch (dialog.Mode)
        {
            case CallCreateMode.CallReplication:
                if (TryCreateSiblingDiffWithCascade(
                    () => _store.AddCallsWithDeviceResolved(EntityKind.Work, targetWorkId, targetWorkId, dialog.CallNames, true, systemTypeOption),
                    TabKind.Work,
                    targetWorkId,
                    rawPos,
                    siblings))
                    StatusText = $"Added {dialog.CallNames.Count} call(s).";
                break;

            case CallCreateMode.ApiCallReplication:
                if (dialog.CallNames.Count > 1)
                {
                    if (TryCreateMultipleWithCascade(
                        dialog.CallNames.Select(fullName => new Func<Guid>(() =>
                            _store.AddCallWithMultipleDevicesResolved(
                                EntityKind.Work,
                                targetWorkId,
                                targetWorkId,
                                dialog.CallDevicesAlias,
                                NormalizeApiName(fullName),
                                dialog.DeviceAliases,
                                systemTypeOption))),
                        rawPos,
                        siblings.Positions))
                        StatusText = $"Added {dialog.CallNames.Count} call(s).";
                }
                else
                {
                    if (TryCreateSingleWithCascade(
                        () => _store.AddCallWithMultipleDevicesResolved(
                            EntityKind.Work,
                            targetWorkId,
                            targetWorkId,
                            dialog.CallDevicesAlias,
                            dialog.CallApiName,
                            dialog.DeviceAliases,
                            systemTypeOption),
                        rawPos,
                        siblings.Positions))
                        StatusText = "Call added.";
                }
                break;

            case CallCreateMode.ApiDefPicker:
                if (TryCreateSingleWithCascade(
                    () => _store.AddCallWithLinkedApiDefs(
                        targetWorkId,
                        dialog.DevicesAlias,
                        dialog.ApiName,
                        dialog.SelectedApiDefs.Select(m => m.ApiDefId)),
                    rawPos,
                    siblings.Positions))
                    StatusText = "Call added.";
                break;
        }
    }
}
