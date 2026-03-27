using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Store;
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
        var projects = DsQuery.allProjects(_store);
        if (projects.IsEmpty) return true;
        var activeSystems = DsQuery.activeSystemsOf(projects.Head.Id, _store);
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
                EntityKind.System => !DsQuery.flowsOf(selected.Id, _store).IsEmpty,
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

    private static string GetUniqueNameForFlow(string baseName, Microsoft.FSharp.Collections.FSharpList<Flow> existingFlows)
    {
        var existingNames = new HashSet<string>(existingFlows.Select(f => f.Name));
        if (!existingNames.Contains(baseName))
            return baseName;

        var counter = 1;
        string candidateName;
        do
        {
            candidateName = $"{baseName}{counter}";
            counter++;
        } while (existingNames.Contains(candidateName));

        return candidateName;
    }

    private static string GetUniqueNameForWork(string baseName, Microsoft.FSharp.Collections.FSharpList<Work> existingWorks)
    {
        var existingNames = new HashSet<string>(existingWorks.Select(w => w.LocalName));
        if (!existingNames.Contains(baseName))
            return baseName;

        var counter = 1;
        string candidateName;
        do
        {
            candidateName = $"{baseName}{counter}";
            counter++;
        } while (existingNames.Contains(candidateName));

        return candidateName;
    }

    private (EntityKind? SelectedEntityKind, Guid? SelectedEntityId, TabKind? ActiveTabKind, Guid? ActiveTabRootId) SnapshotContext() =>
        (SelectedNode?.EntityType, SelectedNode?.Id, Canvas.ActiveTab?.Kind, Canvas.ActiveTab?.RootId);

    private void CascadeMoveCreatedEntities(
        IReadOnlyCollection<Guid> createdIds,
        Xywh basePos,
        IReadOnlyList<(int X, int Y)> existingPositions)
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

        TryEditorAction(() => _store.MoveEntities(requests));
    }

    private void CascadeMoveNewSiblingDiff(
        TabKind tabKind,
        Guid rootId,
        Xywh basePos,
        SiblingSnapshot before)
    {
        var createdIds = GetSiblingSnapshot(tabKind, rootId).Ids
            .Where(id => !before.Ids.Contains(id))
            .ToList();
        CascadeMoveCreatedEntities(createdIds, basePos, before.Positions);
    }

    private bool TryCreateSingleWithCascade(
        Func<Guid> create,
        Xywh basePos,
        IReadOnlyList<(int X, int Y)> existingPositions)
    {
        if (!TryEditorFunc(create, out Guid createdId, fallback: Guid.Empty) || createdId == Guid.Empty)
            return false;

        CascadeMoveCreatedEntities([createdId], basePos, existingPositions);
        return true;
    }

    private bool TryCreateMultipleWithCascade(
        IEnumerable<Func<Guid>> creators,
        Xywh basePos,
        IReadOnlyList<(int X, int Y)> existingPositions)
    {
        var createdIds = new List<Guid>();
        foreach (var create in creators)
        {
            if (!TryEditorFunc(create, out Guid createdId, fallback: Guid.Empty))
                return false;
            if (createdId != Guid.Empty)
                createdIds.Add(createdId);
        }

        CascadeMoveCreatedEntities(createdIds, basePos, existingPositions);
        return createdIds.Count > 0;
    }

    private bool TryCreateSiblingDiffWithCascade(
        Action create,
        TabKind tabKind,
        Guid rootId,
        Xywh basePos,
        SiblingSnapshot before)
    {
        if (!TryEditorAction(create))
            return false;

        CascadeMoveNewSiblingDiff(tabKind, rootId, basePos, before);
        return true;
    }

    private static string NormalizeApiName(string fullName) =>
        fullName.Contains('.') ? fullName[(fullName.IndexOf('.') + 1)..] : fullName;

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
                () => EditorCanvasProjection.CanvasContentForTab(_store, tabKind, rootId),
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
        var flows = DsQuery.flowsOf(tab.RootId, _store);
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

    private static string NextUniqueName(string baseName, List<string> existing)
    {
        if (!existing.Contains(baseName)) return baseName;
        var i = 1;
        while (existing.Contains($"{baseName}_{i}")) i++;
        return $"{baseName}_{i}";
    }
}
