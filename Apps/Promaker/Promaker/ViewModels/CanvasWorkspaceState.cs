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

public partial class CanvasWorkspaceState : ObservableObject
{
    private readonly MainViewModel.CanvasHost _host;
    private bool _suppressFitToView;
    private CanvasTab? _previousTab;

    public CanvasWorkspaceState(MainViewModel.CanvasHost host)
    {
        _host = host;
    }

    private DsStore Store => _host.Store;

    public ObservableCollection<EntityNode> CanvasNodes { get; } = [];
    public ObservableCollection<CanvasTab> OpenTabs { get; } = [];
    public ObservableCollection<ArrowNode> CanvasArrows { get; } = [];

    [ObservableProperty]
    private CanvasTab? _activeTab;

    /// <summary>모든 탭이 닫혔을 때 발생합니다.</summary>
    public event Action<CanvasWorkspaceState>? AllTabsClosed;

    public Action<Guid>? CenterOnNodeRequested { get; set; }
    public Action? FitToViewZoomOutRequested { get; set; }
    public Action<double>? ApplyZoomCenteredRequested { get; set; }
    public Func<Point?>? GetViewportCenterRequested { get; set; }
    public Func<(double Zoom, double PanX, double PanY)>? GetCurrentViewRequested { get; set; }
    public Action<double, double, double>? RestoreViewRequested { get; set; }
    public Action? RecalculateCanvasSizeRequested { get; set; }

    partial void OnActiveTabChanged(CanvasTab? value)
    {
        // 이전 탭의 줌/팬 상태를 캐시 (탭은 살아있으므로 다시 활성화될 때 복원)
        if (_previousTab is not null
            && OpenTabs.Contains(_previousTab)
            && GetCurrentViewRequested?.Invoke() is { } view)
        {
            _previousTab.SavedZoom = view.Zoom;
            _previousTab.SavedPanX = view.PanX;
            _previousTab.SavedPanY = view.PanY;
            _previousTab.HasSavedView = true;
        }
        _previousTab = value;

        foreach (var t in OpenTabs)
            t.IsActive = t == value;

        _host.Selection.ClearNodeSelection();
        _host.Selection.ClearArrowSelection();
        RefreshCanvasForActiveTab();
        if (_suppressFitToView)
            _suppressFitToView = false;
        else if (value is { HasSavedView: true } && RestoreViewRequested is not null)
            RestoreViewRequested.Invoke(value.SavedZoom, value.SavedPanX, value.SavedPanY);
        else
            FitToViewZoomOutRequested?.Invoke();
        _host.NotifyCommandStatesChanged();
    }

    public void Reset()
    {
        _previousTab = null;
        OpenTabs.Clear();
        CanvasNodes.Clear();
        CanvasArrows.Clear();
        HighlightedFlowId = null;
        ActiveTab = null;
    }

    /// <summary>Flow 더블클릭 시 특정 Flow를 하이라이트하기 위한 필터 ID.</summary>
    public Guid? HighlightedFlowId { get; private set; }

    public void OpenCanvasTab(Guid entityId, EntityKind entityType, bool expandTree = false)
    {
        // Flow 더블클릭 → 부모 System 탭에서 해당 Flow의 Work만 하이라이트 (토글)
        if (entityType == EntityKind.Flow)
        {
            var flow = Queries.getFlow(entityId, Store);
            if (flow == null) return;

            // 같은 Flow 다시 더블클릭 → 하이라이트 해제 (토글)
            var toggle = HighlightedFlowId == entityId;

            // System 탭을 먼저 열고 (HighlightedFlowId=null 초기화 포함)
            OpenCanvasTab(flow.Value.ParentId, EntityKind.System, expandTree);

            if (!toggle)
            {
                HighlightedFlowId = entityId;
                RefreshCanvasForActiveTab();
            }
            return;
        }

        if (!_host.TryRef(
                () => EditorNavigation.TryOpenTabForEntityOrNull(Store, entityType, entityId),
                out var info))
            return;

        // System 탭을 열 때 Flow 하이라이트 초기화
        if (entityType == EntityKind.System)
            HighlightedFlowId = null;

        OpenTab(info.Kind, info.RootId, info.Title);
        if (expandTree)
            _host.ExpandNodeAndAncestors(entityId);
    }

    public void OpenParentCanvasAndFocusNode(Guid entityId, EntityKind entityType, double? zoomOverride = null)
    {
        if (!_host.TryRef(
                () => EditorNavigation.TryOpenParentTabOrNull(Store, entityType, entityId),
                out var info))
            return;

        if (zoomOverride.HasValue)
            _suppressFitToView = true;
        OpenTab(info.Kind, info.RootId, info.Title);

        // 줌/센터는 캔버스 로드 직후 즉시 적용 (2단계 전환 방지)
        if (zoomOverride.HasValue)
            ApplyZoomCenteredRequested?.Invoke(zoomOverride.Value);
        CenterOnNodeRequested?.Invoke(entityId);

        _host.RequestRebuildAll(() =>
        {
            _host.ExpandNodeAndAncestors(entityId);
            var node = CanvasNodes.FirstOrDefault(n => n.Id == entityId);
            if (node is not null)
                _host.SelectNodeFromCanvas(node, ctrlPressed: false, shiftPressed: false);
        });
    }

    private bool CanFocusSelectedInCanvas() =>
        _host.SelectedNode is { EntityType: EntityKind.Work or EntityKind.Call };

