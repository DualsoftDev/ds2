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
        // Flow 더블클릭 → 부모 System 탭에서 해당 Flow의 Work만 하이라이트 (토글).
        // System 탭 fallback 결정은 F# EditorNavigation 위임.
        if (entityType == EntityKind.Flow)
        {
            var systemInfo = EditorNavigation.TryOpenSystemTabForFlowOrNull(Store, entityId);
            if (systemInfo is null) return;

            // 같은 Flow 다시 더블클릭 → 하이라이트 해제 (토글)
            var toggle = HighlightedFlowId == entityId;

            // System 탭을 먼저 열고 (HighlightedFlowId=null 초기화 포함)
            OpenCanvasTab(systemInfo.RootId, EntityKind.System, expandTree);

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

        // System 탭을 열 때 Flow 하이라이트 초기화. 이전에 highlight 가 있었다면 같은 활성 탭이어도
        // 다시 그려야 함 — 그 경우만 forceRefresh.
        bool refreshForHighlight = false;
        if (entityType == EntityKind.System)
        {
            refreshForHighlight = HighlightedFlowId is not null;
            HighlightedFlowId = null;
        }

        OpenTab(info.Kind, info.RootId, info.Title, forceRefresh: refreshForHighlight);
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
        // OpenTab → ActiveTab 변경 시 OnActiveTabChanged 가 RefreshCanvasForActiveTab + RestoreSimStateToCanvas 까지 동기 처리.
        // 같은 활성 탭이면 forceRefresh=false 라 no-op. 즉 OpenTab 반환 시점에 CanvasNodes 는 항상 최신.
        OpenTab(info.Kind, info.RootId, info.Title);

        // 줌/센터는 캔버스 로드 직후 즉시 적용 (2단계 전환 방지)
        if (zoomOverride.HasValue)
            ApplyZoomCenteredRequested?.Invoke(zoomOverride.Value);
        CenterOnNodeRequested?.Invoke(entityId);

        // 트리/다른 panes 재구축 불필요 — 사용자 UI 트리거(트리 클릭/Property navigation/Focus 커맨드)에서만 호출됨.
        // 종전 RequestRebuildAll 은 BuildTrees + RebuildAllPanes 까지 돌려 큰 모델에서 2~3s hitch 의 주범이었음.
        _host.ExpandNodeAndAncestors(entityId);
        var targetNode = CanvasNodes.FirstOrDefault(n => n.Id == entityId);
        if (targetNode is not null)
            _host.SelectNodeFromCanvas(targetNode, ctrlPressed: false, shiftPressed: false);
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

    private void OpenTab(TabKind kind, Guid rootId, string title, bool forceRefresh = false)
    {
        var existing = OpenTabs.FirstOrDefault(t => t.Kind == kind && t.RootId == rootId);
        if (existing is not null)
        {
            if (ActiveTab == existing)
            {
                // 같은 활성 탭 재호출은 기본적으로 no-op. Flow 하이라이트 변경 같은 일부 케이스에만 강제 리프레시.
                // (이전에는 무조건 RefreshCanvasForActiveTab 호출 → 트리에서 Call 클릭 시 캔버스 통째 재생성으로 hitch.)
                if (forceRefresh)
                    RefreshCanvasForActiveTab();
            }
            else
                ActiveTab = existing;
            return;
        }

        // 새 탭을 처음 열 때만 자동 배치 (Mermaid 임포트, JSON 에 Call 좌표 없는 경우 등).
        // AutoLayoutIfNeeded 가 좌표를 채우면 가벼운 EntitiesMoved 이벤트만 발행 — RebuildAll 미트리거.
        _host.TryAction(() => Store.AutoLayoutIfNeeded(kind, rootId));

        var tab = new CanvasTab(rootId, kind, title);
        OpenTabs.Add(tab);
        ActiveTab = tab;
    }

    /// <summary>
    /// 화살표 추가/삭제/타입변경/방향전환 이벤트의 경량 처리.
    /// 노드 ContentPresenter/visual은 보존하고 CanvasArrows에 diff(add/remove/replace)만 적용한 뒤 path 재계산.
    /// </summary>
}
