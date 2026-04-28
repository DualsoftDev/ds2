using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class SplitCanvasContainer : UserControl
{
    private CanvasWorkspace? _secondaryWorkspace;
    private GridSplitter? _splitter;

    public SplitCanvasContainer()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private MainViewModel? VM => DataContext as MainViewModel;

    private SplitCanvasManager? Manager => VM?.CanvasManager;

    public void CenterOnNode(Guid id) => ActiveWorkspace?.CenterOnNode(id);
    public void FitToViewZoomOut() => ActiveWorkspace?.FitToViewZoomOut();
    public Point? GetViewportCenter() => ActiveWorkspace?.GetViewportCenter();

    private CanvasWorkspace? ActiveWorkspace
    {
        get
        {
            if (Manager is null) return PrimaryWorkspace;
            return Manager.ActivePane == Manager.PrimaryPane
                ? (Manager.IsPrimaryFirst ? PrimaryWorkspace : _secondaryWorkspace ?? PrimaryWorkspace)
                : (_secondaryWorkspace ?? PrimaryWorkspace);
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm)
        {
            oldVm.CanvasManager.PropertyChanged -= OnManagerPropertyChanged;
            UnwirePaneCallbacks(oldVm.CanvasManager.PrimaryPane, PrimaryWorkspace);
            UnwirePaneCallbacks(oldVm.CanvasManager.SecondaryPane, _secondaryWorkspace);
        }

        if (VM is not null)
        {
            VM.CanvasManager.PropertyChanged += OnManagerPropertyChanged;
            WirePaneCallbacks(VM.CanvasManager.PrimaryPane, PrimaryWorkspace);
            PrimaryWorkspace.Pane = VM.CanvasManager.PrimaryPane;
            RebuildLayout();
        }
    }

    private void OnManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SplitCanvasManager.SecondaryPane)
            or nameof(SplitCanvasManager.Direction)
            or nameof(SplitCanvasManager.IsPrimaryFirst)
            or nameof(SplitCanvasManager.PrimaryPane))
        {
            RebuildLayout();
        }
    }

    private void RebuildLayout()
    {
        if (Manager is null) return;

        SplitGrid.Children.Clear();
        SplitGrid.RowDefinitions.Clear();
        SplitGrid.ColumnDefinitions.Clear();

        // 항상 PrimaryWorkspace 유지
        PrimaryWorkspace.Pane = Manager.PrimaryPane;

        if (Manager.SecondaryPane is null)
        {
            // 단일 pane
            if (_secondaryWorkspace is not null)
            {
                UnwirePaneCallbacks(Manager.SecondaryPane, _secondaryWorkspace);
                _secondaryWorkspace = null;
            }
            _splitter = null;

            SplitGrid.Children.Add(PrimaryWorkspace);
            return;
        }

        // 2-pane 분할
        if (_secondaryWorkspace is null)
        {
            _secondaryWorkspace = new CanvasWorkspace();
        }
        _secondaryWorkspace.Pane = Manager.SecondaryPane;
        WirePaneCallbacks(Manager.SecondaryPane, _secondaryWorkspace);

        _splitter = new GridSplitter
        {
            Background = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var first = Manager.IsPrimaryFirst ? PrimaryWorkspace : _secondaryWorkspace;
        var second = Manager.IsPrimaryFirst ? _secondaryWorkspace : PrimaryWorkspace;

        if (Manager.Direction == SplitDirection.Horizontal)
        {
            // 좌우 분할
            SplitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            SplitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            SplitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Grid.SetColumn(first, 0);
            Grid.SetColumn(_splitter, 1);
            Grid.SetColumn(second, 2);
            _splitter.Width = 4;
        }
        else
        {
            // 상하 분할
            SplitGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            SplitGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
            SplitGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetRow(first, 0);
            Grid.SetRow(_splitter, 1);
            Grid.SetRow(second, 2);
            _splitter.Height = 4;
        }

        SplitGrid.Children.Add(first);
        SplitGrid.Children.Add(_splitter);
        SplitGrid.Children.Add(second);
    }

    private void WirePaneCallbacks(CanvasWorkspaceState pane, CanvasWorkspace workspace)
    {
        pane.CenterOnNodeRequested = workspace.CenterOnNode;
        pane.FitToViewZoomOutRequested = workspace.FitToViewZoomOut;
        pane.ApplyZoomCenteredRequested = workspace.ApplyZoomCentered;
        pane.GetViewportCenterRequested = workspace.GetViewportCenter;
        pane.GetCurrentViewRequested = workspace.GetCurrentView;
        pane.RestoreViewRequested = workspace.RestoreView;
    }

    private void UnwirePaneCallbacks(CanvasWorkspaceState? pane, CanvasWorkspace? workspace)
    {
        if (pane is null || workspace is null) return;
        pane.CenterOnNodeRequested = null;
        pane.FitToViewZoomOutRequested = null;
        pane.ApplyZoomCenteredRequested = null;
        pane.GetViewportCenterRequested = null;
        pane.GetCurrentViewRequested = null;
        pane.RestoreViewRequested = null;
    }
}
