using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ds2.Store;
using Ds2.Editor;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class ExplorerPane : UserControl
{
    private Point _treeDragStartPoint;
    private bool _treeDragCandidate;

    public ExplorerPane()
    {
        InitializeComponent();
        ConfigureDebugPanels();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;
    internal int UpperPanelsHostRow => Grid.GetRow(UpperPanelsHost);

    private void ConfigureDebugPanels()
    {
        // History panel moved back into ExplorerPane.
        // No additional debug panel wiring is needed here.
    }

    private void TreeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl tabControl || ViewModel is null) return;
        ViewModel.Selection.SetActiveTreePane(tabControl.SelectedItem == DeviceTreeTab
            ? TreePaneKind.Device
            : TreePaneKind.Control);
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => HandleTreeSelectionChanged(ResolveTreePane(sender), e.NewValue);

    private void Tree_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeView tree)
            tree.Focus();
    }

    private void TreeViewItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: EntityNode node }
            && node.EntityType == EntityKind.Call)
        {
            _treeDragStartPoint = e.GetPosition(null);
            _treeDragCandidate = true;
        }
        else
        {
            _treeDragCandidate = false;
        }

        HandleTreeItemMouseDown(ResolveTreePane(sender), sender, e, requireModifiers: true);
    }

    private void TreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 이미 선택된 노드를 우클릭한 경우 선택 변경하지 않음 (다중 선택 유지)
        if (sender is TreeViewItem { DataContext: EntityNode node } && node.IsTreeSelected)
        {
            e.Handled = true;
            return;
        }
        HandleTreeItemMouseDown(ResolveTreePane(sender), sender, e, requireModifiers: false);
    }

    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TreeView tree || tree.ContextMenu is not { } menu) return;
        var vm = ViewModel;
        var kind = vm?.SelectedNode?.EntityType;
        var isDeviceTree = ReferenceEquals(tree, DeviceTree);
        var hasProject = vm is not null && vm.ControlTreeRoots.Count > 0;
        var hasSystem = hasProject && vm!.ControlTreeRoots.Any(p => p.Children.Count > 0);

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
            ViewModel.Canvas.OpenParentCanvasAndFocusNode(node.Id, node.EntityType, zoomOverride: doubleClickZoom);
        else if (node.EntityType is EntityKind.System or EntityKind.Flow or EntityKind.Work)
        {
            ViewModel.Canvas.OpenCanvasTab(node.Id, node.EntityType);
            ViewModel.Canvas.ApplyZoomCenteredRequested?.Invoke(doubleClickZoom);
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

        _treeDragCandidate = false;
        var data = new DataObject("ConditionCallNode", node);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    private void HandleTreeSelectionChanged(TreePaneKind pane, object? newValue)
    {
        if (ViewModel is null) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None)
            return;

        ViewModel.Selection.SetActiveTreePane(pane);
        if (newValue is not EntityNode node) return;

        ViewModel.Selection.SelectNodeFromTree(node, ctrlPressed: false, shiftPressed: false);
    }

    private void HandleTreeItemMouseDown(TreePaneKind pane, object sender, MouseButtonEventArgs e, bool requireModifiers)
    {
        if (ViewModel is null) return;
        ViewModel.Selection.SetActiveTreePane(pane);
        if (sender is not TreeViewItem { DataContext: EntityNode node } item) return;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject))) return;

        var ctrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (requireModifiers && !ctrlPressed && !shiftPressed) return;

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
        return TreeTabs.SelectedItem == DeviceTreeTab
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
}
