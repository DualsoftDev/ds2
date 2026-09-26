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

    // ── 정지 게이트 (doc/31 §3.1) ─────────────────────────────────────────
    // 실측 교훈: "다시 돌았나" 만 물으면 애초에 서지 않은 경고가 전부 고장으로 둔갑한다.
    // 현장 복구 23건 중 21건이 이것이었다(경고 배수 0.5~1.6 vs 멈춤 3.3·4.9 — 사이가 비어 있다).

    /// <summary>3분 리듬으로 도는 설비의 사이클 시작 시각.</summary>
    private static List<long> Beats(int count, long step = 3 * Min, long from = 0)
        => [.. Enumerable.Range(0, count).Select(i => from + i * step)];

    [Fact]
    public void 리듬이_안_끊겼으면_설비가_선_것이_아니다()
    {
        var starts = Beats(10);                       // 0,3,6,9,… 분
        var r = RhythmMs(starts);
        Assert.Equal(3 * Min, r);
        // 7분에 울린 알람 — 그 알람을 품은 간격은 6~9분(3분)이라 리듬 그대로다.
        Assert.Equal(StopVerdict.NonStopWarning, JudgeStop(7 * Min, starts, r));
    }

    [Fact]
    public void 리듬이_배수를_넘게_끊기면_멈춘_것이다()
    {
        // 9분 뒤 20분까지 사이클이 없다 = 11분 공백(리듬의 3.7배).
        List<long> starts = [0, 3 * Min, 6 * Min, 9 * Min, 20 * Min, 23 * Min, 26 * Min];
        Assert.Equal(StopVerdict.Stopped, JudgeStop(10 * Min, starts, RhythmMs(starts)));
    }

    [Fact]
    public void 리듬을_못_구하면_판정_불가지_경고가_아니다()
    {
        // 경고와 섞으면 v68 모델링 문제가 알람 축 통계로 둔갑한다.
        Assert.Equal(StopVerdict.Unknown, JudgeStop(5 * Min, [], 0));
        Assert.Equal(StopVerdict.Unknown, JudgeStop(5 * Min, [0], RhythmMs([0])));
    }

    [Fact]
    public void 관측_구간_끝에_걸리면_판정하지_않는다()
    {
        // 다음 사이클이 없는 것과 설비가 선 것은 다르다.
        var starts = Beats(5);
        Assert.Equal(StopVerdict.Unknown, JudgeStop(99 * Min, starts, RhythmMs(starts)));
    }

    // ── 재접속 스냅샷 (doc/31 §4.1) ───────────────────────────────────────

    [Fact]
    public void 통신이_붙는_순간의_발화는_스냅샷이다()
    {
        // 실측: 재접속 12:30:01 ↔ 발화 12:30:01(같은 초), 재접속 12:07:13 ↔ 발화 12:07:24(11초).
        Assert.True(IsLinkSnapshot(10 * Min, [10 * Min]));
        Assert.True(IsLinkSnapshot(10 * Min + 11_000, [10 * Min]));
        Assert.False(IsLinkSnapshot(10 * Min + 60_000, [10 * Min]));
        Assert.False(IsLinkSnapshot(10 * Min, []));
    }

    [Fact]
    public void 접속_이전의_발화는_스냅샷이_아니다()
    {
        Assert.False(IsLinkSnapshot(9 * Min, [10 * Min]));
    }

    // ── 사건 묶기 (doc/31 §2.1) ───────────────────────────────────────────

    [Fact]
    public void 한_정지에서_태그가_여럿_울리면_한_사건이다()
    {
        // 같은 디바이스의 서로 다른 태그 2개가 같은 정지에서 울렸다 — 사이에 재가동이 없다.
        List<long> starts = [0, 30 * Min];
        var events = BuildEvents(
            [A(5 * Min, 10 * Min, window: Hour), A(6 * Min, 12 * Min, window: Hour)], starts, nowMs: Hour);

        var e = Assert.Single(events);
        Assert.Equal(5 * Min, e.OnsetMs);          // 고장이 시작된 시각 = 최소 발생
        Assert.Equal(12 * Min, e.ClearedMs);       // 사건 해소 = 최대 해소
        Assert.Equal(2, e.Members.Count);
        Assert.Equal(30 * Min, e.Recovery.RestartMs);
    }

    [Fact]
    public void 사이에_재가동이_끼면_별개_사건이다()
    {
        List<long> starts = [12 * Min, 40 * Min];
        var events = BuildEvents(
            [A(5 * Min, 10 * Min, window: Hour), A(20 * Min, 30 * Min, window: Hour)], starts, nowMs: 2 * Hour);

        Assert.Equal(2, events.Count);
        Assert.Equal(12 * Min, events[0].Recovery.RestartMs);
        Assert.Equal(40 * Min, events[1].Recovery.RestartMs);
    }

    [Fact]
    public void 해소가_끝났어도_아직_안_돌았으면_같은_사건이다()
    {
        // ★경계는 해소가 아니라 재가동이다. [발생,해소] 로 겹침을 보면 2건으로 세어 틀린다.
        List<long> starts = [30 * Min];
        var events = BuildEvents(
            [A(5 * Min, 10 * Min, window: Hour), A(12 * Min, 20 * Min, window: Hour)], starts, nowMs: Hour);

        Assert.Single(events);
    }

    [Fact]
    public void 안_꺼지는_알람은_첫_재가동에서_흡수를_멈춘다()
    {
        // 실측 #셔틀 — 재접속 스냅샷 2건(미해소)이 12:56 의 진짜 고장을 삼키면 안 된다.
        // 삼키면 고장 건수가 줄어 가장 나쁜 설비가 가장 좋아 보인다(방향이 나쁘다).
        var starts = Beats(40);                                  // 3분마다 계속 돈다
        var events = BuildEvents(
            [A(1 * Min, null, window: Hour),                     // 래치 — 영영 안 꺼진다
             A(30 * Min, 32 * Min, window: Hour)],               // 그 뒤의 진짜 고장
            starts, nowMs: 2 * Hour);

        Assert.Equal(2, events.Count);
        Assert.Equal(RecoveryState.InProgress, events[0].Recovery.State);
        Assert.Equal(RecoveryState.Recovered, events[1].Recovery.State);
    }

    [Fact]
    public void 재가동이_아예_없으면_전부_한_사건이다()
    {
        var events = BuildEvents([A(0, null), A(5 * Min, null), A(9 * Hour, null)], [], nowMs: 10 * Hour);
        Assert.Equal(3, Assert.Single(events).Members.Count);
    }

    // ── 분모 = 가동시간 (doc/31 §4) ───────────────────────────────────────

    [Fact]
    public void 가동시간은_리듬이_유지된_간격만_더한다()
    {
        // 0~9분 정상(3분×3) · 9~30분 공백(정지) · 30~36분 정상(3분×2)
        List<long> starts = [0, 3 * Min, 6 * Min, 9 * Min, 30 * Min, 33 * Min, 36 * Min];
        Assert.Equal(9 * Min + 6 * Min, OperatingMs(starts, RhythmMs(starts), 0, 36 * Min));
    }

    [Fact]
    public void 가동시간은_조회_창으로_자른다()
    {
        // 창 잘림은 시간 합이라 '겹친 만큼'(doc/30 §7.2).
        var starts = Beats(11);                                   // 0~30분
        Assert.Equal(10 * Min, OperatingMs(starts, RhythmMs(starts), 5 * Min, 15 * Min));
    }

    [Fact]
    public void 사이클이_없는_구간은_분모에_안_들어간다()
    {
        // 야간·주말이 저절로 빠진다 — κ 를 읽지 않고 '가동시간 분모' 가 되는 길이다.
        Assert.Equal(0, OperatingMs([], 0, 0, Day));
    }

    // ── 집계 ──────────────────────────────────────────────────────────────

    private static FaultEvent Ev(long onset, long? cleared, long? restart, StopVerdict stop, SkipCause skip) =>
        new(onset, cleared, [0])
        {
            Recovery = restart is { } r
                ? new Recovery(RecoveryState.Recovered, r)
                : new Recovery(cleared is null ? RecoveryState.InProgress : RecoveryState.RestartUnconfirmed, null),
            Stop = stop,
            Skip = skip,
        };

    private static FaultEvent Stopped(long onset, long cleared, long restart) =>
        Ev(onset, cleared, restart, StopVerdict.Stopped, SkipCause.None);

    // 스코프 롤업은 겹치는 정지를 합치므로 구간이 필요하다. 테스트에서는 디바이스별 사건을 그대로 넘긴다.
    private static readonly Dictionary<string, List<(long, long)>> DevStops = [];

    private static DeviceSummary Dev(string system, string flow, string device,
        IReadOnlyList<FaultEvent> events, long operatingMs, int minSample = MinSample)
    {
        DevStops[system + "|" + flow + "|" + device] =
            [.. events.Where(e => e.Counts)
                .Select(e => (e.OnsetMs, e.Recovery.RestartMs is { } r && r > e.OnsetMs ? r : e.OnsetMs))];
        return AggregateDevice(system, flow, device, events, operatingMs, minSample);
    }

    private static List<(long, long)> Stops(params DeviceSummary[] devices) =>
        [.. devices.SelectMany(d => DevStops.TryGetValue(d.System + "|" + d.Flow + "|" + d.Device, out var v) ? v : [])];

    [Fact]
    public void eMTBF_는_가동시간을_고장_건수로_나눈_값이다()
    {
        // ★간격의 평균이 아니다 — 간격 평균은 관측창을 넘는 값을 낼 수 없어(실측 11.6분 = 상한의 91%)
        //   설비가 아니라 창 길이를 재게 된다.
        var d = Dev("Line1", "#1", "Conveyor1",
        [
            Stopped(0, 5 * Min, 10 * Min),
            Stopped(2 * Hour, 2 * Hour + Min, 2 * Hour + 10 * Min),
            Stopped(4 * Hour, 4 * Hour + Min, 4 * Hour + 10 * Min),
        ], operatingMs: 9 * Hour, minSample: 3);

        Assert.Equal(3, d.FaultCount);
        Assert.Equal(3 * Hour, d.EMtbfMs);
    }

    [Fact]
    public void 무정지_경고와_스냅샷은_고장이_아니다()
    {
        var d = Dev("Line1", "#1", "Conveyor1",
        [
            Stopped(0, 5 * Min, 10 * Min),
            Ev(1 * Hour, 1 * Hour, 1 * Hour + Min, StopVerdict.NonStopWarning, SkipCause.NonStopWarning),
            Ev(2 * Hour, null, null, StopVerdict.Unknown, SkipCause.LinkSnapshot),
            Ev(3 * Hour, 3 * Hour, null, StopVerdict.Unknown, SkipCause.UnknownStop),
        ], operatingMs: 4 * Hour, minSample: 1);

        Assert.Equal(1, d.FaultCount);
        Assert.Equal(1, d.NonStopWarningCount);
        Assert.Equal(1, d.LinkSnapshotCount);
        Assert.Equal(1, d.UnknownStopCount);
        Assert.Equal(4 * Hour, d.EMtbfMs);          // 분모는 고장 1건뿐
    }

    [Fact]
    public void eMTTR_은_발생에서_재가동까지의_평균이고_복구_완료만_센다()
    {
        var d = Dev("Line1", "#1", "Conveyor1",
        [
            Stopped(0, 5 * Min, 10 * Min),                                  // 10분
            Stopped(1 * Hour, 1 * Hour + Min, 1 * Hour + 30 * Min),         // 30분
            Ev(2 * Hour, 2 * Hour + Min, null, StopVerdict.Stopped, SkipCause.None),  // 미확정 — 건수엔 들되 평균엔 안 든다
        ], operatingMs: 3 * Hour, minSample: 1);

        Assert.Equal(3, d.FaultCount);
        Assert.Equal(2, d.RecoveredCount);
        Assert.Equal(20 * Min, d.EMttrMs);
    }

    [Fact]
    public void 표본이_모자라면_숫자_대신_null_이다()
    {
        var d = Dev("Line1", "#1", "Conveyor1", [Stopped(0, 5 * Min, 10 * Min)], operatingMs: Hour, minSample: 3);
        Assert.Equal(1, d.FaultCount);
        Assert.Null(d.EMtbfMs);
        Assert.Null(d.EMttrMs);
    }

    [Fact]
    public void 가동시간이_0_이면_eMTBF_를_내지_않는다()
    {
        var d = Dev("Line1", "#1", "Conveyor1",
        [
            Stopped(0, Min, 2 * Min),
            Stopped(Hour, Hour + Min, Hour + 2 * Min),
            Stopped(2 * Hour, 2 * Hour + Min, 2 * Hour + 2 * Min),
        ], operatingMs: 0, minSample: 3);

        Assert.Null(d.EMtbfMs);
    }

    // ── 스코프 롤업 (2026-09-22 개정) ────────────────────────────────────
    // 종전 1/Σλ(직렬 합산)를 버렸다. 그 식은 모든 디바이스가 같은 시간대에 함께 돌 때만 맞는데,
    // 실측에서 가동시간이 7.6~30.1시간으로 4배 차이 났고(서로 다른 라인이 섞임) 값이 29% 짧아졌다.

    [Fact]
    public void 스코프_값은_그_스코프_가동시간을_총고장으로_나눈_값이다()
    {
        // 서로 다른 시각에 선 두 디바이스 — 스코프는 4번 섰다.
        var a = Dev("L", "#A", "A", [Stopped(0, Min, 2 * Min), Stopped(2 * Hour, 2 * Hour + Min, 2 * Hour + 2 * Min)], 10 * Hour, minSample: 1);
        var b = Dev("L", "#A", "B", [Stopped(4 * Hour, 4 * Hour + Min, 4 * Hour + 2 * Min), Stopped(6 * Hour, 6 * Hour + Min, 6 * Hour + 2 * Min)], 10 * Hour, minSample: 1);

        // 두 디바이스가 같은 창에서 돌았으므로 그 창은 한 번만 센다.
        var scope = RollUp([a, b], Stops(a, b), operatingMs: 10 * Hour, minSample: 1);
        Assert.Equal(4, scope.FaultCount);
        Assert.Equal(10 * Hour / 4.0, scope.EMtbfMs!.Value, 3);
    }

    [Fact]
    public void 동시에_선_두_디바이스는_스코프를_한_번_세운_것이다()
    {
        // ★실측에서 사건 150건 중 88건이 다른 디바이스와 겹쳤다(병합 후 62건).
        //   그냥 더하면 건수가 부풀고 정지시간이 이중 계상된다 — OEE 가 downtimeMs 에 쓰는 union 과 같은 원칙.
        var a = Dev("L", "#A", "A", [Stopped(0, 5 * Min, 10 * Min)], 10 * Hour, minSample: 1);
        var b = Dev("L", "#A", "B", [Stopped(2 * Min, 6 * Min, 12 * Min)], 10 * Hour, minSample: 1);

        var scope = RollUp([a, b], Stops(a, b), operatingMs: 10 * Hour, minSample: 1);
        Assert.Equal(1, scope.FaultCount);            // 디바이스 합은 2건이지만 스코프는 한 번 섰다
        Assert.Equal(12 * Min, scope.TotalDownMs);    // 0~12분 union (합산이면 22분)
        Assert.Equal(10 * Hour / 1.0, scope.EMtbfMs);
    }

    [Fact]
    public void 겹치지_않는_정지는_합치지_않는다()
    {
        var a = Dev("L", "#A", "A", [Stopped(0, 5 * Min, 10 * Min)], 10 * Hour, minSample: 1);
        var b = Dev("L", "#A", "B", [Stopped(20 * Min, 25 * Min, 30 * Min)], 10 * Hour, minSample: 1);

        var scope = RollUp([a, b], Stops(a, b), operatingMs: 10 * Hour, minSample: 1);
        Assert.Equal(2, scope.FaultCount);
        Assert.Equal(20 * Min, scope.TotalDownMs);
    }

    [Fact]
    public void 복구되지_않은_사건은_건수엔_들되_정지시간엔_안_든다()
    {
        var a = Dev("L", "#A", "A",
            [Ev(0, null, null, StopVerdict.Stopped, SkipCause.None)], 10 * Hour, minSample: 1);

        var scope = RollUp([a], Stops(a), operatingMs: 10 * Hour, minSample: 1);
        Assert.Equal(1, scope.FaultCount);
        Assert.Equal(0, scope.TotalDownMs);
        Assert.Equal(0, scope.RecoveredCount);
        Assert.Null(scope.EMttrMs);
    }

    [Fact]
    public void 가동시간이_다른_디바이스를_섞어도_외삽하지_않는다()
    {
        // 7.6h 관측된 디바이스를 30h 돈 것처럼 늘려 잡던 것이 1/Σλ 의 결함이었다.
        var big = Dev("L", "#A", "A", [Stopped(0, Min, 2 * Min)], 30 * Hour, minSample: 1);
        var small = Dev("L", "#B", "B", [Stopped(5 * Hour, 5 * Hour + Min, 5 * Hour + 2 * Min)], 3 * Hour, minSample: 1);

        var scope = RollUp([big, small], Stops(big, small), operatingMs: 30 * Hour, minSample: 1);
        Assert.Equal(15 * Hour, scope.EMtbfMs!.Value, 3);      // 30h ÷ 2건

        // 옛 식(1/Σλ)이라면 1/(1/30 + 1/3) = 2.73h 로 5배 이상 짧게 나왔다.
        var oldWay = 1.0 / ((1.0 / (30 * Hour)) + (1.0 / (3 * Hour)));
        Assert.True(oldWay < scope.EMtbfMs!.Value / 5);
    }

    [Fact]
    public void 표본_미달_디바이스도_스코프_합산에_들어간다()
    {
        // 빼면 고장이 과소 계상되어 스코프 eMTBF 가 부풀어 오른다.
        var big = Dev("L", "#A", "A",
        [
            Stopped(0, Min, 2 * Min),
            Stopped(Hour, Hour + Min, Hour + 2 * Min),
            Stopped(2 * Hour, 2 * Hour + Min, 2 * Hour + 2 * Min),
        ], 6 * Hour);
        var small = Dev("L", "#A", "B", [Stopped(4 * Hour, 4 * Hour + Min, 4 * Hour + 2 * Min)], 6 * Hour);

        Assert.Null(small.EMtbfMs);
        var scope = RollUp([big, small], Stops(big, small), operatingMs: 6 * Hour);
        Assert.Equal(4, scope.FaultCount);
        Assert.Equal(6 * Hour / 4.0, scope.EMtbfMs!.Value, 3);
    }

    [Fact]
    public void 총_정지시간은_겹친_만큼만_센다()
    {
        // 같은 시각에 선 두 디바이스의 정지는 한 번의 정지다 — 더하면 실제보다 길어진다.
        var a = Dev("L", "#A", "A", [Stopped(0, Min, 10 * Min)], Hour, minSample: 1);
        var b = Dev("L", "#B", "B", [Stopped(0, Min, 20 * Min)], Hour, minSample: 1);

        Assert.Equal(20 * Min, RollUp([a, b], Stops(a, b), operatingMs: Hour, minSample: 1).TotalDownMs);
    }

    [Fact]
    public void 가동시간이_0_이면_스코프_eMTBF_를_내지_않는다()
    {
        var a = Dev("L", "#A", "A", [Stopped(0, Min, 2 * Min)], Hour, minSample: 1);
        Assert.Null(RollUp([a], Stops(a), operatingMs: 0, minSample: 1).EMtbfMs);
    }

    [Fact]
    public void 빈_입력은_0_건이고_숫자가_없다()
    {
        var scope = RollUp([], [], operatingMs: Hour, minSample: 1);
        Assert.Equal(0, scope.FaultCount);
        Assert.Null(scope.EMttrMs);
        Assert.Null(scope.EMtbfMs);
    }

    // ── 스코프 가동시간 = flow 합집합 ────────────────────────────────────

    [Fact]
    public void 동시에_돈_설비의_가동시간은_한_번만_센다()
    {
        // 단순 합으로 더하면 분모가 두 배가 되어 지표가 두 배 좋아 보인다.
        var f1 = ((IReadOnlyList<long>)Beats(11), 3.0 * Min);          // 0~30분
        var f2 = ((IReadOnlyList<long>)Beats(11), 3.0 * Min);          // 같은 구간

        Assert.Equal(30 * Min, OperatingMsUnion([f1, f2], 0, 30 * Min));
    }

    [Fact]
    public void 엇갈려_돈_설비는_겹친_만큼만_합친다()
    {
        var f1 = ((IReadOnlyList<long>)Beats(6), 3.0 * Min);                        // 0~15분
        var f2 = ((IReadOnlyList<long>)Beats(6, from: 9 * Min), 3.0 * Min);         // 9~24분

        Assert.Equal(24 * Min, OperatingMsUnion([f1, f2], 0, 60 * Min));
    }

    [Fact]
    public void 리듬이_끊긴_구간은_어느_설비에서도_가동이_아니다()
    {
        List<long> starts = [0, 3 * Min, 6 * Min, 30 * Min, 33 * Min];
        Assert.Equal(9 * Min, OperatingMsUnion([((IReadOnlyList<long>)starts, RhythmMs(starts))], 0, 33 * Min));
    }

    // ── 공유 디바이스 (Ds2 코어가 허용하는 모양) ──────────────────────────
    // Queries.findConflictingDeviceSystemType: "같은 devAlias 를 쓰는 Call 이 여러 Flow/Work 에 있어도".
    // 공유 디바이스가 고장 나면 그걸 쓰는 설비가 전부 서므로, 설비 롤업은 각 설비에 센다.
    // 그래서 설비 행의 합 > 전체가 되는데, 그건 중복이 아니라 사실이다.

    [Fact]
    public void 공유_디바이스의_고장은_쓰는_설비_모두에_센다()
    {
        // D 는 #A·#B 가 함께 쓴다. D 가 서면 두 설비가 다 선다.
        var shared = Dev("L", "#A·#B", "D", [Stopped(0, Min, 2 * Min)], 2 * Hour, minSample: 1);
        var onlyA  = Dev("L", "#A", "E", [Stopped(Hour, Hour + Min, Hour + 2 * Min)], 2 * Hour, minSample: 1);
        // 두 정지는 시각이 달라 병합되지 않는다 — 공유 여부만 보려는 테스트다.

        var a = RollUp([shared, onlyA], Stops(shared, onlyA), operatingMs: 2 * Hour, minSample: 1);   // 설비 #A
        var b = RollUp([shared], Stops(shared), operatingMs: 2 * Hour, minSample: 1);          // 설비 #B
        var all = RollUp([shared, onlyA], Stops(shared, onlyA), operatingMs: 2 * Hour, minSample: 1); // 전체(디바이스에서 직접)

        Assert.Equal(2, a.FaultCount);
        Assert.Equal(1, b.FaultCount);
        // 설비 합(3) > 전체(2) — 공유 디바이스가 양쪽에 세어진 결과다.
        Assert.True(a.FaultCount + b.FaultCount > all.FaultCount);
        Assert.Equal(2, all.FaultCount);
    }

    [Fact]
    public void 전체_값은_설비를_합치지_않고_디바이스에서_굴려_올린다()
    {
        // 설비 행을 더해 전체를 내면 공유 디바이스가 이중 계상된다 — 그래서 전체는 디바이스 목록으로 낸다.
        var shared = Dev("L", "#A·#B", "D",
        [
            Stopped(0, Min, 2 * Min),
            Stopped(Hour, Hour + Min, Hour + 2 * Min),
            Stopped(2 * Hour, 2 * Hour + Min, 2 * Hour + 2 * Min),
        ], 6 * Hour);

        var all = RollUp([shared], Stops(shared), operatingMs: 6 * Hour);
        Assert.Equal(3, all.FaultCount);
        Assert.Equal(2 * Hour, all.EMtbfMs);       // 6h ÷ 3건 — 설비 수와 무관
    }

    // ── 스코프 합치기 (2026-09-24) ───────────────────────────────────────
    // 겹침 규칙은 층마다 다르다. 같은 라인 안 디바이스는 같은 시간을 공유해 동시에 서면 한 번이지만,
    // 독립된 라인끼리는 동시에 서도 두 번의 사고이고 가동시간도 별개 자원이다.

    private static ScopeSummary Sc(string name, Summary totals) => new("system", name, totals);

    [Fact]
    public void 서로_다른_라인은_동시에_서도_따로_센다()
    {
        // 두 PLC 가 같은 시각에 섰다. union 하면 한 번으로 깎이지만, 라인이 다르므로 두 번이 맞다.
        var a = Dev("PLC-A", "#A", "A", [Stopped(0, 5 * Min, 10 * Min)], 10 * Hour, minSample: 1);
        var b = Dev("PLC-B", "#B", "B", [Stopped(0, 5 * Min, 10 * Min)], 10 * Hour, minSample: 1);

        var sa = RollUp([a], Stops(a), operatingMs: 10 * Hour, minSample: 1);
        var sb = RollUp([b], Stops(b), operatingMs: 10 * Hour, minSample: 1);
        var all = Combine([Sc("PLC-A", sa), Sc("PLC-B", sb)], minSample: 1);

        Assert.Equal(2, all.FaultCount);                 // union 이면 1건으로 깎였다
        Assert.Equal(20 * Min, all.TotalDownMs);         // union 이면 10분
        Assert.Equal(20 * Hour, all.OperatingMs);        // 가동시간도 별개 자원이라 합산
        Assert.Equal(20 * Hour / 2.0, all.EMtbfMs);
    }

    [Fact]
    public void 스코프가_하나면_그_값_그대로다()
    {
        var a = Dev("PLC-A", "#A", "A", [Stopped(0, 5 * Min, 10 * Min)], 10 * Hour, minSample: 1);
        var sa = RollUp([a], Stops(a), operatingMs: 10 * Hour, minSample: 1);

        var all = Combine([Sc("PLC-A", sa)], minSample: 1);
        Assert.Equal(sa.FaultCount, all.FaultCount);
        Assert.Equal(sa.EMtbfMs, all.EMtbfMs);
        Assert.Equal(sa.TotalDownMs, all.TotalDownMs);
    }

    [Fact]
    public void 스코프가_없으면_숫자가_없다()
    {
        var all = Combine([], minSample: 1);
        Assert.Equal(0, all.FaultCount);
        Assert.Null(all.EMtbfMs);
        Assert.Null(all.EMttrMs);
    }
}
