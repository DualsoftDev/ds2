using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Core;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void AddSystem()
    {
        var name = DialogHelpers.PromptName("New System", "NewSystem");
        if (name is null) return;
        var (selType, selId, tabKind, tabRoot) = SnapshotContext();
        TryEditorAction(() => _store.AddSystemResolved(
            name, Selection.ActiveTreePane == TreePaneKind.Control,
            selType, selId, tabKind, tabRoot));
    }

    [RelayCommand]
    private void AddFlow()
    {
        var name = DialogHelpers.PromptName("New Flow", "NewFlow");
        if (name is null) return;
        var (selType, selId, tabKind, tabRoot) = SnapshotContext();
        TryEditorAction(() => _store.AddFlowResolved(
            name, selType, selId, tabKind, tabRoot));
    }

    private (EntityKind? SelectedEntityKind, Guid? SelectedEntityId, TabKind? ActiveTabKind, Guid? ActiveTabRootId) SnapshotContext() =>
        (SelectedNode?.EntityType, SelectedNode?.Id, Canvas.ActiveTab?.Kind, Canvas.ActiveTab?.RootId);

    [RelayCommand]
    private void AddWork()
    {
        var flowId = ResolveTargetId(EntityKind.Flow, TabKind.Flow);
        if (flowId is not { } id) return;

        var name = DialogHelpers.PromptName("New Work", "NewWork");
        if (name is null) return;

        if (!TryEditorFunc(() => _store.AddWork(name, id), out Guid workId, fallback: Guid.Empty))
            return;
        if (workId == Guid.Empty) return;

        var basePos = ConsumeAddPosition();
        var siblings = GetSiblingPositions(TabKind.Flow, id);
        var pos = CascadePosition(basePos, 0, siblings);
        TryEditorAction(() => _store.MoveEntities([new MoveEntityRequest(workId, pos)]));
    }

    [RelayCommand]
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
        var siblings = GetSiblingPositions(TabKind.Work, targetWorkId);

        if (dialog.IsDeviceMode)
        {
            var beforeIds = GetSiblingNodeIds(TabKind.Work, targetWorkId);
            TryEditorAction(
                () => _store.AddCallsWithDeviceResolved(EntityKind.Work, targetWorkId, targetWorkId, dialog.CallNames, true));
            var newCallIds = GetSiblingNodeIds(TabKind.Work, targetWorkId)
                .Where(id => !beforeIds.Contains(id))
                .ToList();
            if (newCallIds.Count > 0)
            {
                var assigned = new List<(int X, int Y)>(siblings);
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
                var pos = CascadePosition(rawPos, 0, siblings);
                TryEditorAction(() => _store.MoveEntities([new MoveEntityRequest(callId, pos)]));
            }
        }
    }

    [RelayCommand]
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
            MessageBox.Show("같은 종류의 항목만 복사할 수 있습니다.", "복사 불가",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (result.IsMixedParents)
        {
            MessageBox.Show("서로 다른 위치에 있는 항목은 함께 복사할 수 없습니다.", "복사 불가",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (result is not CopyValidationResult.Ok ok)
            return;

        var validated = ok.Item;
        _clipboardSelection.Clear();
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
            MessageBox.Show(
                "붙여넣기 대상으로 Flow를 선택하세요.",
                "붙여넣기 불가",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!TryEditorRef(
                () => _store.PasteEntities(
                    batchType,
                    _clipboardSelection.Select(k => k.Id),
                    target.Value.EntityType,
                    target.Value.EntityId),
                out var pastedIds))
            return;

        if (pastedIds.Length > 0)
        {
            StatusText = $"Pasted {pastedIds.Length} {batchType}(s).";
            var idSet = new HashSet<Guid>(pastedIds);
            RequestRebuildAll(() =>
            {
                Selection.ClearNodeSelection();
                foreach (var node in Canvas.CanvasNodes.Where(n => idSet.Contains(n.Id)))
                    Selection.SelectNodeFromCanvas(node, ctrlPressed: true, shiftPressed: false);
            });
        }
    }

    private (EntityKind EntityType, Guid EntityId)? ResolvePasteTarget()
    {
        if (SelectedNode is { } selected)
            return (selected.EntityType, selected.Id);

        if (Canvas.ActiveTab is not { } tab)
            return null;

        return (EntityHierarchyQueries.entityKindForTabKind(tab.Kind), tab.RootId);
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

    private HashSet<Guid> GetSiblingNodeIds(TabKind tabKind, Guid rootId)
    {
        if (!TryEditorRef(
                () => _store.CanvasContentForTab(tabKind, rootId),
                out var content))
            return [];

        return content.Nodes
            .Select(n => n.Id)
            .ToHashSet();
    }

    private IReadOnlyList<(int X, int Y)> GetSiblingPositions(TabKind tabKind, Guid rootId)
    {
        if (!TryEditorRef(
                () => _store.CanvasContentForTab(tabKind, rootId),
                out var content))
            return [];

        return content.Nodes
            .Select(n => ((int)n.X, (int)n.Y))
            .ToList();
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

    private Guid? ResolveTargetId(EntityKind selectedEntityType, TabKind activeTabKind)
    {
        if (SelectedNode is { EntityType: var type } node && type == selectedEntityType)
            return node.Id;

        if (Canvas.ActiveTab is { Kind: var kind } tab && kind == activeTabKind)
            return tab.RootId;

        return null;
    }
}
