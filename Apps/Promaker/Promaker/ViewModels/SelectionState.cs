using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.FSharp.Collections;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

public class SelectionState
{
    private readonly MainViewModel.SelectionHost _host;
    private TreePaneKind _activeTreePane = TreePaneKind.Control;
    private readonly List<SelectionKey> _orderedNodeSelection = [];
    private readonly List<Guid> _orderedArrowSelection = [];
    private SelectionKey? _selectionAnchor;

    public SelectionState(MainViewModel.SelectionHost host)
    {
        _host = host;
    }

    private DsStore Store => _host.Store;
    private IEnumerable<EntityNode> ControlTreeRoots => _host.ControlTreeRoots;
    private IEnumerable<EntityNode> DeviceTreeRoots => _host.DeviceTreeRoots;
    private IEnumerable<EntityNode> CanvasNodes => _host.CanvasNodes;
    private IEnumerable<ArrowNode> CanvasArrows => _host.CanvasArrows;

    public TreePaneKind ActiveTreePane => _activeTreePane;
    public IReadOnlyList<SelectionKey> OrderedNodeSelection => _orderedNodeSelection;
    public IReadOnlyList<Guid> OrderedArrowSelection => _orderedArrowSelection;

    public void Reset()
    {
        _orderedNodeSelection.Clear();
        _orderedArrowSelection.Clear();
        _selectionAnchor = null;
        _host.SelectedNode = null;
        _host.SelectedArrow = null;
        _host.NotifyCommandStatesChanged();
    }

    public void SetActiveTreePane(TreePaneKind pane) => _activeTreePane = pane;

    public void SelectNodeFromTree(EntityNode? node, bool ctrlPressed, bool shiftPressed)
    {
        var orderedKeys = EnumerateVisibleActiveTreeNodes().Select(ToKey).ToList();
        UpdateNodeSelection(node, ctrlPressed, shiftPressed, orderedKeys);
    }

    public void SelectNodeFromCanvas(EntityNode? node, bool ctrlPressed, bool shiftPressed)
    {
        var orderedKeys = CanvasSelectionOrderKeys();
        UpdateNodeSelection(node, ctrlPressed, shiftPressed, orderedKeys);
    }

