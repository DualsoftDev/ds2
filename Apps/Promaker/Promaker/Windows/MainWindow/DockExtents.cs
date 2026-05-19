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
        QueueDockPaneExtentUpdate();
        Dispatcher.BeginInvoke(new Action(BringMainWindowToFront), DispatcherPriority.Background);
    }

    private void BringMainWindowToFront()
    {
        if (!IsVisible) return;
        Topmost = true;
        Activate();
        Dispatcher.BeginInvoke(new Action(() => Topmost = false),
            DispatcherPriority.ApplicationIdle);
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

        if (IsDockedInMainLayout(rightPanel))
        {
            var nextRight = HasVisibleDockedAnchorable(rightPanel) ? RightDefaultW : ZeroLength;
            if (rightPanel.DockWidth != nextRight) rightPanel.DockWidth = nextRight;
        }

        RecomputeDockVisibility();
    }

    private void NormalizeDockLayoutAfterMutation()
    {
        int removedGroups = RemoveEmptyAnchorablePaneGroups();
        RecomputeDockVisibility();
        if (removedGroups > 0)
            TraceDock($"NormalizeDockLayout removedEmptyAnchorablePaneGroups={removedGroups}", includeTree: true);
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

    private bool ShouldKeepPaneExtent(LayoutAnchorablePane pane)
    {
        if (pane.IsVisible) return true;
        var keepPlaceholder = pane.ChildrenCount == 0
            && _dockPlacements.Any(kv =>
                kv.Key.IsFloating
                && ReferenceEquals(kv.Value.PaneElement, pane));
        if (keepPlaceholder)
            TraceDock($"KeepPaneExtentForFloatingPlaceholder pane={ElementDesc(pane)}");
        return keepPlaceholder;
    }

    private bool IsDockedInMainLayout(ILayoutElement element)
    {
        return element.Root == dockManager.Layout
            && element.FindParent<LayoutFloatingWindow>() == null;
    }

    private bool HasVisibleDockedAnchorable(ILayoutElement element)
    {
        return element.Descendents().OfType<LayoutAnchorable>()
            .Any(a => a.IsVisible && a.FindParent<LayoutFloatingWindow>() == null);
    }
}
