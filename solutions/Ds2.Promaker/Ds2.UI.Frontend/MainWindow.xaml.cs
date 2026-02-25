using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ds2.UI.Frontend;
using Ds2.UI.Frontend.ViewModels;

namespace Ds2.UI.Frontend;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void TreeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl tabControl) return;
        _vm.SetActiveTreePane(tabControl.SelectedItem == DeviceTreeTab
            ? TreePaneKind.Device
            : TreePaneKind.Control);
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => HandleTreeSelectionChanged(ResolveTreePane(sender), e.NewValue);

    private void TreeViewItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => HandleTreeItemPreviewMouseLeftButtonDown(ResolveTreePane(sender), sender, e);

    private void TreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => HandleTreeItemPreviewMouseRightButtonDown(ResolveTreePane(sender), sender, e);

    private void TreeViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item) return;
        if (!ReferenceEquals(item, FindOwningTreeViewItem(e.OriginalSource as DependencyObject))) return;
        if (item.DataContext is not EntityNode node) return;

        if (EntityTypes.IsCanvasOpenable(node.EntityType))
        {
            _vm.OpenCanvasTab(node.Id, node.EntityType);
            e.Handled = true;
        }
        else if (EntityTypes.Is(node.EntityType, EntityTypes.ApiDef))
        {
            _vm.EditApiDefNode(node.Id);
            e.Handled = true;
        }
    }

    private void TabHeader_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CanvasTab tab })
            _vm.ActiveTab = tab;
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CanvasTab tab })
            _vm.CloseTabCommand.Execute(tab);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: CanvasTab tab })
            _vm.CloseTabCommand.Execute(tab);
    }

    private void CloseOtherTabs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: CanvasTab keepTab })
        {
            var toClose = _vm.OpenTabs.Where(t => t != keepTab).ToList();
            foreach (var t in toClose)
                _vm.CloseTabCommand.Execute(t);
        }
    }

    private void CloseAllTabs_Click(object sender, RoutedEventArgs e)
    {
        var toClose = _vm.OpenTabs.ToList();
        foreach (var t in toClose)
            _vm.CloseTabCommand.Execute(t);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedNode is null) return;

        var newName = _vm.NameEditorText.Trim();
        if (!string.IsNullOrEmpty(newName) && newName != _vm.SelectedNode.Name)
            _vm.RenameSelectedCommand.Execute(newName);
    }

    private void ApplyName_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedNode is null) return;

        var newName = _vm.NameEditorText.Trim();
        if (!string.IsNullOrEmpty(newName))
            _vm.RenameSelectedCommand.Execute(newName);
    }

    private static TreeViewItem? FindOwningTreeViewItem(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is TreeViewItem item) return item;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static TreeView? FindOwningTreeView(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is TreeView treeView) return treeView;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private TreePaneKind ResolveTreePane(object sender)
    {
        if (sender is TreeView treeView)
            return ReferenceEquals(treeView, DeviceTree) ? TreePaneKind.Device : TreePaneKind.Control;

        if (sender is DependencyObject dependencyObject && ReferenceEquals(FindOwningTreeView(dependencyObject), DeviceTree))
            return TreePaneKind.Device;

        return TreePaneKind.Control;
    }

    private void HandleTreeSelectionChanged(TreePaneKind pane, object? newValue)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None)
            return;

        _vm.SetActiveTreePane(pane);
        if (newValue is EntityNode node)
            _vm.SelectNodeFromTree(node, ctrlPressed: false, shiftPressed: false);
    }

    private void HandleTreeItemPreviewMouseLeftButtonDown(TreePaneKind pane, object sender, MouseButtonEventArgs e)
    {
        _vm.SetActiveTreePane(pane);
        if (sender is not TreeViewItem { DataContext: EntityNode node } item) return;
        if (!ReferenceEquals(item, FindOwningTreeViewItem(e.OriginalSource as DependencyObject))) return;

        var ctrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (!ctrlPressed && !shiftPressed) return;

        _vm.SelectNodeFromTree(node, ctrlPressed, shiftPressed);
        item.Focus();
        e.Handled = true;
    }

    private void HandleTreeItemPreviewMouseRightButtonDown(TreePaneKind pane, object sender, MouseButtonEventArgs e)
    {
        _vm.SetActiveTreePane(pane);
        if (sender is not TreeViewItem { DataContext: EntityNode node } item) return;
        if (!ReferenceEquals(item, FindOwningTreeViewItem(e.OriginalSource as DependencyObject))) return;

        _vm.SelectNodeFromTree(node, ctrlPressed: false, shiftPressed: false);
        item.Focus();
        e.Handled = true;
    }
}
