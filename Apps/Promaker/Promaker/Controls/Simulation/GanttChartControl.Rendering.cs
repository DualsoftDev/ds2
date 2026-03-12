using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Promaker.Controls;

public partial class GanttChartControl
{
    private void StartRendering() => _renderTimer.Start();

    private void StopRendering() => _renderTimer.Stop();

    private void OnRenderTick()
    {
        if (_viewModel == null) return;
        if (_viewModel.IsRunning) _viewModel.CurrentTime = DateTime.Now;
        RenderTimeline();
        RenderTimeRuler();
        UpdateCurrentTimeIndicator();
    }

    private void InvalidateTimeline()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_viewModel is { IsRunning: true }) _viewModel.CurrentTime = DateTime.Now;
            RenderTimeline();
            RenderTimeRuler();
            UpdateCurrentTimeIndicator();
        }, DispatcherPriority.Render);
    }

    private void RenderTimeline()
    {
        if (_viewModel == null || _viewModel.Entries.Count == 0) return;

        TimelineCanvas.Children.Clear();

        double y = 0;
        double totalSeconds = Math.Max(_viewModel.TotalDuration.TotalSeconds, 1);
        double totalWidth = totalSeconds * _viewModel.PixelsPerSecond;
        double totalHeight = _viewModel.Entries.Where(entry => entry.IsVisible).Sum(entry => entry.RowHeight + RowGap);

        TimelineCanvas.Width = Math.Max(totalWidth + 100, TimelineScrollViewer.ActualWidth);
        TimelineCanvas.Height = Math.Max(totalHeight, TimelineScrollViewer.ActualHeight);

        foreach (var entry in _viewModel.Entries)
        {
            if (!entry.IsVisible) continue;
            entry.YOffset = y;
            double rowHeight = entry.RowHeight;

            var rowBackground = new Rectangle
            {
                Width = TimelineCanvas.Width,
                Height = rowHeight,
                Fill = entry.RowBackground
            };
            Canvas.SetLeft(rowBackground, 0);
            Canvas.SetTop(rowBackground, y);
            TimelineCanvas.Children.Add(rowBackground);

            var rowLine = new Line
            {
                X1 = 0,
                X2 = TimelineCanvas.Width,
                Y1 = y + rowHeight,
                Y2 = y + rowHeight,
                Stroke = Application.Current.TryFindResource("BorderBrush") as Brush ?? Brushes.Gray,
                StrokeThickness = 0.5
            };
            TimelineCanvas.Children.Add(rowLine);

            foreach (var segment in entry.Segments)
            {
                double startX = (segment.StartTime - _viewModel.StartTime).TotalSeconds * _viewModel.PixelsPerSecond;
                var segmentEndTime = segment.EndTime ?? _viewModel.CurrentTime;
                double width = (segmentEndTime - segment.StartTime).TotalSeconds * _viewModel.PixelsPerSecond;
                if (width < 2) width = 2;

                var bar = new Rectangle
                {
                    Width = width,
                    Height = rowHeight - 4,
                    Fill = segment.StateBrush,
                    RadiusX = 2,
                    RadiusY = 2,
                    Tag = new BarTagInfo { Entry = entry, Segment = segment },
                    Cursor = Cursors.Hand
                };
                bar.MouseEnter += OnBarMouseEnter;
                bar.MouseLeave += OnBarMouseLeave;

                Canvas.SetLeft(bar, startX);
                Canvas.SetTop(bar, y + 2);
                TimelineCanvas.Children.Add(bar);
            }

            y += rowHeight + RowGap;
        }
    }

    private void RenderTimeRuler()
    {
        if (_viewModel == null) return;
        TimeRulerCanvas.Children.Clear();

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

        for (double sec = startSec; sec <= endSec; sec += tickInterval)
        {
            double x = sec * pixelsPerSecond - offset;
            if (x < -50 || x > viewportWidth + 50) continue;

            var tick = new Line
            {
                X1 = x,
                Y1 = 18,
                X2 = x,
                Y2 = 24,
                Stroke = Application.Current.TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray,
                StrokeThickness = 1
            };
            TimeRulerCanvas.Children.Add(tick);

            var label = new TextBlock
            {
                Text = FormatTime(TimeSpan.FromSeconds(sec)),
                FontSize = 9,
                Foreground = Application.Current.TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray
            };
            Canvas.SetLeft(label, x + 3);
            Canvas.SetTop(label, 4);
            TimeRulerCanvas.Children.Add(label);
        }
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
