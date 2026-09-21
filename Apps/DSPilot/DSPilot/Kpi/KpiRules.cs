// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Kpi;

/// <summary>
/// 시간 기반 코어 v68 — 사이클 상태. doc/30 §6.
/// 유효 상태 셋(가동·비가동·비생산)은 모든 합산에 들어가고, 제외는 어디에도 들어가지 않는다.
/// </summary>
public enum CycleState
{
    /// <summary>계산 밖 — 잘림 · 미상(UNK) · 진행 중 · 미분류 · 기준 없음 · 경계 초과.</summary>
    Excluded = 0,
    /// <summary>가동 — 비생산도 비가동도 아닌 나머지.</summary>
    Run = 1,
    /// <summary>비가동 — work 하나라도 W × κ_work 초과, <b>또는</b> MT 가 MT중앙 × κ_MT 초과.</summary>
    Down = 2,
    /// <summary>비생산 — CT ≥ κ_비생산 × R. 비가동보다 우선한다.</summary>
    NonProd = 3,
}

/// <summary>제외 사유. doc/30 §6 순서 0.</summary>
public enum ExcludeReason
{
    None = 0,
    // 1 = 종전 Cut(구간에 잘림). 2026-09-21 폐기 — 잘림은 행의 성질이 아니라 (행, 창) 쌍의 성질이라
    //     제외 사유가 아니다(doc/30 §7.2). 저장된 적이 없는 값이지만 번호는 재사용하지 않는다.
    /// <summary>이전 경계를 알 수 없는 구간(수집 시작 직후·데이터 공백 직후).</summary>
    Unknown = 2,
    /// <summary>다음 경계가 아직 없는 열린 사이클 — R 을 적립하지 않는다.</summary>
    InProgress = 3,
    /// <summary>표본 K 미달로 R 을 박제하지 못한 행.</summary>
    NoBaseline = 4,
    /// <summary>어느 분기도 통과하지 못한 행(doc/30 §3). 계산·표본 밖 — 사용자가 분기 정의를 고쳐야 한다.</summary>
    Unclassified = 5,
    /// <summary>call 구간이 CT 끝을 허용치 이상 넘었다(doc/30 §3) — 모델링 이슈. 정상 CT 로 인정하지 않는다.</summary>
    Overflow = 6,
}

/// <summary>비가동을 만든 축. 상태처럼 저장하지 않고 조회 시 도출한다.</summary>
public enum DownAxis
{
    None = 0,
    /// <summary>개별 work 가 자기 W 의 κ_work 배를 넘었다.</summary>
    Work = 1,
    /// <summary>MT(경계→마지막 work 끝)가 MT중앙의 κ_MT 배를 넘었다 — work 사이 공백이 벌어진 정지.</summary>
    Mt = 2,
}

