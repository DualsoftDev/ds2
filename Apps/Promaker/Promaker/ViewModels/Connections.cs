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

        var newById = content.Arrows.ToDictionary(a => a.Id);

        // 1) 제거: 새 set에 없는 기존 화살표
        for (int i = CanvasArrows.Count - 1; i >= 0; i--)
        {
            if (!newById.ContainsKey(CanvasArrows[i].Id))
                CanvasArrows.RemoveAt(i);
        }

        // 2) 교체/추가: ID 기준으로 매칭, 불변 필드(SourceId/TargetId/ArrowType)가 다르면 새 객체로 교체
        var existingById = new Dictionary<Guid, ArrowNode>(CanvasArrows.Count);
        foreach (var arrow in CanvasArrows) existingById[arrow.Id] = arrow;

        foreach (var a in content.Arrows)
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

        RefreshArrowPaths();
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
