using System;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Controls;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class GanttChartControlRenderingTests
{
    [Fact]
    public void ResolveRowBackgroundResourceKey_returns_work_brush_for_work_entries()
    {
        var entry = new GanttTimelineEntry
        {
            Id = Guid.NewGuid(),
            Name = "WorkA",
            Kind = EntityKind.Work
        };

        var key = GanttChartControl.ResolveRowBackgroundResourceKey(entry);

        Assert.Equal("GanttWorkRowBackgroundBrush", key);
    }

    [Fact]
    public void ResolveRowBackgroundResourceKey_returns_call_brush_for_call_entries()
    {
        var entry = new GanttTimelineEntry
        {
            Id = Guid.NewGuid(),
            Name = "CallA",
            Kind = EntityKind.Call
        };

        var key = GanttChartControl.ResolveRowBackgroundResourceKey(entry);

        Assert.Equal("GanttCallRowBackgroundBrush", key);
    }

    [Fact]
    public void ResolveSegmentRenderParts_splits_virtual_append_as_outline_tail()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var entry = new GanttTimelineEntry
        {
            Id = Guid.NewGuid(),
            Name = "CallA",
            Kind = EntityKind.Call,
            BaseDurationMs = 1000,
            VirtualAppendMs = 200
        };
        var segment = new GanttStateSegment
        {
            State = Status4.Going,
            StartTime = start,
            EndTime = start.AddMilliseconds(1200)
        };

        var parts = GanttChartControl.ResolveSegmentRenderParts(entry, segment, segment.EndTime!.Value).ToArray();

        Assert.Equal(2, parts.Length);
        Assert.Equal(GanttChartControl.GanttSegmentRenderKind.Filled, parts[0].Kind);
        Assert.Equal(start, parts[0].StartTime);
        Assert.Equal(start.AddMilliseconds(1000), parts[0].EndTime);
        Assert.Equal(GanttChartControl.GanttSegmentRenderKind.VirtualAppendOutline, parts[1].Kind);
        Assert.Equal(start.AddMilliseconds(1000), parts[1].StartTime);
        Assert.Equal(start.AddMilliseconds(1200), parts[1].EndTime);
    }

    [Fact]
    public void ResolveVirtualAppendCornerRadius_keeps_left_edge_square()
    {
        var radius = GanttChartControl.ResolveVirtualAppendCornerRadius();

        Assert.Equal(0, radius.TopLeft);
        Assert.Equal(0, radius.BottomLeft);
        Assert.Equal(2, radius.TopRight);
        Assert.Equal(2, radius.BottomRight);
    }

    [Fact]
    public void ResolveVirtualAppendBorderThickness_removes_left_boundary()
    {
        var thickness = GanttChartControl.ResolveVirtualAppendBorderThickness();

        Assert.Equal(0, thickness.Left);
        Assert.Equal(1.5, thickness.Top);
        Assert.Equal(1.5, thickness.Right);
        Assert.Equal(1.5, thickness.Bottom);
    }

    [Fact]
    public void ResolveSegmentRenderParts_adds_output_append_dashed_tail_after_finish()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var finish = start.AddMilliseconds(500);
        var entry = new GanttTimelineEntry
        {
            Id = Guid.NewGuid(),
            Name = "CallA",
            Kind = EntityKind.Call,
            OutputAppendMs = 200
        };
        var segment = new GanttStateSegment
        {
            State = Status4.Going,
            StartTime = start,
            EndTime = finish
        };

        var parts = GanttChartControl.ResolveSegmentRenderParts(entry, segment, segment.EndTime!.Value).ToArray();

        Assert.Equal(3, parts.Length);
        Assert.Equal(GanttChartControl.GanttSegmentRenderKind.Filled, parts[0].Kind);
        Assert.Equal(GanttChartControl.GanttSegmentRenderKind.OutputAppendLine, parts[1].Kind);
        Assert.Equal(finish, parts[1].StartTime);
        Assert.Equal(finish.AddMilliseconds(200), parts[1].EndTime);
        Assert.Equal(GanttChartControl.GanttSegmentRenderKind.OutputAppendEnd, parts[2].Kind);
        Assert.Equal(finish.AddMilliseconds(200), parts[2].StartTime);
        Assert.Equal(finish.AddMilliseconds(200), parts[2].EndTime);
    }

    [Fact]
    public void TimelineDuration_includes_output_append_tail_without_moving_current_time()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var chart = new GanttChartState();
        var callId = Guid.NewGuid();

        chart.Reset(start);
        chart.AddEntry(callId, "CallA", EntityKind.Call, outputAppendMs: 200);
        chart.UpdateNodeState(callId, Status4.Going, start);
        chart.UpdateNodeState(callId, Status4.Finish, start.AddMilliseconds(500));

        Assert.Equal(500, chart.TotalDuration.TotalMilliseconds);
        Assert.Equal(700, chart.TimelineDuration.TotalMilliseconds);
    }
}
