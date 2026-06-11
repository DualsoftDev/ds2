using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class GanttChartControl
{
    // ViewModel 과 ScrollViewer 의 수평 오프셋을 함께 갱신해
    // 점선(Overlay)과 간트 바(ScrollViewer 내부) 좌표계를 어긋나지 않게 한다.
    private void ApplyHorizontalOffset(double offset)
    {
        if (_viewModel == null) return;
        double clamped = Math.Max(0, offset);
        _viewModel.HorizontalOffset = clamped;
        TimelineScrollViewer.ScrollToHorizontalOffset(clamped);
    }

    private void OnTimelineMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewModel == null) return;

        // Shift+휠 = 시간축 확대/축소. Shift 없는 일반 휠은 세로 스크롤(ScrollViewer 기본 동작)에 위임.
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
            return;

        // GetPosition(TimelineCanvas) 는 canvas 의 내부 좌표계(= 절대 좌표) 반환.
        // HorizontalOffset 을 더하면 이중 가산이 되므로 mousePos.X 자체가 절대 위치.
        var mousePos = e.GetPosition(TimelineCanvas);
        double oldPixelsPerSecond = _viewModel.PixelsPerSecond;
        double oldHorizontalOffset = _viewModel.HorizontalOffset;
        double timeAtMouse = mousePos.X / oldPixelsPerSecond;

        double factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
        double newPixelsPerSecond = Math.Clamp(
            oldPixelsPerSecond * factor,
            GanttChartState.MinPixelsPerSecond,
            GanttChartState.MaxPixelsPerSecond);

        _viewModel.PixelsPerSecond = newPixelsPerSecond;

        // PixelsPerSecond 변경을 Canvas.Width 에 즉시 반영해 ExtentWidth 를 확정.
        // 비동기 InvalidateTimeline 을 먼저 호출하면 ScrollToHorizontalOffset 가
        // 이전 ExtentWidth 기준으로 clamp 되어 점선과 바가 어긋난다.
        RenderTimeline();
        TimelineScrollViewer.UpdateLayout();

        // 뷰포트 내 마우스 상대 위치(= mousePos.X - oldHorizontalOffset) 를 유지하도록
        // 새 HorizontalOffset 계산.
        double viewportX = mousePos.X - oldHorizontalOffset;
        ApplyHorizontalOffset(timeAtMouse * newPixelsPerSecond - viewportX);

        RenderTimeRuler();
        UpdateCurrentTimeIndicator();
        e.Handled = true;
    }

    private void OnTimelineMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _isPanning = true;
            _panStartPoint = e.GetPosition(this);
            _panStartHorizontalOffset = _viewModel?.HorizontalOffset ?? 0;
            TimelineCanvas.CaptureMouse();
            Cursor = Cursors.Hand;
            e.Handled = true;
        }
    }

    private void OnTimelineMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning || _viewModel == null) return;
        double deltaX = _panStartPoint.X - e.GetPosition(this).X;
        ApplyHorizontalOffset(_panStartHorizontalOffset + deltaX);
        InvalidateTimeline();
    }

    private void OnTimelineMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        TimelineCanvas.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    private void OnTimelineScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_viewModel == null || _isSyncingScroll) return;

        // 사용자 가로 스크롤(휠/팬/스크롤바) — 빨간선이 뷰포트 밖으로 나가면 따라가기 해제(과거 탐색),
        // 빨간선이 보이는 위치로 돌아오면 재개. 자동 스크롤(_isAutoScrolling)은 판정 제외.
        if (!_isAutoScrolling && e.HorizontalChange != 0 && _viewModel.IsRunning)
        {
            double lineScreenX =
                _viewModel.TotalDuration.TotalSeconds * _viewModel.PixelsPerSecond - e.HorizontalOffset;
            _followCurrentTime = lineScreenX >= 0 && lineScreenX <= e.ViewportWidth;
        }

        try
        {
            _isSyncingScroll = true;
            LabelScrollViewer.ScrollToVerticalOffset(e.VerticalOffset);
            _viewModel.HorizontalOffset = e.HorizontalOffset;
            _viewModel.VerticalOffset = e.VerticalOffset;
            RenderAll();
        }
        finally
        {
            _isSyncingScroll = false;
        }
    }

    private void OnLabelScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_viewModel == null || _isSyncingScroll) return;

        try
        {
            _isSyncingScroll = true;
            TimelineScrollViewer.ScrollToVerticalOffset(e.VerticalOffset);
            _viewModel.VerticalOffset = e.VerticalOffset;
        }
        finally
        {
            _isSyncingScroll = false;
        }
    }

    private void OnRowLabelMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel == null) return;
        if (sender is not FrameworkElement { Tag: GanttTimelineEntry entry }) return;
        if (!entry.HasChildren) return;   // Work/Call 만 접기-펴기 토글 (ApiCall 은 leaf)

        // 행 라벨 단일 클릭으로 접기-펴기 (아이콘 클릭과 동일 동작 — 더블클릭 요구 제거).
        _viewModel.SetExpanded(entry.Id, !entry.IsExpanded);
        InvalidateTimeline();
        e.Handled = true;
    }

    // ▼/▶ 아이콘 단일 클릭으로 접기-펴기 (Work/Call).
    private void OnToggleIconClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel == null) return;
        if (sender is FrameworkElement { DataContext: GanttTimelineEntry entry } && entry.HasChildren)
        {
            _viewModel.SetExpanded(entry.Id, !entry.IsExpanded);
            InvalidateTimeline();
            e.Handled = true;
        }
    }

    // 우클릭 컨텍스트 메뉴 — 전체 Call/Work 접기·펴기.
    private void OnExpandAllCalls(object sender, RoutedEventArgs e) => ApplyExpandAll(GanttRowKind.Call, true);
    private void OnCollapseAllCalls(object sender, RoutedEventArgs e) => ApplyExpandAll(GanttRowKind.Call, false);
    private void OnExpandAllWorks(object sender, RoutedEventArgs e) => ApplyExpandAll(GanttRowKind.Work, true);
    private void OnCollapseAllWorks(object sender, RoutedEventArgs e) => ApplyExpandAll(GanttRowKind.Work, false);

    private void ApplyExpandAll(GanttRowKind kind, bool expanded)
    {
        if (_viewModel == null) return;
        _viewModel.ExpandAll(kind, expanded);
        InvalidateTimeline();
    }
}
