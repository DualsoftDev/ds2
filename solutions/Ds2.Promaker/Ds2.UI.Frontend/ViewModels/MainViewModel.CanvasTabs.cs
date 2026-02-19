using System;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Core;
using Microsoft.FSharp.Core;

namespace Ds2.UI.Frontend.ViewModels;

public partial class MainViewModel
{
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

    public void OpenCanvasTab(Guid entityId, string entityType)
    {
        var infoOpt = EntityHierarchyQueries.tryOpenTabForEntity(_store, entityType, entityId);
        if (!FSharpOption<TabOpenInfo>.get_IsSome(infoOpt))
            return;

        var info = infoOpt.Value;
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

        var content = CanvasProjection.canvasContentForTab(_store, ActiveTab.Kind, ActiveTab.RootId);

        foreach (var n in content.Nodes)
        {
            CanvasNodes.Add(new EntityNode(n.Id, n.EntityType, n.Name, n.ParentId)
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
        if (ActiveTab is null || CanvasArrows.Count == 0) return;

        var flowIds = EntityHierarchyQueries.flowIdsForTab(_store, ActiveTab.Kind, ActiveTab.RootId);
        foreach (var flowId in flowIds)
            ApplyArrowPathsFromFlow(flowId);
    }

    private void ApplyArrowPathsFromFlow(Guid flowId)
    {
        var paths = _editor.GetFlowArrowPaths(flowId);
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

        var trees = TreeProjection.buildTrees(_store);
        foreach (var info in trees.Item1)
            ControlTreeRoots.Add(MapToEntityNode(info));
        foreach (var info in trees.Item2)
            DeviceTreeRoots.Add(MapToEntityNode(info));

        ApplyExpansionState(ControlTreeRoots, expandedNodes);
        ApplyExpansionState(DeviceTreeRoots, expandedNodes);

        var deadTabs = OpenTabs.Where(t => !TabExists(t)).ToList();
        foreach (var t in deadTabs)
            OpenTabs.Remove(t);

        if (ActiveTab is not null && !OpenTabs.Contains(ActiveTab))
            ActiveTab = OpenTabs.Count > 0 ? OpenTabs[0] : null;

        foreach (var t in OpenTabs)
            t.Title = ResolveTabTitle(t);

        RefreshCanvasForActiveTab();
        RestoreSelection(prevSelection, prevSelectedArrowIds);
    }

    private bool TabExists(CanvasTab tab) => EntityHierarchyQueries.tabExists(_store, tab.Kind, tab.RootId);

    private string ResolveTabTitle(CanvasTab tab)
    {
        var titleOpt = EntityHierarchyQueries.tabTitle(_store, tab.Kind, tab.RootId);
        return FSharpOption<string>.get_IsSome(titleOpt) ? titleOpt.Value : tab.Title;
    }

    private static EntityNode MapToEntityNode(TreeNodeInfo info)
    {
        var parentId = FSharpOption<Guid>.get_IsSome(info.ParentId) ? (Guid?)info.ParentId.Value : null;
        var node = new EntityNode(info.Id, info.EntityType, info.Name, parentId);
        foreach (var child in info.Children)
            node.Children.Add(MapToEntityNode(child));
        return node;
    }

    private void ApplyNodeMove(Guid nodeId, FSharpOption<Xywh> newPos)
    {
        var node = CanvasNodes.FirstOrDefault(n => n.Id == nodeId);
        if (node is null)
            return;

        if (FSharpOption<Xywh>.get_IsSome(newPos))
        {
            var p = newPos.Value;
            node.X = p.X;
            node.Y = p.Y;
            node.Width = p.W;
            node.Height = p.H;
        }
        else
        {
            // Position=None means "use default canvas bounds".
            node.X = UiDefaults.DefaultNodeXf;
            node.Y = UiDefaults.DefaultNodeYf;
            node.Width = UiDefaults.DefaultNodeWidthf;
            node.Height = UiDefaults.DefaultNodeHeightf;
        }

        RefreshArrowPaths();
    }
}
