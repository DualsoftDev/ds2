// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Kpi;

/// <summary>
/// 시간 기반 코어 v68 — 사이클 상태. doc/30 §5.
/// 유효 상태 셋(가동·비가동·비생산)은 모든 합산에 들어가고, 제외는 어디에도 들어가지 않는다.
/// </summary>
public enum CycleState
{
    /// <summary>계산 밖 — 잘림 · 미상(UNK) · 진행 중 · 기준 없음.</summary>
    Excluded = 0,
    /// <summary>가동 — 비생산도 비가동도 아닌 나머지.</summary>
    Run = 1,
    /// <summary>비가동 — work 중 하나라도 자기 중앙값 W × κ_비가동 초과(OR 조건).</summary>
    Down = 2,
    /// <summary>비생산 — CT ≥ κ_비생산 × R. 비가동보다 우선한다.</summary>
    NonProd = 3,
}

/// <summary>제외 사유. doc/30 §5 순서 0.</summary>
public enum ExcludeReason
{
    None = 0,
    /// <summary>조회 구간 경계에 걸쳐 잘린 행 — 개수·합산 제외(연표에는 잘라 그린다).</summary>
    Cut = 1,
    /// <summary>이전 head 를 알 수 없는 구간(수집 시작 직후·데이터 공백 직후).</summary>
    Unknown = 2,
    /// <summary>다음 head 가 아직 없는 열린 사이클 — R 을 적립하지 않는다.</summary>
    InProgress = 3,
    /// <summary>표본 K 미달로 R 을 박제하지 못한 행.</summary>
    NoBaseline = 4,
}

/// <summary>
/// 사용자 조절 계수와 품질. doc/30 §4 · §6.
/// 기본값은 <see cref="Default"/>. 값이 바뀌면 과거 행도 즉시 재라벨된다(상태 미저장, 조회 시 도출).
/// </summary>
public readonly record struct KpiKappa(double NonProd, double Down, double Quality)
{
    public const double NonProdDefault = 10.0;
    public const double DownDefault = 2.5;
    public const double QualityDefault = 1.0;

    public const double NonProdMin = 3.0;
    public const double NonProdMax = 100.0;
    public const double DownMin = 1.5;
    public const double DownMax = 10.0;

    public static KpiKappa Default => new(NonProdDefault, DownDefault, QualityDefault);

    /// <summary>범위 밖 입력을 안전값으로 접는다. 0·NaN 은 기본값으로 되돌린다.</summary>
    public KpiKappa Normalized() => new(
        double.IsFinite(NonProd) && NonProd > 0 ? Math.Clamp(NonProd, NonProdMin, NonProdMax) : NonProdDefault,
        double.IsFinite(Down) && Down > 0 ? Math.Clamp(Down, DownMin, DownMax) : DownDefault,
        double.IsFinite(Quality) && Quality >= 0 ? Math.Clamp(Quality, 0.0, 1.0) : QualityDefault);
}

/// <summary>
/// 판정·집계에 필요한 사이클 행의 최소 사실. DB 행에서 그대로 뽑아 온다.
/// <para>
/// <paramref name="RMs"/> 와 <paramref name="WorstRatio"/> 는 <b>사이클 완료 시점에 박제</b>된 값이다(doc/30 §3).
/// 기준선이 나중에 움직여도 이 값은 변하지 않는다. 반대로 κ 는 조회 시점 값을 쓰므로 계수 변경은 과거까지 재라벨된다.
/// </para>
/// </summary>
/// <param name="StartMs">사이클 시작(head) — Unix epoch ms(UTC).</param>
/// <param name="EndMs">사이클 끝(= 다음 head) — Unix epoch ms(UTC).</param>
/// <param name="CtMs">EndMs − StartMs.</param>
/// <param name="RMs">박제된 기준 R(ms). 0 이하면 기준 없음.</param>
/// <param name="WorstRatio">이 사이클에서 (work 지속시간 ÷ 그 work 의 W) 최댓값. 0 이면 비교 대상 work 없음.</param>
/// <param name="WorstWork">위 최댓값을 만든 work 이름(표시용).</param>
/// <param name="Exclude">이미 확정된 제외 사유. None 이면 규칙으로 판정한다.</param>
public readonly record struct CycleFact(
    long Id,
    long StartMs,
    long EndMs,
    long CtMs,
    double RMs,
    double WorstRatio,
    string? WorstWork,
    ExcludeReason Exclude);

/// <summary>구간 집계 결과. doc/30 §6.</summary>
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
    double? TbfMs)
{
    public static KpiTotals Empty(double q) =>
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, q, null, null, null, 0, null, null);
}

/// <summary>연표 세그먼트 — 인접한 같은 상태의 사이클을 하나로 묶은 것. doc/30 §8.1.</summary>
public sealed record KpiSegment(
    long StartMs,
    long EndMs,
    CycleState State,
    ExcludeReason Reason,
    int Cycles,
    long CtMs,
    double RMs,
    string? WorstWork,
    double WorstRatio);

/// <summary>
/// 시간 기반 코어의 판정·집계 순수 함수 SSOT. DB·HTTP·설정에 의존하지 않는다.
/// 화면·엑셀·메일이 모두 이 결과만 본다.
/// </summary>
public static class KpiRules
{
    /// <summary>기준선(R·W)을 박제하기 위한 최소 완료 사이클 수. doc/30 §3.</summary>
    public const int MinBaselineSamples = 10;

    /// <summary>기준선 이동 중앙값 창(일). doc/30 §3.</summary>
    public const int BaselineWindowDays = 14;

