using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private readonly record struct SiblingSnapshot(
        HashSet<Guid> Ids,
        IReadOnlyList<(int X, int Y)> Positions)
    {
        public static SiblingSnapshot Empty { get; } = new([], []);
    }

    private bool CanAddSystem()
    {
        if (!HasProject) return false;
        var projects = DsQuery.allProjects(_store);
        if (projects.IsEmpty) return true;
        var activeSystems = DsQuery.activeSystemsOf(projects.Head.Id, _store);
        return activeSystems.IsEmpty;
    }

    [RelayCommand(CanExecute = nameof(CanAddSystem))]
    private void AddSystem()
    {
        var name = _dialogService.PromptName(Resources.Strings.NewSystem, "NewSystem");
        if (name is null) return;
        var (selType, selId, tabKind, tabRoot) = SnapshotContext();
        TryEditorAction(() => _store.AddSystemResolved(
            name, Selection.ActiveTreePane == TreePaneKind.Control,
            selType, selId, tabKind, tabRoot));
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void AddFlow()
    {
        var name = _dialogService.PromptName(Resources.Strings.NewFlow, "NewFlow");
        if (name is null) return;
        var (selType, selId, tabKind, tabRoot) = SnapshotContext();
        TryEditorAction(() => _store.AddFlowResolved(
            name, selType, selId, tabKind, tabRoot));
    }

    private (EntityKind? SelectedEntityKind, Guid? SelectedEntityId, TabKind? ActiveTabKind, Guid? ActiveTabRootId) SnapshotContext() =>
        (SelectedNode?.EntityType, SelectedNode?.Id, Canvas.ActiveTab?.Kind, Canvas.ActiveTab?.RootId);

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void AddWork()
    {
        var flowId = ResolveTargetId(EntityKind.Flow, TabKind.Flow);
        if (flowId is not { } id) return;

        var name = _dialogService.PromptName(Resources.Strings.NewWork, "NewWork");
        if (name is null) return;

        if (!TryEditorFunc(() => _store.AddWork(name, id), out Guid workId, fallback: Guid.Empty))
            return;
        if (workId == Guid.Empty) return;

        var basePos = ConsumeAddPosition();
        var siblings = GetSiblingSnapshot(TabKind.Flow, id);
        var pos = CascadePosition(basePos, 0, siblings.Positions);
        TryEditorAction(() => _store.MoveEntities([new MoveEntityRequest(workId, pos)]));
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void AddCall()
    {
        var workId = ResolveTargetId(EntityKind.Work, TabKind.Work);
        if (workId is not { } targetWorkId)
            return;

        var dialog = new CallCreateDialog(
            apiNameFilter =>
            {
                if (!TryEditorRef(
                        () => _store.FindApiDefsByName(apiNameFilter),
                        out var matches))
                    return [];

                return matches;
            })
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true)
            return;

        var rawPos = ConsumeAddPosition();
        var siblings = GetSiblingSnapshot(TabKind.Work, targetWorkId);

        switch (dialog.Mode)
        {
            case CallCreateMode.CallReplication:
            {
                TryEditorAction(
                    () => _store.AddCallsWithDeviceResolved(EntityKind.Work, targetWorkId, targetWorkId, dialog.CallNames, true));
                var newCallIds = GetSiblingSnapshot(TabKind.Work, targetWorkId).Ids
                    .Where(id => !siblings.Ids.Contains(id))
                    .ToList();
                if (newCallIds.Count > 0)
                {
                    var assigned = new List<(int X, int Y)>(siblings.Positions);
                    var requests = new List<MoveEntityRequest>();
                    for (var i = 0; i < newCallIds.Count; i++)
                    {
                        var pos = CascadePosition(rawPos, i, assigned);
                        requests.Add(new MoveEntityRequest(newCallIds[i], pos));
                        assigned.Add((pos.X, pos.Y));
                    }
                    TryEditorAction(() => _store.MoveEntities(requests));
                }
                break;
            }
            case CallCreateMode.ApiCallReplication:
            {
                if (dialog.CallNames.Count > 1)
                {
                    // multi-apiName: apiName별로 각각 AddCallWithMultipleDevicesResolved 호출
                    var newCallIds = new List<Guid>();
                    foreach (var fullName in dialog.CallNames)
                    {
                        var apiName = fullName.Contains('.') ? fullName[(fullName.IndexOf('.') + 1)..] : fullName;
                        if (TryEditorFunc(
                                () => _store.AddCallWithMultipleDevicesResolved(
                                    EntityKind.Work, targetWorkId, targetWorkId,
                                    dialog.CallDevicesAlias, apiName, dialog.DeviceAliases),
                                out Guid cid, fallback: Guid.Empty) && cid != Guid.Empty)
                            newCallIds.Add(cid);
                    }
                    if (newCallIds.Count > 0)
                    {
                        var assigned = new List<(int X, int Y)>(siblings.Positions);
                        var requests = new List<MoveEntityRequest>();
                        for (var i = 0; i < newCallIds.Count; i++)
                        {
                            var pos = CascadePosition(rawPos, i, assigned);
                            requests.Add(new MoveEntityRequest(newCallIds[i], pos));
                            assigned.Add((pos.X, pos.Y));
                        }
                        TryEditorAction(() => _store.MoveEntities(requests));
                    }
                }
                else
                {
                    // single apiName
                    if (TryEditorFunc(
                            () => _store.AddCallWithMultipleDevicesResolved(
                                EntityKind.Work, targetWorkId, targetWorkId,
                                dialog.CallDevicesAlias, dialog.CallApiName, dialog.DeviceAliases),
                            out Guid callId, fallback: Guid.Empty) && callId != Guid.Empty)
                    {
                        var pos = CascadePosition(rawPos, 0, siblings.Positions);
                        TryEditorAction(() => _store.MoveEntities([new MoveEntityRequest(callId, pos)]));
                    }
                }
                break;
            }
            case CallCreateMode.ApiDefPicker:
            {
                if (TryEditorFunc(
                        () => _store.AddCallWithLinkedApiDefs(
                            targetWorkId,
                            dialog.DevicesAlias,
                            dialog.ApiName,
                            dialog.SelectedApiDefs.Select(m => m.ApiDefId)),
                        out Guid callId,
                        fallback: Guid.Empty)
                    && callId != Guid.Empty)
                {
                    var pos = CascadePosition(rawPos, 0, siblings.Positions);
                    TryEditorAction(() => _store.MoveEntities([new MoveEntityRequest(callId, pos)]));
                }
                break;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void DeleteSelected()
    {
        if (Selection.OrderedArrowSelection.Count > 0)
        {
            if (TryEditorAction(() => _store.RemoveArrows(Selection.OrderedArrowSelection)))
                Selection.ClearArrowSelection();
            return;
        }

        if (Selection.OrderedNodeSelection.Count > 0)
        {
            var selections = Selection.OrderedNodeSelection
                .Where(k => k.EntityKind != EntityKind.Project)
                .Select(k => Tuple.Create(k.EntityKind, k.Id));
            TryEditorAction(() => _store.RemoveEntities(selections));
            return;
        }

        if (SelectedNode is { EntityType: not EntityKind.Project } node)
            TryEditorAction(
                () => _store.RemoveEntities(new[] { Tuple.Create(node.EntityType, node.Id) }));
    }

    [RelayCommand]
    private void RenameSelected(string newName)
    {
        if (SelectedNode is null || string.IsNullOrWhiteSpace(newName))
            return;

        TryEditorAction(
            () => _store.RenameEntity(SelectedNode.Id, SelectedNode.EntityType, newName));
    }

    [RelayCommand]
    private void CopySelected()
    {
        var candidates = Selection.OrderedNodeSelection.Count > 0
            ? Selection.OrderedNodeSelection
            : SelectedNode is { } single
                ? [new SelectionKey(single.Id, single.EntityType)]
                : (IReadOnlyList<SelectionKey>)[];

        if (!TryEditorFunc(
                () => _store.ValidateCopySelection(candidates),
                out CopyValidationResult result,
                fallback: CopyValidationResult.NothingToCopy))
            return;

        if (result.IsMixedTypes)
        {
            _dialogService.ShowWarning("같은 종류의 항목만 복사할 수 있습니다.");
            return;
        }
        if (result.IsMixedParents)
        {
            _dialogService.ShowWarning("서로 다른 위치에 있는 항목은 함께 복사할 수 없습니다.");
            return;
        }
        if (result is not CopyValidationResult.Ok ok)
            return;

        var validated = ok.Item;
        _clipboardSelection.Clear();
        _pasteCount = 0;
        foreach (var key in validated)
            _clipboardSelection.Add(key);

        StatusText = $"Copied {_clipboardSelection.Count} {validated[0].EntityKind}(s).";
    }

    [RelayCommand]
    private void PasteCopied()
    {
        if (_clipboardSelection.Count == 0)
            return;

        var target = ResolvePasteTarget();
        if (!target.HasValue)
            return;

        var batchType = _clipboardSelection[0].EntityKind;

        // Work/Call 붙여넣기 시 System이 선택된 경우 → Flow를 선택하도록 안내
        if (target.Value.EntityType == EntityKind.System
            && (batchType == EntityKind.Work || batchType == EntityKind.Call))
        {
            _dialogService.ShowWarning("붙여넣기 대상으로 Flow를 선택하세요.");
            return;
        }

        // Flow 붙여넣기: 새 이름 입력 다이얼로그
        if (batchType == EntityKind.Flow)
        {
            PasteFlowsWithRename(target.Value);
            return;
        }

        var pasteIndex = _pasteCount * _clipboardSelection.Count;
        if (!TryEditorRef(
                () => _store.PasteEntities(
                    batchType,
                    _clipboardSelection.Select(k => k.Id),
                    target.Value.EntityType,
                    target.Value.EntityId,
                    pasteIndex),
                out var pastedIds))
            return;

        _pasteCount++;
        ApplyPasteSelection(pastedIds, $"Pasted {pastedIds.Length} {batchType}(s).");
    }

    private void PasteFlowsWithRename((EntityKind EntityType, Guid EntityId) target)
    {
        var pastedIds = new List<Guid>();
        var targetSystemIdOpt = StoreHierarchyQueries.resolveTarget(
            _store, EntityKind.System, target.EntityType, target.EntityId);

        foreach (var key in _clipboardSelection)
        {
            if (!_store.FlowsReadOnly.TryGetValue(key.Id, out var srcFlow))
                continue;

            var newName = _dialogService.PromptName("Flow 복사 — 새 이름", srcFlow.Name);
            if (newName is null) return; // 취소 시 전체 중단

            var sysId = targetSystemIdOpt != null ? targetSystemIdOpt.Value : srcFlow.ParentId;

            if (!TryEditorRef(
                    () => _store.PasteFlowWithRename(key.Id, sysId, newName),
                    out var resultOpt))
                return;

            if (resultOpt != null)
                pastedIds.Add(resultOpt.Value);
        }

        ApplyPasteSelection(pastedIds, $"Pasted {pastedIds.Count} Flow(s).");
    }

    private (EntityKind EntityType, Guid EntityId)? ResolvePasteTarget()
    {
        if (SelectedNode is { } selected)
            return (selected.EntityType, selected.Id);

        if (Canvas.ActiveTab is not { } tab)
            return null;

        return (EntityTabQueries.entityKindForTabKind(tab.Kind), tab.RootId);
    }

    private void ApplyPasteSelection(IReadOnlyCollection<Guid> pastedIds, string statusText)
    {
        if (pastedIds.Count == 0)
            return;

        StatusText = statusText;
        var idSet = pastedIds.ToHashSet();
        RequestRebuildAll(() =>
        {
            Selection.ClearNodeSelection();
            foreach (var node in Canvas.CanvasNodes.Where(n => idSet.Contains(n.Id)))
                Selection.SelectNodeFromCanvas(node, ctrlPressed: true, shiftPressed: false);
        });
    }

    private const int CascadeOffset = 30;
    private const int DefaultViewportCenterX = 1300;
    private const int DefaultViewportCenterY = 1000;

    private Xywh ConsumeAddPosition()
    {
        var pos = PendingAddPosition;
        PendingAddPosition = null;

        if (pos is { } p)
            return p;

        if (Canvas.GetViewportCenterRequested?.Invoke() is { } center)
            return new Xywh(
                (int)center.X - UiDefaults.DefaultNodeWidth / 2,
                (int)center.Y - UiDefaults.DefaultNodeHeight / 2,
                UiDefaults.DefaultNodeWidth, UiDefaults.DefaultNodeHeight);

        return new Xywh(
            DefaultViewportCenterX - UiDefaults.DefaultNodeWidth / 2,
            DefaultViewportCenterY - UiDefaults.DefaultNodeHeight / 2,
            UiDefaults.DefaultNodeWidth, UiDefaults.DefaultNodeHeight);
    }

    private SiblingSnapshot GetSiblingSnapshot(TabKind tabKind, Guid rootId)
    {
        if (!TryEditorRef(
                () => _store.CanvasContentForTab(tabKind, rootId),
                out var content))
            return SiblingSnapshot.Empty;

        return new SiblingSnapshot(
            content.Nodes.Select(n => n.Id).ToHashSet(),
            content.Nodes.Select(n => ((int)n.X, (int)n.Y)).ToList());
    }

    private Xywh CascadePosition(Xywh basePos, int index, IReadOnlyList<(int X, int Y)>? storePositions = null)
    {
        var x = basePos.X + CascadeOffset * index;
        var y = basePos.Y + CascadeOffset * index;

        bool HasOverlap(int cx, int cy)
        {
            if (Canvas.CanvasNodes.Any(n => Math.Abs(n.X - cx) < 10 && Math.Abs(n.Y - cy) < 10))
                return true;
            return storePositions?.Any(p => Math.Abs(p.X - cx) < 10 && Math.Abs(p.Y - cy) < 10) == true;
        }

        while (HasOverlap(x, y))
        {
            x += CascadeOffset;
            y += CascadeOffset;
        }

        return new Xywh(x, y, basePos.W, basePos.H);
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void AutoLayout()
    {
        if (Canvas.ActiveTab is not { } tab) return;

        if (!TryEditorRef(
                () => _store.ComputeAutoLayout(tab.Kind, tab.RootId),
                out var requests))
            return;

        if (requests.IsEmpty) return;

        TryEditorAction(() => _store.MoveEntities(requests));
        RequestRebuildAll(() => Canvas.FitToViewZoomOutRequested?.Invoke());
    }

    private Guid? ResolveTargetId(EntityKind selectedEntityType, TabKind activeTabKind)
    {
        if (SelectedNode is { EntityType: var type } node && type == selectedEntityType)
            return node.Id;

        if (Canvas.ActiveTab is { Kind: var kind } tab && kind == activeTabKind)
            return tab.RootId;

        return null;
    }
}
