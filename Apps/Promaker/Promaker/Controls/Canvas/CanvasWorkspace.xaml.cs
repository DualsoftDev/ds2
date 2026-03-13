using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class CanvasWorkspace : UserControl
{
    public CanvasWorkspace()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public void CenterOnNode(Guid id) => Canvas.CenterOnNode(id);

    public Point? GetViewportCenter() => Canvas.GetViewportCenter();

    private void TabHeader_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null) return;
        if (sender is FrameworkElement { DataContext: CanvasTab tab })
            ViewModel.Canvas.ActiveTab = tab;
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (sender is FrameworkElement { Tag: CanvasTab tab })
            ViewModel.Canvas.CloseTabCommand.Execute(tab);
    }

    private void CloseOtherTabs_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (sender is MenuItem { Tag: CanvasTab keepTab })
            CloseTabs(ViewModel.Canvas.OpenTabs.Where(t => t != keepTab));
    }

    private void CloseAllTabs_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        CloseTabs(ViewModel.Canvas.OpenTabs);
    }

    private void CloseTabs(IEnumerable<CanvasTab> tabs)
    {
        if (ViewModel is null) return;
        foreach (var tab in tabs.ToList())
            ViewModel.Canvas.CloseTabCommand.Execute(tab);
    }
}
