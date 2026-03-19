using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class CanvasWorkspace : UserControl, INotifyPropertyChanged
{
    private CanvasWorkspaceState? _pane;

    public CanvasWorkspace()
    {
        InitializeComponent();
    }

    /// <summary>이 workspace가 표시하는 pane입니다.</summary>
    public CanvasWorkspaceState? Pane
    {
        get => _pane;
        set
        {
            if (_pane == value) return;
            _pane = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pane)));
            EditorCanvasControl.Pane = value;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public void CenterOnNode(Guid id) => EditorCanvasControl.CenterOnNode(id);
    public void FitToViewZoomOut() => EditorCanvasControl.FitToViewZoomOut();
    public Point? GetViewportCenter() => EditorCanvasControl.GetViewportCenter();

    private void TabHeader_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Pane is null) return;
        if (sender is FrameworkElement { DataContext: CanvasTab tab })
            Pane.ActiveTab = tab;
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (Pane is null) return;
        if (sender is FrameworkElement { Tag: CanvasTab tab })
            Pane.CloseTabCommand.Execute(tab);
    }

    private void CloseOtherTabs_Click(object sender, RoutedEventArgs e)
    {
        if (Pane is null) return;
        if (sender is MenuItem { Tag: CanvasTab keepTab })
            CloseTabs(Pane.OpenTabs.Where(t => t != keepTab));
    }

    private void CloseAllTabs_Click(object sender, RoutedEventArgs e)
    {
        if (Pane is null) return;
        CloseTabs(Pane.OpenTabs);
    }

    private void CloseTabs(IEnumerable<CanvasTab> tabs)
    {
        if (Pane is null) return;
        foreach (var tab in tabs.ToList())
            Pane.CloseTabCommand.Execute(tab);
    }

    private void SplitRight_Click(object sender, RoutedEventArgs e) => SplitTab(sender, SplitSide.Right);
    private void SplitDown_Click(object sender, RoutedEventArgs e) => SplitTab(sender, SplitSide.Down);
    private void SplitLeft_Click(object sender, RoutedEventArgs e) => SplitTab(sender, SplitSide.Left);
    private void SplitUp_Click(object sender, RoutedEventArgs e) => SplitTab(sender, SplitSide.Up);

    private void SplitTab(object sender, SplitSide side)
    {
        if (ViewModel is null) return;
        if (sender is FrameworkElement { Tag: CanvasTab tab })
            ViewModel.CanvasManager.SplitTab(tab, side);
    }

    private void OnPaneFocused(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && Pane is not null)
            ViewModel.CanvasManager.ActivePane = Pane;
    }
}
