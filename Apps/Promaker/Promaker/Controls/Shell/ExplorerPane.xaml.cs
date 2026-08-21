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
    private const string TreeMoveCallNodeDataFormat = "TreeMoveCallNode";
    private const string TreeMoveCallIdsDataFormat = "TreeMoveCallIds";
    private const string TreeMoveWorkIdsDataFormat = "TreeMoveWorkIds";  // 트리 Work → 다른 Flow 이동/복사
    private const string FlowDragIdsFormat = "FlowDragIds";          // 트리 Flow → 비활성화 섹션(=비활성화) 또는 타 System 노드(=이동/복사)
    private const string FlowRestoreIdsFormat = "FlowRestoreIds";    // 비활성화 섹션 Flow → 트리(=복원)

    private Point _treeDragStartPoint;
    private Point _disabledDragStart;
    private bool _treeDragCandidate;
    private EntityNode? _pendingTreeSelectionNode;
    private EntityNode? _treeMoveDropTarget;
    private TreePaneKind _pendingTreeSelectionPane = TreePaneKind.Control;
    private MainViewModel? _boundViewModel;
    private TreePaneKind _activeTreePane = TreePaneKind.Control;
    private readonly DispatcherTimer _searchRefreshTimer;

    public ObservableCollection<EntityNode> FilteredControlTreeRoots { get; } = [];
    public ObservableCollection<EntityNode> FilteredDeviceTreeRoots { get; } = [];
    public ObservableCollection<EntityNode> FilteredDisabledFlows { get; } = [];

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
            && (node.EntityType == EntityKind.Call || node.EntityType == EntityKind.Work)
            && !ctrlPressed
            && !shiftPressed)
        {
            _treeDragCandidate = true;
            _pendingTreeSelectionNode = node;
            _pendingTreeSelectionPane = pane;
            try { _treeDragStartPoint = e.GetPosition(null); }
            catch { _treeDragStartPoint = default; }

            // 선택 확정은 drag 시작(다중 유지) 또는 mouse-up(드래그 안 했을 때 단일 선택)에 위임한다.
            // 여기서 HandleTreeItemMouseDown 으로 단일 리셋하면, 다중 선택해 둔 Call 을 끌려고
            // 누르는 순간 선택이 1개로 줄어들어 다중 드래그가 깨진다.
            ViewModel?.Selection.SetActiveTreePane(pane);
            return;
        }

        // Flow 노드: 비활성화 섹션으로 드래그하기 위한 시작점만 기록 (선택/candidate 는 기존 경로 유지).
        if (item.DataContext is EntityNode { EntityType: EntityKind.Flow } && !ctrlPressed && !shiftPressed)
        {
            try { _disabledDragStart = e.GetPosition(null); }
            catch { _disabledDragStart = default; }
        }

        ClearPendingTreeDragSelection();
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
        // Call 은 drag-candidate 경로라 HandleTreeSelectionChanged 를 안 타므로 여기서 선택 확정 + 검색 동기화.
        SelectNodeFromTreeAndSyncSearch(node, ctrlPressed: false, shiftPressed: false);
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
                () => ViewModel?.Canvas.ApplyZoomTopLeftRequested?.Invoke(doubleClickZoom),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else if (action.IsEditApiDef)
            ViewModel.EditApiDefNode(node.Id);

        e.Handled = true;
    }

    private void TreeViewItem_PreviewMouseMove_Drag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        // Flow 노드 드래그 — 비활성화 섹션(비활성화) 또는 타 System 노드(이동, Ctrl=복사)
        if (sender is TreeViewItem { DataContext: EntityNode { EntityType: EntityKind.Flow } flowNode } flowItem
            && ReferenceEquals(flowItem, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)))
        {
            var fd = e.GetPosition(null) - _disabledDragStart;
            if (Math.Abs(fd.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(fd.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                DragDrop.DoDragDrop(flowItem, new DataObject(FlowDragIdsFormat, SelectedFlowIdsForDrag(flowNode)),
                    DragDropEffects.Copy | DragDropEffects.Move);
                ClearTreeMoveDropTarget();
            }
            return;
        }

        if (!_treeDragCandidate) return;

        var pos = e.GetPosition(null);
        var diff = pos - _treeDragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (sender is not TreeViewItem { DataContext: EntityNode node } item) return;
        if (node.EntityType != EntityKind.Call && node.EntityType != EntityKind.Work) return;
        // PreviewMouseMove 는 터널링이라 부모 TreeViewItem 들도 이 핸들러를 받는다. 포인터 아래
        // 최근접 노드일 때만 drag 를 시작해야 부모 Work 가 자식 Call 의 drag 를 가로채지 않는다
        // (Work drag 추가 전에는 node!=Call return 이 부모를 걸렀으나, Work 허용 후 보호가 깨졌음).
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject))) return;

        ClearPendingTreeDragSelection();

        // 다중 선택 지원: selection 에 들어 있는 같은 종류 노드 모두 + (없으면) drag 시작 노드
        var selectedIds = ViewModel?.Selection.OrderedNodeSelection
            .Where(k => k.EntityKind == node.EntityType)
            .Select(k => k.Id)
            .ToList() ?? new System.Collections.Generic.List<Guid>();
        if (!selectedIds.Contains(node.Id))
            selectedIds = new System.Collections.Generic.List<Guid> { node.Id };

        var data = new DataObject();
        if (node.EntityType == EntityKind.Call)
        {
            data.SetData(ConditionDropHelper.DataFormat, node);
            data.SetData(TreeMoveCallNodeDataFormat, node);
            data.SetData(TreeMoveCallIdsDataFormat, selectedIds.ToArray());
        }
        else // Work → 다른 Flow 이동/복사
        {
            data.SetData(TreeMoveWorkIdsDataFormat, selectedIds.ToArray());
        }
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move);
        ClearTreeMoveDropTarget();
    }

    private void TreeViewItem_DragOver(object sender, DragEventArgs e)
    {
        // 비활성화 섹션에서 끌어온 Flow → 트리에 놓으면 복원
        if (e.Data.GetDataPresent(FlowRestoreIdsFormat))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (TryResolveCallMoveTarget(sender, e, out var sourceCallIds, out var targetWork)
            && ViewModel?.CanMoveCallsToWorkFromTree(sourceCallIds, targetWork.Id) == true)
        {
            SetTreeMoveDropTarget(targetWork);
            var copyMode = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            e.Effects = copyMode ? DragDropEffects.Copy : DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        // Work → 다른 Flow 이동/복사 (drop 대상 = Flow 노드)
        if (TryResolveWorkMoveTarget(sender, e, out var sourceWorkIds, out var targetFlow)
            && ViewModel?.CanMoveWorksToFlowFromTree(sourceWorkIds, targetFlow.Id) == true)
        {
            SetTreeMoveDropTarget(targetFlow);
            var copyMode = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            e.Effects = copyMode ? DragDropEffects.Copy : DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        // Flow → 다른 System 이동/복사 (drop 대상 = System 노드) — 멀티 PLC
        if (TryResolveFlowMoveTarget(sender, e, out var sourceFlowIds, out var targetSystem)
            && ViewModel?.CanMoveFlowsToSystemFromTree(sourceFlowIds, targetSystem.Id) == true)
        {
            SetTreeMoveDropTarget(targetSystem);
            var flowCopyMode = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            e.Effects = flowCopyMode ? DragDropEffects.Copy : DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        ClearTreeMoveDropTarget();
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void TreeViewItem_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: EntityNode node } && ReferenceEquals(node, _treeMoveDropTarget))
            ClearTreeMoveDropTarget();
    }

    private void TreeViewItem_Drop(object sender, DragEventArgs e)
    {
        // 비활성화 섹션에서 끌어온 Flow → 복원
        if (e.Data.GetDataPresent(FlowRestoreIdsFormat) && e.Data.GetData(FlowRestoreIdsFormat) is Guid[] restoreIds)
        {
            ViewModel?.RestoreFlows(restoreIds);
            e.Handled = true;
            return;
        }

        // Work → 다른 Flow 이동/복사
        if (TryResolveWorkMoveTarget(sender, e, out var sourceWorkIds, out var targetFlow))
        {
            ClearTreeMoveDropTarget();
            var workCopyMode = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (ViewModel?.TryMoveWorksToFlowFromTree(sourceWorkIds, targetFlow.Id, workCopyMode) == true)
            {
                e.Effects = workCopyMode ? DragDropEffects.Copy : DragDropEffects.Move;
                e.Handled = true;
            }
            return;
        }

        // Flow → 다른 System 이동/복사
        if (TryResolveFlowMoveTarget(sender, e, out var sourceFlowIds, out var targetSystem))
        {
            ClearTreeMoveDropTarget();
            var flowCopyMode = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (ViewModel?.TryMoveFlowsToSystemFromTree(sourceFlowIds, targetSystem.Id, flowCopyMode) == true)
            {
                e.Effects = flowCopyMode ? DragDropEffects.Copy : DragDropEffects.Move;
                e.Handled = true;
            }
            return;
        }

        if (!TryResolveCallMoveTarget(sender, e, out var sourceCallIds, out var targetWork))
            return;

        ClearTreeMoveDropTarget();
        var copyMode = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ViewModel?.TryMoveCallsToWorkFromTree(sourceCallIds, targetWork.Id, copyMode) == true)
        {
            e.Effects = copyMode ? DragDropEffects.Copy : DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private static bool TryResolveCallMoveTarget(
        object sender,
        DragEventArgs e,
        out IReadOnlyList<Guid> sourceCallIds,
        out EntityNode targetWork)
    {
        sourceCallIds = Array.Empty<Guid>();
        targetWork = null!;

        if (sender is not TreeViewItem { DataContext: EntityNode { EntityType: EntityKind.Work } workNode } item)
            return false;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)))
            return false;

        // 다중: TreeMoveCallIdsDataFormat 우선, 없으면 단일 fallback
        if (e.Data.GetDataPresent(TreeMoveCallIdsDataFormat)
            && e.Data.GetData(TreeMoveCallIdsDataFormat) is Guid[] ids && ids.Length > 0)
        {
            sourceCallIds = ids;
            targetWork = workNode;
            return true;
        }
        if (e.Data.GetDataPresent(TreeMoveCallNodeDataFormat)
            && e.Data.GetData(TreeMoveCallNodeDataFormat) is EntityNode { EntityType: EntityKind.Call } draggedCall)
        {
            sourceCallIds = new[] { draggedCall.Id };
            targetWork = workNode;
            return true;
        }
        return false;
    }

    /// <summary>Work 드래그의 drop 대상은 Flow 노드 — sender 가 Flow 이고 TreeMoveWorkIds 페이로드가 있으면 resolve.</summary>
    private static bool TryResolveWorkMoveTarget(
        object sender,
        DragEventArgs e,
        out IReadOnlyList<Guid> sourceWorkIds,
        out EntityNode targetFlow)
    {
        sourceWorkIds = Array.Empty<Guid>();
        targetFlow = null!;

        if (sender is not TreeViewItem { DataContext: EntityNode { EntityType: EntityKind.Flow } flowNode } item)
            return false;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)))
            return false;

        if (e.Data.GetDataPresent(TreeMoveWorkIdsDataFormat)
            && e.Data.GetData(TreeMoveWorkIdsDataFormat) is Guid[] ids && ids.Length > 0)
        {
            sourceWorkIds = ids;
            targetFlow = flowNode;
            return true;
        }
        return false;
    }

    /// <summary>Flow 드래그의 drop 대상은 System 노드 — sender 가 System 이고 FlowDragIds 페이로드가 있으면 resolve.</summary>
    private static bool TryResolveFlowMoveTarget(
        object sender,
        DragEventArgs e,
        out IReadOnlyList<Guid> sourceFlowIds,
        out EntityNode targetSystem)
    {
        sourceFlowIds = Array.Empty<Guid>();
        targetSystem = null!;

        if (sender is not TreeViewItem { DataContext: EntityNode { EntityType: EntityKind.System } systemNode } item)
            return false;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)))
            return false;

        if (e.Data.GetDataPresent(FlowDragIdsFormat)
            && e.Data.GetData(FlowDragIdsFormat) is Guid[] ids && ids.Length > 0)
        {
            sourceFlowIds = ids;
            targetSystem = systemNode;
            return true;
        }
        return false;
    }

    private void SetTreeMoveDropTarget(EntityNode node)
    {
        if (ReferenceEquals(_treeMoveDropTarget, node))
            return;
        ClearTreeMoveDropTarget();
        _treeMoveDropTarget = node;
        node.IsDropTarget = true;
    }

    private void ClearTreeMoveDropTarget()
    {
        if (_treeMoveDropTarget is not null)
            _treeMoveDropTarget.IsDropTarget = false;
        _treeMoveDropTarget = null;
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

        SelectNodeFromTreeAndSyncSearch(node, ctrlPressed: false, shiftPressed: false);

        if (HasActiveSearch && node.EntityType is EntityKind.System or EntityKind.Flow or EntityKind.Work)
            ViewModel.Canvas.OpenParentCanvasAndFocusNode(node.Id, node.EntityType);
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
                SelectNodeFromTreeAndSyncSearch(node, ctrlPressed: false, shiftPressed: false);
                e.Handled = true;
            }

            return;
        }

        SelectNodeFromTreeAndSyncSearch(node, ctrlPressed, shiftPressed);
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
        // 검색 중에는 화면에 filtered(클론) 트리가 떠 있다. 원본 roots 의 IsExpanded 를 바꿔봐야
        // 화면에 반영 안 되므로(펼치기/접기 먹통), 검색 중엔 filtered roots 를 대상으로 한다.
        if (HasActiveSearch)
            return _activeTreePane == TreePaneKind.Device
                ? FilteredDeviceTreeRoots
                : FilteredControlTreeRoots;
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

    // 트리 선택을 적용하고, 검색 중이면 화면에 떠 있는 클론 트리에 선택 하이라이트를 동기화한다.
    // (검색 중에는 원본이 아니라 클론 노드가 표시되므로 IsTreeSelected 동기화가 없으면 하이라이트가 안 보임)
    private void SelectNodeFromTreeAndSyncSearch(EntityNode node, bool ctrlPressed, bool shiftPressed)
    {
        if (ViewModel is null) return;
        ViewModel.Selection.SelectNodeFromTree(node, ctrlPressed, shiftPressed);
        if (HasActiveSearch)
            RefreshFilteredSelectionState();
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

    // ─── 비활성화 섹션 드래그&드롭 ───────────────────────────────────────────
    // 트리 Flow → 섹션 = 비활성화 / 섹션 Flow → 트리 = 복원. 편집 의미는 ViewModel(F#) 위임.
    private Guid[] SelectedFlowIdsForDrag(EntityNode node)
    {
        var ids = ViewModel?.Selection.OrderedNodeSelection
            .Where(k => k.EntityKind == EntityKind.Flow)
            .Select(k => k.Id)
            .ToList() ?? new List<Guid>();
        if (!ids.Contains(node.Id))
            ids = new List<Guid> { node.Id };
        return ids.ToArray();
    }

    private void DisabledSection_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(FlowDragIdsFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void DisabledSection_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(FlowDragIdsFormat) && e.Data.GetData(FlowDragIdsFormat) is Guid[] ids)
        {
            ViewModel?.DisableFlows(ids);
            e.Handled = true;
        }
    }

    private void DisabledList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { _disabledDragStart = e.GetPosition(null); }
        catch { _disabledDragStart = default; }
    }

    private void DisabledList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _disabledDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var ids = DisabledList.SelectedItems.OfType<EntityNode>().Select(n => n.Id).ToArray();
        if (ids.Length == 0) return;
        DragDrop.DoDragDrop(DisabledList, new DataObject(FlowRestoreIdsFormat, ids), DragDropEffects.Move);
    }
}
