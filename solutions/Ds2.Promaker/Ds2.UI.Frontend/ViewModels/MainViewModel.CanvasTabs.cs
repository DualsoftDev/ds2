using System;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Core;
using Microsoft.FSharp.Core;

namespace Ds2.UI.Frontend.ViewModels;

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

    public void OpenCanvasTab(Guid entityId, string entityType)
    {
        if (!TryEditorFunc(
                "TryOpenTabForEntity",
                () => _editor.TryOpenTabForEntity(entityType, entityId),
                out var infoOpt,
                fallback: (FSharpOption<TabOpenInfo>?)null))
            return;

        if (!FSharpOption<TabOpenInfo>.get_IsSome(infoOpt))
            return;

        var info = infoOpt!.Value;
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

        if (!TryEditorFunc(
                "CanvasContentForTab",
                () => _editor.CanvasContentForTab(ActiveTab.Kind, ActiveTab.RootId),
                out var content,
                fallback: (CanvasContent?)null,
                statusOverride: "[ERROR] Failed to refresh canvas content."))
            return;

        if (content is null)
            return;

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
        if (ActiveTab is null || CanvasArrows.Count == 0)
            return;

        if (!TryEditorFunc(
                "FlowIdsForTab",
                () => _editor.FlowIdsForTab(ActiveTab.Kind, ActiveTab.RootId),
                out var flowIds,
                fallback: null,
                statusOverride: "[ERROR] Failed to resolve flow ids for canvas."))
            return;

        if (flowIds is null)
            return;

        foreach (var flowId in flowIds)
            ApplyArrowPathsFromFlow(flowId);
    }

    private void ApplyArrowPathsFromFlow(Guid flowId)
    {
        if (!TryEditorFunc(
                "GetFlowArrowPaths",
                () => _editor.GetFlowArrowPaths(flowId),
                out var paths,
                fallback: null))
            return;

        if (paths is null)
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

        if (!TryEditorFunc(
                "BuildTrees",
                () => _editor.BuildTrees(),
                out var trees,
                fallback: null,
                statusOverride: "[ERROR] Failed to rebuild tree views."))
            return;

        if (trees is null)
            return;

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

    private bool TabExists(CanvasTab tab)
    {
        if (!TryEditorFunc("TabExists", () => _editor.TabExists(tab.Kind, tab.RootId), out var exists, fallback: false))
            return false;

        return exists;
    }

    private string ResolveTabTitle(CanvasTab tab)
    {
        if (!TryEditorFunc(
                "TabTitle",
                () => _editor.TabTitle(tab.Kind, tab.RootId),
                out var titleOpt,
                fallback: (FSharpOption<string>?)null))
            return tab.Title;

        if (!FSharpOption<string>.get_IsSome(titleOpt))
            return tab.Title;

        return titleOpt!.Value;
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
            node.X = UiDefaults.DefaultNodeXf;
            node.Y = UiDefaults.DefaultNodeYf;
            node.Width = UiDefaults.DefaultNodeWidthf;
            node.Height = UiDefaults.DefaultNodeHeightf;
        }

        RefreshArrowPaths();
    }
}
