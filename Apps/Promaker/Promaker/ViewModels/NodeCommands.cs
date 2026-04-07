using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;

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
        var projects = Queries.allProjects(_store);
        if (projects.IsEmpty) return true;
        var activeSystems = Queries.activeSystemsOf(projects.Head.Id, _store);
        return activeSystems.IsEmpty;
    }

    private bool CanAddWork()
    {
        if (!HasProject)
            return false;

        if (SelectedNode is { } selected)
        {
            return selected.EntityType switch
            {
                EntityKind.Flow => true,
                EntityKind.System => !Queries.flowsOf(selected.Id, _store).IsEmpty,
                _ => false
            };
        }

        if (Canvas.ActiveTab is { Kind: TabKind.Flow })
            return true;

        return ResolveFirstFlowInSystemTab().HasValue;
    }

    private bool CanAddCall()
    {
        if (!HasProject)
            return false;

        if (SelectedNode is { } selected)
            return selected.EntityType == EntityKind.Work;

        return Canvas.ActiveTab is { Kind: TabKind.Work };
    }

    private bool CanDeleteSelected()
    {
        if (!HasProject)
            return false;

        if (Selection.OrderedArrowSelection.Count > 0 || SelectedArrow is not null)
            return true;

        if (Selection.OrderedNodeSelection.Any(key => key.EntityKind != EntityKind.Project))
            return true;

        return SelectedNode is { EntityType: not EntityKind.Project };
    }

    private bool CanCopySelected() =>
        Selection.OrderedNodeSelection.Count > 0 || SelectedNode is not null;

    private bool CanPasteCopied() =>
        _clipboardSelection.Count > 0 && ResolvePasteTarget().HasValue;

    private bool CanConnectSelectedNodes() =>
        HasProject && Selection.CanConnectSelectedNodesInOrder();

    private bool CanAutoLayout() =>
        HasProject && Canvas.ActiveTab is not null;

    private static string GetUniqueName(string baseName, IEnumerable<string> existingNames, string separator = "")
    {
        var names = new HashSet<string>(existingNames);
        if (!names.Contains(baseName))
            return baseName;

        var counter = 1;
        string candidateName;
        do
        {
            candidateName = $"{baseName}{separator}{counter}";
            counter++;
        } while (names.Contains(candidateName));

        return candidateName;
    }

    private (EntityKind? SelectedEntityKind, Guid? SelectedEntityId, TabKind? ActiveTabKind, Guid? ActiveTabRootId) SnapshotContext() =>
        (SelectedNode?.EntityType, SelectedNode?.Id, Canvas.ActiveTab?.Kind, Canvas.ActiveTab?.RootId);

    private void CascadeMoveCreatedEntities(
        IReadOnlyCollection<Guid> createdIds,
        Xywh basePos,
        IReadOnlyList<(int X, int Y)> existingPositions,
        int mergeCount = 0,
        string? mergeLabel = null)
    {
        var ids = createdIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return;

        var assigned = new List<(int X, int Y)>(existingPositions);
        var requests = new List<MoveEntityRequest>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            var pos = CascadePosition(basePos, i, assigned);
            requests.Add(new MoveEntityRequest(ids[i], pos));
            assigned.Add((pos.X, pos.Y));
        }

        TryEditorFunc(() => _store.MoveEntities(requests), out int movedCount, fallback: 0);

        if (mergeLabel is not null)
        {
            var actualMerge = movedCount > 0 ? mergeCount : mergeCount - 1;
            if (actualMerge >= 2)
                _store.MergeLastTransactions(actualMerge, mergeLabel);
        }
    }

    private void CascadeMoveNewSiblingDiff(
        TabKind tabKind,
        Guid rootId,
        Xywh basePos,
        SiblingSnapshot before,
        string? mergeLabel = null)
    {
        var createdIds = GetSiblingSnapshot(tabKind, rootId).Ids
            .Where(id => !before.Ids.Contains(id))
            .ToList();
        // SiblingDiff: 1 create transaction + 1 move transaction
        CascadeMoveCreatedEntities(createdIds, basePos, before.Positions, mergeCount: 2, mergeLabel: mergeLabel);
    }

    private bool TryCreateSingleWithCascade(
        Func<Guid> create,
        Xywh basePos,
        IReadOnlyList<(int X, int Y)> existingPositions,
        string? mergeLabel = null)
    {
        if (!TryEditorFunc(create, out Guid createdId, fallback: Guid.Empty) || createdId == Guid.Empty)
            return false;

        // Single: 1 create transaction + 1 move transaction
        CascadeMoveCreatedEntities([createdId], basePos, existingPositions, mergeCount: 2, mergeLabel: mergeLabel);
        return true;
    }

    private bool TryCreateMultipleWithCascade(
        IEnumerable<Func<Guid>> creators,
        Xywh basePos,
        IReadOnlyList<(int X, int Y)> existingPositions,
        string? mergeLabel = null)
    {
        var createdIds = new List<Guid>();
        foreach (var create in creators)
        {
            if (!TryEditorFunc(create, out Guid createdId, fallback: Guid.Empty))
                return false;
            if (createdId != Guid.Empty)
                createdIds.Add(createdId);
        }

        // Multiple: N create transactions + 1 move transaction
        CascadeMoveCreatedEntities(createdIds, basePos, existingPositions,
            mergeCount: createdIds.Count + 1, mergeLabel: mergeLabel);
        return createdIds.Count > 0;
    }

    private bool TryCreateSiblingDiffWithCascade(
        Action create,
        TabKind tabKind,
        Guid rootId,
        Xywh basePos,
        SiblingSnapshot before,
        string? mergeLabel = null)
    {
        if (!TryEditorAction(create))
            return false;

        CascadeMoveNewSiblingDiff(tabKind, rootId, basePos, before, mergeLabel);
        return true;
    }

    private static string NormalizeApiName(string fullName) =>
        fullName.Contains('.') ? fullName[(fullName.IndexOf('.') + 1)..] : fullName;

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
                () => EditorCanvasProjection.CanvasContentForTab(_store, tabKind, rootId),
                out var content))
            return SiblingSnapshot.Empty;

        return new SiblingSnapshot(
            content.Nodes.Select(n => n.Id).ToHashSet(),
            content.Nodes.Select(n => ((int)n.X, (int)n.Y)).ToList());
    }

    private Xywh CascadePosition(Xywh basePos, int index, IReadOnlyList<(int X, int Y)>? storePositions = null)
    {
        var gridX = UiDefaults.DefaultNodeWidth;
        var gridY = UiDefaults.DefaultNodeHeight;
        var maxX = 3000 - gridX;

        var startX = (int)(Math.Round((double)basePos.X / gridX) * gridX);
        var startY = (int)(Math.Round((double)basePos.Y / gridY) * gridY);
        startX = Math.Clamp(startX, 0, maxX);
        startY = Math.Max(startY, 0);

        var cols = (maxX - startX) / gridX + 1;
        int x, y;
        if (cols > 0 && index >= cols)
        {
            x = startX + gridX * (index % cols);
            y = startY + gridY * (index / cols);
        }
        else
        {
            x = startX + gridX * index;
            y = startY;
        }

        bool HasOverlap(int cx, int cy)
        {
            if (Canvas.CanvasNodes.Any(n => Math.Abs(n.X - cx) < gridX && Math.Abs(n.Y - cy) < gridY))
                return true;
            return storePositions?.Any(p => Math.Abs(p.X - cx) < gridX && Math.Abs(p.Y - cy) < gridY) == true;
        }

        var maxAttempts = (3000 / gridX) * (2000 / gridY);
        var attempts = 0;
        while (HasOverlap(x, y) && attempts++ < maxAttempts)
        {
            x += gridX;
            if (x > maxX)
            {
                x = startX;
                y += gridY;
            }
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

    private Guid? ResolveFirstFlowInSystemTab()
    {
        if (Canvas.ActiveTab is not { Kind: TabKind.System } tab) return null;
        var flows = Queries.flowsOf(tab.RootId, _store);
        return flows.IsEmpty ? null : (Guid?)flows.Head.Id;
    }

    private (EntityKind EntityType, Guid EntityId)? ResolvePasteTarget()
    {
        if (SelectedNode is { } selected)
            return (selected.EntityType, selected.Id);

        if (Canvas.ActiveTab is not { } tab)
            return null;

        return (EditorNavigation.EntityKindForTabKind(tab.Kind), tab.RootId);
    }

    private void ApplyPasteSelection(IReadOnlyCollection<Guid> pastedIds, string statusText)
    {
        if (pastedIds.Count == 0)
        {
            StatusText = statusText;
            return;
        }

        StatusText = statusText;
        var idSet = pastedIds.ToHashSet();
        RequestRebuildAll(() =>
        {
            Selection.ClearNodeSelection();
            foreach (var node in Canvas.CanvasNodes.Where(n => idSet.Contains(n.Id)))
                Selection.SelectNodeFromCanvas(node, ctrlPressed: true, shiftPressed: false);
        });
    }

}
