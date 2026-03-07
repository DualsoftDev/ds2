using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Promaker.ViewModels;

namespace Promaker;

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
        => HandleTreeItemMouseDown(ResolveTreePane(sender), sender, e, requireModifiers: true);

    private void TreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => HandleTreeItemMouseDown(ResolveTreePane(sender), sender, e, requireModifiers: false);

    private void HistoryListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryListBox.SelectedItem is HistoryPanelItem item)
            _vm.JumpToHistoryCommand.Execute(item);
    }

    private void TreeViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item) return;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject))) return;
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

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CanvasTab tab })
            _vm.CloseTabCommand.Execute(tab);
    }

    private void CloseOtherTabs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: CanvasTab keepTab })
            CloseTabs(_vm.OpenTabs.Where(t => t != keepTab));
    }

    private void CloseAllTabs_Click(object sender, RoutedEventArgs e)
    {
        CloseTabs(_vm.OpenTabs);
    }

    private void CloseTabs(IEnumerable<CanvasTab> tabs)
    {
        foreach (var t in tabs.ToList())
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

    private TreePaneKind ResolveTreePane(object sender)
    {
        if (sender is TreeView treeView)
            return ReferenceEquals(treeView, DeviceTree) ? TreePaneKind.Device : TreePaneKind.Control;

        if (sender is DependencyObject dependencyObject && ReferenceEquals(FindAncestor<TreeView>(dependencyObject), DeviceTree))
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

    private void HandleTreeItemMouseDown(TreePaneKind pane, object sender, MouseButtonEventArgs e, bool requireModifiers)
    {
        _vm.SetActiveTreePane(pane);
        if (sender is not TreeViewItem { DataContext: EntityNode node } item) return;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject))) return;

        var ctrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (requireModifiers && !ctrlPressed && !shiftPressed) return;

        _vm.SelectNodeFromTree(node, ctrlPressed, shiftPressed);
        item.Focus();
        e.Handled = true;
    }
}
