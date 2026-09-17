// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Kpi;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// 시간 기반 코어 v68 판정·집계 규칙 고정(doc/30 §5 · §6).
/// 여기서 지키는 것은 넷이다 — 우선순위(비생산 ▸ 비가동 ▸ 가동), work 별 OR 조건,
/// 제외 행이 어떤 합에도 안 들어감, 그리고 A×P·TEEP 항등식.
/// </summary>
public class KpiRulesTests
{
    private const long R = 10_000;   // 기준 사이클 10초
    private static readonly KpiKappa K = KpiKappa.Default;   // 비생산 10 · 비가동 2.5 · Q 1.0

    private static CycleFact Fact(
        long id, long startMs, long ctMs, double r = R, double worst = 0,
        ExcludeReason exclude = ExcludeReason.None, string? worstWork = null)
        => new(id, startMs, startMs + ctMs, ctMs, r, worst, worstWork, exclude);

    // ── 판정 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void 기준_안이고_work_정상이면_가동()
    {
        Assert.Equal(CycleState.Run, KpiRules.Classify(Fact(1, 0, 12_000, worst: 1.4), K));
    }

    [Fact]
    public void work_하나라도_계수를_넘으면_비가동()
    {
        // 사이클 길이는 평소와 같아도(12초) work 하나가 W 의 3배면 비가동.
        Assert.Equal(CycleState.Down, KpiRules.Classify(Fact(1, 0, 12_000, worst: 3.0), K));
    }

    [Fact]
    public void 계수와_같으면_비가동_아님_초과라야_비가동()
    {
        Assert.Equal(CycleState.Run, KpiRules.Classify(Fact(1, 0, 12_000, worst: 2.5), K));
        Assert.Equal(CycleState.Down, KpiRules.Classify(Fact(1, 0, 12_000, worst: 2.5001), K));
    }

    [Fact]
    public void CT가_계수배_R_이상이면_비생산()
    {
        Assert.Equal(CycleState.NonProd, KpiRules.Classify(Fact(1, 0, 100_000), K));
        Assert.Equal(CycleState.Run, KpiRules.Classify(Fact(1, 0, 99_999), K));
    }

    [Fact]
    public void 비생산과_비가동이_동시면_비생산이_이긴다()
    {
        // 길이도 넘고 work 도 넘은 행 — 아주 긴 사이클은 장기 비활성으로 본다(doc/30 §5).
        var f = Fact(1, 0, 600_000, worst: 40.0, worstWork: "1st_stp");
        Assert.Equal(CycleState.NonProd, KpiRules.Classify(f, K));
    }

    [Fact]
    public void 기준선이_없으면_제외()
    {
        var f = Fact(1, 0, 12_000, r: 0, exclude: ExcludeReason.NoBaseline);
        Assert.Equal(CycleState.Excluded, KpiRules.Classify(f, K));
        Assert.Equal(ExcludeReason.NoBaseline, KpiRules.ResolveExclude(f));
    }

    [Fact]
    public void 잘린_행은_제외()
    {
        var f = Fact(1, 0, 12_000, exclude: ExcludeReason.Cut);
        Assert.Equal(CycleState.Excluded, KpiRules.Classify(f, K));
    }

    [Fact]
    public void 계수를_바꾸면_같은_행이_다시_라벨된다()
    {
        // 상태를 저장하지 않고 조회 시 도출하므로 κ 변경이 과거까지 즉시 반영된다(doc/30 §4).
        var f = Fact(1, 0, 60_000, worst: 1.0);
        Assert.Equal(CycleState.Run, KpiRules.Classify(f, K));
        Assert.Equal(CycleState.NonProd, KpiRules.Classify(f, new KpiKappa(5, 2.5, 1.0)));
    }

    // ── 집계 ──────────────────────────────────────────────────────────────────