    public IReadOnlyList<EntityNode> PrepareCanvasDragSelection(EntityNode node, bool ctrlPressed, bool shiftPressed)
    {
        var keepCurrentSelection =
            !ctrlPressed &&
            !shiftPressed &&
            node.IsSelected &&
            _orderedNodeSelection.Count > 1;

        if (!keepCurrentSelection)
            SelectNodeFromCanvas(node, ctrlPressed, shiftPressed);

        var dragNodes = _host.CanvasNodes
            .Where(n => n.IsSelected && EntityKindRules.isDraggableKind(n.EntityType))
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

        var orderedKeys = EditorSelectionQueries.OrderCanvasSelectionKeysForBox(
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

    public void ClearNodeSelection()
    {
        _orderedNodeSelection.Clear();
        _selectionAnchor = null;
        ApplyNodeSelectionVisuals();
    }

    public bool TryGetOrderedSelectionConnectEntityType(out EntityKind entityType)
    {
        entityType = default;
        if (!TryGetOrderedConnectLinks(out var links))
            return false;
        entityType = links.Head.Item1;
        return true;
    }

    public bool CanConnectSelectedNodesInOrder() =>
        TryGetOrderedConnectLinks(out _);

    public bool ConnectSelectedNodesInOrder(ArrowType arrowType)
    {
        if (_orderedNodeSelection.Count < 2)
            return false;

        if (!_host.TryFunc(
                () => Store.ConnectSelectionInOrder(_orderedNodeSelection.Select(s => s.Id), arrowType),
                out var created,
                fallback: 0,
                statusOverride: "[ERROR] Failed to connect selected nodes."))
            return false;

        if (created <= 0)
            return false;

        _host.SetStatusText($"Connected {created} arrow(s) from ordered selection.");
        return true;
    }

    private bool TryGetOrderedConnectLinks(out FSharpList<System.Tuple<EntityKind, Guid, Guid, Guid>> links)
    {
        links = FSharpList<System.Tuple<EntityKind, Guid, Guid, Guid>>.Empty;

        if (_orderedNodeSelection.Count < 2)
            return false;

        if (!_host.TryRef(
                () => ConnectionQueries.orderedArrowLinksForSelection(Store, _orderedNodeSelection.Select(s => s.Id), ArrowType.Start),
                out var resolvedLinks))
            return false;

        links = resolvedLinks;
        return !links.IsEmpty;
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

    public void RestoreSelection(List<SelectionKey> selectedKeys, List<Guid> selectedArrowIds)
    {
        _orderedNodeSelection.Clear();

        var existingNodeKeys = TreeNodeSearch.EnumerateNodes(EnumerateTreeRoots())
            .Select(ToKey)
            .ToHashSet();
        existingNodeKeys.UnionWith(_host.CanvasNodes.Select(ToKey));

        var restoredNodeSet = new HashSet<SelectionKey>();
        foreach (var key in selectedKeys)
        {
            if (existingNodeKeys.Contains(key) && restoredNodeSet.Add(key))
                _orderedNodeSelection.Add(key);
        }

        _selectionAnchor = _orderedNodeSelection.Count > 0 ? _orderedNodeSelection[^1] : null;

        ApplyNodeSelectionVisuals();
        _orderedArrowSelection.Clear();

        var canvasArrowIds = _host.CanvasArrows.Select(a => a.Id).ToHashSet();
        var restoredArrowSet = new HashSet<Guid>();
        foreach (var arrowId in selectedArrowIds)
        {
            if (canvasArrowIds.Contains(arrowId) && restoredArrowSet.Add(arrowId))
                _orderedArrowSelection.Add(arrowId);
        }

        ApplyArrowSelectionVisuals();
    }

    public void ClearArrowSelection()
    {
        _orderedArrowSelection.Clear();
        ApplyArrowSelectionVisuals();
    }

    public void ExpandNodeAndAncestors(Guid nodeId)
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

    public void ExpandAndSelectNode(Guid nodeId)
    {
        ExpandNodeAndAncestors(nodeId);

        var node = TreeNodeSearch.EnumerateNodes(EnumerateTreeRoots())
            .FirstOrDefault(n => n.Id == nodeId);
        if (node is null) return;

        SelectNodeFromTree(node, ctrlPressed: false, shiftPressed: false);
    }

    public HashSet<SelectionKey> GetExpandedKeys() =>
        EnumerateTreeNodes()
            .Where(n => n.IsExpanded)
            .Select(ToKey)
            .ToHashSet();

    public void ApplyExpansionStateTo(IEnumerable<EntityNode> roots, HashSet<SelectionKey> expandedNodes)
    {
        foreach (var node in roots)
        {
            node.IsExpanded = expandedNodes.Contains(ToKey(node));
            ApplyExpansionStateTo(node.Children, expandedNodes);
        }
    }

    public void ApplyNodeSelectionVisuals()
    {
        var selectionOrder = new Dictionary<SelectionKey, int>();
        for (var i = 0; i < _orderedNodeSelection.Count; i++)
            selectionOrder[_orderedNodeSelection[i]] = i + 1;

        ApplySelectionTo(_host.CanvasNodes, selectionOrder, static (n, s) => n.IsSelected = s);
        ApplySelectionTo(EnumerateTreeNodes(), selectionOrder, static (n, s) => n.IsTreeSelected = s);

        _host.SelectedNode = ResolvePrimarySelectedNode();
        _host.NotifyCommandStatesChanged();
    }

    private List<SelectionKey> CanvasSelectionOrderKeys() =>
        EditorSelectionQueries.OrderCanvasSelectionKeys(_host.CanvasNodes.Select(ToCanvasSelectionCandidate))
            .ToList();

    private static CanvasSelectionCandidate ToCanvasSelectionCandidate(EntityNode node) =>
        new(ToKey(node), node.X, node.Y, node.Width, node.Height, node.Name);

    private void UpdateNodeSelection(
        EntityNode? node,
        bool ctrlPressed,
        bool shiftPressed,
        IReadOnlyList<SelectionKey> orderedKeys)
    {
        var result = EditorSelectionQueries.ApplyNodeSelection(
            _orderedNodeSelection,
            _selectionAnchor,
            node is null ? null : ToKey(node),
            ctrlPressed,
            shiftPressed,
            orderedKeys);

        _orderedNodeSelection.Clear();
        foreach (var key in result.OrderedKeys)
            if (!_orderedNodeSelection.Contains(key))
                _orderedNodeSelection.Add(key);

        _selectionAnchor = result.AnchorOrNull;

        ApplyNodeSelectionVisuals();
        if (node is not null)
            ClearArrowSelection();
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
        var canvasNode = _host.CanvasNodes.FirstOrDefault(n => n.Id == key.Id && n.EntityType == key.EntityKind);
        if (canvasNode is not null)
            return canvasNode;

        return TreeNodeSearch.FindByKey(EnumerateTreeRoots(), key);
    }

    public void ApplyArrowSelectionVisuals()
    {
        _orderedArrowSelection.RemoveAll(id => _host.CanvasArrows.All(arrow => arrow.Id != id));
        var selected = _orderedArrowSelection.ToHashSet();
        foreach (var arrow in _host.CanvasArrows)
            arrow.IsSelected = selected.Contains(arrow.Id);

        Guid? primaryArrowId = _orderedArrowSelection.Count > 0 ? _orderedArrowSelection[^1] : null;
        _host.SelectedArrow = primaryArrowId is { } id
            ? _host.CanvasArrows.FirstOrDefault(a => a.Id == id)
            : null;
        _host.NotifyCommandStatesChanged();
    }

    private IEnumerable<EntityNode> EnumerateActiveTreeRoots() =>
        _activeTreePane == TreePaneKind.Device ? _host.DeviceTreeRoots : _host.ControlTreeRoots;

    private IEnumerable<EntityNode> EnumerateVisibleActiveTreeNodes() =>
        TreeNodeSearch.EnumerateVisibleNodes(EnumerateActiveTreeRoots());

    private IEnumerable<EntityNode> EnumerateTreeRoots() => _host.ControlTreeRoots.Concat(_host.DeviceTreeRoots);

    public IEnumerable<EntityNode> EnumerateTreeNodes() => TreeNodeSearch.EnumerateNodes(EnumerateTreeRoots());

    private static SelectionKey ToKey(EntityNode node) => new(node.Id, node.EntityType);
}
