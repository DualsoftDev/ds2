// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// <see cref="OeeCommHealthService.ComputeUnmeasured"/> 단위 테스트 — doc/22 §3.4 미계측 판정을 코드로 고정한다:
/// plcOk=1 심박 1개 = [t, t+cover) 계측 보증, 보증 안 된 잔여 = 미계측, minReport 미만 조각은 보고 안 함(보수),
/// plcOk=0 심박은 아무것도 보증하지 않음(그 시각 PLC 미연결 = 미계측).
/// </summary>
public class OeeCommHealthTests
{
    private const double Min = 60_000;                              // 1분(ms)
    private const double Cover = OeeCommHealthService.CoverWindowMs;      // 150s
    private const double Report = OeeCommHealthService.MinReportGapMs;    // 180s

    private static List<(double SampleMs, bool PlcOk)> Beats(double startMs, int count, double intervalMs = Min, bool ok = true)
    {
        var res = new List<(double, bool)>();
        for (int i = 0; i < count; i++) res.Add((startMs + i * intervalMs, ok));
        return res;
    }

    // ── 정상 심박 = 미계측 없음 ───────────────────────────────────────────

    [Fact]
    public void Continuous_heartbeat_yields_no_unmeasured()
    {
        var samples = Beats(0, 61); // 0~60분, 매분
        var gaps = OeeCommHealthService.ComputeUnmeasured(0, 60 * Min, samples, Cover, Report);
        Assert.Empty(gaps);
    }

    [Fact]
    public void Single_missed_beat_is_tolerated_by_cover_window()
    {
        // 10분 지점 심박 1개 유실(간격 2분) — 커버 창 150s ≥ 120s 라 공백 없음.
        var samples = Beats(0, 10).Concat(Beats(11 * Min, 10)).ToList();
        var gaps = OeeCommHealthService.ComputeUnmeasured(0, 20 * Min, samples, Cover, Report);
        Assert.Empty(gaps);
    }

    // ── 공백 = 미계측 ─────────────────────────────────────────────────────

    [Fact]
    public void Mid_range_gap_is_unmeasured_from_cover_end_to_next_beat()
    {
        // 심박 0~10분, 그 후 30분 공백, 40분부터 재개.
        var samples = Beats(0, 11).Concat(Beats(40 * Min, 21)).ToList();
        var gaps = OeeCommHealthService.ComputeUnmeasured(0, 60 * Min, samples, Cover, Report);

        var g = Assert.Single(gaps);
        Assert.Equal(10 * Min + Cover, g.S); // 마지막 심박 커버 끝
        Assert.Equal(40 * Min, g.E);         // 재개 심박 시각
    }

    [Fact]
    public void Empty_samples_marks_whole_range_unmeasured()
    {
        // 범위 안 심박 전무(앱 다운) — 전체 미계측. (epoch 게이트는 호출측 책임)
        var gaps = OeeCommHealthService.ComputeUnmeasured(0, 60 * Min, new List<(double, bool)>(), Cover, Report);
        var g = Assert.Single(gaps);
        Assert.Equal(0, g.S);
        Assert.Equal(60 * Min, g.E);
    }

    [Fact]
    public void Leading_and_trailing_gaps_are_reported()
    {
        // 20~40분에만 심박 — 앞뒤가 미계측.
        var samples = Beats(20 * Min, 21);
        var gaps = OeeCommHealthService.ComputeUnmeasured(0, 60 * Min, samples, Cover, Report);

        Assert.Equal(2, gaps.Count);
        Assert.Equal((0d, 20 * Min), gaps[0]);
        Assert.Equal((40 * Min + Cover, 60 * Min), gaps[1]);
    }

    // ── plcOk=0 = 미계측 ─────────────────────────────────────────────────

    [Fact]
    public void PlcDown_beats_cover_nothing()
    {
        // 앱은 살아있지만(심박 존재) PLC 미연결(plcOk=0) 20분 — 그 구간은 미계측.
        var samples = Beats(0, 11)
            .Concat(Beats(11 * Min, 19, ok: false))
            .Concat(Beats(30 * Min, 31))
            .ToList();
        var gaps = OeeCommHealthService.ComputeUnmeasured(0, 60 * Min, samples, Cover, Report);

        var g = Assert.Single(gaps);
        Assert.Equal(10 * Min + Cover, g.S);
        Assert.Equal(30 * Min, g.E);
    }

    // ── 보수 필터 ────────────────────────────────────────────────────────

    [Fact]
    public void Short_gap_below_min_report_is_dropped()
    {
        // 4분 공백(커버 끝 기준 90s 잔여) < 3분 보고 하한 — 미계측 주장 안 함.
        var samples = Beats(0, 11).Concat(Beats(14 * Min, 10)).ToList();
        var gaps = OeeCommHealthService.ComputeUnmeasured(0, 24 * Min, samples, Cover, Report);
        Assert.Empty(gaps);
    }

    [Fact]
    public void Empty_or_inverted_range_returns_empty()
    {
        Assert.Empty(OeeCommHealthService.ComputeUnmeasured(10, 10, Beats(0, 5), Cover, Report));
        Assert.Empty(OeeCommHealthService.ComputeUnmeasured(20, 10, Beats(0, 5), Cover, Report));
    }

    [Fact]
    public void Unsorted_samples_are_handled()
    {
        // 정렬 안 된 입력도 동일 결과(내부 정렬) — 커버 0~10분+40~60분, 공백 10분+150s~40분.
        var samples = Beats(40 * Min, 21).Concat(Beats(0, 11)).ToList();
        var gaps = OeeCommHealthService.ComputeUnmeasured(0, 60 * Min, samples, Cover, Report);

        var g = Assert.Single(gaps);
        Assert.Equal(10 * Min + Cover, g.S);
        Assert.Equal(40 * Min, g.E);
    }

