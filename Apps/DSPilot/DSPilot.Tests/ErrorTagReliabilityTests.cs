// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Kpi;
using Xunit;
using static DSPilot.Kpi.ErrorTagReliability;

namespace DSPilot.Tests;

/// <summary>
/// 등록 에러 태그 기반 신뢰성 지표(eMTBF · eMTTR) 판정 규칙 — doc/31.
/// </summary>
public class ErrorTagReliabilityTests
{
    private const long Min = 60_000;
    private const long Hour = 60 * Min;
    private const long Day = 24 * Hour;

    private static AlertInput A(long occurred, long? cleared, long window = 10 * Min) =>
        new(occurred, cleared, window);

    // ── 회복 판정 ──────────────────────────────────────────────────────────

    [Fact]
    public void 해소가_없으면_진행_중이다()
    {
        var r = Resolve(A(0, null), [5 * Min], nowMs: Hour);
        Assert.Equal(RecoveryState.InProgress, r.State);
        Assert.Null(r.RestartMs);
    }

    [Fact]
    public void 해소_뒤_창_안에_재가동이_오면_복구_완료다()
    {
        var r = Resolve(A(0, 5 * Min), [8 * Min], nowMs: Hour);
        Assert.Equal(RecoveryState.Recovered, r.State);
        Assert.Equal(8 * Min, r.RestartMs);
    }

    [Fact]
    public void 해소_이전의_재가동은_인정하지_않는다()
    {
        // 알람이 걸려 있는 동안 옆 사이클이 돌았다고 회복으로 치면 안 된다.
        // 해소 전 재가동만 있으면 창이 지났을 때 '미확인' 으로 떨어져야 한다.
        var r = Resolve(A(0, 5 * Min), [1 * Min, 2 * Min, 3 * Min], nowMs: Hour);
        Assert.Equal(RecoveryState.RestartUnconfirmed, r.State);
        Assert.Null(r.RestartMs);
    }

    [Fact]
    public void 해소와_같은_순간의_재가동도_인정하지_않는다()
    {
        // 해소를 만든 신호와 같은 ms 에 시작된 사이클은 그 해소의 결과일 수 없다.
        var r = Resolve(A(0, 5 * Min), [5 * Min], nowMs: Hour);
        Assert.NotEqual(RecoveryState.Recovered, r.State);
    }

    [Fact]
    public void 창이_아직_안_지났으면_미확인이_아니라_대기다()
    {
        // 방금 해소된 건을 바로 경고로 찍으면 정상 복구 중인 건이 전부 빨갛게 뜬다.
        var r = Resolve(A(0, 5 * Min, window: 10 * Min), [], nowMs: 10 * Min);
        Assert.Equal(RecoveryState.AwaitingRestart, r.State);
    }

    [Fact]
    public void 창을_넘기면_재가동_미확인이다()
    {
        var r = Resolve(A(0, 5 * Min, window: 10 * Min), [], nowMs: 20 * Min);
        Assert.Equal(RecoveryState.RestartUnconfirmed, r.State);
    }

    [Fact]
    public void 창을_넘겨_도착한_재가동은_회복_근거가_아니다()
    {
        // 무한정 기다리면 6개월 뒤 사이클로도 확정이 되어 버린다.
        var r = Resolve(A(0, 5 * Min, window: 10 * Min), [60 * Min], nowMs: 2 * Hour);
        Assert.Equal(RecoveryState.RestartUnconfirmed, r.State);
        Assert.Null(r.RestartMs);
    }

    [Fact]
    public void 창이_0_이면_무한이라_늦은_재가동도_인정한다()
    {
        var r = Resolve(A(0, 5 * Min, window: 0), [60 * Min], nowMs: 2 * Hour);
        Assert.Equal(RecoveryState.Recovered, r.State);
        Assert.Equal(60 * Min, r.RestartMs);
    }

    [Fact]
    public void 재가동_목록에서_해소_직후_첫_사이클을_고른다()
    {
        var r = Resolve(A(0, 5 * Min, window: Hour), [1 * Min, 4 * Min, 6 * Min, 7 * Min, 30 * Min], nowMs: 2 * Hour);
        Assert.Equal(6 * Min, r.RestartMs);
    }

    // ── 비생산 차감 ────────────────────────────────────────────────────────

    [Fact]
    public void 주말이_수리_시간에_들어가지_않는다()
    {
        // ★금요일 19시에 고치고 월요일 08시에 라인이 돌면 차감 없이 61시간이 찍힌다.
        long occurred = 0;
        long repairedAndCleared = 1 * Hour;     // 1시간 만에 고침
        long restart = 61 * Hour;               // 라인은 월요일 아침에야 돈다
        var weekend = new List<Span> { new(2 * Hour, 60 * Hour) };  // 58시간 비생산

        var gross = ElapsedExcludingNonProduction(occurred, restart, []);
        var net = ElapsedExcludingNonProduction(occurred, restart, weekend);

        Assert.Equal(61 * Hour, gross);
        Assert.Equal(3 * Hour, net);            // 61 − 58
        Assert.True(net < gross / 10);
        _ = repairedAndCleared;
    }

    [Fact]
    public void 비생산이_구간을_전부_덮으면_0_이다()
    {
        var net = ElapsedExcludingNonProduction(10, 20, [new Span(0, 100)]);
        Assert.Equal(0, net);
    }

    [Fact]
    public void 차감이_총량을_넘어도_음수가_되지_않는다()
    {
        // 겹치는 구간이 섞여 들어오면 합이 총량을 넘을 수 있다 — 음수 지표를 내보내지 않는다.
        var net = ElapsedExcludingNonProduction(0, 10, [new Span(0, 10), new Span(0, 10)]);
        Assert.Equal(0, net);
    }

