using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class GanttChartControl
{
    private void OnTimelineMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewModel == null) return;

        var mousePos = e.GetPosition(TimelineCanvas);
        double timeAtMouse = (_viewModel.HorizontalOffset + mousePos.X) / _viewModel.PixelsPerSecond;
        double factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
        double newPixelsPerSecond = Math.Clamp(
            _viewModel.PixelsPerSecond * factor,
            GanttChartState.MinPixelsPerSecond,
            GanttChartState.MaxPixelsPerSecond);

        _viewModel.PixelsPerSecond = newPixelsPerSecond;
        _viewModel.HorizontalOffset = Math.Max(0, timeAtMouse * newPixelsPerSecond - mousePos.X);
        InvalidateTimeline();
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
        _viewModel.HorizontalOffset = Math.Max(0, _panStartHorizontalOffset + deltaX);
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

        try
        {
            _isSyncingScroll = true;
            LabelScrollViewer.ScrollToVerticalOffset(e.VerticalOffset);
            _viewModel.HorizontalOffset = e.HorizontalOffset;
            _viewModel.VerticalOffset = e.VerticalOffset;
            RenderTimeRuler();
            UpdateCurrentTimeIndicator();
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
        if (!entry.IsWork) return;

        var now = DateTime.Now;
        bool isDoubleClick = (now - _lastRowClickTime).TotalMilliseconds < 300 && _lastClickedEntry == entry;

        if (isDoubleClick)
        {
            entry.IsExpanded = !entry.IsExpanded;
            foreach (var child in _viewModel.Entries)
            {
                if (child.IsCall && child.ParentWorkId == entry.Id)
                    child.IsVisible = entry.IsExpanded;
            }

            InvalidateTimeline();
            _lastClickedEntry = null;
        }
        else
        {
            _lastClickedEntry = entry;
        }

        _lastRowClickTime = now;
        e.Handled = true;
    }
}
