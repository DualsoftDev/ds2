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
    internal enum GanttSegmentRenderKind
    {
        Filled,
        VirtualAppendOutline,
        OutputAppendLine,
        OutputAppendEnd
    }

    internal readonly record struct GanttSegmentRenderPart(
        GanttSegmentRenderKind Kind,
        DateTime StartTime,
        DateTime EndTime);

    /// <summary>plan overlay 배경 바 구간 — Going 시작 시점부터 plan(BaseDurationMs) 만큼.</summary>
    internal readonly record struct GanttPlanOverlayPart(DateTime StartTime, DateTime EndTime);

    /// <summary>plan overlay (비-Simulation 모드) — Going 세그먼트의 plan 배경 바 구간. 대상 아니면 null.</summary>
    internal static GanttPlanOverlayPart? ResolvePlanOverlayPart(GanttTimelineEntry entry, GanttStateSegment segment)
    {
        if (segment.State != Ds2.Core.Status4.Going) return null;
        if (entry.BaseDurationMs is not { } durationMs || durationMs <= 0) return null;
        return new GanttPlanOverlayPart(segment.StartTime, segment.StartTime.AddMilliseconds(durationMs));
    }

    /// <summary>plan overlay 모드에서 actual 상태 막대 높이 — 배경(plan) 바 안에 얇게 가운데.</summary>
    internal const double PlanOverlayActualBarHeight = 8;

    internal static double ResolveBarHeight(double rowHeight, bool planOverlay)
        => planOverlay ? PlanOverlayActualBarHeight : rowHeight - 4;

    internal static double ResolveBarTop(double rowY, double rowHeight, double barHeight)
        => rowY + (rowHeight - barHeight) / 2.0;

    internal static string ResolveRowBackgroundResourceKey(GanttTimelineEntry entry)
        => entry.IsWork ? "GanttWorkRowBackgroundBrush" : "GanttCallRowBackgroundBrush";
    // ── 엘리먼트 풀 (Children.Clear() 대신 재사용) ──
    private readonly List<Rectangle> _rowBgPool = new();
    private readonly List<Line> _rowLinePool = new();
    private readonly List<Rectangle> _barPool = new();
    private readonly List<Rectangle> _planBarPool = new();
    private readonly List<Border> _virtualAppendPool = new();
    private readonly List<Line> _outputAppendLinePool = new();
    private readonly List<Line> _outputAppendEndPool = new();
    private readonly List<Line> _rulerTickPool = new();
    private readonly List<TextBlock> _rulerLabelPool = new();

    // 빨간선(현재 시각) 따라가기 — 사용자가 과거를 보러 수동 스크롤하면 해제,
    // 빨간선이 보이는 위치로 돌아오면 재개. PLAY 시 재개.
    private bool _followCurrentTime = true;
    private bool _isAutoScrolling;

    private void StartRendering()
    {
        _followCurrentTime = true;
        _renderTimer.Start();
    }

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

    /// <summary>현재 시간 빨간 라인이 뷰포트 안에 보이도록 자동 스크롤.
    /// 사용자가 과거를 보러 수동 스크롤하면(_followCurrentTime=false) 따라가지 않는다 — 정지 없이 과거 조회 가능.</summary>
    private void AutoScrollToCurrentTime()
    {
        if (_viewModel is not { IsRunning: true }) return;
        if (!_followCurrentTime) return;

        double currentTimeX = _viewModel.TotalDuration.TotalSeconds * _viewModel.PixelsPerSecond;
        double viewportWidth = TimelineScrollViewer.ViewportWidth;
        if (viewportWidth <= 0) return;

        double targetOffset = currentTimeX - viewportWidth * 0.8;
        if (targetOffset < 0) targetOffset = 0;

        double lineScreenX = currentTimeX - TimelineScrollViewer.HorizontalOffset;
        if (lineScreenX < 0 || lineScreenX > viewportWidth)
        {
            _isAutoScrolling = true;
            try { ApplyHorizontalOffset(targetOffset); }
            finally { _isAutoScrolling = false; }
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
            bar => { bar.MouseEnter += OnBarMouseEnter; bar.MouseLeave += OnBarMouseLeave; Panel.SetZIndex(bar, 10); });

    /// plan overlay 배경 바 — actual 바(ZIndex 10) 아래, 행 배경 위.
    private Rectangle GetOrCreatePlanBar(int index)
        => GetOrCreate(_planBarPool, TimelineCanvas, index,
            () => new Rectangle { RadiusX = 2, RadiusY = 2, Cursor = Cursors.Hand },
            bar => { bar.MouseEnter += OnBarMouseEnter; bar.MouseLeave += OnBarMouseLeave; Panel.SetZIndex(bar, 5); });

    private Border GetOrCreateVirtualAppend(int index)
        => GetOrCreate(_virtualAppendPool, TimelineCanvas, index,
            () => new Border
            {
                Background = Brushes.Transparent,
                BorderThickness = ResolveVirtualAppendBorderThickness(),
                CornerRadius = ResolveVirtualAppendCornerRadius(),
                Cursor = Cursors.Hand
            },
            border => { border.MouseEnter += OnBarMouseEnter; border.MouseLeave += OnBarMouseLeave; });

    internal static CornerRadius ResolveVirtualAppendCornerRadius() => new(0, 2, 2, 0);

    internal static Thickness ResolveVirtualAppendBorderThickness() => new(0, 1.5, 1.5, 1.5);

    private Line GetOrCreateOutputAppendLine(int index)
        => GetOrCreate(_outputAppendLinePool, TimelineCanvas, index,
            () => new Line { StrokeThickness = 2, Cursor = Cursors.Hand, StrokeDashArray = new DoubleCollection { 5, 4 } },
            line => { line.MouseEnter += OnBarMouseEnter; line.MouseLeave += OnBarMouseLeave; Panel.SetZIndex(line, 50); });

    private Line GetOrCreateOutputAppendEnd(int index)
        => GetOrCreate(_outputAppendEndPool, TimelineCanvas, index,
            () => new Line { StrokeThickness = 2, Cursor = Cursors.Hand },
            line => { line.MouseEnter += OnBarMouseEnter; line.MouseLeave += OnBarMouseLeave; Panel.SetZIndex(line, 50); });

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
        _planBarPool.Clear();
        _virtualAppendPool.Clear();
        _outputAppendLinePool.Clear();
        _outputAppendEndPool.Clear();
        _rulerTickPool.Clear();
        _rulerLabelPool.Clear();
        TimelineCanvas.Children.Clear();
        TimeRulerCanvas.Children.Clear();
    }

    internal static IReadOnlyList<GanttSegmentRenderPart> ResolveSegmentRenderParts(
        GanttTimelineEntry entry,
        GanttStateSegment segment,
        DateTime visibleEndTime)
    {
        if (visibleEndTime <= segment.StartTime) return [];

        var parts = new List<GanttSegmentRenderPart>(capacity: 4);
        var segmentEnd = visibleEndTime;
        var virtualAppendMs = entry.VirtualAppendMs;
        var hasVirtualAppend = virtualAppendMs > 0 && segment.State == Ds2.Core.Status4.Going;

        if (!hasVirtualAppend)
        {
            parts.Add(new GanttSegmentRenderPart(GanttSegmentRenderKind.Filled, segment.StartTime, segmentEnd));
        }
        else
        {
            var appendStart =
                entry.BaseDurationMs is { } durationMs
                    ? segment.StartTime.AddMilliseconds(durationMs)
                    : segment.EndTime is { } fixedEnd
                        ? fixedEnd.AddMilliseconds(-virtualAppendMs)
                        : segmentEnd;

            if (appendStart > segment.StartTime)
            {
                var filledEnd = appendStart < segmentEnd ? appendStart : segmentEnd;
                if (filledEnd > segment.StartTime)
                    parts.Add(new GanttSegmentRenderPart(GanttSegmentRenderKind.Filled, segment.StartTime, filledEnd));
            }

            if (segmentEnd > appendStart)
                parts.Add(new GanttSegmentRenderPart(GanttSegmentRenderKind.VirtualAppendOutline, appendStart, segmentEnd));
        }

        // timeAppend(출력 유지) 빨간 점선은 Call/Work 줄이 아니라 device I/O 줄(ApiCall 아래줄)에 그린다 → RenderApiCallSubRow 참고.

        return parts;
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
            HideRemaining(_planBarPool, 0);
            HideRemaining(_virtualAppendPool, 0);
            HideRemaining(_outputAppendLinePool, 0);
            HideRemaining(_outputAppendEndPool, 0);
            return;
        }

        double y = 0;
        double totalSeconds = Math.Max(_viewModel.TimelineDuration.TotalSeconds, 1);
        double totalWidth = totalSeconds * _viewModel.PixelsPerSecond;
        double totalHeight = _viewModel.Entries.Where(entry => entry.IsVisible).Sum(entry => entry.RowHeight + RowGap);

        TimelineCanvas.Width = Math.Max(totalWidth + 100, TimelineScrollViewer.ActualWidth);
        TimelineCanvas.Height = Math.Max(totalHeight, TimelineScrollViewer.ActualHeight);

        var borderBrush = Application.Current.TryFindResource("BorderBrush") as Brush ?? Brushes.Gray;
        int rowIdx = 0, lineIdx = 0, barIdx = 0, planBarIdx = 0, virtualAppendIdx = 0, outputAppendLineIdx = 0, outputAppendEndIdx = 0;
        var outputAppendBrush = new SolidColorBrush(Color.FromRgb(242, 100, 43));
        bool planOverlay = _viewModel.ShowPlanOverlay;
        var planOverlayBrush = Application.Current.TryFindResource("GanttPlanOverlayBrush") as Brush
            ?? new SolidColorBrush(Color.FromArgb(0x2E, 0xD4, 0x88, 0x3A));

        // viewport culling — 장시간 운전 시 segment 가 누적되어도 화면 밖은 Rectangle 안 만든다.
        double scrollOffset = TimelineScrollViewer.HorizontalOffset;
        double viewportPx = TimelineScrollViewer.ViewportWidth > 0
            ? TimelineScrollViewer.ViewportWidth
            : TimelineScrollViewer.ActualWidth;
        const double CullMargin = 100;
        double cullLeft = scrollOffset - CullMargin;
        double cullRight = scrollOffset + viewportPx + CullMargin;

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

            if (entry.IsApiCall)
            {
                // ApiCall — 한 행을 위(Plan=Segments)/아래(I/O=IoSegments) 2줄로.
                double subH = entry.SubRowHeight;

                // plan overlay — PLN 줄 Going 구간에 plan duration 배경 틀 (subrow 는 얇아서 actual 바 두께는 유지).
                if (planOverlay)
                {
                    bool firstGoingSkipped = !_viewModel.SuppressFirstGoingPlanOverlay;
                    foreach (var segment in entry.Segments)
                    {
                        if (ResolvePlanOverlayPart(entry, segment) is not { } plan) continue;
                        if (!firstGoingSkipped) { firstGoingSkipped = true; continue; }   // 합류 사이클 — 시작점이 가짜
                        double pStartX = (plan.StartTime - _viewModel.StartTime).TotalSeconds * _viewModel.PixelsPerSecond;
                        double pWidth = (plan.EndTime - plan.StartTime).TotalSeconds * _viewModel.PixelsPerSecond;
                        if (pWidth < 1 || pStartX + pWidth < cullLeft || pStartX > cullRight) continue;
                        var planBar = GetOrCreatePlanBar(planBarIdx++);
                        planBar.Width = pWidth;
                        planBar.Height = subH - 2;
                        planBar.Fill = planOverlayBrush;
                        planBar.Tag = new BarTagInfo { Entry = entry, Segment = segment };
                        Canvas.SetLeft(planBar, pStartX);
                        Canvas.SetTop(planBar, y + 1);
                    }
                }

                RenderApiCallSubRow(entry, entry.Segments, y, subH, ref barIdx, cullLeft, cullRight, showReady: true,
                    drawOutputAppend: false, ref outputAppendLineIdx, ref outputAppendEndIdx, outputAppendBrush);    // PLN: R/G/F/H 전부
                RenderApiCallSubRow(entry, entry.OutSegments, y + subH, subH, ref barIdx, cullLeft, cullRight, showReady: false,
                    drawOutputAppend: false, ref outputAppendLineIdx, ref outputAppendEndIdx, outputAppendBrush); // I/O Out(주황)
                RenderApiCallSubRow(entry, entry.InSegments, y + subH, subH, ref barIdx, cullLeft, cullRight, showReady: false,
                    drawOutputAppend: true, ref outputAppendLineIdx, ref outputAppendEndIdx, outputAppendBrush);  // I/O In(파랑) + In 시작 timeAppend 점선
            }
            else
            {
            bool firstGoingPlanSkipped = !_viewModel.SuppressFirstGoingPlanOverlay;
            foreach (var segment in entry.Segments)
            {
                var segmentEndTime = segment.EndTime ?? _viewModel.CurrentTime;

                // plan overlay (비-Simulation): Going 시작부터 plan duration 만큼 행을 채우는 약한 배경 바.
                // actual 바가 이 틀보다 짧으면 빨랐던 것, 뚫고 나가면 느린 것, 틀만 있고 actual 이 없으면 추정(coast) 구간.
                if (planOverlay && ResolvePlanOverlayPart(entry, segment) is { } plan)
                {
                    if (!firstGoingPlanSkipped)
                    {
                        // 신호 유추 모드(VP/Monitoring)의 첫 Going = 사이클 중간 합류 — 시작점이 가짜라 틀 생략.
                        firstGoingPlanSkipped = true;
                    }
                    else
                    {
                        double pStartX = (plan.StartTime - _viewModel.StartTime).TotalSeconds * _viewModel.PixelsPerSecond;
                        double pWidth = (plan.EndTime - plan.StartTime).TotalSeconds * _viewModel.PixelsPerSecond;
                        if (pWidth >= 1 && pStartX + pWidth >= cullLeft && pStartX <= cullRight)
                        {
                            var planBar = GetOrCreatePlanBar(planBarIdx++);
                            planBar.Width = pWidth;
                            planBar.Height = rowHeight - 4;
                            planBar.Fill = planOverlayBrush;
                            planBar.Tag = new BarTagInfo { Entry = entry, Segment = segment };
                            Canvas.SetLeft(planBar, pStartX);
                            Canvas.SetTop(planBar, y + 2);
                        }
                    }
                }

                double barHeight = ResolveBarHeight(rowHeight, planOverlay);
                double barTop = ResolveBarTop(y, rowHeight, barHeight);

                foreach (var part in ResolveSegmentRenderParts(entry, segment, segmentEndTime))
                {
                    double startX = (part.StartTime - _viewModel.StartTime).TotalSeconds * _viewModel.PixelsPerSecond;
                    double width = (part.EndTime - part.StartTime).TotalSeconds * _viewModel.PixelsPerSecond;
                    if (width < 2) width = 2;

                    if (startX + width < cullLeft) continue;
                    if (startX > cullRight) continue;

                    if (part.Kind == GanttSegmentRenderKind.VirtualAppendOutline)
                    {
                        var border = GetOrCreateVirtualAppend(virtualAppendIdx++);
                        border.Width = width;
                        border.Height = barHeight;
                        border.BorderBrush = segment.StateBrush;
                        border.Tag = new BarTagInfo { Entry = entry, Segment = segment };
                        Canvas.SetLeft(border, startX);
                        Canvas.SetTop(border, barTop);
                    }
                    else
                    {
                        var bar = GetOrCreateBar(barIdx++);
                        bar.Width = width;
                        bar.Height = barHeight;
                        bar.Fill = segment.StateBrush;
                        bar.Stroke = null;
                        bar.StrokeThickness = 0;
                        bar.Tag = new BarTagInfo { Entry = entry, Segment = segment };
                        Canvas.SetLeft(bar, startX);
                        Canvas.SetTop(bar, barTop);
                    }
                }
            }
            }

            y += rowHeight + RowGap;
        }

        HideRemaining(_rowBgPool, rowIdx);
        HideRemaining(_rowLinePool, lineIdx);
        HideRemaining(_barPool, barIdx);
        HideRemaining(_planBarPool, planBarIdx);
        HideRemaining(_virtualAppendPool, virtualAppendIdx);
        HideRemaining(_outputAppendLinePool, outputAppendLineIdx);
        HideRemaining(_outputAppendEndPool, outputAppendEndIdx);
    }

    /// <summary>ApiCall 한 줄(Plan 또는 I/O) 막대 렌더 — Ready(빈 구간)는 skip.
    /// drawOutputAppend=true(I/O 줄)면 Out(=Going) high 끝에 timeAppend(출력 유지) 빨간 점선을 그린다.</summary>
    private void RenderApiCallSubRow(
        GanttTimelineEntry entry,
        System.Collections.Generic.IEnumerable<GanttStateSegment> segments,
        double yTop, double subH, ref int barIdx, double cullLeft, double cullRight,
        bool showReady, bool drawOutputAppend,
        ref int outputAppendLineIdx, ref int outputAppendEndIdx, Brush outputAppendBrush)
    {
        if (_viewModel == null) return;
        foreach (var segment in segments)
        {
            if (!showReady && segment.State == Ds2.Core.Status4.Ready) continue;
            var endTime = segment.EndTime ?? _viewModel.CurrentTime;
            double startX = (segment.StartTime - _viewModel.StartTime).TotalSeconds * _viewModel.PixelsPerSecond;
            double width = (endTime - segment.StartTime).TotalSeconds * _viewModel.PixelsPerSecond;
            if (width < 2) width = 2;
            if (startX + width < cullLeft || startX > cullRight) continue;

            var bar = GetOrCreateBar(barIdx++);
            bar.Width = width;
            bar.Height = Math.Max(2, subH - 3);
            bar.Fill = segment.StateBrush;
            bar.Stroke = null;
            bar.StrokeThickness = 0;
            bar.Tag = new BarTagInfo { Entry = entry, Segment = segment };
            Canvas.SetLeft(bar, startX);
            Canvas.SetTop(bar, yTop + 1);

            // device I/O 줄: In(센서=완료 신호) 들어온 시점부터 timeAppend(출력 유지) 만큼 빨간 점선.
            // (출력 유지는 device 가 In 을 받은 뒤에도 Out 을 더 끄지 않고 유지하는 구간이므로 In on 부터 그린다.)
            if (drawOutputAppend && entry.OutputAppendMs > 0
                && segment.State == Ds2.Core.Status4.Finish)
            {
                var inOn = segment.StartTime;
                var outputEnd = inOn.AddMilliseconds(entry.OutputAppendMs);
                double oStartX = (inOn - _viewModel.StartTime).TotalSeconds * _viewModel.PixelsPerSecond;
                double oWidth = (outputEnd - inOn).TotalSeconds * _viewModel.PixelsPerSecond;
                if (oWidth >= 1 && oStartX + oWidth >= cullLeft && oStartX <= cullRight)
                {
                    double yMid = yTop + subH / 2.0;
                    var line = GetOrCreateOutputAppendLine(outputAppendLineIdx++);
                    line.X1 = oStartX; line.X2 = oStartX + oWidth;
                    line.Y1 = yMid; line.Y2 = yMid;
                    line.Stroke = outputAppendBrush;
                    line.Tag = new BarTagInfo { Entry = entry, Segment = segment };

                    var endLine = GetOrCreateOutputAppendEnd(outputAppendEndIdx++);
                    endLine.X1 = oStartX + oWidth; endLine.X2 = oStartX + oWidth;
                    endLine.Y1 = yTop + 1; endLine.Y2 = yTop + subH - 1;
                    endLine.Stroke = outputAppendBrush;
                    endLine.Tag = new BarTagInfo { Entry = entry, Segment = segment };
                }
            }
        }
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

        double totalSeconds = Math.Max(_viewModel.TimelineDuration.TotalSeconds, 1);
        double pixelsPerSecond = _viewModel.PixelsPerSecond;
        double viewportWidth = TimeRulerCanvas.ActualWidth;
        double offset = TimelineScrollViewer.HorizontalOffset;

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
        double x = _viewModel.TotalDuration.TotalSeconds * _viewModel.PixelsPerSecond - TimelineScrollViewer.HorizontalOffset;
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
