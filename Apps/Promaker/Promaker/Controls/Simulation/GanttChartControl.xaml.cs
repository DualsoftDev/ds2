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
    internal static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(33);

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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        _renderTimer = new DispatcherTimer { Interval = RenderInterval };
        _renderTimer.Tick += (_, _) => OnRenderTick();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // TabControl 탭 전환 시 재구독 + 렌더 타이머 복원
        if (_viewModel != null)
        {
            _viewModel.Entries.CollectionChanged -= OnEntriesChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Entries.CollectionChanged += OnEntriesChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            if (_viewModel.IsRunning) StartRendering();
            else InvalidateTimeline();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _renderTimer.Stop();
        if (_viewModel != null)
        {
            _viewModel.Entries.CollectionChanged -= OnEntriesChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
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
        if (_viewModel?.IsRunning == true)
        {
            StartRendering();
        }
        else
        {
            StopRendering();
            // STEP 끝나는 시점 등 IsRunning=false 진입 시 마지막 한 번 render —
            // 마지막 advance 직전 보간값에서 빨간선이 멈춰있던 잔여 정정.
            InvalidateTimeline();
        }
    }
}
