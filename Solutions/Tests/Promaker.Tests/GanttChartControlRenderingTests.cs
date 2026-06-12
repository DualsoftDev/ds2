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

    // ── 동적 재예측 — Work plan 틀 끝을 끝난 자식 Call 의 actual 기준으로 갱신 ──

    private static (GanttTimelineEntry Work, GanttStateSegment Going, GanttTimelineEntry CallA, GanttTimelineEntry CallB, DateTime Start)
        BuildDynamicPlanFixture()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var workId = Guid.NewGuid();
        var work = new GanttTimelineEntry
        {
            Id = workId, Name = "W", Kind = EntityKind.Work, BaseDurationMs = 1100   // 자식 합 1000 + 갭 100
        };
        var callA = new GanttTimelineEntry
        {
            Id = Guid.NewGuid(), Name = "A", Kind = EntityKind.Call, RowKind = GanttRowKind.Call,
            ParentWorkId = workId, BaseDurationMs = 500
        };
        var callB = new GanttTimelineEntry
        {
            Id = Guid.NewGuid(), Name = "B", Kind = EntityKind.Call, RowKind = GanttRowKind.Call,
            ParentWorkId = workId, BaseDurationMs = 500
        };
        var going = new GanttStateSegment { State = Status4.Going, StartTime = start };
        return (work, going, callA, callB, start);
    }

    [Fact]
    public void DynamicPlanEnd_anchors_on_completed_child_actual_and_predicts_remainder()
    {
        var (work, going, callA, callB, start) = BuildDynamicPlanFixture();
        // A: 0→520 완료(plan 500 대비 +20ms 지연) — anchor 가 actual 로 이동.
        callA.Segments.Add(new GanttStateSegment { State = Status4.Going, StartTime = start, EndTime = start.AddMilliseconds(520) });

        var end = GanttChartControl.ResolveDynamicPlanEnd(work, going, [work, callA, callB], start.AddMilliseconds(600));

        // 520(actual) + 500(B plan) + 50(남은 갭 = 100 × 1/2) — 고정 plan(1100)이면 A 의 +20 이 잔차로 남았을 것.
        Assert.Equal(start.AddMilliseconds(1070), end);
    }

    [Fact]
    public void DynamicPlanEnd_uses_in_progress_child_going_start_as_anchor()
    {
        var (work, going, callA, callB, start) = BuildDynamicPlanFixture();
        callA.Segments.Add(new GanttStateSegment { State = Status4.Going, StartTime = start, EndTime = start.AddMilliseconds(520) });
        callB.Segments.Add(new GanttStateSegment { State = Status4.Going, StartTime = start.AddMilliseconds(530) });   // 진행 중

        var end = GanttChartControl.ResolveDynamicPlanEnd(work, going, [work, callA, callB], start.AddMilliseconds(700));

        // 530(B 시작) + 500(B plan) + 50(남은 갭) = 1080.
        Assert.Equal(start.AddMilliseconds(1080), end);
    }

    [Fact]
    public void DynamicPlanEnd_applies_only_to_in_progress_work_cycle()
    {
        // 완료된 사이클에 적용하면 자식 탐색이 최신 사이클의 자식 Going 을 오인해
        // 과거 틀이 현재까지 늘어나 겹치는 회귀가 있었음 — 닫힌 Going 은 고정 plan.
        var (work, going, callA, callB, start) = BuildDynamicPlanFixture();
        going.EndTime = start.AddMilliseconds(1120);   // 완료된 과거 사이클
        callA.Segments.Add(new GanttStateSegment { State = Status4.Going, StartTime = start.AddMilliseconds(20_000) });   // 최신 사이클 자식

        Assert.Null(GanttChartControl.ResolveDynamicPlanEnd(work, going, [work, callA, callB], start.AddMilliseconds(21_000)));
    }

    [Fact]
    public void DynamicPlanEnd_returns_null_without_children_or_child_plans()
    {
        var (work, going, callA, _, start) = BuildDynamicPlanFixture();

        Assert.Null(GanttChartControl.ResolveDynamicPlanEnd(work, going, [work], start));   // 자식 없음

        var noPlanChild = new GanttTimelineEntry
        {
            Id = Guid.NewGuid(), Name = "N", Kind = EntityKind.Call, RowKind = GanttRowKind.Call,
            ParentWorkId = work.Id
        };
        Assert.Null(GanttChartControl.ResolveDynamicPlanEnd(work, going, [work, callA, noPlanChild], start));
    }

    [Fact]
    public void FreezeOpenSegments_closes_only_open_segments_at_blackout_time()
    {
        // 통신 blackout — 열린 세그먼트(상태·I/O)는 두절 시각으로 닫혀 "무한 연장" 중단,
        // 이미 닫힌 세그먼트는 불변.
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var chart = new GanttChartState();
        chart.Reset(start);
        var workId = Guid.NewGuid();
        chart.AddEntry(workId, "W", EntityKind.Work);
        chart.UpdateNodeState(workId, Status4.Going, start.AddMilliseconds(100));   // 열린 Going

        var apiCallId = Guid.NewGuid();
        chart.AddApiCallEntry(apiCallId, "A", workId, Guid.NewGuid(), outAddress: "%Q1", inAddress: "%I1");
        chart.UpdateIoState("%Q1", true, start.AddMilliseconds(150));               // 열린 Out high

        var freezeAt = start.AddMilliseconds(2000);
        chart.FreezeOpenSegments(freezeAt);

        var work = chart.FindEntry(workId)!;
        Assert.Equal(freezeAt, work.Segments[^1].EndTime);                          // Going 동결
        Assert.Equal(start.AddMilliseconds(100), work.Segments[^2].EndTime);        // 직전(Ready→닫힘) 불변

        var apiCall = chart.FindEntry(apiCallId)!;
        Assert.Equal(freezeAt, apiCall.OutSegments[^1].EndTime);                    // I/O 줄도 동결
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
