// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// 비생산 시간대 학습기(doc/22 §3.5 — 일별 샘플 투표제) 순수함수 테스트.
/// 정책 고정: 하루 1표(단발 정지는 창을 못 만듦), 승격 = 활동일의 promoteRatio 이상 반복,
/// 표본 부족(minActiveDays 미만) = 창 미성립, 슬롯 투표 = 슬롯 절반 이상 커버만.
/// </summary>
public class OeeNonProdPatternTests
{
    private const int Slot = 30;           // 30분 슬롯 (기본)
    private const double Ratio = 0.6;      // 승격 컷 (기본)
    private const int MinDays = 3;         // 표본 하한 (기본)

    private static bool[] DayVote(params (int S, int E)[] windows)
        => OeeMath.SlotVotesFromMinuteWindows(windows.Select(w => (w.S, w.E)), Slot);

    // ── SlotVotesFromMinuteWindows: 슬롯 투표 규칙 ────────────────────────

    [Fact]
    public void Lunch_window_votes_exactly_its_slots()
    {
        var v = DayVote((720, 780)); // 12:00–13:00
        Assert.True(v[24] && v[25]); // 12:00, 12:30 슬롯
        Assert.False(v[23] || v[26]);
        Assert.Equal(2, v.Count(x => x));
    }

    [Fact]
    public void Edge_sliver_below_half_slot_does_not_vote()
    {
        // 12:20–12:40 — 슬롯24 는 10분(<15), 슬롯25 는 10분(<15) → 어느 쪽도 투표 없음
        var v = DayVote((740, 760));
        Assert.False(v[24] || v[25]);
        // 12:10–12:40 — 슬롯24 20분(≥15) 투표, 슬롯25 10분 미투표
        var v2 = DayVote((730, 760));
        Assert.True(v2[24]);
        Assert.False(v2[25]);
    }

    [Fact]
    public void Split_windows_accumulate_cover_within_slot()
    {
        // 같은 슬롯 안 두 조각(12:00–12:10 + 12:15–12:25 = 20분 ≥ 15분) → 투표
        var v = DayVote((720, 730), (735, 745));
        Assert.True(v[24]);
    }

    [Fact]
    public void Out_of_range_windows_are_clamped()
    {
        var v = DayVote((-60, 30), (1410, 1500)); // 앞뒤 범위 밖은 클램프
        Assert.True(v[0]);
        Assert.True(v[47]);
        Assert.Equal(2, v.Count(x => x));
    }

    // ── BuildNonProdPatternWindows: 투표 → 승격 ──────────────────────────

    [Fact]
    public void Repeated_lunch_is_promoted_single_incident_is_not()
    {
        // 14 활동일: 점심(12:00–13:00) 9일 반복(64%) + 08:00–12:00 사고 1일(7%)
        var votes = new List<bool[]>();
        for (int d = 0; d < 9; d++) votes.Add(DayVote((720, 780)));
        for (int d = 0; d < 4; d++) votes.Add(DayVote());
        votes.Add(DayVote((480, 720))); // 사고 하루

        var wins = OeeMath.BuildNonProdPatternWindows(votes, Slot, Ratio, MinDays);

        var w = Assert.Single(wins);
        Assert.Equal((720, 780), w); // 점심만 승격 — 단발 사고는 탈락
    }

    [Fact]
    public void Below_ratio_is_not_promoted()
    {
        // 8/14 = 57% < 60% → 미승격
        var votes = new List<bool[]>();
        for (int d = 0; d < 8; d++) votes.Add(DayVote((720, 780)));
        for (int d = 0; d < 6; d++) votes.Add(DayVote());
        Assert.Empty(OeeMath.BuildNonProdPatternWindows(votes, Slot, Ratio, MinDays));
    }

    [Fact]
    public void Exact_ratio_boundary_is_promoted()
    {
        // 9/15 = 정확히 0.6 → 승격(부동소수 경계 보호)
        var votes = new List<bool[]>();
        for (int d = 0; d < 9; d++) votes.Add(DayVote((0, 360)));
        for (int d = 0; d < 6; d++) votes.Add(DayVote());
        var wins = OeeMath.BuildNonProdPatternWindows(votes, Slot, Ratio, MinDays);
        Assert.Equal((0, 360), Assert.Single(wins));
    }

    [Fact]
    public void Insufficient_active_days_yields_no_windows()
    {
        // 매일 야간 정지라도 활동일 2 < 하한 3 → 창 미성립(가짜 창 금지)
        var votes = new List<bool[]> { DayVote((0, 360)), DayVote((0, 360)) };
        Assert.Empty(OeeMath.BuildNonProdPatternWindows(votes, Slot, Ratio, MinDays));
    }

    [Fact]
    public void Adjacent_promoted_slots_merge_into_one_window()
    {
        // 야간 00:00–06:00 + 점심 12:00–13:00, 전 활동일 반복 → 창 2개(병합·분리)
        var votes = new List<bool[]>();
        for (int d = 0; d < 5; d++) votes.Add(DayVote((0, 360), (720, 780)));
        var wins = OeeMath.BuildNonProdPatternWindows(votes, Slot, Ratio, MinDays);
        Assert.Equal(2, wins.Count);
        Assert.Equal((0, 360), wins[0]);
        Assert.Equal((720, 780), wins[1]);
    }

    [Fact]
    public void Full_day_votes_cap_at_1440()
    {
        var votes = new List<bool[]>();
        for (int d = 0; d < 5; d++) votes.Add(DayVote((0, 1440)));
        var wins = OeeMath.BuildNonProdPatternWindows(votes, Slot, Ratio, MinDays);
        Assert.Equal((0, 1440), Assert.Single(wins));
    }

    [Fact]
    public void Empty_votes_yield_no_windows()
    {
        Assert.Empty(OeeMath.BuildNonProdPatternWindows(new List<bool[]>(), Slot, Ratio, MinDays));
    }
}
