using System;
using System.ComponentModel;
using AvalonDock;
using AvalonDock.Layout;

namespace Promaker;

public partial class MainWindow
{
    // floating -> docked 복귀 시 직전 dock 위치 복원.
    private static string PaneDesc(object? parent)
    {
        if (parent == null) return "null";
        if (parent is LayoutAnchorablePane p)
            return $"AnchorablePane(n={p.Children.Count})";
        return parent.GetType().Name;
    }

    private void OnAnchorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LayoutAnchorable anchor) return;
        if (e.PropertyName == "IsFloating")
        {
            TraceDock($"AnchorPropertyChanged IsFloating anchor={ContentDesc(anchor)} parent={ElementDesc(anchor.Parent as ILayoutElement)}", anchor, includeTree: true);
            QueueDockPaneExtentUpdate();
            return;
        }
        if (e.PropertyName != "Parent") return;
        TraceDock($"AnchorPropertyChanged Parent anchor={ContentDesc(anchor)} parent={ElementDesc(anchor.Parent as ILayoutElement)}", anchor, includeTree: true);
        CaptureDockPlacement(anchor);
        QueueDockPaneExtentUpdate();
    }

    // anchor 가 main dock layout 안에 docked 상태일 때 그 pane + index 를 기록한다.
    private void CaptureDockPlacement(LayoutAnchorable anchor)
    {
        if (anchor.IsFloating)
        {
            TraceDock($"CaptureDockPlacement skipped floating anchor={ContentDesc(anchor)}", anchor);
            return;
        }
        if (anchor.Parent is not ILayoutPane pane)
        {
            TraceDock($"CaptureDockPlacement skipped non-pane parent anchor={ContentDesc(anchor)} parent={ElementDesc(anchor.Parent as ILayoutElement)}", anchor);
            return;
        }
        if (anchor.Parent is not ILayoutGroup paneGroup)
        {
            TraceDock($"CaptureDockPlacement skipped non-group parent anchor={ContentDesc(anchor)} parent={ElementDesc(anchor.Parent as ILayoutElement)}", anchor);
            return;
        }
        if (pane is not ILayoutElement paneElement)
        {
            TraceDock($"CaptureDockPlacement skipped pane not layout element anchor={ContentDesc(anchor)} parent={PaneDesc(anchor.Parent)}", anchor);
            return;
        }
        if (paneElement.Root != dockManager.Layout)
        {
            TraceDock($"CaptureDockPlacement skipped other-root anchor={ContentDesc(anchor)} parent={ElementDesc(paneElement)} root={RootDesc(paneElement)}", anchor);
            return;
        }
        if (paneElement.FindParent<LayoutFloatingWindow>() != null)
        {
            TraceDock($"CaptureDockPlacement skipped floating parent anchor={ContentDesc(anchor)} parent={ElementDesc(paneElement)}", anchor);
            return;
        }

        int idx = paneGroup.IndexOfChild(anchor);
        if (idx < 0)
        {
            TraceDock($"CaptureDockPlacement skipped missing child anchor={ContentDesc(anchor)} pane={ElementDesc(paneElement)}", anchor);
            return;
        }

        var parentGroup = paneElement.Parent as ILayoutGroup;
        int paneIndex = parentGroup?.IndexOfChild(paneElement) ?? -1;
        var parentGroupElement = parentGroup as ILayoutElement;
        var parentParentGroup = parentGroupElement?.Parent as ILayoutGroup;
        int parentGroupIndex = parentParentGroup?.IndexOfChild(parentGroupElement) ?? -1;

        _dockPlacements[anchor] = new DockAnchorPlacement(
            paneElement,
            paneGroup,
            parentGroup,
            paneIndex,
            idx,
            parentGroupElement,
            parentParentGroup,
            parentGroupIndex);
        TraceDock($"CaptureDockPlacement captured anchor={ContentDesc(anchor)} pane={ElementDesc(paneElement)} childIndex={idx} paneIndex={paneIndex}", anchor);
    }

    private void OnDockManagerContentDocking(object? sender, ContentDockingEventArgs e)
    {
        TraceDock($"ContentDocking content={ContentDesc(e.Content)}", e.Content as LayoutAnchorable, includeTree: true);
        if (e.Content is not LayoutAnchorable anchor) return;
        if (!anchor.IsFloating)
        {
            TraceDock($"ContentDocking ignored non-floating anchor={ContentDesc(anchor)}", anchor);
            return;
        }
        if (!TryDockAnchorAtCapturedPlacement(anchor))
        {
            TraceDock($"ContentDocking fallback AvalonDock Dock anchor={ContentDesc(anchor)}", anchor, includeTree: true);
            return;
        }

        e.Cancel = true;
        dockManager.Layout?.CollectGarbage();
        TraceDock($"ContentDocking handled by captured placement anchor={ContentDesc(anchor)}", anchor, includeTree: true);
    }

    private bool TryDockAnchorAtCapturedPlacement(LayoutAnchorable anchor)
    {
        TraceDock($"TryDockAnchorAtCapturedPlacement begin anchor={ContentDesc(anchor)}", anchor, includeTree: true);
        if (!_dockPlacements.TryGetValue(anchor, out var placement))
        {
            TraceDock($"TryDockAnchorAtCapturedPlacement no-placement anchor={ContentDesc(anchor)}", anchor);
            return false;
        }

        if (!EnsureDockPaneAttached(placement))
        {
            TraceDock($"TryDockAnchorAtCapturedPlacement ensure-failed anchor={ContentDesc(anchor)} placementPane={ElementDesc(placement.PaneElement)}", anchor, includeTree: true);
            return false;
        }

        if (placement.PaneElement.FindParent<LayoutFloatingWindow>() != null)
        {
            TraceDock($"TryDockAnchorAtCapturedPlacement placement-floating anchor={ContentDesc(anchor)} placementPane={ElementDesc(placement.PaneElement)}", anchor, includeTree: true);
            return false;
        }

        if (ReferenceEquals(anchor.Parent, placement.PaneGroup))
        {
            anchor.IsSelected = true;
            anchor.IsActive = true;
            CaptureDockPlacement(anchor);
            QueueDockPaneExtentUpdate();
            TraceDock($"TryDockAnchorAtCapturedPlacement already-at-placement anchor={ContentDesc(anchor)}", anchor, includeTree: true);
            return true;
        }

        int insertIndex = Math.Clamp(placement.ChildIndex, 0, placement.PaneGroup.ChildrenCount);
        placement.PaneGroup.InsertChildAt(insertIndex, anchor);
        anchor.IsSelected = true;
        anchor.IsActive = true;

        CaptureDockPlacement(anchor);
        QueueDockPaneExtentUpdate();
        TraceDock($"TryDockAnchorAtCapturedPlacement inserted anchor={ContentDesc(anchor)} index={insertIndex}", anchor, includeTree: true);
        return true;
    }

    private static bool EnsureDockPaneAttached(DockAnchorPlacement placement)
    {
        if (placement.PaneElement.Parent != null
            && placement.PaneElement.Root != null
            && placement.PaneElement.FindParent<LayoutFloatingWindow>() == null)
            return true;

        if (placement.ParentGroup is null)
            return false;

        if (placement.ParentGroupElement is { Parent: null }
            && placement.ParentParentGroup is not null)
        {
            int parentGroupIndex = placement.ParentGroupIndex;
            if (parentGroupIndex < 0 || parentGroupIndex > placement.ParentParentGroup.ChildrenCount)
                parentGroupIndex = placement.ParentParentGroup.ChildrenCount;

            placement.ParentParentGroup.InsertChildAt(parentGroupIndex, placement.ParentGroupElement);
        }

        if (!ReferenceEquals(placement.PaneElement.Parent, placement.ParentGroup))
        {
            int paneIndex = placement.PaneIndex;
            if (paneIndex < 0 || paneIndex > placement.ParentGroup.ChildrenCount)
                paneIndex = placement.ParentGroup.ChildrenCount;

            placement.ParentGroup.InsertChildAt(paneIndex, placement.PaneElement);
        }

        return placement.PaneElement.Parent != null
            && placement.PaneElement.Root != null
            && placement.PaneElement.FindParent<LayoutFloatingWindow>() == null;
    }

    private sealed class DockAnchorPlacement(
        ILayoutElement paneElement,
        ILayoutGroup paneGroup,
        ILayoutGroup? parentGroup,
        int paneIndex,
        int childIndex,
        ILayoutElement? parentGroupElement,
        ILayoutGroup? parentParentGroup,
        int parentGroupIndex)
    {
        public ILayoutElement PaneElement { get; } = paneElement;
        public ILayoutGroup PaneGroup { get; } = paneGroup;
        public ILayoutGroup? ParentGroup { get; } = parentGroup;
        public int PaneIndex { get; } = paneIndex;
        public int ChildIndex { get; } = childIndex;
        public ILayoutElement? ParentGroupElement { get; } = parentGroupElement;
        public ILayoutGroup? ParentParentGroup { get; } = parentParentGroup;
        public int ParentGroupIndex { get; } = parentGroupIndex;
    }

    private sealed class DockLayoutUpdateStrategy(MainWindow owner) : ILayoutUpdateStrategy
    {
        public bool BeforeInsertAnchorable(
            LayoutRoot layout,
            LayoutAnchorable anchorableToShow,
            ILayoutContainer destinationContainer)
        {
            owner.TraceDock($"LayoutUpdateStrategy.BeforeInsertAnchorable anchor={ContentDesc(anchorableToShow)} destination={ElementDesc(destinationContainer as ILayoutElement)}", anchorableToShow, includeTree: true);
            return owner.TryDockAnchorAtCapturedPlacement(anchorableToShow);
        }

        public void AfterInsertAnchorable(LayoutRoot layout, LayoutAnchorable anchorableShown)
        {
            owner.TraceDock($"LayoutUpdateStrategy.AfterInsertAnchorable anchor={ContentDesc(anchorableShown)}", anchorableShown, includeTree: true);
            owner.CaptureDockPlacement(anchorableShown);
            owner.QueueDockPaneExtentUpdate();
        }

        public bool BeforeInsertDocument(
            LayoutRoot layout,
            LayoutDocument anchorableToShow,
            ILayoutContainer destinationContainer) => false;

        public void AfterInsertDocument(LayoutRoot layout, LayoutDocument anchorableShown)
        {
        }
    }

    // View -> VM (X 버튼 = 사용자 명시 close 한 곳만). auto-hide / float 상태 변화는 무관.
    private void OnLlmChatHiding(object? sender, CancelEventArgs e)
    {
        if (_suppressLlmChatSync) return;
        _suppressLlmChatSync = true;
        try
        {
            _vm.IsLlmChatVisible = false;
        }
        finally { _suppressLlmChatSync = false; }
    }
}
