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
        var pane = ResolveTreePane(sender);
        var ctrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (sender is TreeViewItem { DataContext: EntityNode node } item
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

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            e.Handled = true;
            return;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!HasActiveSearch)
        {
            _searchRefreshTimer.Stop();
            RefreshTreeItemsSource();
            return;
        }

        QueueSearchRefresh();
    }

    private void SearchRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _searchRefreshTimer.Stop();
        RefreshTreeItemsSource();
    }

    private void QueueSearchRefresh()
    {
        _searchRefreshTimer.Stop();
        _searchRefreshTimer.Start();
    }

    private void RefreshTreeItemsSource()
    {
        if (_boundViewModel is null)
        {
            ControlTree.ItemsSource = null;
            DeviceTree.ItemsSource = null;
            return;
        }

        if (!HasActiveSearch)
        {
            ControlTree.ItemsSource = _boundViewModel.ControlTreeRoots;
            DeviceTree.ItemsSource = _boundViewModel.DeviceTreeRoots;
            return;
        }

        var query = SearchBox.Text.Trim();
        RebuildFilteredRoots(_boundViewModel.ControlTreeRoots, FilteredControlTreeRoots, query);
        RebuildFilteredRoots(_boundViewModel.DeviceTreeRoots, FilteredDeviceTreeRoots, query);
        ControlTree.ItemsSource = FilteredControlTreeRoots;
        DeviceTree.ItemsSource = FilteredDeviceTreeRoots;
        _boundViewModel.StatusText =
            FilteredControlTreeRoots.Count + FilteredDeviceTreeRoots.Count > 0
                ? $"탐색기에서 '{query}' 검색 결과를 표시합니다."
                : $"탐색기에서 '{query}' 검색 결과가 없습니다.";
    }

    private static void BringTreeNodeIntoView(TreeView tree, Guid nodeId)
    {
        if (TryFindTreeItem(tree, nodeId, out var item) && item is not null)
            item.BringIntoView();
    }

    private static void RebuildFilteredRoots(
        IEnumerable<EntityNode> sourceRoots,
        ObservableCollection<EntityNode> targetRoots,
        string query)
    {
        targetRoots.Clear();
        foreach (var node in sourceRoots)
        {
            var filtered = CloneFilteredNode(node, query);
            if (filtered is not null)
                targetRoots.Add(filtered);
        }
    }

    private static EntityNode? CloneFilteredNode(EntityNode source, string query)
    {
        var isMatch = source.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);

        var filteredChildren = source.Children
            .Select(child => CloneFilteredNode(child, query))
            .Where(child => child is not null)
            .Cast<EntityNode>()
            .ToList();

        if (isMatch)
        {
            var clone = CloneNode(source);

            if (filteredChildren.Count > 0)
            {
                // 부모 매칭 + 자식도 매칭 → 펼침, 모든 자식 포함 (매칭 자식은 필터 버전 사용)
                clone.IsExpanded = true;
                var filteredIds = new HashSet<Guid>(filteredChildren.Select(c => c.Id));
                foreach (var child in source.Children)
                    clone.Children.Add(filteredIds.Contains(child.Id)
                        ? filteredChildren.First(c => c.Id == child.Id)
                        : CloneSubtree(child));
            }
            else
            {
                // 부모만 매칭 → 접힘, 모든 자식 포함
                clone.IsExpanded = false;
                foreach (var child in source.Children)
                    clone.Children.Add(CloneSubtree(child));
            }

            return clone;
        }

        if (filteredChildren.Count == 0)
            return null;

        var filteredClone = CloneNode(source);
        filteredClone.IsExpanded = true;
        foreach (var child in filteredChildren)
            filteredClone.Children.Add(child);
        return filteredClone;
    }

    private static EntityNode CloneSubtree(EntityNode source)
    {
        var clone = CloneNode(source);
        foreach (var child in source.Children)
            clone.Children.Add(CloneSubtree(child));
        return clone;
    }

    private static EntityNode CloneNode(EntityNode source)
    {
        var clone = new EntityNode(source.Id, source.EntityType, source.Name, source.ParentId)
        {
            ReferenceOfId = source.ReferenceOfId
        };
        clone.X = source.X;
        clone.Y = source.Y;
        clone.Width = source.Width;
        clone.Height = source.Height;
        clone.IsSelected = source.IsSelected;
        clone.IsTreeSelected = source.IsTreeSelected;
        clone.IsExpanded = source.IsExpanded;
        clone.SelectionOrder = source.SelectionOrder;
        clone.IsGhost = source.IsGhost;
        clone.IsReference = source.IsReference;
        clone.HasAutoAux = source.HasAutoAux;
        clone.HasComAux = source.HasComAux;
        clone.HasSkipUnmatch = source.HasSkipUnmatch;
        clone.IsWarning = source.IsWarning;
        clone.IsDropTarget = source.IsDropTarget;
        clone.SimState = source.SimState;
        clone.SimTokenDisplay = source.SimTokenDisplay;
        return clone;
    }

    private static bool TryFindTreeItem(ItemsControl parent, Guid nodeId, out TreeViewItem? item)
    {
        foreach (var current in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(current) is not TreeViewItem container)
                continue;

            if (container.DataContext is EntityNode node && node.Id == nodeId)
            {
                item = container;
                return true;
            }

            container.ApplyTemplate();
            container.UpdateLayout();

            if (TryFindTreeItem(container, nodeId, out item))
                return true;
        }

        item = null;
        return false;
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

        if (node.EntityType == EntityKind.Call)
            ViewModel.Canvas.OpenParentCanvasAndFocusNode(node.Id, node.EntityType, zoomOverride: 1.0);
        else if (node.EntityType is EntityKind.System or EntityKind.Flow or EntityKind.Work)
        {
            ViewModel.Canvas.OpenCanvasTab(node.Id, node.EntityType);
            Dispatcher.InvokeAsync(
                () => ViewModel?.Canvas.ApplyZoomCenteredRequested?.Invoke(doubleClickZoom),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else if (node.EntityType == EntityKind.ApiDef)
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
            if (node.IsTreeSelected)
            {
                ViewModel.Selection.SelectNodeFromTree(node, ctrlPressed: false, shiftPressed: false);
                e.Handled = true;
            }

            return;
        }

        ViewModel.Selection.SelectNodeFromTree(node, ctrlPressed, shiftPressed);
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
