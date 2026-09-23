using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    /// <param name="includeCanvas">false 면 트리만 다시 맞추고 캔버스 pane 은 그대로 둔다.
    /// 속성 변경처럼 캔버스 노드 집합이 그대로인 갱신까지 캔버스를 다시 만들 이유가 없다 —
    /// 노드 배지 같은 건 HandleEvent 가 이미 직접 patch 한다.</param>
    private void RebuildAll(bool includeCanvas)
    {
        var prevSelection = Selection.OrderedNodeSelection.ToList();
        var prevSelectedArrowIds = Selection.OrderedArrowSelection.ToList();
        var expandedNodes = Selection.GetExpandedKeys();

        if (!TryEditorRef(
                () => EditorTreeProjection.BuildTrees(_store),
                out var trees,
                statusOverride: "[ERROR] Failed to rebuild tree views."))
        {
            ControlTreeRoots.Clear();
            DeviceTreeRoots.Clear();
            DisabledFlows.Clear();
            return;
        }

        ReconcileTreeLevel(ControlTreeRoots, trees.Item1);
        ReconcileTreeLevel(DeviceTreeRoots, trees.Item2);
        ReconcileTreeLevel(DisabledFlows, EditorTreeProjection.DisabledFlows(_store));

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

    /// <summary>트리 한 단계를 제자리 조정 — 같은 엔티티는 <b>객체를 유지하고</b> 바뀐 것만 쓴다.
    ///
    /// <para>종전엔 <c>ControlTreeRoots.Clear()</c> 후 전 노드를 새로 만들었다. 실측하면 이 한 줄이
    /// 탐색기 갱신 비용의 거의 전부였다 — 트리행 1,043개 모델에서 같은 객체를 도로 넣어도 176ms,
    /// 전부 접으면 29ms, 전부 펼치면 158ms. 내용과 무관하고 펼쳐진 깊이에만 비례한다.
    /// <c>Clear()</c> 가 쏘는 Reset 에 WPF 가 펼쳐진 계층 전체의 패널·제너레이터를 재구성하기
    /// 때문이다. 가상화로는 못 줄인다(같은 모델에서 실체화된 TreeViewItem 은 34개뿐인데도 비용은
    /// 그대로였다). 반면 컬렉션을 건드리지 않으면 전 노드 갱신도 0ms 다.</para>
    ///
    /// <para>재사용 조건에 ParentId 를 넣는 이유: <c>EntityNode</c> 에서 불변이라 고칠 수 없다.
    /// 부모가 바뀐 노드는 같은 Id 라도 새로 만들어야 한다.</para>
    ///
    /// <para>펼침/선택 상태는 호출측이 뒤이어 다시 칠한다(ApplyExpansionStateTo / RestoreSelection)
    /// — 재사용한 노드에 이전 값이 남지 않는다.</para></summary>
    private static void ReconcileTreeLevel(
        ObservableCollection<EntityNode> target, IEnumerable<TreeNodeInfo> infos)
    {
        var list = infos as IList<TreeNodeInfo> ?? infos.ToList();

        // 1) 새 내용에 없는 노드 제거 (뒤에서부터 — 인덱스 흔들림 방지)
        var newKeys = new HashSet<(Guid, EntityKind)>(list.Count);
        foreach (var info in list) newKeys.Add((info.Id, info.EntityKind));
        for (var i = target.Count - 1; i >= 0; i--)
            if (!newKeys.Contains((target[i].Id, target[i].EntityType)))
                target.RemoveAt(i);

        var existing = new Dictionary<(Guid, EntityKind), EntityNode>(target.Count);
        foreach (var node in target) existing.TryAdd((node.Id, node.EntityType), node);

        // 2) 새 내용 순서대로 자리를 맞춘다. 앞자리(0..i-1)는 확정이라 i 번째로 와야 할 노드는
        //    컬렉션에 없거나 i 이후에 있다.
        var placed = new HashSet<(Guid, EntityKind)>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var info = list[i];
            var key = (info.Id, info.EntityKind);
            var parentId = info.ParentIdOrNull;

            EntityNode? node = null;
            if (placed.Add(key)
                && existing.TryGetValue(key, out var candidate)
                && candidate.ParentId == parentId)
            {
                node = candidate;
                node.Name = info.Name;
            }

            node ??= new EntityNode(info.Id, info.EntityKind, info.Name, parentId);

            ReconcileTreeLevel(node.Children, info.Children);

            var at = IndexOfNodeFrom(target, node, i);
            if (at < 0) target.Insert(i, node);
            else if (at != i) target.Move(at, i);
        }

        // 3) 재사용되지 않고 꼬리에 밀린 잔여분 제거
        while (target.Count > list.Count)
            target.RemoveAt(target.Count - 1);
    }

    private static int IndexOfNodeFrom(ObservableCollection<EntityNode> target, EntityNode node, int start)
    {
        for (var i = start; i < target.Count; i++)
            if (ReferenceEquals(target[i], node))
                return i;
        return -1;
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
