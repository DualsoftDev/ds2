// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Services;

/// <summary>
/// 원시 PLC 엣지(Head OutTag↑ 시작 경계 + Tail InTag↑ 완료 마커)로부터 사이클 단위 분해를 만드는
/// 순수 함수 모음. 화면(<c>CallTestController.ComputeCycleStats</c>)과 과거 history 재계산
/// (<c>CycleRecomputeService</c>)이 <b>같은 코드</b>를 쓰게 해 측정 <b>정의</b>(per-cycle 분해 + 유휴 필터)를
/// 일치시킨다(드리프트 방지). 같은 시간 범위·경계를 비교하면 수치가 일치한다 — 단 대시보드 평균은
/// 전체 이력, 화면은 보이는 윈도우라는 <b>범위</b> 차이와, history 가 정수 ms 라 화면 double 과의 sub-ms 절단
/// 차이는 정의상 남는다.
///
/// <para>분해 정의 (라이브 엔진의 가산 정의 CT=MT+WT 와 동일, 단 source 가 PLC 엣지):</para>
/// <list type="bullet">
///   <item>시작(start)   = Head OutTag↑</item>
///   <item>완료(complete) = 해당 사이클 구간 내 첫 Tail InTag↑</item>
///   <item>MT(활성)  = complete − start</item>
///   <item>CT(주기)  = 다음 start − start  (마지막 열린 사이클은 null)</item>
///   <item>WT(대기)  = 다음 start − complete = CT − MT  (라이브 WT 정의와 동일)</item>
/// </list>
/// </summary>
public static class CycleDerivation
{
    /// <summary>한 사이클의 도출 결과. 시각은 입력 엣지의 Kind(보통 Local)를 그대로 보존한다.</summary>
    public readonly record struct CycleRecord(
        DateTime Start,
        DateTime? Complete,
        double? ActiveMs,
        double? PeriodMs);

    /// <summary>
    /// Head 시작 경계(<paramref name="starts"/>)와 Tail 완료 엣지(<paramref name="tailEdges"/>)로
    /// 사이클별 (start, complete, MT, CT) 을 만든다. <paramref name="windowEnd"/> 는 마지막 사이클의
    /// 활성 구간 상한(다음 start 가 없을 때 사용). 두 입력 모두 오름차순 정렬 가정.
    ///
    /// <para><c>CallTestController.ComputeCycleStats</c> 의 기존 로직과 1:1 동일하다:
    /// PeriodMs[i] = starts[i+1]−starts[i] (마지막은 null); ActiveMs[i] = (cEnd 전의 첫 Tail 엣지)−starts[i],
    /// cEnd = starts[i+1] 또는 windowEnd. tail 포인터(ti)는 사이클 간에 공유되어 한 번 쓰인 엣지는 재사용 안 됨.</para>
    /// </summary>
    public static List<CycleRecord> BuildCycles(
        IReadOnlyList<DateTime> starts, IReadOnlyList<DateTime> tailEdges, DateTime windowEnd)
        => BuildCycles(starts, new[] { tailEdges }, windowEnd);

