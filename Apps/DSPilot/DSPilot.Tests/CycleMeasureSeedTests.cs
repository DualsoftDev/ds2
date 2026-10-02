// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Kpi;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// 사이클 경계를 <b>주소(태그 지정)</b>로 고른 경우의 work 구간 측정(doc/30 §2.2.1, 2026-10-02 개정).
/// 사용자가 지목한 신호가 사이클 시작이므로 그 주소의 call 은 §2.2 의 OUT↑ 가 아니라 경계(cs)에서 구간을 연다.
/// <list type="bullet">
///   <item>Anchored(call 에 OUT 있음): OUT↑ 구간은 버리고 [cs, 첫 완료 마커) 하나. 경계보다 앞서 뜬 OUT↑ 구간이
///     앞 사이클 MT 를 경계까지 늘려 WT 를 0 으로 만들던 것을 막는다.</item>
///   <item>시드(IN 전용 센서): work 봉투만 cs 에서 열고 끝은 형제 call 이 정한다.</item>
/// </list>
/// </summary>
public class CycleMeasureSeedTests
{
    private static readonly HashSet<string> NoExclusion = new(StringComparer.OrdinalIgnoreCase);

    private static CycleIngestService.CallSpanSet Call(string name, string work, params (long S, long E)[] spans)
        => new(name, work, spans.Select(x => new Span(x.S, x.E)).ToList());

    private static CycleIngestService.CallSpanSet CallWithMarkers(
        string name, string work, (long S, long E)[] spans, params long[] markers)
        => new(name, work, spans.Select(x => new Span(x.S, x.E)).ToList(), markers.OrderBy(x => x).ToList());

    private static CycleIngestService.BoundaryHead Seed(string call, string work) => new(call, work, Anchored: false);
    private static CycleIngestService.BoundaryHead Anchor(string call, string work) => new(call, work, Anchored: true);

    // ── 시드(IN 전용 센서) — 종전 규칙 유지 ─────────────────────────────────────

    [Fact]
    public void head_없이_재면_work_는_형제_call_이_뜨는_시각부터_잡힌다()
    {
        // 사이클 [1000, 5000). 투입 work 의 실제 동작은 1800 부터 — 경계(1000)의 IN 전용 call 은 구간이 없다.
        var calls = new[] { Call("전진", "투입", (1800, 2600)) };

        var (works, mt, _) = CycleIngestService.MeasureCycle(
            calls, NoExclusion, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, head: null);

        Assert.Equal(("투입", 800L), Assert.Single(works));
        Assert.Equal(1600L, mt);   // 경계 → 마지막 work 끝
    }

    [Fact]
    public void 시드_head_는_work_를_사이클_시작부터_잡는다()
    {
        var calls = new[] { Call("전진", "투입", (1800, 2600)) };

        var (works, mt, _) = CycleIngestService.MeasureCycle(
            calls, NoExclusion, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, head: Seed("감지", "투입"));

        Assert.Equal(("투입", 1600L), Assert.Single(works));   // 1000 → 2600
        Assert.Equal(1600L, mt);                               // MT 는 그대로 — 시드는 끝에 기여하지 않는다
    }

    [Fact]
    public void 시드한_work_에_다른_call_이_없으면_폭_0_이라_제외된다()
    {
        // 관측 전용 work — 지속시간이 원리상 없다. 유령 행을 만들지 않는다.
        var (works, mt, _) = CycleIngestService.MeasureCycle(
            [], NoExclusion, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, head: Seed("감지", "감지"));

        Assert.Empty(works);
        Assert.Null(mt);
    }

    [Fact]
    public void 시드는_다른_work_를_건드리지_않는다()
    {
        var calls = new[]
        {
            Call("전진", "투입", (1800, 2600)),
            Call("용접", "가공", (2800, 4000)),
        };

        var (works, mt, _) = CycleIngestService.MeasureCycle(
            calls, NoExclusion, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, head: Seed("감지", "투입"));

        Assert.Equal(1600L, works.Single(w => w.Work == "투입").DurationMs);
        Assert.Equal(1200L, works.Single(w => w.Work == "가공").DurationMs);
        Assert.Equal(3000L, mt);
    }

    [Fact]
    public void 시드해도_경계_초과_판정은_변하지_않는다()
    {
        var calls = new[] { Call("전진", "투입", (1800, 5300)) };

        var (_, _, overflow) = CycleIngestService.MeasureCycle(
            calls, NoExclusion, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, head: Seed("감지", "투입"));

        Assert.Equal(300L, overflow);
    }

    [Fact]
    public void 제외된_call_만_있는_시드_work_는_열리기만_하고_폭이_없다()
    {
        var calls = new[] { Call("전진", "투입", (1800, 2600)) };
        var excluded = new HashSet<string>(["전진"], StringComparer.OrdinalIgnoreCase);

        var (works, mt, _) = CycleIngestService.MeasureCycle(
            calls, excluded, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, head: Seed("감지", "투입"));

        Assert.Empty(works);
        Assert.Null(mt);
    }