    private static List<CycleFact> Sample()
    {
        // 가동 3(10초·12초·10초) · 비가동 1(work 초과, 30초) · 비생산 1(200초) · 제외 1
        long t = 0;
        var list = new List<CycleFact>();
        void Add(long ct, double worst = 0, ExcludeReason ex = ExcludeReason.None)
        {
            list.Add(new CycleFact(list.Count + 1, t, t + ct, ct, R, worst, worst > 0 ? "w" : null, ex));
            t += ct;
        }
        Add(10_000);
        Add(12_000);
        Add(30_000, worst: 4.0);
        Add(10_000);
        Add(200_000);
        Add(9_000, ex: ExcludeReason.Cut);
        return list;
    }

    [Fact]
    public void 합계는_유효행만_센다()
    {
        var t = KpiRules.Compute(Sample(), K);
        Assert.Equal(3, t.RunCount);
        Assert.Equal(1, t.DownCount);
        Assert.Equal(1, t.NonProdCount);
        Assert.Equal(1, t.ExcludedCount);

        Assert.Equal(32_000, t.TRunMs);       // 10+12+10
        Assert.Equal(30_000, t.TDownMs);
        Assert.Equal(200_000, t.TNonProdMs);
        Assert.Equal(262_000, t.TMs);         // 제외 9초는 어디에도 없다
    }

    [Fact]
    public void A와_P와_TEEP_항등식이_성립한다()
    {
        var t = KpiRules.Compute(Sample(), K);

        Assert.Equal(32_000.0 / 62_000.0, t.A!.Value, 9);        // 가동/(가동+비가동)
        Assert.Equal(30_000.0 / 32_000.0, t.P!.Value, 9);        // ΣR(=3×10초)/T가동
        Assert.Equal(62_000.0 / 262_000.0, t.U!.Value, 9);

        Assert.Equal(t.A!.Value * t.P!.Value * t.Q, t.Oee!.Value, 9);
        Assert.Equal(t.U!.Value * t.Oee!.Value, t.Teep!.Value, 9);
        Assert.True(KpiRules.Verify(t));
    }

    [Fact]
    public void 성능은_100퍼센트를_넘을_수_있다()
    {
        // 기준 10초짜리를 8초에 도는 설비 — 클립하지 않는다(원본 스펙 §7).
        var fast = new List<CycleFact>
        {
            new(1, 0, 8_000, 8_000, R, 0, null, ExcludeReason.None),
            new(2, 8_000, 16_000, 8_000, R, 0, null, ExcludeReason.None),
        };
        var t = KpiRules.Compute(fast, K);
        Assert.Equal(1.25, t.P!.Value, 9);
        Assert.True(t.Oee!.Value > 1.0);
    }

    [Fact]
    public void 품질은_OEE와_TEEP에만_곱해진다()
    {
        var q = new KpiKappa(10, 2.5, 0.985);
        var t = KpiRules.Compute(Sample(), q);
        var full = KpiRules.Compute(Sample(), K);

        Assert.Equal(full.A!.Value, t.A!.Value, 9);
        Assert.Equal(full.P!.Value, t.P!.Value, 9);
        Assert.Equal(full.Oee!.Value * 0.985, t.Oee!.Value, 9);
        Assert.Equal(full.Teep!.Value * 0.985, t.Teep!.Value, 9);
    }

    [Fact]
    public void 유효행이_없으면_지표는_산출_불가()
    {
        var t = KpiRules.Compute(
            [new CycleFact(1, 0, 1000, 1000, 0, 0, null, ExcludeReason.NoBaseline)], K);
        Assert.Null(t.A);
        Assert.Null(t.P);
        Assert.Null(t.Oee);
        Assert.Null(t.Teep);
        Assert.Equal(0, t.TMs);
    }

