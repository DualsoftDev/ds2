// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// 경계 신호 해석(<see cref="CycleBoundaryEdges"/>, 2026-09-17) 고정 — 사이클의 시작/끝을 Call 이 아니라
/// <b>주소 + 에지</b>로 잡는 경로와, 태그를 고르지 않은 기존 저장분이 종전 Call 규칙 그대로 읽히는지를 함께 잠근다.
/// 태그 지정 경로(StartSignals/EndSignals)는 매퍼가 필요해 여기선 순수 부분(에지 정규화·Call 폴백·Spec 정규화)만 다룬다.
/// </summary>
public class CycleBoundarySignalTests
{
    private static CallTagPair Pair(string? inTag, string? outTag, string? inActive = null, string? outActive = null)
        => new(Guid.NewGuid(), inTag, outTag, inActive, outActive);

    // ── 에지 정규화 ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("falling", true)]
    [InlineData("FALLING", true)]
    [InlineData("  falling  ", true)]
    [InlineData("rising", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("오타", false)]     // 알 수 없는 값은 상승 — 경계가 조용히 반대로 뒤집히면 안 된다
    public void Edge_defaults_to_rising_unless_explicitly_falling(string? edge, bool expectFalling)
    {
        Assert.Equal(expectFalling, CycleBoundaryEdges.IsFallingEdge(edge));
        Assert.Equal(expectFalling ? "falling" : "rising", CycleBoundaryEdges.NormalizeEdge(edge));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("%QX0.1.8", true)]
    public void HasTagSpec_treats_blank_as_unset(string? addr, bool expected)
        => Assert.Equal(expected, CycleBoundaryEdges.HasTagSpec(addr));

    // ── Spec 정규화 ───────────────────────────────────────────────────────────

    [Fact]
    public void Spec_normalize_trims_address_and_pins_edge()
    {
        var s = new CycleBoundaryTagSpec("  %QX0.1.8 ", "FALLING", "  ", "falling").Normalized();

        Assert.Equal("%QX0.1.8", s.StartAddress);
        Assert.Equal("falling", s.StartEdge);
        Assert.Null(s.EndAddress);
        Assert.Null(s.EndEdge);      // 주소가 비면 에지도 비운다 — "Call 기준" 을 한 가지 상태로 유지
        Assert.False(s.IsEmpty);     // 시작만 지정돼도 태그 지정이 있는 것
    }

    [Fact]
    public void Spec_none_is_empty()
    {
        Assert.True(CycleBoundaryTagSpec.None.IsEmpty);
        Assert.True(new CycleBoundaryTagSpec(null, "rising", "", "falling").Normalized().IsEmpty);
    }

    // ── Call 기준 폴백(기존 저장분의 해석이 바뀌지 않아야 한다) ──────────────────

    [Fact]
    public void Start_from_pairs_is_union_of_OUT_rising()
    {
        var signals = CycleBoundaryEdges.StartSignalsFromPairs(
        [
            Pair(inTag: "I1", outTag: "Q1"),
            Pair(inTag: "I2", outTag: "Q2", outActive: "false"),
        ]);

        Assert.Equal(2, signals.Count);
        Assert.All(signals, s => Assert.False(s.Falling));                  // 시작 = 상승(활성 진입)
        Assert.Equal(["Q1", "Q2"], signals.Select(s => s.Address));
        Assert.Equal("false", signals[1].ActiveValue);                       // ValueSpec 활성값 보존
    }

    [Fact]
    public void Start_from_pairs_dedups_shared_OUT_address()
    {
        var signals = CycleBoundaryEdges.StartSignalsFromPairs(
        [
            Pair(inTag: "I1", outTag: "Q1"),
            Pair(inTag: "I2", outTag: "Q1"),        // 같은 주소·같은 활성값 = 엣지 조회 1회면 충분
        ]);

        Assert.Single(signals);
    }

    [Fact]
    public void Start_from_pairs_skips_pairs_without_OUT()
    {
        var signals = CycleBoundaryEdges.StartSignalsFromPairs([Pair(inTag: "I1", outTag: null)]);
        Assert.Empty(signals);   // 시작 경계 미해석 → 호출측이 파괴적 재도출을 건너뛰는 근거
    }

    [Fact]
    public void End_from_pairs_prefers_IN_rising_and_labels_InTag()
    {
        var (signals, label) = CycleBoundaryEdges.EndSignalsFromPairs([Pair(inTag: "I1", outTag: "Q1")]);

        Assert.Single(signals);
        Assert.Equal("I1", signals[0].Address);
        Assert.False(signals[0].Falling);
        Assert.Equal("InTag", label);
    }

    [Fact]
    public void End_from_pairs_falls_back_to_OUT_falling_when_no_IN()
    {
        var (signals, label) = CycleBoundaryEdges.EndSignalsFromPairs([Pair(inTag: null, outTag: "Q1")]);

        Assert.Single(signals);
        Assert.Equal("Q1", signals[0].Address);
        Assert.True(signals[0].Falling);      // OutOnly 추정 = 명령 종료
        Assert.Equal("OutTag", label);
    }

    [Fact]
    public void End_from_pairs_mixed_reports_InTag_and_drops_unobservable()
    {
        var (signals, label) = CycleBoundaryEdges.EndSignalsFromPairs(
        [
            Pair(inTag: "I1", outTag: "Q1"),
            Pair(inTag: null, outTag: "Q2"),
            Pair(inTag: null, outTag: null),   // 관측 불가 — AND 에 넣으면 영구 미완료
        ]);

        Assert.Equal(2, signals.Count);
        Assert.Equal("InTag", label);          // 정통 쌍이 하나라도 있으면 '명령 ON 추정' 배지를 띄우지 않는다
    }

    [Fact]
    public void End_from_pairs_without_any_tag_has_no_label()
    {
        var (signals, label) = CycleBoundaryEdges.EndSignalsFromPairs([Pair(null, null)]);
        Assert.Empty(signals);
        Assert.Null(label);
    }

    // ── union 유틸 ────────────────────────────────────────────────────────────

    [Fact]
    public void UnionSorted_merges_and_dedups_by_instant()
    {
        var t0 = new DateTime(2026, 9, 17, 9, 0, 0, DateTimeKind.Local);
        var merged = CycleBoundaryEdges.UnionSorted(
        [
            [t0.AddSeconds(3), t0],
            [t0.AddSeconds(3), t0.AddSeconds(1)],
        ]);

        Assert.Equal([t0, t0.AddSeconds(1), t0.AddSeconds(3)], merged);
    }
}