    /// <summary>
    /// 복수 I/O 쌍(ApiCall) 완료 = <b>AND</b> 오버로드 (2026-09-02, 엔진 canCompleteCall forall 과 정렬).
    /// <paramref name="tailStreams"/> = 쌍별 완료 마커 엣지 스트림(각 오름차순). 사이클 완료 시각은
    /// "스트림별 (start, end) 내 첫 엣지"의 <b>최댓값(마지막 응답)</b>이고, 한 스트림이라도 창 안에 엣지가
    /// 없으면 그 사이클은 미완료(complete=null → CT 만 기록, MT/WT 없음 — 단일 쌍의 tail 미검출과 동일 취급).
    /// 스트림 1개면 기존 단일 정의와 완전히 동일하다(포인터 소비 규약 포함).
    /// 스트림 0개 = 완료 마커 관측 불가 — 전 사이클 미완료(기존 tailEdges 빈 목록과 동일).
    /// </summary>
    public static List<CycleRecord> BuildCycles(
        IReadOnlyList<DateTime> starts, IReadOnlyList<IReadOnlyList<DateTime>> tailStreams, DateTime windowEnd)
    {
        var result = new List<CycleRecord>();
        int n = starts.Count;
        if (n == 0) return result;

        // 스트림별 포인터 — 한 번 쓰인 엣지는 재사용 안 됨(단일 스트림 시절 ti 규약 그대로).
        var ti = new int[tailStreams.Count];

        for (int i = 0; i < n; i++)
        {
            var cStart = starts[i];
            bool hasNext = i + 1 < n;
            var cEnd = hasNext ? starts[i + 1] : windowEnd;
            double? periodMs = hasNext ? (starts[i + 1] - cStart).TotalMilliseconds : (double?)null;

            DateTime? complete = null;
            double? activeMs = null;
            if (tailStreams.Count > 0)
            {
                var worst = DateTime.MinValue;
                bool all = true;
                for (int k = 0; k < tailStreams.Count; k++)
                {
                    var edges = tailStreams[k];
                    while (ti[k] < edges.Count && edges[ti[k]] <= cStart) ti[k]++;
                    if (ti[k] < edges.Count && edges[ti[k]] < cEnd)
                    {
                        // 이 창에 속한 엣지 — AND 성립 여부와 무관하게 소비(다음 사이클 것이 아님).
                        if (edges[ti[k]] > worst) worst = edges[ti[k]];
                        ti[k]++;
                    }
                    else
                    {
                        all = false; // 이 쌍은 창 안에 응답 없음 → 사이클 미완료
                    }
                }
                if (all)
                {
                    complete = worst;
                    activeMs = (worst - cStart).TotalMilliseconds;
                }
            }

            result.Add(new CycleRecord(cStart, complete, activeMs, periodMs));
        }

        return result;
    }

    /// <summary>
    /// 화면용 평균. AvgCycleMs = 주기(Period) 평균(사이클 ≥ 2 일 때만 존재), AvgActiveMs = 활성(MT) 평균.
    /// <para><paramref name="maxCycleTimeMs"/>/<paramref name="minCycleTimeMs"/> 가 &gt;0 이면 비가동(이상치) 사이클
    /// (주기 CT 가 범위 밖)을 대시보드(IsIdle 제외 집계)와 동일하게 평균에서 제외한다 → 화면 ↔ 대시보드 1:1.
    /// 임계가 0(기본)이면 모든 사이클 포함.</para>
    /// </summary>
    public static (double? AvgCycleMs, double? AvgActiveMs) Averages(
        IReadOnlyList<CycleRecord> cycles, int maxCycleTimeMs = 0, int minCycleTimeMs = 0)
    {
        double? avgCycleMs = null;
        double? avgActiveMs = null;

        var periods = new List<double>();
        var actives = new List<double>();
        foreach (var c in cycles)
        {
            // 비가동 판정은 주기(CT) 기준 — 대시보드 IsIdle(CT>Max || CT<Min)과 동일. 주기 없는(마지막 열린)
            // 사이클은 CT 가 없어 비가동 판정 불가 → 활성만 있으면 포함(대시보드 IsIdle=false 와 일치).
            bool idle = c.PeriodMs.HasValue
                && ((maxCycleTimeMs > 0 && c.PeriodMs.Value > maxCycleTimeMs)
                 || (minCycleTimeMs > 0 && c.PeriodMs.Value < minCycleTimeMs));
            if (idle) continue;

            if (c.PeriodMs.HasValue) periods.Add(c.PeriodMs.Value);
            if (c.ActiveMs.HasValue) actives.Add(c.ActiveMs.Value);
        }

        if (periods.Count > 0) avgCycleMs = periods.Average();
        if (actives.Count > 0) avgActiveMs = actives.Average();

        return (avgCycleMs, avgActiveMs);
    }
}
