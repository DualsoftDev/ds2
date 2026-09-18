// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Kpi;

/// <summary>
/// 등록 에러 태그 기반 신뢰성 지표(eMTBF · eMTTR)의 판정 규칙 — doc/31. 순수 함수만 둔다.
/// <para>
/// doc/30 의 리듬축(비가동 기준 MTBF/MTTR)과 <b>별개 축</b>이다. 그쪽은 무엇이 고장인지 모르고 사이클
/// 시간만 보지만, 이 축은 사용자가 이상알람TAG 로 <b>고장을 선언</b>한다. 같은 설비에서 리듬축이 늘 더 많이
/// 잡는 것이 정상이다(느린 사이클 ⊃ 등록된 고장).
/// </para>
/// </summary>
public static class ErrorTagReliability
{
    /// <summary>알람 1건의 회복 상태 — doc/31 §3. 지표에 들어가는 것은 <see cref="Recovered"/> 뿐이다.</summary>
    public enum RecoveryState
    {
        /// <summary>아직 해소되지 않음. 조건이 계속 걸려 있다.</summary>
        InProgress = 0,

        /// <summary>해소됐고 재가동을 기다리는 중 — 확인 창이 아직 남았다.</summary>
        AwaitingRestart = 1,

        /// <summary>확인 창 안에 재가동이 없었다. 리셋만 하고 설비는 돌지 않았을 수 있다.</summary>
        RestartUnconfirmed = 2,

        /// <summary>해소 + 재가동. eMTTR 분모.</summary>
        Recovered = 3,
    }

    /// <summary>
    /// 알람 1건의 입력. 시각은 전부 epoch ms(UTC) — <c>OnCallGoingStarted</c> 계열의 Local DateTime 을
    /// 그대로 넣으면 9시간 어긋난다(doc/31 §7).
    /// </summary>
    /// <param name="OccurredMs">발생.</param>
    /// <param name="ClearedMs">해소. null = 진행 중.</param>
    /// <param name="WindowMs">재가동 확인 창(해소 이후 이만큼). 0 이하면 무한.</param>
    public readonly record struct AlertInput(long OccurredMs, long? ClearedMs, long WindowMs);

    /// <summary>판정 결과. <see cref="RestartMs"/> 는 복구 완료일 때만 값이 있다.</summary>
    public readonly record struct Recovery(RecoveryState State, long? RestartMs)
    {
        public bool IsRecovered => State == RecoveryState.Recovered;
    }

    /// <summary>
    /// 한 건의 회복 판정. <paramref name="restartsAsc"/> 는 그 태그에 묶인 디바이스가 쓰이는 flow 들의
    /// <b>사이클 시작 시각</b> 오름차순 합집합이다(flow 를 가리지 않는 OR — doc/31 §2).
    /// </summary>
    /// <param name="nowMs">
    /// 조회 기준 시각. 확인 창이 아직 안 지났으면 <see cref="RecoveryState.AwaitingRestart"/> 로 남긴다 —
    /// 지금 재가동이 없다고 바로 '미확인' 으로 찍으면 방금 해소된 건이 전부 경고로 뜬다.
    /// </param>
    public static Recovery Resolve(AlertInput alert, IReadOnlyList<long> restartsAsc, long nowMs)
    {
        if (alert.ClearedMs is not { } cleared)
            return new Recovery(RecoveryState.InProgress, null);

        // 재가동은 해소 '이후' 것만 인정한다. 같은 ms 는 인정하지 않는다 — 해소를 만든 신호와 같은 순간에
        // 시작된 사이클은 그 해소의 결과일 수 없다.
        var restart = FirstAfter(restartsAsc, cleared);

        var unlimited = alert.WindowMs <= 0;
        var deadline = unlimited ? long.MaxValue : cleared + alert.WindowMs;

        if (restart is { } r && r <= deadline)
            return new Recovery(RecoveryState.Recovered, r);

        // 창을 넘긴 재가동은 회복 근거로 쓰지 않는다. 창이 아직 안 지났으면 판단을 미룬다.
        if (!unlimited && nowMs > deadline)
            return new Recovery(RecoveryState.RestartUnconfirmed, null);

        return new Recovery(RecoveryState.AwaitingRestart, null);
    }

