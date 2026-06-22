// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// 완료 마커 단일 규칙(<see cref="CycleCompletionResolver"/>) + OutTag↓(falling)로 분해된 사이클 검증.
/// "Tail 에 InTag 있으면 InTag↑, 없으면 OutTag↓(명령 ON 추정)" 규칙과, head==tail OutOnly Flow 가
/// 자기 OutTag 펄스폭을 MT 로 분해하는지(화면/재집계 공통 경로)를 순수 함수 수준에서 고정한다.
/// </summary>
public class CycleCompletionResolverTests
{
    [Fact]
    public void Resolve_InTag_present_uses_rising()
    {
        var tc = CycleCompletionResolver.Resolve(tailInTag: "I.tail", tailOutTag: "Q.tail");
        Assert.Equal("I.tail", tc.Tag);
        Assert.False(tc.Falling);                                   // InTag↑ = rising
        Assert.Equal(CycleCompletionResolver.CompletionSource.InTag, tc.Source);
    }

    [Fact]
    public void Resolve_OutOnly_falls_back_to_OutTag_falling()
    {
        var tc = CycleCompletionResolver.Resolve(tailInTag: null, tailOutTag: "Q.tail");
        Assert.Equal("Q.tail", tc.Tag);
        Assert.True(tc.Falling);                                    // OutTag↓ = falling (명령 종료)
        Assert.Equal(CycleCompletionResolver.CompletionSource.OutTag, tc.Source);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    public void Resolve_no_tags_is_None(string? inTag, string? outTag)
    {
        var tc = CycleCompletionResolver.Resolve(inTag, outTag);
        Assert.Null(tc.Tag);
        Assert.Equal(CycleCompletionResolver.CompletionSource.None, tc.Source);
    }

    [Theory]
    [InlineData(CycleCompletionResolver.CompletionSource.InTag, "InTag")]
    [InlineData(CycleCompletionResolver.CompletionSource.OutTag, "OutTag")]
    [InlineData(CycleCompletionResolver.CompletionSource.None, null)]
    public void SourceLabel_maps_to_dto_string(CycleCompletionResolver.CompletionSource src, string? expected)
        => Assert.Equal(expected, CycleCompletionResolver.SourceLabel(src));

    /// <summary>
    /// head==tail OutOnly: starts = OutTag↑(0,10,20s), 완료 = OutTag↓(2,12,22s).
    /// BuildCycles 가 각 사이클의 첫 falling 을 완료로 잡아 MT=펄스폭(2s), CT=주기(10s), WT=CT-MT=8s 로 분해해야 한다.
    /// </summary>
    [Fact]
    public void BuildCycles_with_falling_edges_yields_pulse_width_MT()
    {
        var t0 = new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Local);
        var starts = new[] { t0, t0.AddSeconds(10), t0.AddSeconds(20) };
        var falling = new[] { t0.AddSeconds(2), t0.AddSeconds(12), t0.AddSeconds(22) };
        var windowEnd = t0.AddSeconds(30);

        var cycles = CycleDerivation.BuildCycles(starts, falling, windowEnd);

        Assert.Equal(3, cycles.Count);
        // 사이클 1·2: MT=2000ms, CT=10000ms
        Assert.Equal(2000, cycles[0].ActiveMs);
        Assert.Equal(10000, cycles[0].PeriodMs);
        Assert.Equal(2000, cycles[1].ActiveMs);
        Assert.Equal(10000, cycles[1].PeriodMs);
        // 마지막(열린) 사이클: MT 는 windowEnd 전 falling(22s)로 잡히고, CT(주기)는 다음 start 없어 null
        Assert.Equal(2000, cycles[2].ActiveMs);
        Assert.Null(cycles[2].PeriodMs);
    }
}