    [Fact]
    public void 끝이_시작보다_빠르면_0_이다()
    {
        Assert.Equal(0, ElapsedExcludingNonProduction(100, 50, []));
    }

    // ── 재발화 병합 ────────────────────────────────────────────────────────

    [Fact]
    public void 회복_없이_다시_울리면_한_건이다()
    {
        // 채터링·반복 발화가 고장 건수를 부풀리면 eMTBF 가 무너진다.
        var kept = DedupeReignitions(
        [
            (0, 10 * Min),        // 복구됨
            (2 * Min, null),      // 복구 전 재발화 → 병합
            (5 * Min, null),      // 역시 병합
            (20 * Min, 30 * Min), // 복구 이후 → 새 고장
        ]);

        Assert.Equal([0, 3], kept);
    }

    [Fact]
    public void 복구되지_않은_건_뒤의_발생은_전부_같은_고장이다()
    {
        var kept = DedupeReignitions([(0, null), (5 * Min, null), (9 * Hour, null)]);
        Assert.Equal([0], kept);
    }

    [Fact]
    public void 복구_시각과_같은_순간의_재발화도_병합한다()
    {
        var kept = DedupeReignitions([(0, 10 * Min), (10 * Min, null)]);
        Assert.Equal([0], kept);
    }

    // ── 집계 ──────────────────────────────────────────────────────────────

    private static (AlertInput, Recovery) Done(long occurred, long cleared, long restart) =>
        (A(occurred, cleared), new Recovery(RecoveryState.Recovered, restart));

    [Fact]
    public void eMTTR_은_발생에서_재가동까지의_평균이다()
    {
        var s = Aggregate(
        [
            Done(0, 5 * Min, 10 * Min),          // 10분
            Done(1 * Hour, 0, 1 * Hour + 20 * Min),  // 20분
            Done(5 * Hour, 0, 5 * Hour + 30 * Min),  // 30분
        ], [], minSample: 3);

        Assert.Equal(3, s.RecoveredCount);
        Assert.Equal(20 * Min, s.EMttrMs);
    }

    [Fact]
    public void eMTBF_는_직전_복구에서_다음_발생까지의_평균이다()
    {
        // 복구 10분 → 발생 1시간(50분) · 복구 1h20m → 발생 5시간(3h40m) · 복구 5h30m → 발생 7시간(1h30m)
        var s = Aggregate(
        [
            Done(0, 5 * Min, 10 * Min),
            Done(1 * Hour, 0, 1 * Hour + 20 * Min),
            Done(5 * Hour, 0, 5 * Hour + 30 * Min),
            Done(7 * Hour, 0, 7 * Hour + 10 * Min),
        ], [], minSample: 3);

        Assert.Equal(3, s.MtbfIntervalCount);
        Assert.Equal((50 * Min + 220 * Min + 90 * Min) / 3.0, s.EMtbfMs);
    }

    [Fact]
    public void 고장_건수는_발생_전체이고_eMTTR_분모는_복구_완료만이다()
    {
        // ★미확정 건을 건수에서도 빼면 고장이 과소 계상되어 eMTBF 가 부풀어 오른다.
        var s = Aggregate(
        [
            Done(0, 5 * Min, 10 * Min),
            (A(1 * Hour, null), new Recovery(RecoveryState.InProgress, null)),
            (A(2 * Hour, 2 * Hour + Min), new Recovery(RecoveryState.RestartUnconfirmed, null)),
            (A(3 * Hour, 3 * Hour + Min), new Recovery(RecoveryState.AwaitingRestart, null)),
        ], [], minSample: 1);

        Assert.Equal(4, s.FaultCount);
        Assert.Equal(1, s.RecoveredCount);
        Assert.Equal(1, s.InProgressCount);
        Assert.Equal(1, s.RestartUnconfirmedCount);
        Assert.Equal(1, s.AwaitingRestartCount);
    }

    [Fact]
    public void 복구되지_않은_건은_eMTBF_간격을_만들지_않는다()
    {
        // 복구를 모르면 그 사이가 가동 시간이었다고 주장할 근거가 없다.
        var s = Aggregate(
        [
            (A(0, null), new Recovery(RecoveryState.InProgress, null)),
            (A(1 * Hour, null), new Recovery(RecoveryState.InProgress, null)),
        ], [], minSample: 1);

        Assert.Equal(0, s.MtbfIntervalCount);
        Assert.Null(s.EMtbfMs);
    }

    [Fact]
    public void 표본이_모자라면_숫자_대신_null_이다()
    {
        var s = Aggregate([Done(0, 5 * Min, 10 * Min)], [], minSample: 3);

        Assert.Equal(1, s.RecoveredCount);
        Assert.Null(s.EMttrMs);
        Assert.True(s.MttrBelowSample);
    }

    [Fact]
    public void 집계도_비생산을_차감한다()
    {
        // 발생 0 → 재가동 10시간이지만 그중 9시간이 비생산이면 수리 시간은 1시간이다.
        var s = Aggregate([Done(0, 30 * Min, 10 * Hour)], [new Span(1 * Hour, 10 * Hour)], minSample: 1);
        Assert.Equal(1 * Hour, s.EMttrMs);
    }

    [Fact]
    public void 빈_입력은_0_건이고_숫자가_없다()
    {
        var s = Aggregate([], [], minSample: 1);
        Assert.Equal(0, s.FaultCount);
        Assert.Null(s.EMttrMs);
        Assert.Null(s.EMtbfMs);
    }
}