    // ── 경계 고정(Anchored) — OUT 이 있는 call 을 IN 또는 하강 에지로 경계 삼은 경우 ─────────

    [Fact]
    public void 경계_고정_head_는_OUT상승_구간_대신_경계에서_첫_마커까지_잰다()
    {
        // head = 전진 의 IN↑(=1000). 전진의 OUT↑ 구간은 [600, 1000)(앞 사이클에서 시작) — 버려진다.
        // 이 사이클 안 전진의 다음 마커(IN↑)는 3000.
        var calls = new[]
        {
            CallWithMarkers("전진", "투입", spans: [(600, 1000)], markers: [1000, 3000]),
            Call("용접", "가공", (1500, 2500)),
        };

        var (works, mt, _) = CycleIngestService.MeasureCycle(
            calls, NoExclusion, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, head: Anchor("전진", "투입"));

        Assert.Equal(2000L, works.Single(w => w.Work == "투입").DurationMs);   // 1000 → 3000
        Assert.Equal(1000L, works.Single(w => w.Work == "가공").DurationMs);
        Assert.Equal(2000L, mt);                                                // MT 끝 = 3000
    }

    [Fact]
    public void 경계_고정_head_의_앞_사이클_OUT상승_구간은_앞_사이클_MT_를_늘리지_않는다()
    {
        // 앞 사이클 [1000, 5000) 안에서 전진 OUT↑(4600)→IN↑(5000=다음 경계). 종전엔 이 구간이 앞 사이클 MT 끝을 5000 까지
        // 밀어 WT 가 0 이 됐다. 이제 head call 의 OUT↑ 구간은 쓰지 않으므로 MT 끝 = 용접 끝(2500).
        var calls = new[]
        {
            CallWithMarkers("전진", "투입", spans: [(4600, 5000)], markers: [1000, 5000]),
            Call("용접", "가공", (1500, 2500)),
        };

        var (works, mt, _) = CycleIngestService.MeasureCycle(
            calls, NoExclusion, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, head: Anchor("전진", "투입"));

        Assert.DoesNotContain(works, w => w.Work == "투입");   // 1000 이후 첫 마커(5000)는 ce 밖 → 폭 0
        Assert.Equal(1500L, mt);                                // 1000 → 2500
    }

    [Fact]
    public void 경계가_그_call_의_IN상승_자신이면_그_마커는_끝으로_쓰지_않는다()
    {
        // cs=1000 이 전진 IN↑ 자신. 마커 1000 은 "cs 초과" 가 아니라 제외 → 다음 마커 없음 → 폭 0.
        var calls = new[] { CallWithMarkers("전진", "투입", spans: [], markers: [1000]) };

        var (works, mt, _) = CycleIngestService.MeasureCycle(
            calls, NoExclusion, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, head: Anchor("전진", "투입"));

        Assert.Empty(works);
        Assert.Null(mt);
    }

    [Fact]
    public void 경계_고정_head_의_구간은_형제_call_과_합쳐_봉투가_된다()
    {
        // 투입 work = 전진(head, [1000, 1800)) + 후진([3000, 4200)) → 봉투 1000~4200.
        var calls = new[]
        {
            CallWithMarkers("전진", "투입", spans: [(500, 1000)], markers: [1000, 1800]),
            Call("후진", "투입", (3000, 4200)),
        };

        var (works, mt, _) = CycleIngestService.MeasureCycle(
            calls, NoExclusion, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, head: Anchor("전진", "투입"));

        Assert.Equal(("투입", 3200L), Assert.Single(works));
        Assert.Equal(3200L, mt);
    }

    [Fact]
    public void 경계_고정_head_는_제외_목록에_있으면_구간을_만들지_않는다()
    {
        var calls = new[] { CallWithMarkers("전진", "투입", spans: [], markers: [3000]) };
        var excluded = new HashSet<string>(["전진"], StringComparer.OrdinalIgnoreCase);

        var (works, mt, _) = CycleIngestService.MeasureCycle(
            calls, excluded, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, head: Anchor("전진", "투입"));

        Assert.Empty(works);
        Assert.Null(mt);
    }

    [Fact]
    public void 완료_마커는_응답_유형을_따른다()
    {
        // o~i(OUT↑ 중 절반 이상이 IN↑ 을 받음) → IN↑ 목록. o~o → OUT↓ 목록. OUT 없음 → 빈 목록.
        var outs = new List<(long Rise, long Fall)> { (100, 400), (1000, 1300) };

        Assert.Equal([250, 1150], WorkSpanMath.CompletionMarkers(outs, [250, 1150]));
        Assert.Equal([400, 1300], WorkSpanMath.CompletionMarkers(outs, []));
        Assert.Empty(WorkSpanMath.CompletionMarkers([], [250]));
    }
}
