using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;

namespace Promaker;

public partial class MainWindow
{
    private void OnDockLayoutUpdated(object? sender, EventArgs e)
    {
        QueueDockPaneExtentUpdate();
    }

    private void OnDockManagerContentFloating(object? sender, ContentFloatingEventArgs e)
    {
        TraceDock($"ContentFloating content={ContentDesc(e.Content)}", e.Content as LayoutAnchorable, includeTree: true);
    }

    private void OnDockManagerContentFloated(object? sender, ContentFloatedEventArgs e)
    {
        TraceDock($"ContentFloated content={ContentDesc(e.Content)}", e.Content as LayoutAnchorable, includeTree: true);
    }

    private void OnDockManagerContentDocked(object? sender, ContentDockedEventArgs e)
    {
        TraceDock($"ContentDocked content={ContentDesc(e.Content)}", e.Content as LayoutAnchorable, includeTree: true);
        if (e.Content is LayoutAnchorable anchor
            && TryRestoreWorkspaceDocumentSplitDock(anchor))
        {
            dockManager.Layout?.CollectGarbage();
            QueueDockPaneExtentUpdate();
            return;
        }

        QueueDockPaneExtentUpdate();
        Dispatcher.BeginInvoke(new Action(WorkspacePane.NormalizeViewportAfterLayoutChange), DispatcherPriority.ApplicationIdle);
        Dispatcher.BeginInvoke(new Action(BringMainWindowToFront), DispatcherPriority.Background);
    }

    private void BringMainWindowToFront()
    {
        if (!IsVisible) return;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
        Focus();
    }

    private void SyncLlmChatAnchorFromVm()
    {
        if (_suppressLlmChatSync) return;
        _suppressLlmChatSync = true;
        try
        {
            bool show = _vm.IsLlmChatVisible && _vm.LlmChatVm != null;
            llmChatAnchor.IsVisible = show;
            llmChatAnchor.IsActive = show;
            llmChatAnchor.IsSelected = show;
        }
        finally { _suppressLlmChatSync = false; }
    }

    private void OnAnchorIsVisibleChanged(object? sender, EventArgs e)
    {
        TraceDock($"IsVisibleChanged anchor={ContentDesc(sender as LayoutContent)}", sender as LayoutAnchorable);
        QueueDockPaneExtentUpdate();
    }

