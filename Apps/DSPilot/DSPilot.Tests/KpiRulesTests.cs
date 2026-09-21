// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Kpi;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// 시간 기반 코어 v68 판정·집계 규칙 고정(doc/30 §6 · §7).
/// 여기서 지키는 것은 다섯이다 — 우선순위(비생산 ▸ 비가동 ▸ 가동), work OR MT 두 축의 비가동,
/// 경계 초과·미분류 제외, 제외 행이 어떤 합에도 안 들어감, 그리고 A×P·TEEP 항등식.
/// </summary>
public class KpiRulesTests
{
    private const long R = 10_000;      // 기준 사이클 10초
    private const long MtMed = 8_000;   // 기준 MT 8초
    private static readonly KpiKappa K = KpiKappa.Default;   // 비생산 10 · work 2.5 · MT 1.5 · Q 1.0 · 초과 5초

    private static CycleFact Fact(
        long id, long startMs, long ctMs, double r = R, double worst = 0,
        long? mt = null, double mtMed = MtMed, long overflow = 0,
        ExcludeReason exclude = ExcludeReason.None, string? worstWork = null)
        => new(id, startMs, startMs + ctMs, ctMs, r, mt, mtMed, worst, worstWork, overflow, exclude);

    // ── 판정 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void 기준_안이고_work_정상이면_가동()
    {
        Assert.Equal(CycleState.Run, KpiRules.Classify(Fact(1, 0, 12_000, worst: 1.4, mt: 9_000), K));
    }

    [Fact]
    public void work_하나라도_계수를_넘으면_비가동()
    {
        // 사이클 길이는 평소와 같아도(12초) work 하나가 W 의 3배면 비가동.
        var f = Fact(1, 0, 12_000, worst: 3.0, mt: 9_000);
        Assert.Equal(CycleState.Down, KpiRules.Classify(f, K));
        Assert.Equal(DownAxis.Work, KpiRules.Axis(f, K));
    }

    [Fact]
    public void 계수와_같으면_비가동_아님_초과라야_비가동()
    {
        Assert.Equal(CycleState.Run, KpiRules.Classify(Fact(1, 0, 12_000, worst: 2.5), K));
        Assert.Equal(CycleState.Down, KpiRules.Classify(Fact(1, 0, 12_000, worst: 2.5001), K));
    }

    [Fact]
    public void MT가_계수를_넘으면_work가_정상이어도_비가동()
    {
        // 개별 work 는 전부 평소인데 work 사이 공백이 벌어져 MT 만 늘어난 사이클(doc/30 §15-⑦).
        var f = Fact(1, 0, 20_000, worst: 1.1, mt: 12_500);   // 12.5초 > 1.5 × 8초 = 12초
        Assert.Equal(CycleState.Down, KpiRules.Classify(f, K));
        Assert.Equal(DownAxis.Mt, KpiRules.Axis(f, K));
    }

    [Fact]
    public void MT는_임계와_같으면_넘지_않은_것()
    {
        Assert.Equal(CycleState.Run, KpiRules.Classify(Fact(1, 0, 20_000, mt: 12_000), K));
    }

    [Fact]
    public void MT중앙이_없으면_MT축은_비교하지_않는다()
    {
        // 표본 부족으로 MT중앙이 아직 없는 flow — MT 가 아무리 길어도 이 축으로는 비가동이 안 된다.
        Assert.Equal(CycleState.Run, KpiRules.Classify(Fact(1, 0, 20_000, mt: 19_000, mtMed: 0), K));
    }

    [Fact]
    public void 두_축이_다_넘으면_임계_대비_더_크게_넘은_축이_대표다()
    {
        var workWins = Fact(1, 0, 20_000, worst: 10.0, mt: 13_000);   // work 4배 · MT 1.08배
        Assert.Equal(DownAxis.Work, KpiRules.Axis(workWins, K));

        var mtWins = Fact(2, 0, 20_000, worst: 2.6, mt: 19_000);       // work 1.04배 · MT 1.58배
        Assert.Equal(DownAxis.Mt, KpiRules.Axis(mtWins, K));
    }

    [Fact]
    public void 굶은_사이클은_비가동이_아니다()
    {
        // CT 는 두 배로 늘었지만 MT 도 work 도 평소 — 늘어난 시간이 WT 다. 남이 멈춰 기다린 것(doc/30 §6).
        var f = Fact(1, 0, 20_000, worst: 1.0, mt: 8_000);
        Assert.Equal(CycleState.Run, KpiRules.Classify(f, K));
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
        // 길이도 넘고 work 도 MT 도 넘은 행 — 아주 긴 사이클은 장기 비활성으로 본다(doc/30 §6).
        var f = Fact(1, 0, 600_000, worst: 40.0, mt: 590_000, worstWork: "1st_stp");
        Assert.Equal(CycleState.NonProd, KpiRules.Classify(f, K));
    }

