using System;
using System.Collections.Generic;
using Ds2.Core;
using Ds2.UI.Core;
using Microsoft.FSharp.Core;

namespace Ds2.UI.Frontend.ViewModels;

public partial class MainViewModel
{
    public void SetActiveTreePane(TreePaneKind pane) => _activeTreePane = pane;

    public void SelectNodeFromTree(EntityNode? node, bool ctrlPressed, bool shiftPressed)
    {
        var orderedKeys = EnumerateVisibleActiveTreeNodes().Select(ToKey).ToList();
        UpdateNodeSelection(node, ctrlPressed, shiftPressed, orderedKeys);
        if (node is not null)
            ExpandAncestors(node.Id);
    }

    public void SelectNodeFromCanvas(EntityNode? node, bool ctrlPressed, bool shiftPressed)
    {
        var orderedKeys = CanvasSelectionOrderKeys();
        UpdateNodeSelection(node, ctrlPressed, shiftPressed, orderedKeys);
    }

    public IReadOnlyList<EntityNode> PrepareCanvasDragSelection(EntityNode node, bool ctrlPressed, bool shiftPressed)
    {
        // Keep multi-selection on plain click-drag when the clicked node is already in current selection.
        var keepCurrentSelection =
            !ctrlPressed &&
            !shiftPressed &&
            node.IsSelected &&
            _orderedNodeSelection.Count > 1;

        if (!keepCurrentSelection)
            SelectNodeFromCanvas(node, ctrlPressed, shiftPressed);

        var dragNodes = CanvasNodes
            .Where(n => n.IsSelected && n.EntityType is "Work" or "Call")
            .ToList();

        if (dragNodes.Count == 0 || dragNodes.All(n => n.Id != node.Id))
            dragNodes = [node];

        return dragNodes;
    }

    public void SelectNodesFromCanvasBox(
        IEnumerable<EntityNode> nodes,
        bool additive,
        double startX,
        double startY,
        double endX,
        double endY)
    {
        if (!additive)
            _orderedNodeSelection.Clear();

        var orderedKeys = SelectionQueries.orderCanvasSelectionKeysForBox(
            startX,
            startY,
            endX,
            endY,
            nodes.Select(ToCanvasSelectionCandidate));

        foreach (var key in orderedKeys)
        {
            if (!_orderedNodeSelection.Contains(key))
                _orderedNodeSelection.Add(key);
        }

        if (_orderedNodeSelection.Count > 0)
            _selectionAnchor = _orderedNodeSelection[^1];

        ApplyNodeSelectionVisuals();
        ClearArrowSelection();
    }

    private List<SelectionKey> CanvasSelectionOrderKeys() =>
        SelectionQueries.orderCanvasSelectionKeys(CanvasNodes.Select(ToCanvasSelectionCandidate))
            .ToList();

    public void ClearNodeSelection()
    {
        _orderedNodeSelection.Clear();
        _selectionAnchor = null;
        ApplyNodeSelectionVisuals();
    }

    public bool TryGetOrderedSelectionConnectEntityType(out string entityType)
    {
        entityType = string.Empty;

        if (_orderedNodeSelection.Count < 2)
            return false;

        foreach (var key in _orderedNodeSelection)
        {
            if (key.EntityType is "Work" or "Call")
            {
                entityType = key.EntityType;
                return true;
            }
        }

        return false;
    }

    public bool ConnectSelectedNodesInOrder(ArrowType arrowType)
    {
        if (_orderedNodeSelection.Count < 2)
            return false;

        var created = _editor.ConnectSelectionInOrder(_orderedNodeSelection.Select(s => s.Id), arrowType);
        if (created <= 0)
            return false;

        StatusText = $"Connected {created} arrow(s) from ordered selection.";
        return true;
    }

    public void SelectArrowFromCanvas(ArrowNode arrow, bool ctrlPressed)
    {
        if (!ctrlPressed)
        {
            _orderedArrowSelection.Clear();
            _orderedArrowSelection.Add(arrow.Id);
        }
        else
        {
            if (_orderedArrowSelection.Contains(arrow.Id))
                _orderedArrowSelection.Remove(arrow.Id);
            else
                _orderedArrowSelection.Add(arrow.Id);
        }

        ApplyArrowSelectionVisuals();
    }

    private void RestoreSelection(List<SelectionKey> selectedKeys, List<Guid> selectedArrowIds)
    {
        _orderedNodeSelection.Clear();

        foreach (var key in selectedKeys)
        {
            var existsInTree = TreeNodeSearch.FindByKey(EnumerateTreeRoots(), key) is not null;
            var existsInCanvas = CanvasNodes.Any(n => n.Id == key.Id && n.EntityType == key.EntityType);
            if (!_orderedNodeSelection.Contains(key) && (existsInTree || existsInCanvas))
                _orderedNodeSelection.Add(key);
        }

        _selectionAnchor = _orderedNodeSelection.Count > 0 ? _orderedNodeSelection[^1] : null;

        ApplyNodeSelectionVisuals();
        _orderedArrowSelection.Clear();

        foreach (var arrowId in selectedArrowIds)
        {
            if (CanvasArrows.Any(a => a.Id == arrowId))
                _orderedArrowSelection.Add(arrowId);
        }

        ApplyArrowSelectionVisuals();
    }

