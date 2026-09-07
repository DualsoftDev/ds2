// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;
using Xunit;
using static DSPilot.Services.SignalDebounce;

namespace DSPilot.Tests;

/// <summary>
/// 채터링 필터(<see cref="SignalDebounce"/>) — "minStableMs 미만 유지 상태 변화 무시" 정의 고정.
/// 실측 사례(2026-09-07 #121 head OUT): 긴 ON → 0.29초 OFF → 30초 ON → 20초 OFF → ON(다음 사이클).
/// 필터 1000ms 이면 0.29초 OFF 는 사라지고(재상승 = 시작 아님) 20초 OFF 뒤 상승만 시작이 된다.
/// </summary>
public class SignalDebounceTests
{
    private static readonly DateTime T0 = new(2026, 9, 7, 10, 21, 22, 993, DateTimeKind.Local);
    private static DateTime At(double sec) => T0.AddSeconds(sec);

    private static List<Transition> Site121Pattern() =>
    [
        new(At(0), true),          // 10:21:22.993 ON (실제 시작)
        new(At(317.03), false),    // 10:26:40.020 OFF  ─┐ 0.29초 글리치
        new(At(317.32), true),     // 10:26:40.312 ON   ─┘ (현행 규칙에선 가짜 시작)
        new(At(347.02), false),    // 10:27:10.008 OFF  (20초 실제 OFF)
        new(At(367.66), true),     // 10:27:30.650 ON   (다음 실제 시작)
    ];

    [Fact]
    public void Site121_short_off_glitch_is_merged_with_1000ms()
    {
        var r = Filter(Site121Pattern(), 1000);
        Assert.Equal(3, r.Count);
        Assert.Equal((At(0), true), (r[0].At, r[0].Active));
        Assert.Equal((At(347.02), false), (r[1].At, r[1].Active));
        Assert.Equal((At(367.66), true), (r[2].At, r[2].Active));
    }

    [Fact]
    public void Zero_minStable_is_passthrough()
    {
        var input = Site121Pattern();
        var r = Filter(input, 0);
        Assert.Equal(input.Count, r.Count);
        Assert.Equal(input, r);
    }

    [Fact]
    public void Glitch_equal_to_threshold_is_kept()
    {
        // 유지 시간 == minStableMs 는 안정(≥) — 경계값 포함.
        var r = Filter(Site121Pattern(), 290);
        Assert.Equal(5, r.Count);
    }

    [Fact]
    public void Short_on_pulse_is_dropped()
    {
        var input = new List<Transition>
        {
            new(At(0), true), new(At(0.3), false),   // 0.3초 ON 펄스 — 오감지
            new(At(10), true), new(At(20), false),    // 10초 실제 동작
        };
        var r = Filter(input, 1000);
        Assert.Equal(2, r.Count);
        Assert.Equal(At(10), r[0].At);
        Assert.True(r[0].Active);
        Assert.Equal(At(20), r[1].At);
        Assert.False(r[1].Active);
    }

    [Fact]
    public void Chatter_burst_settles_at_last_transition()
    {
        var input = new List<Transition>
        {
            new(At(0), true), new(At(0.1), false), new(At(0.2), true), new(At(0.3), false),
            new(At(0.4), true),      // 여기서 안정 → 상승 시각 = 0.4s
            new(At(30), false),
        };
        var r = Filter(input, 1000);
        Assert.Equal(2, r.Count);
        Assert.Equal(At(0.4), r[0].At);
        Assert.True(r[0].Active);
        Assert.Equal(At(30), r[1].At);
    }

    [Fact]
    public void Intervals_merge_short_off_and_keep_open_tail()
    {
        var wEnd = At(400);
        var ivs = new List<(DateTime, DateTime)>
        {
            (At(0), At(317.03)),
            (At(317.32), At(347.02)),
            (At(367.66), wEnd),        // 창 끝까지 ON(열린 구간)
        };
        var r = FilterIntervals(ivs, 1000, wEnd);
        Assert.Equal(2, r.Count);
        Assert.Equal((At(0), At(347.02)), r[0]);
        Assert.Equal((At(367.66), wEnd), r[1]);
    }

    [Fact]
    public void Intervals_drop_short_on_pulse()
    {
        var ivs = new List<(DateTime, DateTime)> { (At(0), At(0.2)), (At(5), At(15)) };
        var r = FilterIntervals(ivs, 1000, At(100));
        Assert.Single(r);
        Assert.Equal((At(5), At(15)), r[0]);
    }
}
