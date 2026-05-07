using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private void RebuildAll()
    {
        var prevSelection = Selection.OrderedNodeSelection.ToList();
        var prevSelectedArrowIds = Selection.OrderedArrowSelection.ToList();
        var expandedNodes = Selection.GetExpandedKeys();

        ControlTreeRoots.Clear();
        DeviceTreeRoots.Clear();

        if (!TryEditorRef(
                () => EditorTreeProjection.BuildTrees(_store),
                out var trees,
                statusOverride: "[ERROR] Failed to rebuild tree views."))
        {
            return;
        }

        foreach (var info in trees.Item1)
            ControlTreeRoots.Add(MapToEntityNode(info));
        foreach (var info in trees.Item2)
            DeviceTreeRoots.Add(MapToEntityNode(info));

        Selection.ApplyExpansionStateTo(ControlTreeRoots, expandedNodes);
        Selection.ApplyExpansionStateTo(DeviceTreeRoots, expandedNodes);

        CanvasManager.RebuildAllPanes();
        Simulation.RestoreSimStateToCanvas();
        Selection.RestoreSelection(prevSelection, prevSelectedArrowIds);
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
            try
            {
                RebuildAll();

                if (_pendingRebuildActions.Count == 0)
                    return;

                var actions = _pendingRebuildActions.ToArray();
                _pendingRebuildActions.Clear();
                foreach (var action in actions)
                    action();
            }
            finally
            {
                _rebuildQueued = false;
            }
        }), DispatcherPriority.Background);
    }

    private static EntityNode MapToEntityNode(TreeNodeInfo info)
    {
        var parentId = info.ParentIdOrNull;
        var node = new EntityNode(info.Id, info.EntityKind, info.Name, parentId);
        foreach (var child in info.Children)
            node.Children.Add(MapToEntityNode(child));
        return node;
    }

    private static IEnumerable<EntityNode> FlattenTree(IEnumerable<EntityNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in FlattenTree(node.Children))
                yield return child;
        }
    }
}
