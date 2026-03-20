using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Core;

namespace Promaker.ViewModels;

public partial class CanvasWorkspaceState : ObservableObject
{
    private readonly MainViewModel.CanvasHost _host;
    private bool _suppressFitToView;

    public CanvasWorkspaceState(MainViewModel.CanvasHost host)
    {
        _host = host;
    }

    private DsStore Store => _host.Store;

    public ObservableCollection<EntityNode> CanvasNodes { get; } = [];
    public ObservableCollection<CanvasTab> OpenTabs { get; } = [];
    public ObservableCollection<ArrowNode> CanvasArrows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextualQuickCreateLabel))]
    [NotifyCanExecuteChangedFor(nameof(QuickAddFlowCommand))]
    [NotifyCanExecuteChangedFor(nameof(QuickAddContextualNodeCommand))]
    private CanvasTab? _activeTab;

    public string ContextualQuickCreateLabel =>
        ActiveTab?.Kind switch
        {
            TabKind.Flow => "Work",
            TabKind.Work => "Call",
            _ => "Work / Call"
        };

    /// <summary>모든 탭이 닫혔을 때 발생합니다.</summary>
    public event Action<CanvasWorkspaceState>? AllTabsClosed;

    public Action<Guid>? CenterOnNodeRequested { get; set; }
    public Action? FitToViewZoomOutRequested { get; set; }
    public Action<double>? ApplyZoomCenteredRequested { get; set; }
    public Func<Point?>? GetViewportCenterRequested { get; set; }

    partial void OnActiveTabChanged(CanvasTab? value)
    {
        foreach (var t in OpenTabs)
            t.IsActive = t == value;

        OnPropertyChanged(nameof(ContextualQuickCreateLabel));
        _host.Selection.ClearNodeSelection();
        _host.Selection.ClearArrowSelection();
        RefreshCanvasForActiveTab();
        if (_suppressFitToView)
            _suppressFitToView = false;
        else
            FitToViewZoomOutRequested?.Invoke();
    }

    public void NotifyQuickCreateStateChanged()
    {
        OnPropertyChanged(nameof(ContextualQuickCreateLabel));
        QuickAddFlowCommand.NotifyCanExecuteChanged();
        QuickAddContextualNodeCommand.NotifyCanExecuteChanged();
    }

    public void Reset()
    {
        OpenTabs.Clear();
        CanvasNodes.Clear();
        CanvasArrows.Clear();
        ActiveTab = null;
    }

    public void OpenCanvasTab(Guid entityId, EntityKind entityType, bool expandTree = false)
    {
        if (!_host.TryRef(
                () => Store.TryOpenTabForEntityOrNull(entityType, entityId),
                out var info))
            return;

        OpenTab(info.Kind, info.RootId, info.Title);
        if (expandTree)
            _host.ExpandNodeAndAncestors(entityId);
    }

    public void OpenParentCanvasAndFocusNode(Guid entityId, EntityKind entityType, double? zoomOverride = null)
    {
        if (!_host.TryRef(
                () => Store.TryOpenParentTabOrNull(entityType, entityId),
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

    [RelayCommand]
    private void FocusSelectedInCanvas()
    {
        if (_host.SelectedNode is not { EntityType: EntityKind.Work or EntityKind.Call } node)
            return;

        OpenParentCanvasAndFocusNode(node.Id, node.EntityType);
    }

    [RelayCommand]
    private void CloseTab(CanvasTab? tab)
    {
        if (tab is null)
            return;

        var idx = OpenTabs.IndexOf(tab);
        OpenTabs.Remove(tab);
        if (ActiveTab == tab)
            ActiveTab = OpenTabs.Count > 0 ? OpenTabs[Math.Min(idx, OpenTabs.Count - 1)] : null;

        if (OpenTabs.Count == 0)
            AllTabsClosed?.Invoke(this);
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

    private bool CanQuickAddFlow() => _host.HasProject;

    [RelayCommand(CanExecute = nameof(CanQuickAddFlow))]
    private void QuickAddFlow() => _host.ExecuteAddFlow();

    private bool CanQuickAddContextualNode() =>
        _host.HasProject && ActiveTab?.Kind is TabKind.Flow or TabKind.Work;

    [RelayCommand(CanExecute = nameof(CanQuickAddContextualNode))]
    private void QuickAddContextualNode()
    {
        switch (ActiveTab?.Kind)
        {
            case TabKind.Flow:
                _host.ExecuteAddWork();
                break;
            case TabKind.Work:
                _host.ExecuteAddCall();
                break;
        }
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
                () => Store.CanvasContentForTab(ActiveTab.Kind, ActiveTab.RootId),
                out var content,
                statusOverride: "[ERROR] Failed to refresh canvas content."))
            return;

        foreach (var n in content.Nodes)
        {
            var node = new EntityNode(n.Id, n.EntityKind, n.Name, n.ParentId)
            {
                X = n.X,
                Y = n.Y,
                Width = n.Width,
                Height = n.Height,
                IsGhost = n.IsGhost
            };
            node.UpdateConditionTypes(n.ConditionTypes);
            CanvasNodes.Add(node);
        }

        foreach (var a in content.Arrows)
            CanvasArrows.Add(new ArrowNode(a.Id, a.SourceId, a.TargetId, a.ArrowType));

        RefreshArrowPaths();
        _host.Selection.ApplyNodeSelectionVisuals();
    }

    public string? ResolveTabTitle(CanvasTab tab)
    {
        if (!_host.TryFunc(
                () => Store.TabTitleOrNull(tab.Kind, tab.RootId),
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

    private void RefreshArrowPaths()
    {
        if (ActiveTab is null || CanvasArrows.Count == 0)
            return;

        if (!_host.TryRef(
                () => Store.FlowIdsForTab(ActiveTab.Kind, ActiveTab.RootId),
                out var flowIds,
                statusOverride: "[ERROR] Failed to resolve flow ids for canvas."))
            return;

        foreach (var flowId in flowIds)
            ApplyArrowPathsFromFlow(flowId);
    }

    private void ApplyArrowPathsFromFlow(Guid flowId)
    {
        if (!_host.TryRef(() => Store.GetFlowArrowPaths(flowId), out var paths))
            return;

        foreach (var arrow in CanvasArrows)
            if (paths.TryGetValue(arrow.Id, out var visual))
                arrow.UpdateFromVisual(visual);
    }
}
