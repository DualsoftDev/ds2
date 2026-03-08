using System;
using System.Collections.Generic;
using Ds2.Core;
using Ds2.UI.Core;
using Promaker;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    public void SetActiveTreePane(TreePaneKind pane) => _activeTreePane = pane;

    public void SelectNodeFromTree(EntityNode? node, bool ctrlPressed, bool shiftPressed)
    {
        var orderedKeys = EnumerateVisibleActiveTreeNodes().Select(ToKey).ToList();
        UpdateNodeSelection(node, ctrlPressed, shiftPressed, orderedKeys);
        if (node is not null)
            ExpandNodeAndAncestors(node.Id);
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
            .Where(n => n.IsSelected && EntityTypes.IsWorkOrCall(n.EntityType))
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

        var orderedKeys = _store.OrderCanvasSelectionKeysForBox(
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
        _store.OrderCanvasSelectionKeys(CanvasNodes.Select(ToCanvasSelectionCandidate))
            .ToList();

    public void ClearNodeSelection()
    {
        _orderedNodeSelection.Clear();
        _selectionAnchor = null;
        ApplyNodeSelectionVisuals();
    }

    public bool TryGetOrderedSelectionConnectEntityType(out EntityKind entityType)
    {
        entityType = default;

        if (_orderedNodeSelection.Count < 2)
            return false;

        foreach (var key in _orderedNodeSelection)
        {
            if (EntityTypes.IsWorkOrCall(key.EntityKind))
            {
                entityType = key.EntityKind;
                return true;
            }
        }

        return false;
    }

    public bool ConnectSelectedNodesInOrder(ArrowType arrowType)
    {
        if (_orderedNodeSelection.Count < 2)
            return false;

        if (!TryEditorFunc(
                () => _store.ConnectSelectionInOrder(_orderedNodeSelection.Select(s => s.Id), arrowType),
                out var created,
                fallback: 0,
                statusOverride: "[ERROR] Failed to connect selected nodes."))
            return false;

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

        var existingNodeKeys = TreeNodeSearch.EnumerateNodes(EnumerateTreeRoots())
            .Select(ToKey)
            .ToHashSet();
        existingNodeKeys.UnionWith(CanvasNodes.Select(ToKey));

        var restoredNodeSet = new HashSet<SelectionKey>();
        foreach (var key in selectedKeys)
        {
            if (existingNodeKeys.Contains(key) && restoredNodeSet.Add(key))
                _orderedNodeSelection.Add(key);
        }

        _selectionAnchor = _orderedNodeSelection.Count > 0 ? _orderedNodeSelection[^1] : null;

        ApplyNodeSelectionVisuals();
        _orderedArrowSelection.Clear();

        var canvasArrowIds = CanvasArrows.Select(a => a.Id).ToHashSet();
        var restoredArrowSet = new HashSet<Guid>();
        foreach (var arrowId in selectedArrowIds)
        {
            if (canvasArrowIds.Contains(arrowId) && restoredArrowSet.Add(arrowId))
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
        var result = _store.ApplyNodeSelection(
            _orderedNodeSelection,
            ToOption(_selectionAnchor),
            ToOption(node is null ? null : ToKey(node)),
            ctrlPressed,
            shiftPressed,
            orderedKeys);

        _orderedNodeSelection.Clear();
        foreach (var key in result.OrderedKeys)
            if (!_orderedNodeSelection.Contains(key))
                _orderedNodeSelection.Add(key);

        _selectionAnchor = result.Anchor?.Value;

        ApplyNodeSelectionVisuals();
        if (node is not null)
            ClearArrowSelection();
    }

    private void ApplyNodeSelectionVisuals()
    {
        var selectionOrder = new Dictionary<SelectionKey, int>();
        for (var i = 0; i < _orderedNodeSelection.Count; i++)
            selectionOrder[_orderedNodeSelection[i]] = i + 1;

        ApplySelectionTo(CanvasNodes, selectionOrder, static (n, s) => n.IsSelected = s);
        ApplySelectionTo(EnumerateTreeNodes(), selectionOrder, static (n, s) => n.IsTreeSelected = s);

        SelectedNode = ResolvePrimarySelectedNode();
    }

    private static void ApplySelectionTo(
        IEnumerable<EntityNode> nodes,
        Dictionary<SelectionKey, int> selectionOrder,
        Action<EntityNode, bool> setSelected)
    {
        foreach (var node in nodes)
        {
            var key = ToKey(node);
            if (selectionOrder.TryGetValue(key, out var order))
            {
                setSelected(node, true);
                node.SelectionOrder = order;
            }
            else
            {
                setSelected(node, false);
                node.SelectionOrder = 0;
            }
        }
    }

    private EntityNode? ResolvePrimarySelectedNode()
    {
        if (_orderedNodeSelection.Count == 0)
            return null;

        var key = _orderedNodeSelection[^1];
        var canvasNode = CanvasNodes.FirstOrDefault(n => n.Id == key.Id && n.EntityType == key.EntityKind);
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

    private void ExpandNodeAndAncestors(Guid nodeId)
    {
        var nodeIndex = TreeNodeSearch.EnumerateNodes(EnumerateTreeRoots())
            .ToDictionary(n => n.Id);

        if (!nodeIndex.TryGetValue(nodeId, out var node))
            return;

        node.IsExpanded = true;
        var parentId = node.ParentId;

        while (parentId is { } id && nodeIndex.TryGetValue(id, out var parent))
        {
            parent.IsExpanded = true;
            parentId = parent.ParentId;
        }
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