    [Fact]
    public void 경계를_허용치_이상_넘은_행은_제외()
    {
        // 5초 허용 — 3.1초 넘긴 #131 사례는 정상, 30초 넘긴 것은 모델링 이슈(doc/30 §3 · §15-③).
        Assert.Equal(CycleState.Run, KpiRules.Classify(Fact(1, 0, 12_000, overflow: 3_100), K));
        var over = Fact(2, 0, 12_000, overflow: 30_000);
        Assert.Equal(CycleState.Excluded, KpiRules.Classify(over, K));
        Assert.Equal(ExcludeReason.Overflow, KpiRules.ResolveExclude(over, K));
    }

    [Fact]
    public void 허용치를_바꾸면_경계_초과_판정도_즉시_바뀐다()
    {
        var f = Fact(1, 0, 12_000, overflow: 3_100);
        var strict = K with { OverflowMs = 1_000 };
        Assert.Equal(CycleState.Excluded, KpiRules.Classify(f, strict));
        Assert.Equal(CycleState.Run, KpiRules.Classify(f, K));
    }

    [Fact]
    public void 미분류_행은_제외()
    {
        var f = Fact(1, 0, 12_000, exclude: ExcludeReason.Unclassified);
        Assert.Equal(CycleState.Excluded, KpiRules.Classify(f, K));
        Assert.Equal(ExcludeReason.Unclassified, KpiRules.ResolveExclude(f, K));
    }

    [Fact]
    public void 기준선이_없으면_제외()
    {
        var f = Fact(1, 0, 12_000, r: 0, exclude: ExcludeReason.NoBaseline);
        Assert.Equal(CycleState.Excluded, KpiRules.Classify(f, K));
        Assert.Equal(ExcludeReason.NoBaseline, KpiRules.ResolveExclude(f, K));
    }

    [Fact]
    public void 창에_걸렸다고_제외되지_않는다()
    {
        // doc/30 §7.2 — 잘림은 행의 성질이 아니라 (행, 창) 쌍의 성질이다. 판정은 창을 보지 않는다.
        var f = Fact(1, 0, 12_000);
        Assert.Equal(CycleState.Run, KpiRules.Classify(f, K));
        Assert.Equal(ExcludeReason.None, KpiRules.ResolveExclude(f, K));
        // 창을 어떻게 잡아도 같은 상태다 — 달라지는 것은 겹친 길이뿐.
        Assert.Equal(4_000, KpiRules.OverlapMs(f, 8_000, 30_000));
        Assert.Equal(12_000, KpiRules.OverlapMs(f, null, null));
    }

    [Fact]
    public void 계수를_바꾸면_같은_행이_다시_라벨된다()
    {
        // 상태를 저장하지 않고 조회 시 도출하므로 κ 변경이 과거까지 즉시 반영된다(doc/30 §5).
        var f = Fact(1, 0, 60_000, worst: 1.0, mt: 8_000);
        Assert.Equal(CycleState.Run, KpiRules.Classify(f, K));
        Assert.Equal(CycleState.NonProd, KpiRules.Classify(f, K with { NonProd = 5 }));
        Assert.Equal(CycleState.Down, KpiRules.Classify(Fact(2, 0, 12_000, mt: 10_000), K with { Mt = 1.2 }));
    }

    // ── 집계 ──────────────────────────────────────────────────────────────────

