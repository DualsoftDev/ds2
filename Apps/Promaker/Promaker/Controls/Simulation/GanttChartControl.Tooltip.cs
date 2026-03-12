using System;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class GanttChartControl
{
    private void OnBarMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Rectangle { Tag: BarTagInfo info })
        {
            var segmentEnd = info.Segment.EndTime ?? _viewModel?.CurrentTime ?? DateTime.Now;
            var duration = segmentEnd - info.Segment.StartTime;
            TooltipTitle.Text = $"{info.Entry.Kind}: {info.Entry.Name}";
            TooltipState.Text = $"상태: {info.Segment.StateFullName}";
            TooltipTime.Text = $"시작: {info.Segment.StartTime:HH:mm:ss.fff}";
            TooltipDuration.Text = $"경과: {duration:mm\\:ss\\.fff}";
            TooltipPopup.IsOpen = true;
        }
    }

    private void OnBarMouseLeave(object sender, MouseEventArgs e) => TooltipPopup.IsOpen = false;

    private class BarTagInfo
    {
        public required GanttTimelineEntry Entry { get; init; }
        public required GanttStateSegment Segment { get; init; }
    }
}
