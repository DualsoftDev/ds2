// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Kpi;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// 사이클 경계를 <b>IN 전용 call</b> 의 주소로 고른 경우의 work 구간 측정(doc/30 §2.3 + 시작점 시드).
/// §2.2 의 call 구간은 OUT 상승에서만 열리므로 IN 전용 call 은 구간을 못 만든다. 그래도 사용자가 그 센서를
/// 사이클 시작으로 지목했으면 그 work 는 <b>사이클 시작부터</b> 재야 한다 — 안 그러면 head 의 work 가
/// 형제 call 이 뜨는 시각부터 시작해 실제보다 짧게 잡히고, 그만큼 비가동 판정이 관대해진다.
/// </summary>
public class CycleMeasureSeedTests
{
    private static readonly HashSet<string> NoExclusion = new(StringComparer.OrdinalIgnoreCase);

    private static CycleIngestService.CallSpanSet Call(string name, string work, params (long S, long E)[] spans)
        => new(name, work, spans.Select(x => new Span(x.S, x.E)).ToList());

    [Fact]
    public void 시드가_없으면_head_work_는_형제_call_이_뜨는_시각부터_잡힌다()
    {
        // 사이클 [1000, 5000). 투입 work 의 실제 동작은 1800 부터 — 경계(1000)의 IN 전용 call 은 구간이 없다.
        var calls = new[] { Call("전진", "투입", (1800, 2600)) };

        var (works, mt, _) = CycleIngestService.MeasureCycle(
            calls, NoExclusion, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, seedWork: null);

        Assert.Equal(("투입", 800L), Assert.Single(works));
        Assert.Equal(1600L, mt);   // 경계 → 마지막 work 끝
    }

    [Fact]
    public void 시드가_있으면_head_work_는_사이클_시작부터_잡힌다()
    {
        var calls = new[] { Call("전진", "투입", (1800, 2600)) };

        var (works, mt, _) = CycleIngestService.MeasureCycle(
            calls, NoExclusion, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, seedWork: "투입");

        Assert.Equal(("투입", 1600L), Assert.Single(works));   // 1000 → 2600
        Assert.Equal(1600L, mt);                               // MT 는 그대로 — 시드는 끝에 기여하지 않는다
    }

    [Fact]
    public void 시드한_work_에_다른_call_이_없으면_폭_0_이라_제외된다()
    {
        // 관측 전용 work — 지속시간이 원리상 없다. 유령 행을 만들지 않는다.
        var (works, mt, _) = CycleIngestService.MeasureCycle(
            [], NoExclusion, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, seedWork: "감지");

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
            calls, NoExclusion, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, seedWork: "투입");

        Assert.Equal(1600L, works.Single(w => w.Work == "투입").DurationMs);
        Assert.Equal(1200L, works.Single(w => w.Work == "가공").DurationMs);
        Assert.Equal(3000L, mt);
    }

    [Fact]
    public void 시드해도_경계_초과_판정은_변하지_않는다()
    {
        // call 구간 끝이 사이클 끝을 넘은 최대량 — 시드는 시작점만 건드린다.
        var calls = new[] { Call("전진", "투입", (1800, 5300)) };

        var (_, _, overflow) = CycleIngestService.MeasureCycle(
            calls, NoExclusion, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, seedWork: "투입");

        Assert.Equal(300L, overflow);
    }

    [Fact]
    public void 제외된_call_만_있는_시드_work_는_열리기만_하고_폭이_없다()
    {
        // 분기 제외 call 은 구간에서 빠진다 — 시드가 그 규칙을 우회해 work 를 되살리면 안 된다.
        var calls = new[] { Call("전진", "투입", (1800, 2600)) };
        var excluded = new HashSet<string>(["전진"], StringComparer.OrdinalIgnoreCase);

        var (works, mt, _) = CycleIngestService.MeasureCycle(
            calls, excluded, cs: 1000, ce: 5000, boundaries: [1000, 5000], snapMs: 0, seedWork: "투입");

        Assert.Empty(works);
        Assert.Null(mt);
    }
}