    /// <summary>
    /// 행 하나의 상태. 우선순위는 제외 ▸ 비생산 ▸ 비가동 ▸ 가동(doc/30 §5).
    /// 비생산과 비가동이 동시에 성립하면 비생산이다 — 아주 긴 사이클은 work 가 멈춰 있었어도 장기 비활성으로 본다.
    /// </summary>
    public static CycleState Classify(in CycleFact f, in KpiKappa k)
    {
        if (f.Exclude != ExcludeReason.None) return CycleState.Excluded;
        if (f.CtMs <= 0) return CycleState.Excluded;
        if (!(f.RMs > 0)) return CycleState.Excluded;

        if (f.CtMs >= k.NonProd * f.RMs) return CycleState.NonProd;
        if (f.WorstRatio > k.Down) return CycleState.Down;
        return CycleState.Run;
    }

    /// <summary>행이 이미 제외로 확정됐는지(규칙 판정 이전).</summary>
    public static ExcludeReason ResolveExclude(in CycleFact f)
    {
        if (f.Exclude != ExcludeReason.None) return f.Exclude;
        if (f.CtMs <= 0) return ExcludeReason.InProgress;
        if (!(f.RMs > 0)) return ExcludeReason.NoBaseline;
        return ExcludeReason.None;
    }

    /// <summary>
    /// 구간 집계. T 는 캘린더가 아니라 유효 행 CT 의 합이다(doc/30 §6).
    /// 입력은 시작 시각 오름차순일 필요가 없다 — TBF 계산을 위해 내부에서 정렬한다.
    /// </summary>
    public static KpiTotals Compute(IReadOnlyList<CycleFact> facts, in KpiKappa kappa)
    {
        var k = kappa.Normalized();
        if (facts.Count == 0) return KpiTotals.Empty(k.Quality);

        int run = 0, down = 0, nonProd = 0, excluded = 0;
        long tRun = 0, tDown = 0, tNonProd = 0;
        double sumR = 0;
        var downStarts = new List<long>();
        long downCtSum = 0;

        foreach (var f in facts)
        {
            switch (Classify(f, k))
            {
                case CycleState.Run:
                    run++; tRun += f.CtMs; sumR += f.RMs; break;
                case CycleState.Down:
                    down++; tDown += f.CtMs; downCtSum += f.CtMs; downStarts.Add(f.StartMs); break;
                case CycleState.NonProd:
                    nonProd++; tNonProd += f.CtMs; break;
                default:
                    excluded++; break;
            }
        }

        long t = tRun + tDown + tNonProd;
        long available = tRun + tDown;

        double? a = available > 0 ? tRun / (double)available : null;
        // P 는 100% 를 넘을 수 있다 — 클립하지 않는다(doc/30 §6, 원본 스펙 §7).
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
            down, ttr, tbf);
    }

    /// <summary>
    /// 연표 세그먼트 생성 — 시작 시각 순으로 정렬한 뒤 인접한 같은 상태(같은 제외 사유)를 병합한다.
    /// 긴 구간도 한 줄에 들어가도록 서버에서 미리 묶어 보낸다(doc/30 §8.1).
    /// </summary>
    public static List<KpiSegment> BuildSegments(IReadOnlyList<CycleFact> facts, in KpiKappa kappa)
    {
        var k = kappa.Normalized();
        var segments = new List<KpiSegment>();
        if (facts.Count == 0) return segments;

        var ordered = facts.OrderBy(f => f.StartMs).ThenBy(f => f.EndMs).ToList();

        long s = 0, e = 0, ct = 0;
        int cycles = 0;
        double rSum = 0, worstRatio = 0;
        string? worstWork = null;
        CycleState state = CycleState.Excluded;
        ExcludeReason reason = ExcludeReason.None;
        bool open = false;

        void Flush()
        {
            if (!open) return;
            segments.Add(new KpiSegment(s, e, state, reason, cycles, ct,
                cycles > 0 ? rSum / cycles : 0, worstWork, worstRatio));
            open = false;
        }

        foreach (var f in ordered)
        {
            var st = Classify(f, k);
            var rs = st == CycleState.Excluded ? ResolveExclude(f) : ExcludeReason.None;

            // 같은 상태이고 시간이 이어지면 병합. 사이에 틈이 있으면 새 세그먼트로 끊는다.
            if (open && st == state && rs == reason && f.StartMs == e)
            {
                e = f.EndMs; ct += f.CtMs; cycles++; rSum += f.RMs;
                if (f.WorstRatio > worstRatio) { worstRatio = f.WorstRatio; worstWork = f.WorstWork; }
                continue;
            }

            Flush();
            s = f.StartMs; e = f.EndMs; ct = f.CtMs; cycles = 1; rSum = f.RMs;
            worstRatio = f.WorstRatio; worstWork = f.WorstWork;
            state = st; reason = rs; open = true;
        }
        Flush();
        return segments;
    }

    /// <summary>
    /// 이동 중앙값. 표본이 <see cref="MinBaselineSamples"/> 미만이면 null 을 돌려 기준선을 만들지 않는다.
    /// 입력 리스트는 정렬되지 않아도 된다(내부 복사본을 정렬).
    /// </summary>
    public static double? Median(IReadOnlyList<long> values, int minSamples = MinBaselineSamples)
    {
        if (values.Count < Math.Max(1, minSamples)) return null;
        var v = values.ToArray();
        Array.Sort(v);
        int n = v.Length;
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2.0;
    }

    /// <summary>검산(doc/30 §6) — A×P 가 ΣR÷(T가동+T비가동) 과, TEEP 이 Q×ΣR÷T 와 맞는지.</summary>
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