    // ── 원인 라벨링(LabelUnmeasured, 2026-09-01) ─────────────────────────
    //   plcOk=0 행의 귀속 창과 겹치는 조각 = 그 행의 cause / 행 부재 잔여 = service(DSPilot 미가동).

    private static List<(double SampleMs, bool PlcOk, string? Cause)> Causes(
        double startMs, int count, string? cause, double intervalMs = Min)
    {
        var res = new List<(double, bool, string?)>();
        for (int i = 0; i < count; i++) res.Add((startMs + i * intervalMs, cause is null, cause));
        return res;
    }

    [Fact]
    public void Label_no_rows_at_all_is_service_down()
    {
        // 심박 행 전무 = DSPilot 미가동 — 전 구간 service.
        var gaps = new List<(double, double)> { (0, 60 * Min) };
        var wins = OeeCommHealthService.LabelUnmeasured(
            gaps, new List<(double, bool, string?)>(), Cover);

        var w = Assert.Single(wins);
        Assert.Equal((0d, 60 * Min, OeeCommHealthService.CauseService), (w.S, w.E, w.Cause));
    }

    [Fact]
    public void Label_plc_down_beats_are_attributed_and_merged()
    {
        // 정상 0~10분 → plc 단절 심박 11~29분 → 정상 30분~. 미계측 = [10분+cover, 30분).
        // plc 심박 창이 구간 전체를 덮으므로 단일 plc 윈도우(60초 간격 조각 병합).
        var samples = Beats(0, 11).Select(s => (s.SampleMs, s.PlcOk, (string?)null))
            .Concat(Causes(11 * Min, 19, OeeCommHealthService.CausePlc))
            .Concat(Beats(30 * Min, 31).Select(s => (s.SampleMs, s.PlcOk, (string?)null)))
            .ToList();
        var gaps = OeeCommHealthService.ComputeUnmeasured(0, 60 * Min,
            samples.Select(s => (s.Item1, s.Item2)).ToList(), Cover, Report);
        var wins = OeeCommHealthService.LabelUnmeasured(gaps, samples, Cover);

        var w = Assert.Single(wins);
        Assert.Equal(OeeCommHealthService.CausePlc, w.Cause);
        Assert.Equal(10 * Min + Cover, w.S);
        Assert.Equal(30 * Min, w.E);
    }

    [Fact]
    public void Label_plc_down_then_service_down_splits_window()
    {
        // plc 단절 심박 11~15분 후 행 자체가 끊김(서비스 다운) → 40분 재개.
        // 미계측 [10분+cover, 40분) 이 plc(마지막 plc 심박+cover 까지) / service(잔여) 로 갈라진다.
        var samples = Beats(0, 11).Select(s => (s.SampleMs, s.PlcOk, (string?)null))
            .Concat(Causes(11 * Min, 5, OeeCommHealthService.CausePlc))
            .Concat(Beats(40 * Min, 21).Select(s => (s.SampleMs, s.PlcOk, (string?)null)))
            .ToList();
        var gaps = OeeCommHealthService.ComputeUnmeasured(0, 60 * Min,
            samples.Select(s => (s.Item1, s.Item2)).ToList(), Cover, Report);
        var wins = OeeCommHealthService.LabelUnmeasured(gaps, samples, Cover);

        Assert.Equal(2, wins.Count);
        Assert.Equal(OeeCommHealthService.CausePlc, wins[0].Cause);
        Assert.Equal(10 * Min + Cover, wins[0].S);
        Assert.Equal(15 * Min + Cover, wins[0].E);   // 마지막 plc 심박(15분) 귀속 창 끝
        Assert.Equal(OeeCommHealthService.CauseService, wins[1].Cause);
        Assert.Equal(15 * Min + Cover, wins[1].S);
        Assert.Equal(40 * Min, wins[1].E);
    }

    [Fact]
    public void Label_legacy_null_cause_is_unknown()
    {
        // cause 컬럼 도입 전 데이터(plcOk=0, cause=NULL) — unknown 으로 라벨.
        var samples = new List<(double, bool, string?)>();
        for (int i = 0; i <= 30; i++) samples.Add((i * Min, false, null));
        var gaps = new List<(double, double)> { (0, 30 * Min) };
        var wins = OeeCommHealthService.LabelUnmeasured(gaps, samples, Cover);

        var w = Assert.Single(wins);
        Assert.Equal(OeeCommHealthService.CauseUnknown, w.Cause);
    }

    [Fact]
    public void Label_union_equals_input_gaps()
    {
        // 라벨링은 분할만 한다 — 합집합 길이는 입력 gap 과 동일해야 한다.
        var samples = Beats(0, 11).Select(s => (s.SampleMs, s.PlcOk, (string?)null))
            .Concat(Causes(20 * Min, 3, OeeCommHealthService.CauseAgent))
            .ToList();
        var gaps = OeeCommHealthService.ComputeUnmeasured(0, 60 * Min,
            samples.Select(s => (s.Item1, s.Item2)).ToList(), Cover, Report);
        var wins = OeeCommHealthService.LabelUnmeasured(gaps, samples, Cover);

        Assert.Equal(gaps.Sum(g => g.E - g.S), wins.Sum(w => w.E - w.S), 3);
        // 인접 윈도우는 빈틈/겹침 없이 이어진다.
        for (int i = 1; i < wins.Count; i++)
            Assert.True(wins[i].S >= wins[i - 1].E - 0.001);
    }
}