    private static CanvasSelectionCandidate ToCanvasSelectionCandidate(EntityNode node) =>
        new(ToKey(node), node.X, node.Y, node.Width, node.Height, node.Name);

    private void UpdateNodeSelection(
        EntityNode? node,
        bool ctrlPressed,
        bool shiftPressed,
        IReadOnlyList<SelectionKey> orderedKeys)
    {
        var targetOption = node is null ? null : FSharpOption<SelectionKey>.Some(ToKey(node));
        var anchorOption = _selectionAnchor is null ? null : FSharpOption<SelectionKey>.Some(_selectionAnchor);

        var result = SelectionQueries.applyNodeSelection(
            _orderedNodeSelection,
            anchorOption,
            targetOption,
            ctrlPressed,
            shiftPressed,
            orderedKeys);

        _orderedNodeSelection.Clear();
        foreach (var key in result.OrderedKeys)
            if (!_orderedNodeSelection.Contains(key))
                _orderedNodeSelection.Add(key);

        _selectionAnchor = FSharpOption<SelectionKey>.get_IsSome(result.Anchor)
            ? result.Anchor.Value
            : null;

        ApplyNodeSelectionVisuals();
        if (node is not null)
            ClearArrowSelection();
    }

    private void ApplyNodeSelectionVisuals()
    {
        var selectionOrder = new Dictionary<SelectionKey, int>();
        for (var i = 0; i < _orderedNodeSelection.Count; i++)
            selectionOrder[_orderedNodeSelection[i]] = i + 1;

        foreach (var node in CanvasNodes)
        {
            var key = ToKey(node);
            if (selectionOrder.TryGetValue(key, out var order))
            {
                node.IsSelected = true;
                node.SelectionOrder = order;
            }
            else
            {
                node.IsSelected = false;
                node.SelectionOrder = 0;
            }
        }

        foreach (var node in EnumerateTreeNodes())
        {
            var key = ToKey(node);
            if (selectionOrder.TryGetValue(key, out var order))
            {
                node.IsTreeSelected = true;
                node.SelectionOrder = order;
            }
            else
            {
                node.IsTreeSelected = false;
                node.SelectionOrder = 0;
            }
        }

        SelectedNode = ResolvePrimarySelectedNode();
    }

    private EntityNode? ResolvePrimarySelectedNode()
    {
        if (_orderedNodeSelection.Count == 0)
            return null;

        var key = _orderedNodeSelection[^1];
        var canvasNode = CanvasNodes.FirstOrDefault(n => n.Id == key.Id && n.EntityType == key.EntityType);
        if (canvasNode is not null)
            return canvasNode;

        return TreeNodeSearch.FindByKey(EnumerateTreeRoots(), key);
    }

    public void ClearArrowSelection()
    {
        _orderedArrowSelection.Clear();
        ApplyArrowSelectionVisuals();
    }

    private void ApplyArrowSelectionVisuals()
    {
        _orderedArrowSelection.RemoveAll(id => CanvasArrows.All(arrow => arrow.Id != id));
        var selected = _orderedArrowSelection.ToHashSet();
        foreach (var arrow in CanvasArrows)
            arrow.IsSelected = selected.Contains(arrow.Id);

        var primaryArrowId = _orderedArrowSelection.Count > 0 ? _orderedArrowSelection[^1] : Guid.Empty;
        SelectedArrow = primaryArrowId == Guid.Empty
            ? null
            : CanvasArrows.FirstOrDefault(a => a.Id == primaryArrowId);
    }

    private void ExpandAncestors(Guid nodeId)
    {
        var parent = TreeNodeSearch.FindParentByChildId(EnumerateTreeRoots(), nodeId);
        while (parent is not null)
        {
            parent.IsExpanded = true;
            parent = parent.ParentId is { } parentId ? TreeNodeSearch.FindById(EnumerateTreeRoots(), parentId) : null;
        }
    }

    private void ExpandNodeAndAncestors(Guid nodeId)
    {
        if (TreeNodeSearch.FindById(EnumerateTreeRoots(), nodeId) is { } node)
            node.IsExpanded = true;

        ExpandAncestors(nodeId);
    }

    private static void ApplyExpansionState(IEnumerable<EntityNode> roots, HashSet<SelectionKey> expandedNodes)
    {
        foreach (var node in roots)
        {
            node.IsExpanded = expandedNodes.Contains(ToKey(node));
            ApplyExpansionState(node.Children, expandedNodes);
        }
    }

    private IEnumerable<EntityNode> EnumerateActiveTreeRoots() =>
        _activeTreePane == TreePaneKind.Device ? DeviceTreeRoots : ControlTreeRoots;

    private IEnumerable<EntityNode> EnumerateVisibleActiveTreeNodes() =>
        TreeNodeSearch.EnumerateVisibleNodes(EnumerateActiveTreeRoots());

    private IEnumerable<EntityNode> EnumerateTreeRoots() => ControlTreeRoots.Concat(DeviceTreeRoots);

    private IEnumerable<EntityNode> EnumerateTreeNodes() => TreeNodeSearch.EnumerateNodes(EnumerateTreeRoots());

    private static SelectionKey ToKey(EntityNode node) => new(node.Id, node.EntityType);
}