    /// <summary>정렬된 목록에서 <paramref name="afterMs"/> 보다 <b>큰</b> 첫 값. 이진 탐색.</summary>
    private static long? FirstAfter(IReadOnlyList<long> ascending, long afterMs)
    {
        if (ascending is not { Count: > 0 }) return null;
        int lo = 0, hi = ascending.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (ascending[mid] > afterMs) hi = mid; else lo = mid + 1;
        }
        return lo < ascending.Count ? ascending[lo] : null;
    }

    /// <summary>
    /// 같은 태그의 재발화를 한 건으로 묶는다 — 회복(재가동)을 사이에 두지 않은 연속 발생은 같은 고장이다.
    /// 입력은 <b>한 태그</b>의 발생 오름차순이어야 하고, 살아남는 것은 각 묶음의 <b>첫 발생</b>이다
    /// (고장이 시작된 시각이 그것이므로).
    /// </summary>
    public static List<int> DedupeReignitions(IReadOnlyList<(long OccurredMs, long? RestartMs)> alertsAsc)
    {
        var kept = new List<int>();
        long? openUntil = null;   // 직전에 살아남은 건의 재가동 시각. null = 아직 회복 안 됨(계속 열려 있음)
        var hasOpen = false;

        for (var i = 0; i < alertsAsc.Count; i++)
        {
            var (occurred, restart) = alertsAsc[i];

            // 앞 건이 아직 회복되지 않았거나, 회복 시각보다 먼저 울린 발생이면 같은 고장의 재발화다.
            if (hasOpen && (openUntil is null || occurred <= openUntil))
                continue;

            kept.Add(i);
            openUntil = restart;
            hasOpen = true;
        }
        return kept;
    }

    /// <summary>
    /// 비생산 구간을 뺀 경과 시간.
    /// 금요일 19시에 고치고 월요일 08시에 라인이 돌면 차감 없이 eMTTR 61시간이 찍힌다 — 수리 시간이 아니라
    /// 주말이다(doc/31 §4). <paramref name="nonProdSpans"/> 는 겹치지 않는 오름차순 구간이어야 한다.
    /// </summary>
    public static long ElapsedExcludingNonProduction(long fromMs, long toMs, IReadOnlyList<Span> nonProdSpans)
    {
        if (toMs <= fromMs) return 0;
        var gross = toMs - fromMs;
        var excluded = WorkSpanMath.OverlapMs(nonProdSpans, fromMs, toMs);
        // 방어 — 겹치는 구간이 섞여 들어오면 차감이 총량을 넘을 수 있다. 음수 지표를 내보내지 않는다.
        return excluded >= gross ? 0 : gross - excluded;
    }

    /// <summary>
    /// 여러 flow 의 비생산 구간을 <b>교집합</b>으로 합친다 — 한 디바이스가 여러 flow 에 걸칠 때
    /// "이 디바이스가 비생산이었다" 는 <b>후보 flow 가 전부</b> 비생산이었다는 뜻이다.
    /// <para>
    /// 회복 판정이 OR("하나라도 돌면 재가동")인 것의 대칭이다. 합집합으로 빼면 한 flow 만 쉬어도
    /// 그 시간이 통째로 차감되어 eMTTR 이 실제보다 짧게 나온다.
    /// </para>
    /// 각 목록은 겹치지 않는 오름차순이어야 한다. 빈 목록이 하나라도 있으면 교집합도 비어 있다.
    /// </summary>
    public static List<Span> IntersectSpans(IReadOnlyList<IReadOnlyList<Span>> perFlowSpans)
    {
        if (perFlowSpans is not { Count: > 0 }) return [];
        if (perFlowSpans.Count == 1) return [.. perFlowSpans[0]];

        var acc = perFlowSpans[0].ToList();
        for (var i = 1; i < perFlowSpans.Count && acc.Count > 0; i++)
            acc = IntersectPair(acc, perFlowSpans[i]);
        return acc;
    }

    /// <summary>정렬된 두 구간 목록의 교집합 — 투 포인터.</summary>
    private static List<Span> IntersectPair(IReadOnlyList<Span> a, IReadOnlyList<Span> b)
    {
        var result = new List<Span>();
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            var s = Math.Max(a[i].S, b[j].S);
            var e = Math.Min(a[i].E, b[j].E);
            if (e > s) result.Add(new Span(s, e));
            // 먼저 끝나는 쪽을 넘긴다 — 남은 쪽은 다음 구간과도 겹칠 수 있다.
            if (a[i].E < b[j].E) i++; else j++;
        }
        return result;
    }

    /// <summary>
    /// 집계 결과. <b>두 지표의 모집단이 다르다</b> — eMTBF 의 고장 건수는 발생 전체이고 eMTTR 평균은
    /// 복구 완료 건만이다. 미확정 건을 건수에서도 빼면 고장이 과소 계상되어 eMTBF 가 부풀어 오른다.
    /// </summary>
    /// <param name="FaultCount">발생 건수(재발화 병합 후). eMTBF 의 분모.</param>
    /// <param name="RecoveredCount">복구 완료 건수. eMTTR 의 분모.</param>
    /// <param name="EMttrMs">복구 완료 건의 (재가동 − 발생) 평균, 비생산 차감. 표본 미달이면 null.</param>
    /// <param name="EMtbfMs">복구 → 다음 발생 간격 평균, 비생산 차감. 표본 미달이면 null.</param>
    public readonly record struct Summary(
        int FaultCount,
        int RecoveredCount,
        int InProgressCount,
        int AwaitingRestartCount,
        int RestartUnconfirmedCount,
        double? EMttrMs,
        double? EMtbfMs,
        int MtbfIntervalCount)
    {
        /// <summary>표본이 K 에 못 미쳐 숫자를 내지 않은 상태인지 — 화면이 "표본 부족 (n=3)" 을 띄운다.</summary>
        public bool MttrBelowSample => EMttrMs is null;
        public bool MtbfBelowSample => EMtbfMs is null;
    }

    /// <summary>표본 하한 — doc/30 의 K 규약과 같은 수. 미달이면 숫자 대신 "표본 부족".</summary>
    public const int MinSample = 3;

    /// <summary>
    /// 판정된 건들을 지표로 모은다. 입력은 <b>발생 오름차순</b>이고 재발화 병합이 끝난 상태여야 한다.
    /// </summary>
    /// <param name="minSample">표본 하한. 테스트·설정에서 낮출 수 있도록 인자로 둔다.</param>
    public static Summary Aggregate(
        IReadOnlyList<(AlertInput Alert, Recovery Recovery)> resolvedAsc,
        IReadOnlyList<Span> nonProdSpans,
        int minSample = MinSample)
    {
        int inProgress = 0, awaiting = 0, unconfirmed = 0;
        var repairs = new List<long>();
        var upSpans = new List<long>();
        long? prevRestart = null;

        foreach (var (alert, recovery) in resolvedAsc)
        {
            switch (recovery.State)
            {
                case RecoveryState.InProgress: inProgress++; break;
                case RecoveryState.AwaitingRestart: awaiting++; break;
                case RecoveryState.RestartUnconfirmed: unconfirmed++; break;
            }

            // eMTBF = 직전 복구 → 이번 발생. 복구가 없는 건은 간격을 끊지 않고 건너뛴다
            // (그 사이가 가동 시간이었다고 주장할 근거가 없다).
            if (prevRestart is { } prev && alert.OccurredMs > prev)
                upSpans.Add(ElapsedExcludingNonProduction(prev, alert.OccurredMs, nonProdSpans));

            if (recovery is { State: RecoveryState.Recovered, RestartMs: { } restart })
            {
                repairs.Add(ElapsedExcludingNonProduction(alert.OccurredMs, restart, nonProdSpans));
                prevRestart = restart;
            }
        }

        return new Summary(
            FaultCount: resolvedAsc.Count,
            RecoveredCount: repairs.Count,
            InProgressCount: inProgress,
            AwaitingRestartCount: awaiting,
            RestartUnconfirmedCount: unconfirmed,
            EMttrMs: repairs.Count >= minSample ? repairs.Average() : null,
            EMtbfMs: upSpans.Count >= minSample ? upSpans.Average() : null,
            MtbfIntervalCount: upSpans.Count);
    }
}