    [Fact]
    public void 비가동_평균시간과_발생간격()
    {
        long t0 = 1_000_000;
        var list = new List<CycleFact>
        {
            new(1, t0,           t0 + 20_000,  20_000, R, 5.0, "w", ExcludeReason.None),
            new(2, t0 + 60_000,  t0 + 100_000, 40_000, R, 5.0, "w", ExcludeReason.None),
        };
        var t = KpiRules.Compute(list, K);
        Assert.Equal(2, t.ToCount);
        Assert.Equal(30_000, t.TtrMs!.Value, 6);   // (20+40)/2
        Assert.Equal(60_000, t.TbfMs!.Value, 6);   // onset 간격
    }

    // ── 연표 세그먼트 ─────────────────────────────────────────────────────────

    [Fact]
    public void 인접한_같은_상태는_한_세그먼트로_묶인다()
    {
        var segs = KpiRules.BuildSegments(Sample(), K);
        Assert.Equal(new[] { "Run", "Down", "Run", "NonProd", "Excluded" },
            segs.Select(s => s.State.ToString()).ToArray());

        var first = segs[0];
        Assert.Equal(2, first.Cycles);          // 10초 + 12초
        Assert.Equal(22_000, first.CtMs);
        Assert.Equal(0, first.StartMs);
        Assert.Equal(22_000, first.EndMs);
    }

    [Fact]
    public void 시간이_끊기면_같은_상태라도_세그먼트를_나눈다()
    {
        var list = new List<CycleFact>
        {
            new(1, 0, 10_000, 10_000, R, 0, null, ExcludeReason.None),
            new(2, 50_000, 60_000, 10_000, R, 0, null, ExcludeReason.None),  // 사이에 공백
        };
        var segs = KpiRules.BuildSegments(list, K);
        Assert.Equal(2, segs.Count);
    }

    [Fact]
    public void 세그먼트는_초과_work_중_가장_큰_것을_남긴다()
    {
        var list = new List<CycleFact>
        {
            new(1, 0, 10_000, 10_000, R, 3.0, "conveyor", ExcludeReason.None),
            new(2, 10_000, 20_000, 10_000, R, 7.5, "stopper", ExcludeReason.None),
        };
        var seg = Assert.Single(KpiRules.BuildSegments(list, K));
        Assert.Equal("stopper", seg.WorstWork);
        Assert.Equal(7.5, seg.WorstRatio, 6);
    }

    // ── 기준선 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 표본이_10개_미만이면_기준선을_만들지_않는다()
    {
        var nine = Enumerable.Repeat(10_000L, 9).ToList();
        Assert.Null(KpiRules.Median(nine));
        nine.Add(10_000L);
        Assert.Equal(10_000, KpiRules.Median(nine)!.Value, 6);
    }

    [Fact]
    public void 중앙값은_긴_정지_한둘에_흔들리지_않는다()
    {
        // 원본 스펙 §3 예시의 취지 — 평균이면 크게 오르지만 중앙값은 제자리.
        var v = new List<long> { 9_800, 10_000, 10_100, 10_200, 25_000, 9_900, 10_050, 10_150, 9_950, 600_000 };
        // 정렬 시 가운데 두 값 10,050 · 10,100 의 평균. 600초 정지 한 건은 아무 영향이 없다.
        Assert.Equal(10_075, KpiRules.Median(v)!.Value, 6);
    }

    [Fact]
    public void 계수는_범위를_벗어나면_접힌다()
    {
        var k = new KpiKappa(1000, 0.1, 5).Normalized();
        Assert.Equal(KpiKappa.NonProdMax, k.NonProd);
        Assert.Equal(KpiKappa.DownMin, k.Down);
        Assert.Equal(1.0, k.Quality);

        var zero = new KpiKappa(0, 0, -1).Normalized();
        Assert.Equal(KpiKappa.NonProdDefault, zero.NonProd);
        Assert.Equal(KpiKappa.DownDefault, zero.Down);
        Assert.Equal(KpiKappa.QualityDefault, zero.Quality);
    }
}
