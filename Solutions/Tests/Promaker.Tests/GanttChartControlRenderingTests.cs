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
    public void RenderInterval_targets_smooth_timeline_animation()
    {
        Assert.True(GanttChartControl.RenderInterval <= TimeSpan.FromMilliseconds(34));
    }

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
            Kind = EntityKind.Call,
            RowKind = GanttRowKind.Call
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
    public void ResolvePlanOverlayPart_returns_plan_span_for_going_segment_with_duration()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var entry = new GanttTimelineEntry
        {
            Id = Guid.NewGuid(),
            Name = "WorkA",
            Kind = EntityKind.Work,
            BaseDurationMs = 1500
        };
        var segment = new GanttStateSegment { State = Status4.Going, StartTime = start };

        var plan = GanttChartControl.ResolvePlanOverlayPart(entry, segment);

        Assert.NotNull(plan);
        Assert.Equal(start, plan.Value.StartTime);
        Assert.Equal(start.AddMilliseconds(1500), plan.Value.EndTime);
    }

    [Fact]
    public void ResolvePlanOverlayPart_skips_non_going_segments_and_missing_duration()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var withDuration = new GanttTimelineEntry
        {
            Id = Guid.NewGuid(),
            Name = "WorkA",
            Kind = EntityKind.Work,
            BaseDurationMs = 1500
        };
        var withoutDuration = new GanttTimelineEntry
        {
            Id = Guid.NewGuid(),
            Name = "WorkB",
            Kind = EntityKind.Work
        };
        var ready = new GanttStateSegment { State = Status4.Ready, StartTime = start };
        var finish = new GanttStateSegment { State = Status4.Finish, StartTime = start };
        var going = new GanttStateSegment { State = Status4.Going, StartTime = start };

        Assert.Null(GanttChartControl.ResolvePlanOverlayPart(withDuration, ready));
        Assert.Null(GanttChartControl.ResolvePlanOverlayPart(withDuration, finish));
        Assert.Null(GanttChartControl.ResolvePlanOverlayPart(withoutDuration, going));
    }

    [Fact]
    public void ResolveBarHeight_thins_actual_bar_only_in_plan_overlay_mode()
    {
        Assert.Equal(18, GanttChartControl.ResolveBarHeight(22, planOverlay: false));
        Assert.Equal(
            GanttChartControl.PlanOverlayActualBarHeight,
            GanttChartControl.ResolveBarHeight(22, planOverlay: true));
    }

    [Fact]
    public void ResolveBarTop_centers_bar_within_row()
    {
        // 기존 모드: 높이 rowHeight-4 → top = y+2 (기존 배치와 동일).
        Assert.Equal(102, GanttChartControl.ResolveBarTop(100, 22, 18));
        // overlay 모드: 얇은 바가 행 세로 가운데.
        Assert.Equal(107, GanttChartControl.ResolveBarTop(100, 22, 8));
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