    private void QueueDockPaneExtentUpdate()
    {
        if (_dockPaneExtentUpdateQueued || _inDockPaneUpdate) return;
        _dockPaneExtentUpdateQueued = true;
        TraceDock("QueueDockPaneExtentUpdate queued");
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _dockPaneExtentUpdateQueued = false;
            _inDockPaneUpdate = true;
            try
            {
                TraceDock("UpdateDockPaneExtents begin", includeTree: true);
                UpdateDockPaneExtents();
                TraceDock("UpdateDockPaneExtents end", includeTree: true);
            }
            finally
            {
                _inDockPaneUpdate = false;
            }
        }), DispatcherPriority.ApplicationIdle);
    }

    private void UpdateDockPaneExtents()
    {
        NormalizeDockLayoutAfterMutation();

        SetDockWidthIfAttached(explorerPane, ExplorerDefaultW);
        SetDockHeightIfAttached(simulationPane, SimulationDefaultH);
        SetDockHeightIfAttached(historyPane, HistoryDefaultH);
        SetDockHeightIfAttached(llmChatPane, StarLength);
        SetDockHeightIfAttached(propertyPane, StarLength);

        SetCurrentAnchorPaneExtent(explorerAnchor, ExplorerDefaultW, ExplorerDefaultW);
        SetCurrentAnchorPaneExtent(simulationAnchor, RightDefaultW, SimulationDefaultH);
        SetCurrentAnchorPaneExtent(propertyAnchor, RightDefaultW, StarLength);
        SetCurrentAnchorPaneExtent(historyAnchor, RightDefaultW, HistoryDefaultH);
        SetCurrentAnchorPaneExtent(llmChatAnchor, RightDefaultW, StarLength);

        if (IsDockedInMainLayout(rightPanel))
        {
            var nextRight = HasVisibleDockedAnchorable(rightPanel) ? RightDefaultW : ZeroLength;
            if (rightPanel.DockWidth != nextRight) rightPanel.DockWidth = nextRight;
        }

        RecomputeDockVisibility();
    }

    private void NormalizeDockLayoutAfterMutation()
    {
        int removedPanes = RemoveEmptyAnchorablePanes();
        int removedGroups = RemoveEmptyAnchorablePaneGroups();
        RecomputeDockVisibility();
        if (removedPanes > 0 || removedGroups > 0)
            TraceDock($"NormalizeDockLayout removedEmptyAnchorablePanes={removedPanes} removedEmptyAnchorablePaneGroups={removedGroups}", includeTree: true);
    }

    private int RemoveEmptyAnchorablePanes()
    {
        var panes = dockManager.Layout?.Descendents()
            .OfType<LayoutAnchorablePane>()
            .Where(p => p.ChildrenCount == 0
                        && IsDockedInMainLayout(p)
                        && !IsCanonicalAnchorablePane(p)
                        && !IsFloatingPlaceholderPane(p))
            .ToArray();
        if (panes is not { Length: > 0 }) return 0;

        int removed = 0;
        foreach (var pane in panes)
        {
            if (pane.Parent is not ILayoutGroup parent) continue;
            int index = parent.IndexOfChild(pane);
            if (index < 0) continue;

            parent.RemoveChildAt(index);
            removed++;
        }

        return removed;
    }

    private int RemoveEmptyAnchorablePaneGroups()
    {
        var groups = dockManager.Layout?.Descendents()
            .OfType<LayoutAnchorablePaneGroup>()
            .Where(g => g.ChildrenCount == 0 && IsDockedInMainLayout(g))
            .ToArray();
        if (groups is not { Length: > 0 }) return 0;

        int removed = 0;
        foreach (var group in groups)
        {
            if (group.Parent is not ILayoutGroup parent) continue;
            int index = parent.IndexOfChild(group);
            if (index < 0) continue;

            parent.RemoveChildAt(index);
            removed++;
        }

        return removed;
    }

    private void RecomputeDockVisibility()
    {
        var layout = dockManager.Layout;
        if (layout == null) return;

        foreach (var element in layout.Descendents().OfType<ILayoutElement>().Reverse().ToArray())
            RecomputeElementVisibility(element);
    }

    private static void RecomputeElementVisibility(ILayoutElement element)
    {
        switch (element)
        {
            case LayoutAnchorablePane pane:
                pane.ComputeVisibility();
                break;
            case LayoutDocumentPane pane:
                pane.ComputeVisibility();
                break;
            case LayoutAnchorablePaneGroup group:
                group.ComputeVisibility();
                break;
            case LayoutDocumentPaneGroup group:
                group.ComputeVisibility();
                break;
            case LayoutPanel panel:
                panel.ComputeVisibility();
                break;
            case ILayoutElementWithVisibility visible:
                visible.ComputeVisibility();
                break;
        }
    }

    private void SetDockWidthIfAttached(LayoutAnchorablePane pane, GridLength visibleLength)
    {
        if (!IsDockedInMainLayout(pane)) return;
        var next = ShouldKeepPaneExtent(pane) ? visibleLength : ZeroLength;
        if (pane.DockWidth != next) pane.DockWidth = next;
    }

    private void SetDockHeightIfAttached(LayoutAnchorablePane pane, GridLength visibleLength)
    {
        if (!IsDockedInMainLayout(pane)) return;
        var next = ShouldKeepPaneExtent(pane) ? visibleLength : ZeroLength;
        if (pane.DockHeight != next) pane.DockHeight = next;
    }

    private void SetCurrentAnchorPaneExtent(LayoutAnchorable anchor, GridLength width, GridLength height)
    {
        if (!anchor.IsVisible || anchor.IsFloating) return;
        if (anchor.Parent is not LayoutAnchorablePane pane) return;
        if (!IsDockedInMainLayout(pane)) return;

        switch (pane.Parent)
        {
            case LayoutPanel { Orientation: System.Windows.Controls.Orientation.Horizontal }:
            case LayoutAnchorablePaneGroup { Orientation: System.Windows.Controls.Orientation.Horizontal }:
                if (pane.DockWidth != width) pane.DockWidth = width;
                break;

            case LayoutPanel { Orientation: System.Windows.Controls.Orientation.Vertical }:
            case LayoutAnchorablePaneGroup { Orientation: System.Windows.Controls.Orientation.Vertical }:
                if (pane.DockHeight != height) pane.DockHeight = height;
                break;
        }
    }

    private bool ShouldKeepPaneExtent(LayoutAnchorablePane pane)
    {
        if (pane.IsVisible) return true;
        var keepPlaceholder = IsFloatingPlaceholderPane(pane);
        if (keepPlaceholder)
            TraceDock($"KeepPaneExtentForFloatingPlaceholder pane={ElementDesc(pane)}");
        return keepPlaceholder;
    }

    private bool IsFloatingPlaceholderPane(LayoutAnchorablePane pane)
    {
        return pane.ChildrenCount == 0
            && _dockPlacements.Any(kv =>
                kv.Key.IsFloating
                && ReferenceEquals(kv.Value.PaneElement, pane));
    }

    private bool IsDockedInMainLayout(ILayoutElement element)
    {
        return element.Root == dockManager.Layout
            && element.FindParent<LayoutFloatingWindow>() == null;
    }

    private bool IsCanonicalAnchorablePane(LayoutAnchorablePane pane)
    {
        return ReferenceEquals(pane, explorerPane)
            || ReferenceEquals(pane, simulationPane)
            || ReferenceEquals(pane, propertyPane)
            || ReferenceEquals(pane, historyPane)
            || ReferenceEquals(pane, llmChatPane);
    }

    private bool HasVisibleDockedAnchorable(ILayoutElement element)
    {
        return element.Descendents().OfType<LayoutAnchorable>()
            .Any(a => a.IsVisible && a.FindParent<LayoutFloatingWindow>() == null);
    }
}
