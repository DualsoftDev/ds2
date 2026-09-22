using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class CanvasWorkspaceState
{
    public void ApplyConnectionsChanged()
    {
        if (ActiveTab is null)
        {
            // 탭이 없으면 그냥 비움
            CanvasNodes.Clear();
            CanvasArrows.Clear();
            _host.Selection.ApplyNodeSelectionVisuals();
            return;
        }

        if (!_host.TryRef(
                () => EditorCanvasProjection.CanvasContentForTab(Store, ActiveTab.Kind, ActiveTab.RootId),
                out var content,
                statusOverride: "[ERROR] Failed to refresh canvas content."))
            return;

        ReconcileCanvasArrows([.. content.Arrows]);
        RefreshArrowPaths();
    }

    /// <summary>화살표를 제자리 조정 — ID 기준으로 매칭하고, 불변 필드(SourceId/TargetId/ArrowType)가
    /// 다르면 그 자리만 새 객체로 교체한다.</summary>
    private void ReconcileCanvasArrows(IReadOnlyList<CanvasArrowInfo> infos)
    {
        var newIds = new HashSet<Guid>(infos.Count);
        foreach (var a in infos) newIds.Add(a.Id);

        for (var i = CanvasArrows.Count - 1; i >= 0; i--)
            if (!newIds.Contains(CanvasArrows[i].Id))
                CanvasArrows.RemoveAt(i);

        var existingById = new Dictionary<Guid, ArrowNode>(CanvasArrows.Count);
        foreach (var arrow in CanvasArrows) existingById[arrow.Id] = arrow;

        foreach (var a in infos)
        {
            if (existingById.TryGetValue(a.Id, out var existing))
            {
                if (existing.SourceId != a.SourceId
                    || existing.TargetId != a.TargetId
                    || existing.ArrowType != a.ArrowType)
                {
                    var idx = CanvasArrows.IndexOf(existing);
                    if (idx >= 0)
                        CanvasArrows[idx] = new ArrowNode(a.Id, a.SourceId, a.TargetId, a.ArrowType);
                }
            }
            else
            {
                CanvasArrows.Add(new ArrowNode(a.Id, a.SourceId, a.TargetId, a.ArrowType));
            }
        }
    }

    /// <summary>캔버스 노드를 제자리 조정 — 같은 엔티티는 <b>객체를 유지하고</b> 바뀐 필드만 쓴다.
    ///
    /// <para>종전엔 <c>CanvasNodes.Clear()</c> 후 전부 새로 만들었다. 캔버스는 ItemsPanel=Canvas 라
    /// 가상화가 없어 노드마다 89 엘리먼트짜리 DataTemplate 을 다시 인플레이트했고(내부 Viewbox 는
    /// 측정 비용도 크다), 큰 모델에서 "무슨 동작을 하면 멈췄다 동작"의 남은 몫이 이것이었다.
    /// 객체를 유지하면 WPF 가 컨테이너를 그대로 쓰므로 바뀐 행만 다시 그린다.</para>
    ///
    /// <para>Id·EntityType·ParentId·ReferenceOfId 는 EntityNode 에서 불변이라 재사용 조건에 넣는다 —
    /// 하나라도 다르면 같은 Id 라도 새 객체로 간다(Work 가 다른 Flow 로 옮겨간 경우 등).</para>
    ///
    /// <para>선택·경고·시뮬 상태는 호출측이 전체 노드에 다시 칠한다(ApplyNodeSelectionVisuals /
    /// RestoreSimStateToCanvas) — 재사용한 노드에 이전 값이 남지 않는다.</para></summary>
    private void ReconcileCanvasNodes(IReadOnlyList<CanvasNodeInfo> infos, HashSet<Guid>? highlightWorkIds)
    {
        var newIds = new HashSet<Guid>(infos.Count);
        foreach (var n in infos) newIds.Add(n.Id);

        // 1) 새 내용에 없는 노드 제거 (뒤에서부터 — 인덱스 흔들림 방지)
        for (var i = CanvasNodes.Count - 1; i >= 0; i--)
            if (!newIds.Contains(CanvasNodes[i].Id))
                CanvasNodes.RemoveAt(i);

        var existingById = new Dictionary<Guid, EntityNode>(CanvasNodes.Count);
        foreach (var node in CanvasNodes) existingById.TryAdd(node.Id, node);

        // 2) 새 내용 순서대로 자리를 맞춘다. 앞자리(0..i-1)는 이미 확정이라, i 번째로 와야 할
        //    노드는 컬렉션에 없거나 i 이후에 있다.
        var placed = new HashSet<Guid>(infos.Count);
        for (var i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            var referenceOfId = info.ReferenceOfId is { } refId ? (Guid?)refId.Value : null;

            EntityNode? node = null;
            if (placed.Add(info.Id)
                && existingById.TryGetValue(info.Id, out var candidate)
                && candidate.EntityType == info.EntityKind
                && candidate.ParentId == info.ParentId
                && candidate.ReferenceOfId == referenceOfId)
            {
                node = candidate;
                node.Name = info.Name;
            }

            node ??= new EntityNode(info.Id, info.EntityKind, info.Name, info.ParentId)
            {
                ReferenceOfId = referenceOfId,
            };

            node.X = info.X;
            node.Y = info.Y;
            node.Width = info.Width;
            node.Height = info.Height;
            // Flow 하이라이트 중이면 대상 Flow 밖의 Work 는 고스트로 흐린다.
            node.IsGhost = info.IsGhost
                           || (highlightWorkIds is not null && !highlightWorkIds.Contains(info.Id));
            node.IsReference = info.IsReference;
            node.UpdateConditionTypes(info.ConditionTypes);

            var at = IndexOfNodeFrom(node, i);
            if (at < 0) CanvasNodes.Insert(i, node);
            else if (at != i) CanvasNodes.Move(at, i);
        }

        // 3) 재사용되지 않고 꼬리에 밀린 잔여분 제거
        while (CanvasNodes.Count > infos.Count)
            CanvasNodes.RemoveAt(CanvasNodes.Count - 1);
    }

    private int IndexOfNodeFrom(EntityNode node, int start)
    {
        for (var i = start; i < CanvasNodes.Count; i++)
            if (ReferenceEquals(CanvasNodes[i], node))
                return i;
        return -1;
    }

    /// <summary>
    /// 노드 이동(EntitiesMoved) 이벤트 처리: 트리/visual tree 재구축 없이
    /// 이동된 노드의 X/Y를 store에서 동기화하고 인접 flow의 화살표 path만 재계산한다.
    /// 드래그/AutoLayout 등 위치만 바뀐 작업의 hitch 제거를 위한 경로.
    /// </summary>
    public void ApplyEntitiesMoved(IReadOnlyCollection<Guid> ids)
    {
        if (ActiveTab is null || ids.Count == 0 || CanvasNodes.Count == 0)
            return;

        var idSet = ids as HashSet<Guid> ?? new HashSet<Guid>(ids);
        foreach (var node in CanvasNodes)
        {
            if (!idSet.Contains(node.Id)) continue;

            var pos = TryGetEntityPosition(node.Id);
            if (pos is null) continue;

            node.X = pos.X;
            node.Y = pos.Y;
        }

        RefreshArrowPaths();
        RecalculateCanvasSizeRequested?.Invoke();
    }

    private Xywh? TryGetEntityPosition(Guid id)
    {
        var work = Queries.getWork(id, Store);
        if (work is not null)
        {
            var posOpt = work.Value.Position;
            if (posOpt is not null) return posOpt.Value;
            return null;
        }

        var call = Queries.getCall(id, Store);
        if (call is not null)
        {
            var posOpt = call.Value.Position;
            if (posOpt is not null) return posOpt.Value;
        }
        return null;
    }

    private void RefreshArrowPaths()
    {
        if (ActiveTab is null || CanvasArrows.Count == 0)
            return;

        if (!_host.TryRef(
                () => EditorNavigation.FlowIdsForTab(Store, ActiveTab.Kind, ActiveTab.RootId),
                out var flowIds,
                statusOverride: "[ERROR] Failed to resolve flow ids for canvas."))
            return;

        foreach (var flowId in flowIds)
            ApplyArrowPathsFromFlow(flowId);

        SyncBidirectionalPairs();
    }

    private void ApplyArrowPathsFromFlow(Guid flowId)
    {
        if (!_host.TryRef(() => ArrowPathCalculator.ComputeFlowArrowPaths(Store, flowId), out var paths))
            return;

        foreach (var arrow in CanvasArrows)
            if (paths.TryGetValue(arrow.Id, out var visual))
                arrow.UpdateFromVisual(visual);
    }

    /// <summary>
    /// 두 노드 사이의 ResetReset 양방향 화살표 쌍을 감지해 시각적으로 통합한다.
    /// 데이터 모델은 그대로 2개 화살표지만, 같은 라인 위에서 각자 절반만 그리고 head는 양 끝.
    /// 한쪽 클릭 시 그 절반에 해당하는 화살표만 선택되도록 hit area는 자동으로 절반만 덮인다.
    /// </summary>
    public void SyncBidirectionalPairs()
    {
        // 1) 모든 화살표 partner 정보 초기화 (페어가 깨졌을 수 있음)
        foreach (var a in CanvasArrows)
        {
            a.BidirectionalPartnerId = null;
            a.RenderCenterMarker = false;
        }

        // 2) 같은 타입의 화살표를 (unordered 노드쌍, ArrowType) 키로 그룹화
        //    StartReset/Reset/Start 만 대상.
        //    ResetReset 은 단일 화살표 자체가 양방향 시맨틱이라 dedup이 데이터 모델 레벨에서 이루어져야 함 (시각 통합 X).
        var groups = new Dictionary<(Guid, Guid, ArrowType), List<ArrowNode>>();
        foreach (var a in CanvasArrows)
        {
            if (a.ArrowType is ArrowType.Unspecified or ArrowType.Group or ArrowType.ResetReset)
                continue;
            var (s, t) = a.SourceId.CompareTo(a.TargetId) < 0
                ? (a.SourceId, a.TargetId)
                : (a.TargetId, a.SourceId);
            var key = (s, t, a.ArrowType);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<ArrowNode>(2);
                groups[key] = list;
            }
            list.Add(a);
        }

        // 3) 정확히 2개로 짝이 맞는 쌍만 양방향 처리
        foreach (var pair in groups.Values)
        {
            if (pair.Count != 2) continue;
            var a = pair[0];
            var b = pair[1];
            // 실제로 방향이 반대인지 확인 (같은 방향 중복 ResetReset이면 패스)
            if (!(a.SourceId == b.TargetId && a.TargetId == b.SourceId))
                continue;

            // 두 화살표의 경로가 다를 수 있으므로 a의 경로를 기준으로 b는 reverse 사용 → 동일 라인 위
            var src = a.LastPoints;
            if (src is null || src.Count == 0) continue;

            var pointsA = new List<Point>(src.Count);
            for (var i = 0; i < src.Count; i++) pointsA.Add(src[i]);

            var pointsB = new List<Point>(src.Count);
            for (var i = src.Count - 1; i >= 0; i--) pointsB.Add(src[i]);

            // partner 설정 후 SetPathPoints 호출 (BidirectionalPartnerId가 set되어 있어야 half-render)
            a.BidirectionalPartnerId = b.Id;
            b.BidirectionalPartnerId = a.Id;
            a.SetPathPoints(pointsA);
            b.SetPathPoints(pointsB);

            // 중앙 마커는 한쪽만 그림 (Id 작은 쪽)
            var leader = a.Id.CompareTo(b.Id) <= 0 ? a : b;
            leader.RenderCenterMarker = true;
        }
    }
}
