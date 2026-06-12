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

    // ── shadow coast — 통신 두절 구간을 직전 완료 사이클 템플릿으로 추정 진행 ──

    /// <summary>2사이클 진행 픽스처 — Work: G@0→F@1000, G@1200(진행 중). Call: G@100→F@500, G@1300(진행 중).
    /// 주기 P = 1200ms, 직전 완료 사이클 템플릿 = Work(0,1000)/Call(100,400).</summary>
    private static (GanttChartState Chart, Guid WorkId, Guid CallId, DateTime Start) BuildShadowCoastFixture()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var chart = new GanttChartState();
        chart.Reset(start);
        var workId = Guid.NewGuid();
        var callId = Guid.NewGuid();
        chart.AddEntry(workId, "W", EntityKind.Work);
        chart.AddEntry(callId, "C", EntityKind.Call, parentWorkId: workId);

        chart.UpdateNodeState(workId, Status4.Going, start);
        chart.UpdateNodeState(callId, Status4.Going, start.AddMilliseconds(100));
        chart.UpdateNodeState(callId, Status4.Finish, start.AddMilliseconds(500));
        chart.UpdateNodeState(workId, Status4.Finish, start.AddMilliseconds(1000));
        chart.UpdateNodeState(workId, Status4.Going, start.AddMilliseconds(1200));
        chart.UpdateNodeState(callId, Status4.Going, start.AddMilliseconds(1300));
        return (chart, workId, callId, start);
    }

    [Fact]
    public void TryBeginShadowCoast_extracts_template_from_last_completed_cycle()
    {
        var (chart, workId, callId, start) = BuildShadowCoastFixture();

        var window = chart.TryBeginShadowCoast(start.AddMilliseconds(1500));

        Assert.NotNull(window);
        Assert.Equal(1200, window!.PeriodMs);
        Assert.Equal(start.AddMilliseconds(1200), window.AnchorCycleStart);
        Assert.Equal(new ShadowTemplateGoing(0, 1000), Assert.Single(window.Template[workId]));
        Assert.Equal(new ShadowTemplateGoing(100, 400), Assert.Single(window.Template[callId]));
        Assert.Same(window, Assert.Single(chart.ShadowWindows));
    }

    [Fact]
    public void TryBeginShadowCoast_returns_null_without_completed_cycle()
    {
        // 첫 사이클 중 두절 — 추정 근거(완료 사이클)가 없으면 공백이 정직하다.
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var chart = new GanttChartState();
        chart.Reset(start);
        var workId = Guid.NewGuid();
        chart.AddEntry(workId, "W", EntityKind.Work);
        chart.UpdateNodeState(workId, Status4.Going, start);

        Assert.Null(chart.TryBeginShadowCoast(start.AddMilliseconds(700)));
        Assert.Empty(chart.ShadowWindows);
    }

    [Fact]
    public void EnumerateShadowBars_tiles_template_with_period_and_clips_to_window()
    {
        var (chart, workId, callId, start) = BuildShadowCoastFixture();
        var window = chart.TryBeginShadowCoast(start.AddMilliseconds(1500))!;

        // 열린 윈도우 — until(현재 시각 3000ms)까지 추정 틀이 자란다.
        var bars = GanttChartState.EnumerateShadowBars(window, start.AddMilliseconds(3000)).ToArray();

        var workBars = bars.Where(b => b.EntryId == workId).OrderBy(b => b.StartTime).ToArray();
        Assert.Equal(2, workBars.Length);
        // k=0 사이클(1200~2200)은 두절 시각 1500 으로 머리가 클립.
        Assert.Equal(start.AddMilliseconds(1500), workBars[0].StartTime);
        Assert.Equal(start.AddMilliseconds(2200), workBars[0].EndTime);
        // k=1 사이클(2400~3400)은 until 3000 으로 꼬리가 클립.
        Assert.Equal(start.AddMilliseconds(2400), workBars[1].StartTime);
        Assert.Equal(start.AddMilliseconds(3000), workBars[1].EndTime);

        var callBars = bars.Where(b => b.EntryId == callId).OrderBy(b => b.StartTime).ToArray();
        Assert.Equal(2, callBars.Length);
        Assert.Equal(start.AddMilliseconds(1500), callBars[0].StartTime);   // 1300~1700 머리 클립
        Assert.Equal(start.AddMilliseconds(1700), callBars[0].EndTime);
        Assert.Equal(start.AddMilliseconds(2500), callBars[1].StartTime);   // 2500~2900 그대로
        Assert.Equal(start.AddMilliseconds(2900), callBars[1].EndTime);
    }

    [Fact]
    public void ShadowCoast_keeps_ghost_per_entry_until_that_entry_resumes()
    {
        // 한 행의 유추 복귀(첫 실측 Going)가 다른 행의 고스트를 끊으면 늦게 복귀하는 행에
        // 공백이 생긴다(실기 확인) — 고스트는 행마다 자기 복귀까지 유지.
        var (chart, workId, callId, start) = BuildShadowCoastFixture();
        chart.TryBeginShadowCoast(start.AddMilliseconds(1500));

        // Call 만 유추 복귀 — Going 세그먼트 기록이 자동으로 그 행의 고스트를 닫는다.
        chart.UpdateNodeState(callId, Status4.Going, start.AddMilliseconds(2600));

        var bars = GanttChartState.EnumerateShadowBars(chart.ShadowWindows[^1], start.AddMilliseconds(3000)).ToArray();

        // Call 행: 복귀(2600) 이후 추정 없음 — k=1 going(2500~2900)이 2600 에서 클립.
        var callBars = bars.Where(b => b.EntryId == callId).OrderBy(b => b.StartTime).ToArray();
        Assert.Equal(start.AddMilliseconds(2600), callBars[^1].EndTime);
        // Work 행: 미복귀 — until(3000)까지 계속 자란다.
        var workBars = bars.Where(b => b.EntryId == workId).OrderBy(b => b.StartTime).ToArray();
        Assert.Equal(start.AddMilliseconds(3000), workBars[^1].EndTime);
        // 전역은 아직 열림(미복귀 행 잔존).
        Assert.Null(chart.ShadowWindows[^1].EndTime);
    }

    [Fact]
    public void ShadowCoast_closes_window_when_all_entries_resume()
    {
        var (chart, workId, callId, start) = BuildShadowCoastFixture();
        chart.TryBeginShadowCoast(start.AddMilliseconds(1500));

        chart.UpdateNodeState(callId, Status4.Going, start.AddMilliseconds(2600));
        chart.UpdateNodeState(workId, Status4.Going, start.AddMilliseconds(2700));

        // 템플릿의 모든 행 복귀 — 마지막 복귀 시각으로 전역 마감(다음 두절이 새 윈도우를 연다).
        Assert.Equal(start.AddMilliseconds(2700), chart.ShadowWindows[^1].EndTime);
    }

    [Fact]
    public void TryBeginShadowCoast_closes_open_window_and_starts_new_one_on_reblackout()
    {
        // 일부 행 미복귀 중 재두절 — 기존 윈도우는 그 시각으로 마감하고 새 윈도우로 구간을
        // 나눈다(복귀했던 행의 actual 구간 위에 옛 추정이 다시 겹치지 않게).
        var (chart, _, callId, start) = BuildShadowCoastFixture();
        var first = chart.TryBeginShadowCoast(start.AddMilliseconds(1500))!;
        chart.UpdateNodeState(callId, Status4.Going, start.AddMilliseconds(2600));   // 부분 복귀

        var second = chart.TryBeginShadowCoast(start.AddMilliseconds(4000));

        Assert.Equal(start.AddMilliseconds(4000), first.EndTime);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(2, chart.ShadowWindows.Count);
    }

    [Fact]
    public void EnumerateShadowBars_stops_at_window_end_after_resume()
    {
        var (chart, workId, _, start) = BuildShadowCoastFixture();
        var window = chart.TryBeginShadowCoast(start.AddMilliseconds(1500))!;
        Assert.True(chart.EndShadowCoast(start.AddMilliseconds(2000)));

        // 재개 후 현재 시각이 더 가도 추정 틀은 두절 구간 [1500, 2000] 이력으로 고정.
        var bars = GanttChartState.EnumerateShadowBars(window, start.AddMilliseconds(60_000)).ToArray();

        var workBar = Assert.Single(bars.Where(b => b.EntryId == workId));
        Assert.Equal(start.AddMilliseconds(1500), workBar.StartTime);
        Assert.Equal(start.AddMilliseconds(2000), workBar.EndTime);
    }

    [Fact]
    public void TryReconcileShadowCoast_joins_when_resume_near_predicted_going()
    {
        var (chart, _, callId, start) = BuildShadowCoastFixture();
        chart.TryBeginShadowCoast(start.AddMilliseconds(1500));
        chart.EndShadowCoast(start.AddMilliseconds(2600));

        // 예측 Call Going 시작 = 1300 + k·1200 → k=1: 2500. 실측 2550 → 오차 50ms ≤ 허용 300ms.
        var result = chart.TryReconcileShadowCoast(callId, start.AddMilliseconds(2550));

        Assert.NotNull(result);
        Assert.True(result!.Value.Joined);
        Assert.Equal(50, result.Value.ErrorMs);
        Assert.False(chart.ShadowWindows[^1].LowConfidence);
        Assert.True(chart.ShadowWindows[^1].Reconciled);
    }

    [Fact]
    public void TryReconcileShadowCoast_degrades_window_when_resume_far_from_prediction()
    {
        var (chart, _, callId, start) = BuildShadowCoastFixture();
        chart.TryBeginShadowCoast(start.AddMilliseconds(1500));
        chart.EndShadowCoast(start.AddMilliseconds(2600));

        // 실측 1900 — 가장 가까운 예측(1300/2500)과 600ms 차 > 허용 300ms → 미확정 강등.
        var result = chart.TryReconcileShadowCoast(callId, start.AddMilliseconds(1900));

        Assert.NotNull(result);
        Assert.False(result!.Value.Joined);
        Assert.True(chart.ShadowWindows[^1].LowConfidence);
    }

    [Fact]
    public void TryReconcileShadowCoast_defers_for_entry_not_in_template()
    {
        var (chart, _, _, start) = BuildShadowCoastFixture();
        chart.TryBeginShadowCoast(start.AddMilliseconds(1500));
        chart.EndShadowCoast(start.AddMilliseconds(2600));

        // 템플릿에 없는 entry 의 Going — 판정 보류(다음 Going 으로 재시도), 윈도우는 미reconcile 유지.
        Assert.Null(chart.TryReconcileShadowCoast(Guid.NewGuid(), start.AddMilliseconds(2550)));
        Assert.False(chart.ShadowWindows[^1].Reconciled);
    }

    [Fact]
    public void AdaptiveSilenceThreshold_grows_with_observed_signal_gap()
    {
        // 고정 3s 임계는 실 PLC(신호 edge 간격 수 초)에서 진입/해제 루프를 돈다 —
        // 임계 = max(바닥 3s, 관측 최대 간격 × 3).
        Assert.Equal(3000, SimulationPanelState.ResolveAdaptiveSilenceThresholdMs(0));
        Assert.Equal(3000, SimulationPanelState.ResolveAdaptiveSilenceThresholdMs(800));    // 800×3=2.4s < 바닥
        Assert.Equal(15000, SimulationPanelState.ResolveAdaptiveSilenceThresholdMs(5000));  // 5s 간격 설비 → 15s
    }

    [Fact]
    public void SignalGapLearning_accepts_resume_gap_only_when_near_threshold()
    {
        // 정상 운전 간격은 항상 학습. blackout 해제 간격은 "오탐 의심"(임계×3 이내)만 —
        // 자연 간격이 임계보다 큰 설비가 영영 학습 못 하는 루프 차단 + 장기 두절 오염 차단.
        Assert.True(SimulationPanelState.ShouldLearnSignalGap(inBlackout: false, gapMs: 60_000, thresholdMs: 3000));
        Assert.True(SimulationPanelState.ShouldLearnSignalGap(inBlackout: true, gapMs: 5000, thresholdMs: 3000));    // 5s ≤ 9s — 자연 간격
        Assert.False(SimulationPanelState.ShouldLearnSignalGap(inBlackout: true, gapMs: 60_000, thresholdMs: 3000)); // 진짜 두절
    }

    [Fact]
    public void ResolveOpenSegmentEnd_clips_open_bars_to_evidence_cap()
    {
        // 통신이 멎으면 blackout 확정(무소식 3s) 전에도 열린 바는 마지막 신호(증거)까지만 —
        // 빨간 선에 붙어 자라다 동결 때 "챡" 되감기는 왜곡 방지.
        var now = new DateTime(2026, 1, 1, 0, 0, 10, DateTimeKind.Local);

        Assert.Equal(now, GanttChartControl.ResolveOpenSegmentEnd(now, null));                       // cap 없음 — 현재까지
        Assert.Equal(now, GanttChartControl.ResolveOpenSegmentEnd(now, now.AddSeconds(5)));          // 미래 cap — 무시
        Assert.Equal(now.AddSeconds(-2), GanttChartControl.ResolveOpenSegmentEnd(now, now.AddSeconds(-2)));   // 과거 cap — 클립
    }

    [Fact]
    public void SuppressNextGoingPlanOverlay_skips_first_going_after_resume_only()
    {
        // 통신 재개 후 첫 Going = 사이클 중간 합류 — plan 틀을 그리면 다음 사이클을 침범한다.
        // 행별 1회만 생략, 둘째 사이클부터 정상 틀.
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var chart = new GanttChartState();
        chart.Reset(start);
        var workId = Guid.NewGuid();
        chart.AddEntry(workId, "W", EntityKind.Work, baseDurationMs: 1000);

        chart.SuppressNextGoingPlanOverlay();   // 재개 직후
        chart.UpdateNodeState(workId, Status4.Going, start.AddMilliseconds(100));    // 합류 Going
        chart.UpdateNodeState(workId, Status4.Finish, start.AddMilliseconds(500));
        chart.UpdateNodeState(workId, Status4.Going, start.AddMilliseconds(1100));   // 다음 사이클

        var work = chart.FindEntry(workId)!;
        var joinGoing = work.Segments.First(s => s.State == Status4.Going);
        var nextGoing = work.Segments.Last(s => s.State == Status4.Going);

        Assert.Null(GanttChartControl.ResolvePlanOverlayPart(work, joinGoing));      // 합류 — 틀 생략
        Assert.NotNull(GanttChartControl.ResolvePlanOverlayPart(work, nextGoing));   // 둘째부터 정상
    }

    // ── abnormal 색 연동 — 판정된 Call 의 해당 사이클 바만 경고색 ──

    [Fact]
    public void MarkAbnormal_flags_latest_going_of_call_and_its_apicall_rows()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var chart = new GanttChartState();
        chart.Reset(start);
        var workId = Guid.NewGuid();
        var callId = Guid.NewGuid();
        var apiCallId = Guid.NewGuid();
        chart.AddEntry(workId, "W", EntityKind.Work);
        chart.AddEntry(callId, "C", EntityKind.Call, parentWorkId: workId);
        chart.AddApiCallEntry(apiCallId, "A", workId, callId);

        // 사이클 1(완료) → 사이클 2(진행 중) — 판정은 최근 사이클에만 귀속.
        chart.UpdateNodeState(callId, Status4.Going, start.AddMilliseconds(100));
        chart.UpdateNodeState(callId, Status4.Finish, start.AddMilliseconds(500));
        chart.UpdateNodeState(callId, Status4.Going, start.AddMilliseconds(1100));

        chart.MarkAbnormal(callId);

        var call = chart.FindEntry(callId)!;
        Assert.True(call.Segments[^1].IsAbnormal);                          // 진행 중(최근) Going 만
        Assert.False(call.Segments[1].IsAbnormal);                          // 과거 사이클 불변
        var apiCall = chart.FindEntry(apiCallId)!;
        Assert.True(apiCall.Segments[^1].IsAbnormal);                       // PLN 줄(자식 행)도 함께
        Assert.All(chart.FindEntry(workId)!.Segments, s => Assert.False(s.IsAbnormal));   // Work 행은 판정 대상 아님
    }

    [Fact]
    public void MarkAbnormal_without_going_segment_is_noop()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var chart = new GanttChartState();
        chart.Reset(start);
        var callId = Guid.NewGuid();
        chart.AddEntry(callId, "C", EntityKind.Call);

        chart.MarkAbnormal(callId);                                          // Ready 만 있음
        chart.MarkAbnormal(Guid.NewGuid());                                  // 미존재 entry

        Assert.All(chart.FindEntry(callId)!.Segments, s => Assert.False(s.IsAbnormal));
    }

    [Fact]
    public void Reset_clears_shadow_coast_history()
    {
        var (chart, _, _, start) = BuildShadowCoastFixture();
        chart.TryBeginShadowCoast(start.AddMilliseconds(1500));

        chart.Reset(start.AddMilliseconds(10_000));

        Assert.Empty(chart.ShadowWindows);
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
