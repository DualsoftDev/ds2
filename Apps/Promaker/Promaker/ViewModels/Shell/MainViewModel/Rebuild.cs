using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    /// <param name="includeCanvas">false 면 트리만 다시 굽고 캔버스 pane 은 그대로 둔다.
    /// 캔버스 재구축은 노드마다 89 엘리먼트짜리 DataTemplate 을 새로 인플레이트하는데
    /// (ItemsPanel=Canvas 라 가상화가 없다) 속성 변경처럼 캔버스 내용이 그대로인 갱신까지
    /// 그 값을 치르고 있었다. 노드 배지 같은 건 HandleEvent 가 이미 직접 patch 한다.</param>
    private void RebuildAll(bool includeCanvas)
    {
        var prevSelection = Selection.OrderedNodeSelection.ToList();
        var prevSelectedArrowIds = Selection.OrderedArrowSelection.ToList();
        var expandedNodes = Selection.GetExpandedKeys();

        ControlTreeRoots.Clear();
        DeviceTreeRoots.Clear();
        DisabledFlows.Clear();

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
        foreach (var info in EditorTreeProjection.DisabledFlows(_store))
            DisabledFlows.Add(MapToEntityNode(info));

        Selection.ApplyExpansionStateTo(ControlTreeRoots, expandedNodes);
        Selection.ApplyExpansionStateTo(DeviceTreeRoots, expandedNodes);

        if (includeCanvas)
        {
            CanvasManager.RebuildAllPanes();
            Simulation.RestoreSimStateToCanvas();
        }

        Selection.RestoreSelection(prevSelection, prevSelectedArrowIds);
    }

    /// <summary>트리 + 캔버스 전면 재구축 예약.</summary>
    private void RequestRebuildAll(Action? afterRebuild = null) =>
        RequestRebuild(includeCanvas: true, afterRebuild);

    /// <summary>트리만 재구축 예약 — 캔버스 내용이 그대로인 갱신용(속성 변경 등).
    /// 같은 tick 에 캔버스가 필요한 요청이 하나라도 섞이면 합쳐서 전면 재구축으로 승격된다.</summary>
    private void RequestRebuildTrees(Action? afterRebuild = null) =>
        RequestRebuild(includeCanvas: false, afterRebuild);

    private void RequestRebuild(bool includeCanvas, Action? afterRebuild)
    {
        if (afterRebuild is not null)
            _pendingRebuildActions.Add(afterRebuild);

        // 합쳐지는 요청 중 하나라도 캔버스를 원하면 전면 재구축 (안전한 쪽으로 승격).
        _rebuildNeedsCanvas |= includeCanvas;

        if (_rebuildQueued)
            return;

        _rebuildQueued = true;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var withCanvas = _rebuildNeedsCanvas;
                _rebuildNeedsCanvas = false;
                RebuildAll(withCanvas);

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
