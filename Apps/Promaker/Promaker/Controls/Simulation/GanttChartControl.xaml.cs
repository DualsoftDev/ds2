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

    // 간트 표시 윈도우 프리셋(분) — 헤더 드롭다운. 선택 즉시 GanttChartState.RenderWindowMinutes 에 반영,
    // 영속화는 앱 설정(ganttWindowMinutes.txt). 순수 뷰 설정이라 PLC 설정/Agent 업로드와 무관.
    private static readonly (int Minutes, string Label)[] WindowPresets =
        { (5, "5분"), (15, "15분"), (30, "30분"), (60, "1시간"), (180, "3시간"), (300, "5시간") };
    private bool _syncingWindowPreset;

    public GanttChartControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => InvalidateTimeline();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        foreach (var (_, label) in WindowPresets)
            WindowPresetCombo.Items.Add(label);

        _renderTimer = new DispatcherTimer { Interval = RenderInterval };
        _renderTimer.Tick += (_, _) => OnRenderTick();
    }

    /// <summary>ViewModel 의 현재 윈도우 값으로 콤보 선택 동기화 — 이하 프리셋 중 가장 큰 것에 스냅.</summary>
    private void SyncWindowPresetFromViewModel()
    {
        if (_viewModel is null) return;
        var index = 0;
        for (var i = 0; i < WindowPresets.Length; i++)
            if (WindowPresets[i].Minutes <= _viewModel.RenderWindowMinutes) index = i;
        _syncingWindowPreset = true;
        try { WindowPresetCombo.SelectedIndex = index; }
        finally { _syncingWindowPreset = false; }
    }

    private void OnWindowPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingWindowPreset || _viewModel is null) return;
        var index = WindowPresetCombo.SelectedIndex;
        if (index < 0 || index >= WindowPresets.Length) return;
        var minutes = WindowPresets[index].Minutes;
        if (_viewModel.RenderWindowMinutes == minutes) return;
        _viewModel.RenderWindowMinutes = minutes;
        Presentation.AppSettingStore.SaveInt(Services.SettingsPaths.GanttWindowMinutes, minutes);
        InvalidateTimeline();   // 정지 상태에서도 즉시 반영 (실행 중엔 렌더 타이머가 반영)
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
            SyncWindowPresetFromViewModel();
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