/// <summary>
/// 사용자 조절 계수와 품질. doc/30 §5.
/// 기본값은 <see cref="Default"/>. 값이 바뀌면 과거 행도 즉시 재라벨된다(상태 미저장, 조회 시 도출).
/// </summary>
/// <param name="NonProd">κ_비생산 — <c>CT ≥ 이 값 × R</c> 이면 비생산.</param>
/// <param name="Work">κ_work — 어느 work 라도 <c>지속시간 &gt; 이 값 × W</c> 면 비가동.</param>
/// <param name="Mt">κ_MT — <c>MT &gt; 이 값 × MT중앙</c> 이면 비가동. work 보다 산포가 작아 더 조인다.</param>
/// <param name="Quality">Q(0~1). OEE·TEEP 에만 곱한다.</param>
/// <param name="OverflowMs">경계 초과 허용(ms). call 구간이 CT 끝을 이보다 더 넘으면 그 행은 제외(Overflow).</param>
public readonly record struct KpiKappa(double NonProd, double Work, double Mt, double Quality, long OverflowMs)
{
    public const double NonProdDefault = 10.0;
    public const double WorkDefault = 2.5;
    public const double MtDefault = 1.5;
    public const double QualityDefault = 1.0;
    public const long OverflowMsDefault = 5_000;

    public const double NonProdMin = 3.0;
    public const double NonProdMax = 100.0;
    public const double WorkMin = 1.5;
    public const double WorkMax = 10.0;
    public const double MtMin = 1.2;
    public const double MtMax = 2.5;
    public const long OverflowMsMin = 0;
    public const long OverflowMsMax = 60_000;

    public static KpiKappa Default => new(NonProdDefault, WorkDefault, MtDefault, QualityDefault, OverflowMsDefault);

    /// <summary>범위 밖 입력을 안전값으로 접는다. 0·NaN 은 기본값으로 되돌린다.</summary>
    public KpiKappa Normalized() => new(
        double.IsFinite(NonProd) && NonProd > 0 ? Math.Clamp(NonProd, NonProdMin, NonProdMax) : NonProdDefault,
        double.IsFinite(Work) && Work > 0 ? Math.Clamp(Work, WorkMin, WorkMax) : WorkDefault,
        double.IsFinite(Mt) && Mt > 0 ? Math.Clamp(Mt, MtMin, MtMax) : MtDefault,
        double.IsFinite(Quality) && Quality >= 0 ? Math.Clamp(Quality, 0.0, 1.0) : QualityDefault,
        OverflowMs >= 0 ? Math.Clamp(OverflowMs, OverflowMsMin, OverflowMsMax) : OverflowMsDefault);
}

/// <summary>
/// 판정·집계에 필요한 사이클 행의 최소 사실. DB 행에서 그대로 뽑아 온다.
/// <para>
/// <paramref name="RMs"/> · <paramref name="MtMedianMs"/> · <paramref name="WorstRatio"/> 는 <b>사이클 완료 시점에
/// 박제</b>된 값이다(doc/30 §4). 기준선이 나중에 움직여도 이 값은 변하지 않는다. 반대로 κ 는 조회 시점 값을 쓰므로
/// 계수 변경은 과거까지 재라벨된다.
/// </para>
/// </summary>
/// <param name="StartMs">사이클 시작 경계 — Unix epoch ms(UTC).</param>
/// <param name="EndMs">사이클 끝(= 다음 경계) — Unix epoch ms(UTC).</param>
/// <param name="CtMs">EndMs − StartMs.</param>
/// <param name="RMs">박제된 기준 R(ms). 0 이하면 기준 없음.</param>
/// <param name="MtMs">경계→마지막 work 끝(ms). work 가 하나도 안 잡히면 null.</param>
/// <param name="MtMedianMs">박제된 MT중앙(ms). 0 이면 MT 축 비교 대상 아님.</param>
/// <param name="WorstRatio">게이트를 통과한 work 중 (지속시간 ÷ W) 최댓값. 0 이면 비교 대상 work 없음.</param>
/// <param name="WorstWork">위 최댓값을 만든 work 이름(표시용).</param>
/// <param name="OverflowMs">call 구간이 CT 끝을 넘은 최대량(ms). 허용치와 비교해 제외를 정한다.</param>
/// <param name="Exclude">이미 확정된 제외 사유. None 이면 규칙으로 판정한다.</param>
public readonly record struct CycleFact(
    long Id,
    long StartMs,
    long EndMs,
    long CtMs,
    double RMs,
    long? MtMs,
    double MtMedianMs,
    double WorstRatio,
    string? WorstWork,
    long OverflowMs,
    ExcludeReason Exclude)
{
    /// <summary>MT ÷ MT중앙. 어느 쪽이든 없으면 0 — 비교 대상에서 빠진다.</summary>
    public double MtRatio => MtMs is long mt && mt > 0 && MtMedianMs > 0 ? mt / MtMedianMs : 0;
}

