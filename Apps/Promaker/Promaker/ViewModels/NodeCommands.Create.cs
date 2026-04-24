using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
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

        var existingFlows = Queries.allFlows(_store);
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

        var flowId = ResolveTargetFlowId();
        if (flowId is not { } id)
        {
            StatusText = "Select a Flow or open a System tab that contains a Flow.";
            return;
        }

        var existingWorks = Queries.worksOf(id, _store);
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
        if (TryCreateSingleWithCascade(() => _store.AddWork(name, id), basePos, siblings.Positions, "Work 추가"))
        {
            _lastAddWorkTargetFlowId = id;
            StatusText = $"Work '{name}' added.";
        }
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

        var project = HasProject ? Queries.allProjects(_store).Head : null;

        var dialog = new CallCreateDialog(
            apiNameFilter =>
            {
                if (!TryEditorRef(
                        () => StoreHierarchyQueries.FindApiDefsByName(_store, apiNameFilter),
                        out var matches))
                    return [];

                return matches;
            },
            project)
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

        // Call 이름 중복 경고: 동일 이름이 있으면 확인 후 진행
        var callNamesToCheck = dialog.Mode switch
        {
            CallCreateMode.CallReplication => dialog.CallNames.ToList(),
            CallCreateMode.ApiCallReplication => dialog.CallNames
                .Select(fullName => $"{dialog.CallDevicesAlias}.{NormalizeApiName(fullName)}")
                .ToList(),
            CallCreateMode.ApiDefPicker => [$"{dialog.DevicesAlias}.{dialog.ApiName}"],
            _ => []
        };

        // DevicesAlias 별 SystemType 충돌: 같은 devAlias 가 이미 다른 SystemType 으로
        // 프로젝트에 등록돼 있으면 강제 거부 (dev.ADV, dev.MOVE 등 이름이 달라도 dev 공유 시)
        if (project is not null)
        {
            var devAliases = callNamesToCheck
                .Select(name => name.Split(new[] { '.' }, 2)[0])
                .Where(a => !string.IsNullOrEmpty(a))
                .Distinct()
                .ToList();
            var typeConflicts = devAliases
                .Select(dev => (Dev: dev,
                                Conflict: Queries.findConflictingDeviceSystemType(
                                    project.Id, dev, systemTypeOption, _store)))
                .Where(x => FSharpOption<Tuple<string, string>>.get_IsSome(x.Conflict))
                .Select(x => (x.Dev, Existing: x.Conflict.Value.Item1, Requested: x.Conflict.Value.Item2))
                .ToList();
            if (typeConflicts.Count > 0)
            {
                var lines = typeConflicts
                    .Select(c => $"  • {c.Dev}  (기존: {c.Existing} / 요청: {c.Requested})");
                _dialogService.ShowWarning(
                    "다음 Device(DevicesAlias) 는 이미 다른 SystemType 으로 등록돼 있어 추가할 수 없습니다:\n\n"
                    + string.Join("\n", lines)
                    + "\n\n같은 SystemType 으로 추가하거나 다른 이름을 사용하세요.");
                return;
            }
        }

        var duplicateCallNames = callNamesToCheck
            .Where(name => !Queries.isCallNameUniqueInWork(targetWorkId, name, FSharpOption<Guid>.None, _store))
            .ToList();
        if (duplicateCallNames.Count > 0)
        {
            var nameList = string.Join(", ", duplicateCallNames);
            if (!_dialogService.Confirm(
                $"'{nameList}' 이름의 Call이 이미 존재합니다.\nApiCall은 동일한 ApiDef를 참조합니다.\n그래도 생성하시겠습니까?",
                "Call 이름 중복"))
                return;
        }

        var worksBefore = _store.Works.Keys.ToHashSet();

        switch (dialog.Mode)
        {
            case CallCreateMode.CallReplication:
                if (TryCreateSiblingDiffWithCascade(
                    () => _store.AddCallsWithDeviceResolved(EntityKind.Work, targetWorkId, targetWorkId, dialog.CallNames, true, systemTypeOption),
                    TabKind.Work,
                    targetWorkId,
                    rawPos,
                    siblings,
                    "Call 추가"))
                {
                    StatusText = $"Added {dialog.CallNames.Count} call(s).";
                    MergeWithMutualResetArrows(worksBefore, "Call 추가");
                }
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
                        siblings.Positions,
                        "Call 추가"))
                    {
                        StatusText = $"Added {dialog.CallNames.Count} call(s).";
                        MergeWithMutualResetArrows(worksBefore, "Call 추가");
                    }
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
                        siblings.Positions,
                        "Call 추가"))
                    {
                        StatusText = "Call added.";
                        MergeWithMutualResetArrows(worksBefore, "Call 추가");
                    }
                }
                break;

            case CallCreateMode.ApiDefPicker: // no passive device created, skip mutual reset
                if (TryCreateSingleWithCascade(
                    () => _store.AddCallWithLinkedApiDefs(
                        targetWorkId,
                        dialog.DevicesAlias,
                        dialog.ApiName,
                        dialog.SelectedApiDefs.Select(m => m.ApiDefId)),
                    rawPos,
                    siblings.Positions,
                    "Call 추가"))
                    StatusText = "Call added.";
                break;
        }
    }

    private void MergeWithMutualResetArrows(HashSet<Guid> worksBefore, string mergeLabel)
    {
        var arrowCount = _store.ConnectWorksWithMutualReset(
            _store.Works.Keys.Where(id => !worksBefore.Contains(id)));
        if (arrowCount > 0)
            _store.MergeLastTransactions(2, mergeLabel);
    }
}
