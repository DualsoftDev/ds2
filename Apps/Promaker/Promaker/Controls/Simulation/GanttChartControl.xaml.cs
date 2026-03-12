using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class GanttChartControl : UserControl
{
    private GanttChartState? _viewModel;
    private readonly DispatcherTimer _renderTimer;

    private const double ZoomStep = 1.2;
    private const double RowGap = 2;

    private bool _isPanning;
    private Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private bool _isSyncingScroll;
    private DateTime _lastRowClickTime = DateTime.MinValue;
    private GanttTimelineEntry? _lastClickedEntry;

    public GanttChartControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => InvalidateTimeline();

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _renderTimer.Tick += (_, _) => OnRenderTick();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.Entries.CollectionChanged -= OnEntriesChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as GanttChartState;

        if (_viewModel != null)
        {
            _viewModel.Entries.CollectionChanged += OnEntriesChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => InvalidateTimeline();

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GanttChartState.IsRunning)) return;
        if (_viewModel?.IsRunning == true) StartRendering();
        else StopRendering();
    }
}