/// <summary>구간 집계 결과. doc/30 §7.</summary>
public sealed record KpiTotals(
    int RunCount,
    int DownCount,
    int NonProdCount,
    int ExcludedCount,
    long TRunMs,
    long TDownMs,
    long TNonProdMs,
    long TMs,
    double SumRMs,
    double? A,
    double? P,
    double Q,
    double? Oee,
    double? U,
    double? Teep,
    int ToCount,
    double? TtrMs,
    double? TbfMs,
    int ClippedCount = 0)
{
    public static KpiTotals Empty(double q) =>
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, q, null, null, null, 0, null, null);
}

/// <summary>연표 세그먼트 — 인접한 같은 상태의 사이클을 하나로 묶은 것. doc/30 §9.1.</summary>
/// <param name="MtMs">묶인 사이클들의 MT 합(있는 것만).</param>
/// <param name="MtMedianMs">묶인 사이클들의 박제 MT중앙 평균.</param>
/// <param name="Axis">비가동이면 가장 크게 넘은 사이클의 축. 그 외 None.</param>
public sealed record KpiSegment(
    long StartMs,
    long EndMs,
    CycleState State,
    ExcludeReason Reason,
    int Cycles,
    long CtMs,
    long MtMs,
    double RMs,
    double MtMedianMs,
    DownAxis Axis,
    string? WorstWork,
    double WorstRatio,
    double MtRatio);

/// <summary>표본의 사분위. 게이트(<see cref="KpiRules.IsGated"/>)의 근거다.</summary>
public readonly record struct Quartiles(double Q1, double Median, double Q3, int Count);

/// <summary>
/// 시간 기반 코어의 판정·집계 순수 함수 SSOT. DB·HTTP·설정에 의존하지 않는다.
/// 화면·엑셀·메일이 모두 이 결과만 본다.
/// </summary>
public static class KpiRules
{
    /// <summary>기준선(R·W·MT중앙)을 박제하기 위한 최소 완료 사이클 수. doc/30 §4.</summary>
    public const int MinBaselineSamples = 10;

    /// <summary>기준선 이동 중앙값 창(일). doc/30 §4.</summary>
    public const int BaselineWindowDays = 14;

    /// <summary>work 신뢰도 게이트 기본값 — Q3 ÷ Q1 이 이 값을 넘는 work 는 비가동 판정에서 뺀다. doc/30 §4.1.</summary>
    public const double WorkGateDefault = 3.0;
    public const double WorkGateMin = 2.0;
    public const double WorkGateMax = 10.0;

    /// <summary>경계 스냅 기본값(ms) — 다음 경계 직전 이 안의 OUT↑ 은 다음 사이클 것. doc/30 §3.</summary>
    public const long BoundarySnapMsDefault = 100;
    public const long BoundarySnapMsMax = 500;

    /// <summary>
    /// 행 하나의 상태. 우선순위는 제외 ▸ 비생산 ▸ 비가동 ▸ 가동(doc/30 §6).
    /// 비생산과 비가동이 동시에 성립하면 비생산이다 — 아주 긴 사이클은 work 가 멈춰 있었어도 장기 비활성으로 본다.
    /// </summary>
    public static CycleState Classify(in CycleFact f, in KpiKappa k)
    {
        if (ResolveExclude(f, k) != ExcludeReason.None) return CycleState.Excluded;
        if (f.CtMs >= k.NonProd * f.RMs) return CycleState.NonProd;
        if (Axis(f, k) != DownAxis.None) return CycleState.Down;
        return CycleState.Run;
    }

    /// <summary>
    /// 비가동을 만든 축. 둘 다 넘었으면 임계 대비 더 크게 넘은 쪽. 비가동이 아니면 None.
    /// 상태와 같이 저장하지 않고 조회 시 도출한다 — κ 가 바뀌면 축도 따라 바뀐다.
    /// </summary>
    public static DownAxis Axis(in CycleFact f, in KpiKappa k)
    {
        bool work = f.WorstRatio > k.Work;
        bool mt = f.MtRatio > k.Mt;
        if (work && mt) return f.WorstRatio / k.Work >= f.MtRatio / k.Mt ? DownAxis.Work : DownAxis.Mt;
        if (work) return DownAxis.Work;
        if (mt) return DownAxis.Mt;
        return DownAxis.None;
    }

