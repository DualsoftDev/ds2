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
    private Point? _dragStartPoint;
    private CanvasTab? _dragCandidate;

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
    public void ApplyZoomCentered(double zoom) => EditorCanvasControl.ApplyZoomCentered(zoom);
    public Point? GetViewportCenter() => EditorCanvasControl.GetViewportCenter();

    private void TabHeader_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Pane is null) return;
        if (sender is FrameworkElement { DataContext: CanvasTab tab })
        {
            Pane.ActiveTab = tab;
            _dragCandidate = tab;
            _dragStartPoint = e.GetPosition(this);
        }
    }

    private void TabHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || _dragStartPoint is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { ResetDrag(); return; }

        var pos = e.GetPosition(this);
        if ((pos - _dragStartPoint.Value).Length < 5) return;

        var tab = _dragCandidate;
        ResetDrag();
        var data = new DataObject(typeof(CanvasTab), tab);
        DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
    }

    private void TabHeader_MouseUp(object sender, MouseButtonEventArgs e) => ResetDrag();

    private void ResetDrag()
    {
        _dragCandidate = null;
        _dragStartPoint = null;
    }

    private void TabBar_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(CanvasTab))) return; // 탭 드래그가 아니면 무시
        e.Effects = CanAcceptDrop(e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void TabBar_Drop(object sender, DragEventArgs e)
    {
        if (!CanAcceptDrop(e)) return;
        var tab = (CanvasTab)e.Data.GetData(typeof(CanvasTab))!;
        ViewModel?.CanvasManager.MoveTabToOtherPane(tab);
        e.Handled = true;
    }

    private bool CanAcceptDrop(DragEventArgs e)
    {
        if (ViewModel is null || Pane is null) return false;
        if (!ViewModel.CanvasManager.IsSplit) return false;
        if (!e.Data.GetDataPresent(typeof(CanvasTab))) return false;

        var tab = (CanvasTab)e.Data.GetData(typeof(CanvasTab))!;
        return !Pane.OpenTabs.Contains(tab);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (Pane is null) return;
        if (sender is FrameworkElement { Tag: CanvasTab tab })
            Pane.CloseTabCommand.Execute(tab);
    }

    private void TabContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (Pane is null || ViewModel is null || sender is not ContextMenu menu)
            return;

        var openTabCount = Pane.OpenTabs.Count;
        var canCloseOtherTabs = openTabCount > 1;
        var canSplit = openTabCount > 1 && !ViewModel.CanvasManager.IsSplit;

        if (menu.Items[1] is MenuItem closeOtherTabs)
            closeOtherTabs.IsEnabled = canCloseOtherTabs;

        if (menu.Items[4] is MenuItem splitRight)
            splitRight.IsEnabled = canSplit;

        if (menu.Items[5] is MenuItem splitDown)
            splitDown.IsEnabled = canSplit;

        if (menu.Items[6] is MenuItem splitLeft)
            splitLeft.IsEnabled = canSplit;

        if (menu.Items[7] is MenuItem splitUp)
            splitUp.IsEnabled = canSplit;
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