    private static List<CycleFact> Sample()
    {
        // 가동 3(10초·12초·10초) · 비가동 1(work 초과, 30초) · 비생산 1(200초) · 제외 1
        long t = 0;
        var list = new List<CycleFact>();
        void Add(long ct, double worst = 0, ExcludeReason ex = ExcludeReason.None)
        {
            list.Add(new CycleFact(list.Count + 1, t, t + ct, ct, R, null, 0, worst, worst > 0 ? "w" : null, 0, ex));
            t += ct;
        }
        Add(10_000);
        Add(12_000);
        Add(30_000, worst: 4.0);
        Add(10_000);
        Add(200_000);
        Add(9_000, ex: ExcludeReason.Unclassified);
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

    // ── 창에 걸친 행 — 자르기와 귀속(doc/30 §7.2) ────────────────────────────────

    /// <summary>자정을 가로지른 비생산 행 하나. 양쪽 날이 겹친 만큼씩 나눠 갖고, 건수는 시작한 날만 센다.</summary>
    private static List<CycleFact> Straddler()
        // [-40초, +60초) = 100초 ≥ κ_비생산(10) × R(10초) → 비생산. 창 경계는 0.
        => new() { Fact(1, -40_000, 100_000) };

    [Fact]
    public void 걸친_행은_겹친_만큼만_시간에_들어간다()
    {
        var before = KpiRules.Compute(Straddler(), K, -100_000, 0);
        var after = KpiRules.Compute(Straddler(), K, 0, 100_000);

        Assert.Equal(40_000, before.TNonProdMs);       // 창 앞쪽 40초
        Assert.Equal(60_000, after.TNonProdMs);        // 창 뒤쪽 60초
        Assert.Equal(1, before.NonProdCount);          // 건수는 시작한 구간에서만
        Assert.Equal(0, after.NonProdCount);
        Assert.Equal(1, before.ClippedCount);
        Assert.Equal(1, after.ClippedCount);
    }

    [Fact]
    public void 창을_쪼개_더하면_전체와_같다()
    {
        // 가산성 — 어떤 분할로 나눠 더해도 시간 합이 보존된다. 종전 '잘림 제외' 규칙에서는 깨졌다.
        var facts = Sample();
        long from = facts.Min(f => f.StartMs), to = facts.Max(f => f.EndMs);
        var whole = KpiRules.Compute(facts, K, from, to);

        long run = 0, down = 0, nonProd = 0;
        const int slices = 7;
        for (int i = 0; i < slices; i++)
        {
            long a = from + (to - from) * i / slices;
            long b = from + (to - from) * (i + 1) / slices;
            var part = KpiRules.Compute(facts, K, a, b);
            run += part.TRunMs; down += part.TDownMs; nonProd += part.TNonProdMs;
        }

        Assert.Equal(whole.TRunMs, run);
        Assert.Equal(whole.TDownMs, down);
        Assert.Equal(whole.TNonProdMs, nonProd);
    }

    [Fact]
    public void 건수는_시작_기준이라_이중_계상되지_않는다()
    {
        var facts = Sample();
        long from = facts.Min(f => f.StartMs), to = facts.Max(f => f.EndMs);
        var whole = KpiRules.Compute(facts, K, from, to);

        int run = 0, down = 0, nonProd = 0;
        const int slices = 5;
        for (int i = 0; i < slices; i++)
        {
            long a = from + (to - from) * i / slices;
            long b = from + (to - from) * (i + 1) / slices;
            var part = KpiRules.Compute(facts, K, a, b);
            run += part.RunCount; down += part.DownCount; nonProd += part.NonProdCount;
        }

        Assert.Equal(whole.RunCount, run);
        Assert.Equal(whole.DownCount, down);
        Assert.Equal(whole.NonProdCount, nonProd);
    }

    [Fact]
    public void 수리시간은_잘려도_원래_CT_로_잰다()
    {
        // MTTR 은 행 속성이다 — 창 때문에 수리 시간이 짧아 보이면 안 된다(doc/30 §7.2 "행 속성").
        var down = new List<CycleFact> { Fact(1, 0, 40_000, worst: 4.0, worstWork: "w") };
        var t = KpiRules.Compute(down, K, 0, 10_000);

        Assert.Equal(1, t.DownCount);
        Assert.Equal(10_000, t.TDownMs);        // 시간은 겹친 만큼
        Assert.Equal(40_000, t.TtrMs);          // 수리 시간은 행 전체
    }

    [Fact]
    public void 창보다_긴_행_하나뿐이어도_집계된다()
    {
        // 하루를 통째로 덮는 비생산 행 — 종전 '온전한 행만' 규칙에서는 "데이터 없음" 이 됐다.
        var t = KpiRules.Compute(Straddler(), K, 0, 20_000);

        Assert.Equal(20_000, t.TNonProdMs);
        Assert.Equal(20_000, t.TMs);
        Assert.Equal(0.0, t.U);                 // 창 전체가 비생산이라 생산가능은 0
        Assert.True(KpiRules.Verify(t));
    }

    [Fact]
    public void 창을_주지_않으면_행_전체를_쓴다()
    {
        // 기존 호출부·단위 테스트 호환 — 창 없는 Compute 는 종전과 같다.
        var facts = Sample();
        var windowed = KpiRules.Compute(facts, K, facts.Min(f => f.StartMs), facts.Max(f => f.EndMs));
        var whole = KpiRules.Compute(facts, K);

        Assert.Equal(whole.TMs, windowed.TMs);
        Assert.Equal(whole.RunCount, windowed.RunCount);
        Assert.Equal(0, whole.ClippedCount);
    }

    [Fact]
    public void 성능은_100퍼센트를_넘을_수_있다()
    {
        // 기준 10초짜리를 8초에 도는 설비 — 클립하지 않는다(원본 스펙 §7).
        var fast = new List<CycleFact>
        {
            Fact(1, 0, 8_000),
            Fact(2, 8_000, 8_000),
        };
        var t = KpiRules.Compute(fast, K);
        Assert.Equal(1.25, t.P!.Value, 9);
        Assert.True(t.Oee!.Value > 1.0);
    }

    [Fact]
    public void 품질은_OEE와_TEEP에만_곱해진다()
    {
        var q = K with { Quality = 0.985 };
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
        var t = KpiRules.Compute([Fact(1, 0, 1000, r: 0, exclude: ExcludeReason.NoBaseline)], K);
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
            Fact(1, t0, 20_000, worst: 5.0, worstWork: "w"),
            Fact(2, t0 + 60_000, 40_000, worst: 5.0, worstWork: "w"),
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
            Fact(1, 0, 10_000),
            Fact(2, 50_000, 10_000),  // 사이에 공백
        };
        var segs = KpiRules.BuildSegments(list, K);
        Assert.Equal(2, segs.Count);
    }