    /// <summary>행이 제외인지와 그 사유(규칙 판정 이전). 경계 초과는 허용치(κ)에 따라 달라지므로 κ 를 받는다.</summary>
    public static ExcludeReason ResolveExclude(in CycleFact f, in KpiKappa k)
    {
        if (f.Exclude != ExcludeReason.None) return f.Exclude;
        if (f.CtMs <= 0) return ExcludeReason.InProgress;
        if (!(f.RMs > 0)) return ExcludeReason.NoBaseline;
        if (f.OverflowMs > k.OverflowMs) return ExcludeReason.Overflow;
        return ExcludeReason.None;
    }

    /// <summary>창과 겹친 길이. 창을 주지 않으면 행 전체(CT). doc/30 §7.2.</summary>
    public static long OverlapMs(in CycleFact f, long? fromMs, long? toMs)
    {
        if (fromMs is not long a || toMs is not long b) return f.CtMs;
        long s = Math.Max(f.StartMs, a), e = Math.Min(f.EndMs, b);
        return e > s ? e - s : 0;
    }

    /// <summary>
    /// 행의 시작이 창 안인가 — 건수(고장 건수·MTBF 간격·MTTR)의 귀속 기준이다. 창을 주지 않으면 항상 참.
    /// 칸마다 세면 긴 행이 부풀고 양쪽 창에서 세면 이중 계상된다(doc/30 §7.2).
    /// </summary>
    public static bool StartsInWindow(in CycleFact f, long? fromMs, long? toMs)
        => fromMs is not long a || toMs is not long b || (f.StartMs >= a && f.StartMs < b);

    /// <summary>
    /// 구간 집계. T 는 캘린더가 아니라 <b>구간 안에서 판정된 시간</b>의 합이다(doc/30 §7).
    /// <para>
    /// <paramref name="fromMs"/>·<paramref name="toMs"/> 를 주면 창에 걸친 행을 <b>겹친 만큼</b> 반영한다
    /// (doc/30 §7.2). 판정은 언제나 행 전체로 하므로 자른다고 상태가 바뀌지 않는다 — 자정을 가로지른
    /// 비생산 행은 양쪽 날 모두 비생산이고 시간만 갈린다. 창을 주지 않으면 행 전체를 쓴다(단위 테스트·전체 합산).
    /// </para>
    /// 입력은 시작 시각 오름차순일 필요가 없다 — TBF 계산을 위해 내부에서 정렬한다.
    /// </summary>
    public static KpiTotals Compute(
        IReadOnlyList<CycleFact> facts, in KpiKappa kappa, long? fromMs = null, long? toMs = null)
    {
        var k = kappa.Normalized();
        if (facts.Count == 0) return KpiTotals.Empty(k.Quality);

        int run = 0, down = 0, nonProd = 0, excluded = 0, clipped = 0;
        long tRun = 0, tDown = 0, tNonProd = 0;
        double sumR = 0;
        var downStarts = new List<long>();
        long downCtSum = 0;

        foreach (var f in facts)
        {
            var state = Classify(f, k);
            long ovMs = OverlapMs(f, fromMs, toMs);
            if (ovMs <= 0 && state != CycleState.Excluded) continue;    // 창 밖(호출측이 걸러 오지만 방어)
            bool starts = StartsInWindow(f, fromMs, toMs);
            if (ovMs < f.CtMs) clipped++;
            // 시간은 겹친 만큼, 건수는 시작 귀속, R 은 CT 와 같은 비율로 안분(doc/30 §7.2).
            double share = f.CtMs > 0 ? ovMs / (double)f.CtMs : 0;
            switch (state)
            {
                case CycleState.Run:
                    if (starts) run++;
                    tRun += ovMs; sumR += f.RMs * share; break;
                case CycleState.Down:
                    tDown += ovMs;
                    // MTTR·MTBF 는 행 속성이라 잘린 길이가 아니라 원래 CT 로 잰다 — 창 때문에 수리 시간이
                    // 짧아 보이면 안 된다. 그래서 시작이 창 안인 행만 센다.
                    if (starts) { down++; downCtSum += f.CtMs; downStarts.Add(f.StartMs); }
                    break;
                case CycleState.NonProd:
                    if (starts) nonProd++;
                    tNonProd += ovMs; break;
                default:
                    excluded++; break;       // 진단 개수 — 창에 걸치기만 해도 센다
            }
        }

        long t = tRun + tDown + tNonProd;
        long available = tRun + tDown;

        double? a = available > 0 ? tRun / (double)available : null;
        // P 는 100% 를 넘을 수 있다 — 클립하지 않는다(doc/30 §7, 원본 스펙 §7).
        double? p = tRun > 0 ? sumR / tRun : null;
        double? oee = a is double av && p is double pv ? av * pv * k.Quality : null;
        double? u = t > 0 ? available / (double)t : null;
        double? teep = u is double uv && oee is double ov ? uv * ov : null;

        double? ttr = down > 0 ? downCtSum / (double)down : null;
        double? tbf = null;
        if (downStarts.Count >= 2)
        {
            downStarts.Sort();
            double gap = 0;
            for (int i = 1; i < downStarts.Count; i++) gap += downStarts[i] - downStarts[i - 1];
            tbf = gap / (downStarts.Count - 1);
        }

        return new KpiTotals(
            run, down, nonProd, excluded,
            tRun, tDown, tNonProd, t,
            sumR, a, p, k.Quality, oee, u, teep,
            down, ttr, tbf, clipped);
    }