    [RelayCommand(CanExecute = nameof(CanFocusSelectedInCanvas))]
    private void FocusSelectedInCanvas()
    {
        if (_host.SelectedNode is not { EntityType: EntityKind.Work or EntityKind.Call } node)
        {
            _host.SetStatusText("Select a Work or Call to focus in canvas.");
            return;
        }

        OpenParentCanvasAndFocusNode(node.Id, node.EntityType);
    }

    [RelayCommand]
    private void CloseTab(CanvasTab? tab)
    {
        if (tab is not null)
            RemoveTab(tab);
    }

    [RelayCommand]
    private void CloseActiveTab()
    {
        if (ActiveTab is not null)
            RemoveTab(ActiveTab);
    }

    /// <summary>외부에서 탭을 추가합니다 (분할 이동 시 사용).</summary>
    public void AddTab(CanvasTab tab)
    {
        OpenTabs.Add(tab);
        ActiveTab = tab;
    }

    /// <summary>외부에서 탭을 제거합니다 (분할 이동 시 사용).</summary>
    public void RemoveTab(CanvasTab tab)
    {
        var idx = OpenTabs.IndexOf(tab);
        if (idx < 0) return;

        OpenTabs.Remove(tab);
        if (ActiveTab == tab)
            ActiveTab = OpenTabs.Count > 0 ? OpenTabs[Math.Min(idx, OpenTabs.Count - 1)] : null;

        if (OpenTabs.Count == 0)
            AllTabsClosed?.Invoke(this);
    }

    /// <summary>탭 타이틀 검증 및 캔버스 갱신 (RebuildAll에서 호출).</summary>
    public void ValidateAndRefresh()
    {
        // 편집 동작(붙여넣기, 삭제, 추가 등) 후 Flow 하이라이트 해제
        HighlightedFlowId = null;
        var deadTabs = new List<CanvasTab>();
        foreach (var t in OpenTabs)
        {
            var title = ResolveTabTitle(t);
            if (title is null)
                deadTabs.Add(t);
            else
                t.Title = title;
        }

        foreach (var t in deadTabs)
            OpenTabs.Remove(t);

        if (ActiveTab is not null && !OpenTabs.Contains(ActiveTab))
            ActiveTab = OpenTabs.Count > 0 ? OpenTabs[0] : null;

        RefreshCanvasForActiveTab();
    }

    public void RefreshCanvasForActiveTab()
    {
        CanvasNodes.Clear();
        CanvasArrows.Clear();

        if (ActiveTab is null)
        {
            _host.Selection.ApplyNodeSelectionVisuals();
            return;
        }

        if (!_host.TryRef(
                () => EditorCanvasProjection.CanvasContentForTab(Store, ActiveTab.Kind, ActiveTab.RootId),
                out var content,
                statusOverride: "[ERROR] Failed to refresh canvas content."))
            return;

        // Flow 하이라이트: System 탭에서 특정 Flow의 Work만 활성화
        HashSet<Guid>? highlightWorkIds = null;
        if (HighlightedFlowId.HasValue && ActiveTab.Kind == TabKind.System)
        {
            var works = Queries.worksOf(HighlightedFlowId.Value, Store);
            highlightWorkIds = works.Select(w => w.Id).ToHashSet();
        }

        foreach (var n in content.Nodes)
        {
            var isGhost = n.IsGhost;
            if (highlightWorkIds is not null && !highlightWorkIds.Contains(n.Id))
                isGhost = true;

            var node = new EntityNode(n.Id, n.EntityKind, n.Name, n.ParentId)
            {
                X = n.X,
                Y = n.Y,
                Width = n.Width,
                Height = n.Height,
                IsGhost = isGhost,
                IsReference = n.IsReference,
                ReferenceOfId = n.ReferenceOfId is { } refId ? refId.Value : null,
            };
            node.UpdateConditionTypes(n.ConditionTypes);
            CanvasNodes.Add(node);
        }

        foreach (var a in content.Arrows)
            CanvasArrows.Add(new ArrowNode(a.Id, a.SourceId, a.TargetId, a.ArrowType));

        RefreshArrowPaths();
        _host.Selection.ApplyNodeSelectionVisuals();
        _host.RestoreSimStateToCanvas();
        RecalculateCanvasSizeRequested?.Invoke();
    }

    public string? ResolveTabTitle(CanvasTab tab)
    {
        if (!_host.TryFunc(
                () => EditorNavigation.TabTitleOrNull(Store, tab.Kind, tab.RootId),
                out string? title,
                fallback: null))
            return null;

        return title;
    }

    private void OpenTab(TabKind kind, Guid rootId, string title)
    {
        var existing = OpenTabs.FirstOrDefault(t => t.Kind == kind && t.RootId == rootId);
        if (existing is not null)
        {
            if (ActiveTab == existing)
                RefreshCanvasForActiveTab(); // Flow 하이라이트 변경 시 강제 리프레시
            else
                ActiveTab = existing;
            return;
        }

        // 새 탭을 처음 열 때만 자동 배치 (Mermaid 임포트 등)
        // RefreshCanvasForActiveTab에서 하면 Undo 시 매번 트리거되어 Undo/Redo 스택을 망침
        _host.TryAction(() => Store.AutoLayoutIfNeeded(kind, rootId));

        var tab = new CanvasTab(rootId, kind, title);
        OpenTabs.Add(tab);
        ActiveTab = tab;
    }

    /// <summary>
    /// 화살표 추가/삭제/타입변경/방향전환 이벤트의 경량 처리.
    /// 노드 ContentPresenter/visual은 보존하고 CanvasArrows에 diff(add/remove/replace)만 적용한 뒤 path 재계산.
    /// </summary>
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