    [Fact]
    public void 세그먼트는_초과_work_중_가장_큰_것을_남긴다()
    {
        var list = new List<CycleFact>
        {
            Fact(1, 0, 10_000, worst: 3.0, worstWork: "conveyor"),
            Fact(2, 10_000, 10_000, worst: 7.5, worstWork: "stopper"),
        };
        var seg = Assert.Single(KpiRules.BuildSegments(list, K));
        Assert.Equal("stopper", seg.WorstWork);
        Assert.Equal(7.5, seg.WorstRatio, 6);
        Assert.Equal(DownAxis.Work, seg.Axis);
    }

    [Fact]
    public void 세그먼트_대표_축은_임계_대비_더_크게_넘은_사이클의_것()
    {
        var list = new List<CycleFact>
        {
            Fact(1, 0, 20_000, worst: 2.6, mt: 20_000),            // MT 2.5배(임계 대비 1.67)
            Fact(2, 20_000, 20_000, worst: 3.0, mt: 8_000, worstWork: "w"),   // work 3배(임계 대비 1.2)
        };
        var seg = Assert.Single(KpiRules.BuildSegments(list, K));
        Assert.Equal(CycleState.Down, seg.State);
        Assert.Equal(DownAxis.Mt, seg.Axis);
        Assert.Equal(2.5, seg.MtRatio, 6);
        Assert.Equal(28_000, seg.MtMs);
    }

    // ── 기준선 · 게이트 ───────────────────────────────────────────────────────

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
    public void 사분위는_정렬_인덱스_n4_와_3n4_다()
    {
        var v = Enumerable.Range(1, 12).Select(i => (long)i * 1000).ToList();   // 1000..12000
        var q = KpiRules.QuartilesOf(v)!.Value;
        Assert.Equal(4_000, q.Q1, 6);      // v[3]
        Assert.Equal(6_500, q.Median, 6);  // (6000+7000)/2
        Assert.Equal(10_000, q.Q3, 6);     // v[9]
        Assert.Equal(12, q.Count);
    }

    [Fact]
    public void 분포가_두_갈래인_work는_게이트에_걸린다()
    {
        // 셔틀.#135_#136 형태 — 1사분 2초 · 3사분 2분 24초(doc/30 §15-⑥).
        var bimodal = new Quartiles(2_000, 3_000, 144_000, 183);
        Assert.True(KpiRules.IsGated(bimodal));

        // #121.투입 형태 — 1사분 2:35 · 3사분 3:25.
        var tight = new Quartiles(155_000, 178_000, 205_000, 176);
        Assert.False(KpiRules.IsGated(tight));

        // 1사분위가 0 이면 비율이 무한 — 걸린다(#134.R134-1 HAND CLAMP).
        Assert.True(KpiRules.IsGated(new Quartiles(0, 0, 23_000, 61)));
    }

    [Fact]
    public void 게이트_값을_바꾸면_같은_분포가_다르게_갈린다()
    {
        var q = new Quartiles(10_000, 20_000, 35_000, 50);   // Q3/Q1 = 3.5
        Assert.True(KpiRules.IsGated(q, 3.0));
        Assert.False(KpiRules.IsGated(q, 4.0));
    }

    [Fact]
    public void 계수는_범위를_벗어나면_접힌다()
    {
        var k = new KpiKappa(1000, 0.1, 9, 5, 999_999).Normalized();
        Assert.Equal(KpiKappa.NonProdMax, k.NonProd);
        Assert.Equal(KpiKappa.WorkMin, k.Work);
        Assert.Equal(KpiKappa.MtMax, k.Mt);
        Assert.Equal(1.0, k.Quality);
        Assert.Equal(KpiKappa.OverflowMsMax, k.OverflowMs);

        var zero = new KpiKappa(0, 0, 0, -1, -1).Normalized();
        Assert.Equal(KpiKappa.NonProdDefault, zero.NonProd);
        Assert.Equal(KpiKappa.WorkDefault, zero.Work);
        Assert.Equal(KpiKappa.MtDefault, zero.Mt);
        Assert.Equal(KpiKappa.QualityDefault, zero.Quality);
        Assert.Equal(KpiKappa.OverflowMsDefault, zero.OverflowMs);
    }
}