    /// <summary>
    /// 연표 세그먼트 생성 — 시작 시각 순으로 정렬한 뒤 인접한 같은 상태(같은 제외 사유)를 병합한다.
    /// 긴 구간도 한 줄에 들어가도록 서버에서 미리 묶어 보낸다(doc/30 §9.1).
    /// </summary>
    public static List<KpiSegment> BuildSegments(IReadOnlyList<CycleFact> facts, in KpiKappa kappa)
    {
        var k = kappa.Normalized();
        var segments = new List<KpiSegment>();
        if (facts.Count == 0) return segments;

        var ordered = facts.OrderBy(f => f.StartMs).ThenBy(f => f.EndMs).ToList();

        long s = 0, e = 0, ct = 0, mt = 0;
        int cycles = 0;
        double rSum = 0, mtMedSum = 0, worstRatio = 0, mtRatio = 0;
        string? worstWork = null;
        DownAxis axis = DownAxis.None;
        CycleState state = CycleState.Excluded;
        ExcludeReason reason = ExcludeReason.None;
        bool open = false;

        void Flush()
        {
            if (!open) return;
            segments.Add(new KpiSegment(
                s, e, state, reason, cycles, ct, mt,
                cycles > 0 ? rSum / cycles : 0,
                cycles > 0 ? mtMedSum / cycles : 0,
                axis, worstWork, worstRatio, mtRatio));
            open = false;
        }

        // 세그먼트가 보일 "대표 초과" — 축을 넘은 정도(임계 대비 배수)가 가장 큰 사이클의 것.
        void Absorb(in CycleFact f)
        {
            e = f.EndMs; ct += f.CtMs; cycles++; rSum += f.RMs; mtMedSum += f.MtMedianMs;
            if (f.MtMs is long m && m > 0) mt += m;
            var ax = Axis(f, k);
            double score = ax switch
            {
                DownAxis.Work => f.WorstRatio / k.Work,
                DownAxis.Mt => f.MtRatio / k.Mt,
                _ => 0,
            };
            double cur = axis switch
            {
                DownAxis.Work => worstRatio / k.Work,
                DownAxis.Mt => mtRatio / k.Mt,
                _ => 0,
            };
            if (ax != DownAxis.None && score >= cur)
            {
                axis = ax; worstRatio = f.WorstRatio; worstWork = f.WorstWork; mtRatio = f.MtRatio;
            }
            else if (axis == DownAxis.None && f.WorstRatio > worstRatio)
            {
                worstRatio = f.WorstRatio; worstWork = f.WorstWork; mtRatio = Math.Max(mtRatio, f.MtRatio);
            }
        }

        foreach (var f in ordered)
        {
            var st = Classify(f, k);
            var rs = st == CycleState.Excluded ? ResolveExclude(f, k) : ExcludeReason.None;

            // 같은 상태이고 시간이 이어지면 병합. 사이에 틈이 있으면 새 세그먼트로 끊는다.
            if (open && st == state && rs == reason && f.StartMs == e)
            {
                Absorb(f);
                continue;
            }

            Flush();
            s = f.StartMs; e = f.StartMs; ct = 0; mt = 0; cycles = 0; rSum = 0; mtMedSum = 0;
            worstRatio = 0; worstWork = null; mtRatio = 0; axis = DownAxis.None;
            state = st; reason = rs; open = true;
            Absorb(f);
        }
        Flush();
        return segments;
    }

