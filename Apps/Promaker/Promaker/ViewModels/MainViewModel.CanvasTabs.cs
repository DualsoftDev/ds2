using System;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Core;
using System.Windows.Threading;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    partial void OnActiveTabChanged(CanvasTab? value)
    {
        foreach (var t in OpenTabs)
            t.IsActive = t == value;

        _orderedNodeSelection.Clear();
        SelectedNode = null;
        ClearArrowSelection();
        RefreshCanvasForActiveTab();
    }

    private void OpenTab(TabKind kind, Guid rootId, string title)
    {
        var existing = OpenTabs.FirstOrDefault(t => t.Kind == kind && t.RootId == rootId);
        if (existing is not null)
        {
            ActiveTab = existing;
            return;
        }

        var tab = new CanvasTab(rootId, kind, title);
        OpenTabs.Add(tab);
        ActiveTab = tab;
    }

    public void OpenCanvasTab(Guid entityId, EntityKind entityType)
    {
        if (!TryEditorRef(
                () => _store.TryOpenTabForEntityOrNull(entityType, entityId),
                out var info))
            return;

        OpenTab(info.Kind, info.RootId, info.Title);
    }

    [RelayCommand]
    private void CloseTab(CanvasTab? tab)
    {
        if (tab is null) return;
        var idx = OpenTabs.IndexOf(tab);
        OpenTabs.Remove(tab);
        if (ActiveTab == tab)
            ActiveTab = OpenTabs.Count > 0 ? OpenTabs[Math.Min(idx, OpenTabs.Count - 1)] : null;
    }

    private void RefreshCanvasForActiveTab()
    {
        CanvasNodes.Clear();
        CanvasArrows.Clear();

        if (ActiveTab is null)
        {
            ApplyNodeSelectionVisuals();
            return;
        }

        if (!TryEditorRef(
                () => _store.CanvasContentForTab(ActiveTab.Kind, ActiveTab.RootId),
                out var content,
                statusOverride: "[ERROR] Failed to refresh canvas content."))
            return;

        foreach (var n in content.Nodes)
        {
            CanvasNodes.Add(new EntityNode(n.Id, n.EntityKind, n.Name, n.ParentId)
            {
                X = n.X,
                Y = n.Y,
                Width = n.Width,
                Height = n.Height
            });
        }

        foreach (var a in content.Arrows)
            CanvasArrows.Add(new ArrowNode(a.Id, a.SourceId, a.TargetId, a.ArrowType));

        RefreshArrowPaths();
        ApplyNodeSelectionVisuals();
    }

    private void RefreshArrowPaths()
    {
        if (ActiveTab is null || CanvasArrows.Count == 0)
            return;

        if (!TryEditorRef(
                () => _store.FlowIdsForTab(ActiveTab.Kind, ActiveTab.RootId),
                out var flowIds,
                statusOverride: "[ERROR] Failed to resolve flow ids for canvas."))
            return;

        foreach (var flowId in flowIds)
            ApplyArrowPathsFromFlow(flowId);
    }

    private void ApplyArrowPathsFromFlow(Guid flowId)
    {
        if (!TryEditorRef(
                () => _store.GetFlowArrowPaths(flowId),
                out var paths))
            return;

        foreach (var arrow in CanvasArrows)
            if (paths.TryGetValue(arrow.Id, out var visual))
                arrow.UpdateFromVisual(visual);
    }

    private void RebuildAll()
    {
        var prevSelection = _orderedNodeSelection.ToList();
        var prevSelectedArrowIds = _orderedArrowSelection.ToList();
        var expandedNodes = EnumerateTreeNodes()
            .Where(n => n.IsExpanded)
            .Select(ToKey)
            .ToHashSet();

        ControlTreeRoots.Clear();
        DeviceTreeRoots.Clear();

        if (!TryEditorRef(
                () => _store.BuildTrees(),
                out var trees,
                statusOverride: "[ERROR] Failed to rebuild tree views."))
            return;

        foreach (var info in trees.Item1)
            ControlTreeRoots.Add(MapToEntityNode(info));
        foreach (var info in trees.Item2)
            DeviceTreeRoots.Add(MapToEntityNode(info));

        ApplyExpansionState(ControlTreeRoots, expandedNodes);
        ApplyExpansionState(DeviceTreeRoots, expandedNodes);

        var deadTabs = new List<CanvasTab>();
        foreach (var t in OpenTabs)
        {
            var title = ResolveTabTitle(t);
            if (title is null)
                deadTabs.Add(t);
            else
                t.Title = title;
        }
        foreach (var t in deadTabs)
            OpenTabs.Remove(t);

        if (ActiveTab is not null && !OpenTabs.Contains(ActiveTab))
            ActiveTab = OpenTabs.Count > 0 ? OpenTabs[0] : null;

        RefreshCanvasForActiveTab();
        RestoreSelection(prevSelection, prevSelectedArrowIds);
    }

    private void RequestRebuildAll(Action? afterRebuild = null)
    {
        if (afterRebuild is not null)
            _pendingRebuildActions.Add(afterRebuild);

        if (_rebuildQueued)
            return;

        _rebuildQueued = true;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            _rebuildQueued = false;
            RebuildAll();

            if (_pendingRebuildActions.Count == 0)
                return;

            var actions = _pendingRebuildActions.ToArray();
            _pendingRebuildActions.Clear();
            foreach (var action in actions)
                action();
        }), DispatcherPriority.Background);
    }

    /// Returns the resolved title, or null if the tab's root entity no longer exists.
    private string? ResolveTabTitle(CanvasTab tab)
    {
        if (!TryEditorFunc(
                () => _store.TabTitleOrNull(tab.Kind, tab.RootId),
                out string? title,
                fallback: null))
            return null;

        return title;
    }

    private static EntityNode MapToEntityNode(TreeNodeInfo info)
    {
        var parentId = info.ParentIdOrNull;
        var node = new EntityNode(info.Id, info.EntityKind, info.Name, parentId);
        foreach (var child in info.Children)
            node.Children.Add(MapToEntityNode(child));
        return node;
    }
}
