using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ds2.UI.Core;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class ExplorerPane : UserControl
{
    private Point _treeDragStartPoint;
    private bool _treeDragCandidate;

    public ExplorerPane()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void TreeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl tabControl || ViewModel is null) return;
        ViewModel.SetActiveTreePane(tabControl.SelectedItem == DeviceTreeTab
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
        => HandleTreeItemMouseDown(ResolveTreePane(sender), sender, e, requireModifiers: false);

    private void HistoryListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null) return;
        if (HistoryListBox.SelectedItem is HistoryPanelItem item)
            ViewModel.JumpToHistoryCommand.Execute(item);
    }

    private void TreeViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null) return;
        if (sender is not TreeViewItem item) return;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject))) return;
        if (item.DataContext is not EntityNode node) return;

        if (node.EntityType == EntityKind.Call)
        {
            ViewModel.OpenParentCanvasAndFocusNode(node.Id, node.EntityType);
            e.Handled = true;
        }
        else if (EntityTypes.IsCanvasOpenable(node.EntityType))
        {
            ViewModel.OpenCanvasTab(node.Id, node.EntityType);
            item.IsExpanded = !item.IsExpanded;
            e.Handled = true;
        }
        else if (node.EntityType == EntityKind.ApiDef)
        {
            ViewModel.EditApiDefNode(node.Id);
            e.Handled = true;
        }
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

        ViewModel.SetActiveTreePane(pane);
        if (newValue is EntityNode node)
            ViewModel.SelectNodeFromTree(node, ctrlPressed: false, shiftPressed: false);
    }

    private void HandleTreeItemMouseDown(TreePaneKind pane, object sender, MouseButtonEventArgs e, bool requireModifiers)
    {
        if (ViewModel is null) return;
        ViewModel.SetActiveTreePane(pane);
        if (sender is not TreeViewItem { DataContext: EntityNode node } item) return;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject))) return;

        var ctrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (requireModifiers && !ctrlPressed && !shiftPressed) return;

        ViewModel.SelectNodeFromTree(node, ctrlPressed, shiftPressed);
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
