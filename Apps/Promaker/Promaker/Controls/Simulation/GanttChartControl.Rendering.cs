using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class GanttChartControl
{
    internal static string ResolveRowBackgroundResourceKey(GanttTimelineEntry entry)
        => entry.IsWork ? "GanttWorkRowBackgroundBrush" : "GanttCallRowBackgroundBrush";
    // ── 엘리먼트 풀 (Children.Clear() 대신 재사용) ──
    private readonly List<Rectangle> _rowBgPool = new();
    private readonly List<Line> _rowLinePool = new();
    private readonly List<Rectangle> _barPool = new();
    private readonly List<Line> _rulerTickPool = new();
    private readonly List<TextBlock> _rulerLabelPool = new();

    private void StartRendering() => _renderTimer.Start();

    private void StopRendering(bool clearVisuals = false)
    {
        _renderTimer.Stop();
        if (clearVisuals) ClearPools();
    }

    private void OnRenderTick()
    {
        if (_viewModel == null) return;
        if (_viewModel.IsRunning) _viewModel.CurrentTime = _viewModel.AdjustedNow;
        AutoScrollToCurrentTime();
        RenderAll();
    }

    /// <summary>현재 시간 빨간 라인이 뷰포트 안에 보이도록 자동 스크롤</summary>
    private void AutoScrollToCurrentTime()
    {
        if (_viewModel is not { IsRunning: true }) return;

        double currentTimeX = _viewModel.TotalDuration.TotalSeconds * _viewModel.PixelsPerSecond;
        double viewportWidth = TimelineScrollViewer.ViewportWidth;
        if (viewportWidth <= 0) return;

        double targetOffset = currentTimeX - viewportWidth * 0.8;
        if (targetOffset < 0) targetOffset = 0;

        double lineScreenX = currentTimeX - _viewModel.HorizontalOffset;
        if (lineScreenX < 0 || lineScreenX > viewportWidth)
        {
            _viewModel.HorizontalOffset = targetOffset;
            TimelineScrollViewer.ScrollToHorizontalOffset(targetOffset);
        }
    }

    private void InvalidateTimeline()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_viewModel is { IsRunning: true }) _viewModel.CurrentTime = _viewModel.AdjustedNow;
            RenderAll();
        }, DispatcherPriority.Render);
    }

    private void RenderAll()
    {
        RenderTimeline();
        RenderTimeRuler();
        UpdateCurrentTimeIndicator();
    }

    // ── 풀 헬퍼 ──

    private T GetOrCreate<T>(List<T> pool, Canvas target, int index, Func<T> factory, Action<T>? init = null)
        where T : UIElement
    {
        if (index < pool.Count)
        {
            pool[index].Visibility = Visibility.Visible;
            return pool[index];
        }
        var element = factory();
        init?.Invoke(element);
        pool.Add(element);
        target.Children.Add(element);
        return element;
    }

    private Rectangle GetOrCreateRowBg(int index)
        => GetOrCreate(_rowBgPool, TimelineCanvas, index, () => new Rectangle());

    private Line GetOrCreateRowLine(int index)
        => GetOrCreate(_rowLinePool, TimelineCanvas, index, () => new Line { StrokeThickness = 0.5 });

    private Rectangle GetOrCreateBar(int index)
        => GetOrCreate(_barPool, TimelineCanvas, index,
            () => new Rectangle { RadiusX = 2, RadiusY = 2, Cursor = Cursors.Hand },
            bar => { bar.MouseEnter += OnBarMouseEnter; bar.MouseLeave += OnBarMouseLeave; });

    private Line GetOrCreateRulerTick(int index)
        => GetOrCreate(_rulerTickPool, TimeRulerCanvas, index, () => new Line { StrokeThickness = 1 });

    private TextBlock GetOrCreateRulerLabel(int index)
        => GetOrCreate(_rulerLabelPool, TimeRulerCanvas, index, () => new TextBlock { FontSize = 9 });

    private static void HideRemaining<T>(List<T> pool, int activeCount) where T : UIElement
    {
        for (int i = activeCount; i < pool.Count; i++)
            pool[i].Visibility = Visibility.Collapsed;
    }

    /// 시뮬 리셋 시 풀 전체 정리
    internal void ClearPools()
    {
        _rowBgPool.Clear();
        _rowLinePool.Clear();
        _barPool.Clear();
        _rulerTickPool.Clear();
        _rulerLabelPool.Clear();
        TimelineCanvas.Children.Clear();
        TimeRulerCanvas.Children.Clear();
    }

    // ── 렌더링 ──

    private void RenderTimeline()
    {
        if (_viewModel == null) return;
        if (_viewModel.Entries.Count == 0)
        {
            HideRemaining(_rowBgPool, 0);
            HideRemaining(_rowLinePool, 0);
            HideRemaining(_barPool, 0);
            return;
        }

        double y = 0;
        double totalSeconds = Math.Max(_viewModel.TotalDuration.TotalSeconds, 1);
        double totalWidth = totalSeconds * _viewModel.PixelsPerSecond;
        double totalHeight = _viewModel.Entries.Where(entry => entry.IsVisible).Sum(entry => entry.RowHeight + RowGap);

        TimelineCanvas.Width = Math.Max(totalWidth + 100, TimelineScrollViewer.ActualWidth);
        TimelineCanvas.Height = Math.Max(totalHeight, TimelineScrollViewer.ActualHeight);

        var borderBrush = Application.Current.TryFindResource("BorderBrush") as Brush ?? Brushes.Gray;
        int rowIdx = 0, lineIdx = 0, barIdx = 0;

        foreach (var entry in _viewModel.Entries)
        {
            if (!entry.IsVisible) continue;
            entry.YOffset = y;
            double rowHeight = entry.RowHeight;

            var rowBg = GetOrCreateRowBg(rowIdx++);
            rowBg.Width = TimelineCanvas.Width;
            rowBg.Height = rowHeight;
            rowBg.Fill =
                Application.Current.TryFindResource(ResolveRowBackgroundResourceKey(entry)) as Brush
                ?? Brushes.Transparent;
            Canvas.SetLeft(rowBg, 0);
            Canvas.SetTop(rowBg, y);

            var rowLine = GetOrCreateRowLine(lineIdx++);
            rowLine.X1 = 0;
            rowLine.X2 = TimelineCanvas.Width;
            rowLine.Y1 = y + rowHeight;
            rowLine.Y2 = y + rowHeight;
            rowLine.Stroke = borderBrush;

            foreach (var segment in entry.Segments)
            {
                double startX = (segment.StartTime - _viewModel.StartTime).TotalSeconds * _viewModel.PixelsPerSecond;
                var segmentEndTime = segment.EndTime ?? _viewModel.CurrentTime;
                double width = (segmentEndTime - segment.StartTime).TotalSeconds * _viewModel.PixelsPerSecond;
                if (width < 2) width = 2;

                var bar = GetOrCreateBar(barIdx++);
                bar.Width = width;
                bar.Height = rowHeight - 4;
                bar.Fill = segment.StateBrush;
                bar.Tag = new BarTagInfo { Entry = entry, Segment = segment };
                Canvas.SetLeft(bar, startX);
                Canvas.SetTop(bar, y + 2);
            }

            y += rowHeight + RowGap;
        }

        HideRemaining(_rowBgPool, rowIdx);
        HideRemaining(_rowLinePool, lineIdx);
        HideRemaining(_barPool, barIdx);
    }

    private void RenderTimeRuler()
    {
        if (_viewModel == null) return;
        if (_viewModel.Entries.Count == 0)
        {
            HideRemaining(_rulerTickPool, 0);
            HideRemaining(_rulerLabelPool, 0);
            return;
        }

        double totalSeconds = Math.Max(_viewModel.TotalDuration.TotalSeconds, 1);
        double pixelsPerSecond = _viewModel.PixelsPerSecond;
        double viewportWidth = TimeRulerCanvas.ActualWidth;
        double offset = _viewModel.HorizontalOffset;

        double tickInterval = pixelsPerSecond >= 100 ? 1
            : pixelsPerSecond >= 50 ? 5
            : pixelsPerSecond >= 20 ? 10
            : pixelsPerSecond >= 10 ? 30
            : 60;
        double startSec = Math.Floor(offset / pixelsPerSecond / tickInterval) * tickInterval;
        double endSec = totalSeconds + tickInterval;

        var tickBrush = Application.Current.TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray;
        int tickIdx = 0, labelIdx = 0;

        for (double sec = startSec; sec <= endSec; sec += tickInterval)
        {
            double x = sec * pixelsPerSecond - offset;
            if (x < -50 || x > viewportWidth + 50) continue;

            var tick = GetOrCreateRulerTick(tickIdx++);
            tick.X1 = x;
            tick.Y1 = 18;
            tick.X2 = x;
            tick.Y2 = 24;
            tick.Stroke = tickBrush;

            var label = GetOrCreateRulerLabel(labelIdx++);
            label.Text = FormatTime(TimeSpan.FromSeconds(sec));
            label.Foreground = tickBrush;
            Canvas.SetLeft(label, x + 3);
            Canvas.SetTop(label, 4);
        }

        HideRemaining(_rulerTickPool, tickIdx);
        HideRemaining(_rulerLabelPool, labelIdx);
    }

    private void UpdateCurrentTimeIndicator()
    {
        if (_viewModel == null) return;
        double x = _viewModel.TotalDuration.TotalSeconds * _viewModel.PixelsPerSecond - _viewModel.HorizontalOffset;
        Canvas.SetLeft(CurrentTimeLine, x);
        CurrentTimeLine.Y2 = CurrentTimeOverlay.ActualHeight;
        CurrentTimeLine.Visibility = x >= 0 && x <= CurrentTimeOverlay.ActualWidth
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string FormatTime(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return ts.ToString(@"h\:mm\:ss");
        if (ts.TotalMinutes >= 1) return ts.ToString(@"m\:ss");
        return $"{ts.TotalSeconds:F1}s";
    }
}
