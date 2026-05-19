using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class ExplorerPane : UserControl
{
    private Point _treeDragStartPoint;
    private bool _treeDragCandidate;
    private EntityNode? _pendingTreeSelectionNode;
    private TreePaneKind _pendingTreeSelectionPane = TreePaneKind.Control;
    private MainViewModel? _boundViewModel;
    private TreePaneKind _activeTreePane = TreePaneKind.Control;
    private readonly DispatcherTimer _searchRefreshTimer;

    public ObservableCollection<EntityNode> FilteredControlTreeRoots { get; } = [];
    public ObservableCollection<EntityNode> FilteredDeviceTreeRoots { get; } = [];

    public ExplorerPane()
    {
        InitializeComponent();
        _searchRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _searchRefreshTimer.Tick += SearchRefreshTimer_Tick;
        DataContextChanged += ExplorerPane_DataContextChanged;
        Loaded   += ExplorerPane_Loaded;
        Unloaded += ExplorerPane_Unloaded;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private bool HasActiveSearch => !string.IsNullOrWhiteSpace(SearchBox.Text);

    private void SetActiveTreePane(TreePaneKind pane)
    {
        _activeTreePane = pane;
        ControlTree.Visibility = pane == TreePaneKind.Control ? Visibility.Visible : Visibility.Collapsed;
        DeviceTree.Visibility = pane == TreePaneKind.Device ? Visibility.Visible : Visibility.Collapsed;
        ControlTreeButton.IsChecked = pane == TreePaneKind.Control;
        DeviceTreeButton.IsChecked = pane == TreePaneKind.Device;

        if (ViewModel is not null)
            ViewModel.Selection.SetActiveTreePane(pane);
    }

    private void ExplorerPane_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        RebindViewModel(e.OldValue as MainViewModel, e.NewValue as MainViewModel);
    }

    private void ExplorerPane_Unloaded(object sender, RoutedEventArgs e) =>
        RebindViewModel(_boundViewModel, null);

    private void ExplorerPane_Loaded(object sender, RoutedEventArgs e)
    {
        var currentVm = ViewModel;
        if (currentVm is null) return;
        if (!ReferenceEquals(_boundViewModel, currentVm) || ControlTree.ItemsSource is null)
            RebindViewModel(_boundViewModel, currentVm);
    }

    private void HandleExplorerRebindRequested()
    {
        if (_boundViewModel is not null)
            RebindViewModel(_boundViewModel, _boundViewModel);
    }

    private void RebindViewModel(MainViewModel? oldVm, MainViewModel? newVm)
    {
        _searchRefreshTimer.Stop();

        if (oldVm is not null)
        {
            oldVm.ControlTreeRoots.CollectionChanged -= TreeRoots_CollectionChanged;
            oldVm.DeviceTreeRoots.CollectionChanged -= TreeRoots_CollectionChanged;
            oldVm.SearchResetRequested -= ClearSearch;
            oldVm.ExplorerRebindRequested -= HandleExplorerRebindRequested;
        }

        _boundViewModel = newVm;

        if (newVm is not null)
        {
            newVm.ControlTreeRoots.CollectionChanged += TreeRoots_CollectionChanged;
            newVm.DeviceTreeRoots.CollectionChanged += TreeRoots_CollectionChanged;
            newVm.SearchResetRequested += ClearSearch;
            newVm.ExplorerRebindRequested += HandleExplorerRebindRequested;
        }

        RefreshTreeItemsSource();
    }

    private void ClearSearch() => SearchBox.Clear();

    private void TreeRoots_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshTreeItemsSource();

    private void ControlTreeButton_Click(object sender, RoutedEventArgs e) =>
        SetActiveTreePane(TreePaneKind.Control);

    private void DeviceTreeButton_Click(object sender, RoutedEventArgs e) =>
        SetActiveTreePane(TreePaneKind.Device);

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => HandleTreeSelectionChanged(ResolveTreePane(sender), e.NewValue);

    private void Tree_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeView tree)
            tree.Focus();
    }

    private void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || ViewModel is null)
            return;

        if (ViewModel.DeleteSelectedCommand.CanExecute(null))
        {
            ViewModel.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void TreeViewItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item) return;

        // Expander(셰브론) 클릭은 WPF 기본 토글에 맡긴다.
        // 선택된 부모 노드에서 e.Handled=true 가 걸리면 ToggleButton 이 클릭을 받지 못해 펼침/접힘이 실패한다.
        if (IsClickOnExpander(e.OriginalSource as DependencyObject, item))
        {
            ClearPendingTreeDragSelection();
            return;
        }

        var pane = ResolveTreePane(sender);
        var ctrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (item.DataContext is EntityNode node
            && node.EntityType == EntityKind.Call
            && !ctrlPressed
            && !shiftPressed)
        {
            _treeDragCandidate = true;
            _pendingTreeSelectionNode = node;
            _pendingTreeSelectionPane = pane;
            try { _treeDragStartPoint = e.GetPosition(null); }
            catch { _treeDragStartPoint = default; }
        }
        else
        {
            ClearPendingTreeDragSelection();
        }

        HandleTreeItemMouseDown(pane, sender, e, requireModifiers: true);
    }

    private static bool IsClickOnExpander(DependencyObject? source, DependencyObject stopAt)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, stopAt))
        {
            if (current is ToggleButton) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void TreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ClearPendingTreeDragSelection();

        // 이미 선택된 노드를 우클릭한 경우 선택 변경하지 않음 (다중 선택 유지)
        if (sender is TreeViewItem { DataContext: EntityNode node } && node.IsTreeSelected)
        {
            e.Handled = true;
            return;
        }
        HandleTreeItemMouseDown(ResolveTreePane(sender), sender, e, requireModifiers: false);
    }

    private void TreeViewItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_treeDragCandidate || _pendingTreeSelectionNode is null || ViewModel is null)
            return;

        if (sender is not TreeViewItem { DataContext: EntityNode node } item)
            return;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)))
            return;
        if (!ReferenceEquals(node, _pendingTreeSelectionNode))
            return;

        ViewModel.Selection.SetActiveTreePane(_pendingTreeSelectionPane);
        ViewModel.Selection.SelectNodeFromTree(node, ctrlPressed: false, shiftPressed: false);
        ClearPendingTreeDragSelection();
        e.Handled = true;
    }

    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TreeView tree || tree.ContextMenu is not { } menu) return;
        var vm = ViewModel;
        var kind = ResolveContextMenuNode(tree, e.OriginalSource as DependencyObject)?.EntityType
                   ?? vm?.SelectedNode?.EntityType;
        var isDeviceTree = ReferenceEquals(tree, DeviceTree);
        var hasProject = vm?.HasProject == true;

        // 1차 노드 타입단계 메뉴 항목 표시/숨김
        foreach (var item in menu.Items)
        {
            var tag = item switch
            {
                MenuItem mi => mi.Tag as string,
                Separator sep => sep.Tag as string,
                _ => null
            };
            if (tag is null) continue;

            var visible = EntityKindRules.isMenuOperationAllowed(kind, tag, hasProject, isDeviceTree);

            if (item is FrameworkElement fe)
                fe.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // 2차 연속/선행/후행 구분선 제거
        CollapseDuplicateSeparators(menu);
    }


    private static EntityNode? ResolveContextMenuNode(TreeView tree, DependencyObject? originalSource)
    {
        if (FindAncestor<TreeViewItem>(originalSource) is { DataContext: EntityNode node })
            return node;

        if (Mouse.DirectlyOver is DependencyObject hovered
            && ReferenceEquals(FindAncestor<TreeView>(hovered), tree)
            && FindAncestor<TreeViewItem>(hovered) is { DataContext: EntityNode hoveredNode })
            return hoveredNode;

        return null;
    }

    private static void CollapseDuplicateSeparators(ContextMenu menu)
    {
        FrameworkElement? lastVisibleSep = null;
        var hasVisibleItemSinceLastSep = false;

        foreach (var item in menu.Items)
        {
            if (item is not FrameworkElement fe) continue;
            if (fe.Visibility != Visibility.Visible) continue;

            if (item is Separator sep)
            {
                if (!hasVisibleItemSinceLastSep)
                    sep.Visibility = Visibility.Collapsed;
                else
                    lastVisibleSep = sep;
                hasVisibleItemSinceLastSep = false;
            }
            else
            {
                hasVisibleItemSinceLastSep = true;
                lastVisibleSep = null;
            }
        }

        // 마지막이 구분선이면 제거
        if (lastVisibleSep is not null)
            lastVisibleSep.Visibility = Visibility.Collapsed;
    }

    private void TreeViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null) return;
        if (sender is not TreeViewItem item) return;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject))) return;
        if (item.DataContext is not EntityNode node) return;

        const double doubleClickZoom = 0.52;

        // 더블클릭 → action 분류는 F# EditorNavigation 위임. UI dispatch 만 C# 에 남김.
        var action = EditorNavigation.ClassifyTreeDoubleClick(node.EntityType);
        if (action.IsFocusInParentCanvas)
            ViewModel.Canvas.OpenParentCanvasAndFocusNode(node.Id, node.EntityType, zoomOverride: 1.0);
        else if (action.IsOpenCanvasTab)
        {
            ViewModel.Canvas.OpenCanvasTab(node.Id, node.EntityType);
            Dispatcher.InvokeAsync(
                () => ViewModel?.Canvas.ApplyZoomCenteredRequested?.Invoke(doubleClickZoom),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else if (action.IsEditApiDef)
            ViewModel.EditApiDefNode(node.Id);

        e.Handled = true;
    }

    private void TreeViewItem_PreviewMouseMove_Drag(object sender, MouseEventArgs e)
    {
        if (!_treeDragCandidate || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(null);
        var diff = pos - _treeDragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (sender is not TreeViewItem { DataContext: EntityNode node }) return;
        if (node.EntityType != EntityKind.Call) return;

        ClearPendingTreeDragSelection();
        var data = new DataObject("ConditionCallNode", node);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    private void HandleTreeSelectionChanged(TreePaneKind pane, object? newValue)
    {
        if (ViewModel is null) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None)
            return;

        if (newValue is not EntityNode node)
            return;

        if (_activeTreePane != pane)
            SetActiveTreePane(pane);

        ViewModel.Selection.SetActiveTreePane(pane);

        if (_treeDragCandidate
            && _pendingTreeSelectionNode is not null
            && ReferenceEquals(node, _pendingTreeSelectionNode))
            return;

        ViewModel.Selection.SelectNodeFromTree(node, ctrlPressed: false, shiftPressed: false);

        if (HasActiveSearch)
        {
            RefreshFilteredSelectionState();
            if (node.EntityType is EntityKind.System or EntityKind.Flow or EntityKind.Work)
                ViewModel.Canvas.OpenParentCanvasAndFocusNode(node.Id, node.EntityType);
        }
    }

    private void HandleTreeItemMouseDown(TreePaneKind pane, object sender, MouseButtonEventArgs e, bool requireModifiers)
    {
        if (ViewModel is null) return;
        ViewModel.Selection.SetActiveTreePane(pane);
        if (sender is not TreeViewItem { DataContext: EntityNode node } item) return;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject))) return;

        var ctrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (requireModifiers && !ctrlPressed && !shiftPressed)
        {
            // ClickCount>=2 (더블클릭의 두 번째 다운)에서 Handled=true 가 걸리면
            // Control.HandleDoubleClick 클래스 핸들러가 MouseDoubleClick 을 발생시키지 못한다.
            if ((node.IsTreeSelected || item.IsSelected) && e.ClickCount == 1)
            {
                ViewModel.Selection.SelectNodeFromTree(node, ctrlPressed: false, shiftPressed: false);
                e.Handled = true;
            }

            return;
        }

        ViewModel.Selection.SelectNodeFromTree(node, ctrlPressed, shiftPressed);
        if (e.ClickCount == 1)
            e.Handled = true;
    }

    private TreePaneKind ResolveTreePane(object sender)
    {
        if (sender is TreeView treeView)
            return ReferenceEquals(treeView, DeviceTree) ? TreePaneKind.Device : TreePaneKind.Control;

        if (sender is DependencyObject dependencyObject && ReferenceEquals(FindAncestor<TreeView>(dependencyObject), DeviceTree))
            return TreePaneKind.Device;

        return TreePaneKind.Control;
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
        => SetExpandedRecursive(GetActiveTreeRoots(), true);

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
        => SetExpandedRecursive(GetActiveTreeRoots(), false);

    private IEnumerable<EntityNode>? GetActiveTreeRoots()
    {
        var vm = ViewModel;
        if (vm is null) return null;
        return _activeTreePane == TreePaneKind.Device
            ? vm.DeviceTreeRoots
            : vm.ControlTreeRoots;
    }

    private static void SetExpandedRecursive(IEnumerable<EntityNode>? roots, bool expanded)
    {
        if (roots is null) return;
        foreach (var node in roots)
            SetExpandedNode(node, expanded);
    }

    private static void SetExpandedNode(EntityNode node, bool expanded)
    {
        node.IsExpanded = expanded;
        foreach (var child in node.Children)
            SetExpandedNode(child, expanded);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T result) return result;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void ClearPendingTreeDragSelection()
    {
        _treeDragCandidate = false;
        _pendingTreeSelectionNode = null;
    }

    private void RefreshFilteredSelectionState()
    {
        if (_boundViewModel is null || !HasActiveSearch)
            return;

        var controlIndex = TreeNodeSearch.EnumerateNodes(_boundViewModel.ControlTreeRoots)
            .ToDictionary(NodeKey);
        var deviceIndex = TreeNodeSearch.EnumerateNodes(_boundViewModel.DeviceTreeRoots)
            .ToDictionary(NodeKey);

        SyncFilteredNodes(FilteredControlTreeRoots, controlIndex);
        SyncFilteredNodes(FilteredDeviceTreeRoots, deviceIndex);
    }

    private static void SyncFilteredNodes(
        IEnumerable<EntityNode> filteredNodes,
        IReadOnlyDictionary<string, EntityNode> sourceIndex)
    {
        foreach (var filteredNode in filteredNodes)
        {
            if (sourceIndex.TryGetValue(NodeKey(filteredNode), out var sourceNode))
            {
                filteredNode.IsTreeSelected = sourceNode.IsTreeSelected;
                filteredNode.SelectionOrder = sourceNode.SelectionOrder;
                filteredNode.IsExpanded = filteredNode.Children.Count > 0 || sourceNode.IsExpanded;
                filteredNode.IsWarning = sourceNode.IsWarning;
                filteredNode.SimState = sourceNode.SimState;
                filteredNode.SimTokenDisplay = sourceNode.SimTokenDisplay;
            }

            SyncFilteredNodes(filteredNode.Children, sourceIndex);
        }
    }

    private static string NodeKey(EntityNode node) =>
        $"{(int)node.EntityType}:{node.Id}";
}