    /// <summary>
    /// 이동 중앙값. 표본이 <see cref="MinBaselineSamples"/> 미만이면 null 을 돌려 기준선을 만들지 않는다.
    /// 입력 리스트는 정렬되지 않아도 된다(내부 복사본을 정렬).
    /// </summary>
    public static double? Median(IReadOnlyList<long> values, int minSamples = MinBaselineSamples)
        => QuartilesOf(values, minSamples)?.Median;

    /// <summary>
    /// 사분위. Q1 = 정렬 후 n/4 번째, Q3 = 3n/4 번째(0 기준 인덱스) — 현장 실측(doc/30 §15-⑥)에 쓴 정의와 같다.
    /// 표본이 <paramref name="minSamples"/> 미만이면 null.
    /// </summary>
    public static Quartiles? QuartilesOf(IReadOnlyList<long> values, int minSamples = MinBaselineSamples)
    {
        if (values.Count < Math.Max(1, minSamples)) return null;
        var v = values.ToArray();
        Array.Sort(v);
        int n = v.Length;
        double med = n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2.0;
        return new Quartiles(v[n / 4], med, v[3 * n / 4], n);
    }

    /// <summary>
    /// work 신뢰도 게이트(doc/30 §4.1) — 분포가 두 갈래(Q3 ÷ Q1 &gt; gate)인 work 는 중앙값이 바닥에 깔려
    /// 배수 임계가 무의미하다. Q1 이 0 이면 비율이 무한이라 역시 뺀다. 게이트에 걸린 work 는 표시는 하되 판정 근거로 쓰지 않는다.
    /// </summary>
    public static bool IsGated(in Quartiles q, double gate = WorkGateDefault)
        => !(q.Q1 > 0) || q.Q3 / q.Q1 > gate;

    /// <summary>검산(doc/30 §7) — A×P 가 ΣR÷(T가동+T비가동) 과, TEEP 이 Q×ΣR÷T 와 맞는지.</summary>
    public static bool Verify(KpiTotals t, double tolerance = 1e-6)
    {
        long available = t.TRunMs + t.TDownMs;
        if (t.A is double a && t.P is double p && available > 0)
        {
            double lhs = a * p;
            double rhs = t.SumRMs / available;
            if (Math.Abs(lhs - rhs) > tolerance * Math.Max(1.0, Math.Abs(rhs))) return false;
        }
        if (t.Teep is double teep && t.TMs > 0)
        {
            double rhs = t.Q * t.SumRMs / t.TMs;
            if (Math.Abs(teep - rhs) > tolerance * Math.Max(1.0, Math.Abs(rhs))) return false;
        }
        return t.TMs == t.TRunMs + t.TDownMs + t.TNonProdMs;
    }
}
