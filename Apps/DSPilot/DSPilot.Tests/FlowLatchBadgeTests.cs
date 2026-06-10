// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System;
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// <see cref="FlowLatchBadge"/> 순수 함수 단위 테스트 — head-start→tail-complete 엣지 래치 배지의
/// 상태함수·적격(eligibility)·워치독 abandon 판정. 수용 기준 1~5(Body 가동중 유지 / 완료→대기 / 박제 해제 /
/// 엣지 유실 복구 후보 / 복수·미정의 폴백)을 코드로 고정한다.
/// </summary>
public class FlowLatchBadgeTests
{
    private static readonly DateTime Now = new(2026, 6, 9, 12, 0, 0, DateTimeKind.Local);

    // ── 1. 배지 상태함수 Compute ────────────────────────────────────────

    [Fact]
    public void Compute_cycle_active_is_Going_regardless_of_previous_finish()
    {
        Assert.Equal(FlowLatchBadge.Going, FlowLatchBadge.Compute(true, null, Now));
        Assert.Equal(FlowLatchBadge.Going, FlowLatchBadge.Compute(true, Now.AddMilliseconds(-10), Now));
    }

    [Fact]
    public void Compute_within_finish_hold_is_Finish()
    {
        var prevFinish = Now.AddMilliseconds(-100); // hold=250 → 100<250
        Assert.Equal(FlowLatchBadge.Finish, FlowLatchBadge.Compute(false, prevFinish, Now));
    }

    [Fact]
    public void Compute_past_finish_hold_is_Ready()
    {
        var prevFinish = Now.AddMilliseconds(-300); // 300>=250
        Assert.Equal(FlowLatchBadge.Ready, FlowLatchBadge.Compute(false, prevFinish, Now));
    }

    [Fact]
    public void Compute_finish_hold_boundary_is_exclusive_so_Ready()
    {
        var prevFinish = Now.AddMilliseconds(-FlowLatchBadge.FinishHoldMs); // 정확히 250 → Ready
        Assert.Equal(FlowLatchBadge.Ready, FlowLatchBadge.Compute(false, prevFinish, Now));
    }

    [Fact]
    public void Compute_no_previous_finish_is_Ready()
        => Assert.Equal(FlowLatchBadge.Ready, FlowLatchBadge.Compute(false, null, Now));

    [Fact]
    public void Compute_future_previous_finish_clock_skew_is_Ready_not_Finish()
    {
        // PreviousCycleFinish 가 미래(시계 보정/스큐) → 음수 경과는 Finish 로 오인하지 않는다.
        var prevFinish = Now.AddMilliseconds(50);
        Assert.Equal(FlowLatchBadge.Ready, FlowLatchBadge.Compute(false, prevFinish, Now));
    }

    [Fact]
    public void Compute_scenario_body_window_stays_Going()
    {
        // 수용 기준 1: head 끝~tail 시작 사이(중간 Call 진행)에도 래치는 열린 상태 → 배지 "가동중" 유지.
        Assert.Equal(FlowLatchBadge.Going, FlowLatchBadge.Compute(isCycleActive: true, previousCycleFinish: null, now: Now));
    }

    [Fact]
    public void Compute_scenario_tail_finish_then_settles()
    {
        // 수용 기준 2: tail 완료 직후 "완료" 잠깐, hold 만료 후 "대기".
        var finishAt = Now;
        Assert.Equal(FlowLatchBadge.Finish, FlowLatchBadge.Compute(false, finishAt, finishAt.AddMilliseconds(100)));
        Assert.Equal(FlowLatchBadge.Ready, FlowLatchBadge.Compute(false, finishAt, finishAt.AddMilliseconds(400)));
    }

    // ── 2. 적격 판정 IsEligible ─────────────────────────────────────────

    [Fact]
    public void IsEligible_explicit_override_is_eligible()
        // 토폴로지가 복수(2/2)여도 명시 override 만으로 적격.
        => Assert.True(Eligible(hasOverride: true, headCount: 2, tailCount: 2));

    [Fact]
    public void IsEligible_head_and_tail_labels_is_eligible()
        // 토폴로지가 복수(2/2)여도 Head&Tail 라벨이 둘 다 있으면 적격.
        => Assert.True(Eligible(hasHead: true, hasTail: true, headCount: 2, tailCount: 2));

    [Fact]
    public void IsEligible_head_label_only_without_single_topology_is_not_eligible()
        => Assert.False(Eligible(hasHead: true, hasTail: false, headCount: 2, tailCount: 1));

    [Fact]
    public void IsEligible_single_topology_is_eligible()
        => Assert.True(Eligible(headCount: 1, tailCount: 1));

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void IsEligible_non_single_topology_without_labels_is_not_eligible(int headCount, int tailCount)
        => Assert.False(Eligible(headCount: headCount, tailCount: tailCount));

    [Fact]
    public void IsEligible_ambiguous_head_demotes_even_with_single_topology()
        => Assert.False(Eligible(headCount: 1, tailCount: 1, headAmbiguous: true));

    [Fact]
    public void IsEligible_ambiguous_tail_demotes_even_with_override()
        => Assert.False(Eligible(hasOverride: true, tailAmbiguous: true));

    [Fact]
    public void IsEligible_ambiguous_demotes_even_with_labels_and_single_topology()
        => Assert.False(Eligible(hasHead: true, hasTail: true, headCount: 1, tailCount: 1, headAmbiguous: true));

    // ── 3. 워치독 abandon 판정 ShouldAbandon ────────────────────────────

    [Fact]
    public void ShouldAbandon_open_cycle_beyond_max_is_true()
        => Assert.True(FlowLatchBadge.ShouldAbandon(true, Now.AddMilliseconds(-2000), maxMs: 1000, Now));

    [Fact]
    public void ShouldAbandon_open_cycle_within_max_is_false()
        => Assert.False(FlowLatchBadge.ShouldAbandon(true, Now.AddMilliseconds(-500), maxMs: 1000, Now));

    [Fact]
    public void ShouldAbandon_boundary_is_exclusive()
        => Assert.False(FlowLatchBadge.ShouldAbandon(true, Now.AddMilliseconds(-1000), maxMs: 1000, Now));

    [Fact]
    public void ShouldAbandon_no_max_limit_never_abandons()
        => Assert.False(FlowLatchBadge.ShouldAbandon(true, Now.AddMilliseconds(-999999), maxMs: 0, Now));

    [Fact]
    public void ShouldAbandon_closed_cycle_is_false()
        => Assert.False(FlowLatchBadge.ShouldAbandon(false, Now.AddMilliseconds(-999999), maxMs: 1000, Now));

    [Fact]
    public void ShouldAbandon_null_start_is_false()
        => Assert.False(FlowLatchBadge.ShouldAbandon(true, null, maxMs: 1000, Now));

    private static bool Eligible(
        bool hasOverride = false,
        bool hasHead = false,
        bool hasTail = false,
        int headCount = 1,
        int tailCount = 1,
        bool headAmbiguous = false,
        bool tailAmbiguous = false)
        => FlowLatchBadge.IsEligible(hasOverride, hasHead, hasTail, headCount, tailCount, headAmbiguous, tailAmbiguous);
}
