// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Kpi;

/// <summary>
/// 등록 에러 태그 기반 신뢰성 지표(eMTBF · eMTTR)의 판정 규칙 — doc/31. 순수 함수만 둔다.
/// <para>
/// doc/30 의 리듬축(비가동 기준 MTBF/MTTR)과 <b>별개 축</b>이다. 그쪽은 무엇이 고장인지 모르고 사이클
/// 시간만 보지만, 이 축은 사용자가 이상알람TAG 로 <b>고장을 선언</b>한다.
/// </para>
/// <para>
/// 2026-09-22 개정. 현장 실측(100.66.47.73, 9/21)에서 화면 값이 통째로 틀린 것이 드러났다 —
/// 복구 23건 중 21건이 <b>설비가 도는 중에 울린 경고</b>였고, 6건은 <b>통신 재접속 순간의 스냅샷</b>이었다.
/// 종전 규칙은 "다시 돌았나" 만 물었기 때문에, 애초에 서지 않은 경고가 전부 '고장 → 복구' 로 둔갑했다.
/// 그래서 세 가지를 바꿨다.
/// <list type="number">
///   <item>정지 게이트 — 알람 시각에 <b>실제로 섰는지</b>를 사이클 시작 리듬으로 판정한다(<see cref="JudgeStop"/>).</item>
///   <item>사건 단위 — 고장의 단위가 태그가 아니라 <b>디바이스의 정지 1회</b>다(<see cref="BuildEvents"/>).</item>
///   <item>분모 — eMTBF 가 간격의 평균이 아니라 <b>가동시간 ÷ 고장 건수</b>다(<see cref="OperatingMs"/>).</item>
/// </list>
/// 셋 다 <b>사이클 시작 시각</b> 하나만 더 쓴다. 상태(가동·비가동·비생산) 라벨도 κ 도 읽지 않으므로
/// doc/31 §5 의 "v68 에서 빌려 읽는 것은 cycle.startMs 하나뿐" 이라는 계약은 그대로다.
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
    /// 알람 시각에 설비가 <b>실제로 멈췄는가</b> — doc/31 §3.1. 집계 진입 조건이지 회복 상태가 아니다.
    /// </summary>
    public enum StopVerdict
    {
        /// <summary>리듬 기준을 구할 수 없었다(사이클 표본 부족·경계 밖). 경고와 섞지 말 것.</summary>
        Unknown = 0,

        /// <summary>사이클 리듬이 끊기지 않았다 = 설비가 계속 돌았다. <b>고장이 아니라 경고다.</b></summary>
        NonStopWarning = 1,

        /// <summary>사이클 리듬이 끊겼다 = 설비가 섰다. 집계 대상.</summary>
        Stopped = 2,
    }

    /// <summary>
    /// 사건이 집계에서 빠진 이유 — 화면이 "고장 2건 / 경고 21건 / 스냅샷 6건" 으로 분해해 보이는 근거다.
    /// 숫자 하나만 내놓고 틀리는 것보다 분해를 보여 주는 쪽이 낫다는 실측 교훈(doc/31 §8).
    /// </summary>
    public enum SkipCause
    {
        /// <summary>집계 대상.</summary>
        None = 0,

        /// <summary>설비가 서지 않았다(<see cref="StopVerdict.NonStopWarning"/>).</summary>
        NonStopWarning = 1,

        /// <summary>통신이 붙는 순간 이미 켜져 있던 조건이 한꺼번에 발화한 것.</summary>
        LinkSnapshot = 2,

        /// <summary><c>Changed</c> 매치옵 — 발화 즉시 해소되어 지속시간이 뜻을 갖지 못한다.</summary>
        ChangedOp = 3,

        /// <summary>리듬 기준이 없어 정지 여부를 판정하지 못했다.</summary>
        UnknownStop = 4,
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
    public static long? FirstAfter(IReadOnlyList<long> ascending, long afterMs)
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

    /// <summary>정렬된 목록에서 <paramref name="atMs"/> <b>이하</b>인 마지막 값. 이진 탐색.</summary>
    public static long? LastAtOrBefore(IReadOnlyList<long> ascending, long atMs)
    {
        if (ascending is not { Count: > 0 }) return null;
        int lo = 0, hi = ascending.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (ascending[mid] <= atMs) lo = mid + 1; else hi = mid;
        }
        return lo > 0 ? ascending[lo - 1] : null;
    }

    // ── 리듬 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 리듬 기준 R = 사이클 <b>시작 간격의 중앙값</b>. 박제 R(<c>cycle.rUsedMs</c>)을 쓰지 않는 이유는
    /// doc/31 §7 — 박제 R 은 v68 기준선 기계의 산물이라 표본 게이트에 걸리면 0 이 되고, 새 DB 에서
    /// 기준선이 영영 안 잡히는 교착 전례가 있다(2026-09-21). 시작 시각만으로 구하면 그 의존이 사라진다.
    /// 실측에서 두 값은 거의 같았다(#135: 박제 192.5초 vs 실측 194초).
    /// </summary>
    public static double RhythmMs(IReadOnlyList<long> startsAsc)
    {
        if (startsAsc is not { Count: > 1 }) return 0;
        var gaps = new List<long>(startsAsc.Count - 1);
        for (var i = 1; i < startsAsc.Count; i++)
            if (startsAsc[i] > startsAsc[i - 1])
                gaps.Add(startsAsc[i] - startsAsc[i - 1]);
        if (gaps.Count == 0) return 0;
        gaps.Sort();
        return gaps[gaps.Count / 2];
    }

    /// <summary>리듬이 이 배수를 넘게 끊기면 '멈춤'. 실측 분리가 1.6배 vs 3.3배로 비어 있어 2.0 을 둔다.</summary>
    public const double StopRhythmMultiple = 2.0;

    /// <summary>
    /// 알람 시각에 설비가 섰는가 — doc/31 §3.1.
    /// <para>
    /// 사이클 <b>구간</b>이 아니라 <b>시작 시각</b>만 쓴다. 구간을 쓰면 분기 행이 서로 겹쳐(실측 #135 는
    /// 37행 중 28행이 겹쳤다) 한 시각의 상태가 여러 개가 되어 판정이 서지 않는다. 시작은 점이라 겹치지 않는다.
    /// </para>
    /// <para>
    /// 제외 행(UNK·진행 중·미분류)의 시작도 근거로 쓴다 — 제외는 행의 판정 성질이고 시작 시각 자체는
    /// 진짜 head 신호다. doc/31 이 재가동 근거를 고를 때 이미 쓰는 것과 같은 논리다.
    /// </para>
    /// </summary>
    public static StopVerdict JudgeStop(
        long atMs, IReadOnlyList<long> startsAsc, double rhythmMs, double multiple = StopRhythmMultiple)
    {
        if (rhythmMs <= 0) return StopVerdict.Unknown;

        var prev = LastAtOrBefore(startsAsc, atMs);
        var next = FirstAfter(startsAsc, atMs);
        // 관측 구간의 끝에 걸리면 판정하지 않는다 — 다음 사이클이 없는 것과 설비가 선 것은 다르다.
        if (prev is not { } p || next is not { } n) return StopVerdict.Unknown;

        return (n - p) > rhythmMs * multiple ? StopVerdict.Stopped : StopVerdict.NonStopWarning;
    }

    /// <summary>
    /// 가동시간 = 리듬이 유지된 시작 간격의 합(조회 창으로 자름). eMTBF 의 분모다.
    /// <para>
    /// 끊긴 간격(= 정지)은 빠지고, 사이클이 아예 없는 야간·주말도 자연히 빠진다. κ 를 읽지 않고
    /// "가동시간 분모" 가 되는 유일한 길이다 — 비생산이 사이클 <b>행</b>으로 존재하므로 구간 합집합으로는
    /// 안 된다는 것이 실측에서 드러났다(#135: 전체행 149분 vs 리듬유지 105분, 창 150분).
    /// </para>
    /// </summary>
    public static long OperatingMs(
        IReadOnlyList<long> startsAsc, double rhythmMs, long fromMs, long toMs,
        double multiple = StopRhythmMultiple)
    {
        if (rhythmMs <= 0 || startsAsc is not { Count: > 1 }) return 0;

        var bound = rhythmMs * multiple;
        long total = 0;
        for (var i = 1; i < startsAsc.Count; i++)
        {
            long a = startsAsc[i - 1], b = startsAsc[i];
            if (b <= a || (b - a) > bound) continue;          // 끊김 = 정지 구간이라 분모가 아니다
            long s = Math.Max(a, fromMs), e = Math.Min(b, toMs);
            if (e > s) total += e - s;                        // 창 잘림은 겹친 만큼(doc/30 §7.2)
        }
        return total;
    }

    // ── 재접속 스냅샷 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 통신이 붙은 뒤 이 시간 안의 발화는 '처음 읽었을 때 이미 켜져 있던 것' 으로 본다.
    /// 실측 오차는 0~11초였다(재접속 12:30:01 ↔ 발화 12:30:01, 재접속 12:07:13 ↔ 발화 12:07:24).
    /// </summary>
    public const long LinkSnapshotGraceMs = 30_000;

    /// <summary>
    /// 발화가 통신 재접속 직후인가 — doc/31 §4.1.
    /// <paramref name="linkAtAsc"/> 는 접속 전이 시각 오름차순이다. 링크 이벤트의 system 은 PLC 연결
    /// 이름이고 알람의 system 은 AASX System 이라 이름이 맞지 않으므로, <b>전 시스템의 합집합</b>을 쓴다
    /// (보수적으로 더 많이 걸러내는 쪽 — 유령 고장을 남기는 것보다 낫다).
    /// </summary>
    public static bool IsLinkSnapshot(
        long onsetMs, IReadOnlyList<long> linkAtAsc, long graceMs = LinkSnapshotGraceMs)
    {
        if (linkAtAsc is not { Count: > 0 }) return false;
        var prev = LastAtOrBefore(linkAtAsc, onsetMs);
        return prev is { } p && onsetMs - p <= graceMs;
    }

    // ── 사건(디바이스의 정지 1회) ─────────────────────────────────────────────

    /// <summary>
    /// 고장 사건 1건 — <b>한 디바이스가 한 번 선 것</b>. 태그 여러 개가 같은 정지에서 울렸으면 한 건이다.
    /// </summary>
    /// <param name="OnsetMs">묶인 알람의 최소 발생 — 고장이 시작된 시각.</param>
    /// <param name="ClearedMs">묶인 알람의 최대 해소. 하나라도 미해소면 null(사건이 안 닫혔다).</param>
    /// <param name="Members">묶인 알람의 입력 인덱스. 표에서 사건을 접어 보이는 근거.</param>
    public sealed record FaultEvent(long OnsetMs, long? ClearedMs, List<int> Members)
    {
        public long? ClearedMs { get; set; } = ClearedMs;
        public Recovery Recovery { get; set; }
        public StopVerdict Stop { get; set; }
        public SkipCause Skip { get; set; }

        /// <summary>발생 → 재가동. 복구 완료일 때만. 달력 시간 그대로(차감 없음 — doc/31 §4).</summary>
        public long? DownMs =>
            Recovery is { State: RecoveryState.Recovered, RestartMs: { } r } && r > OnsetMs ? r - OnsetMs : null;

        public bool Counts => Skip == SkipCause.None;
    }

    /// <summary>
    /// 한 디바이스의 알람들을 사건으로 묶는다 — doc/31 §2.1.
    /// <para>
    /// 경계는 <b>재가동</b>이지 해소가 아니다. 두 알람 사이에 그 디바이스의 flow 가 다시 돌지 않았으면
    /// 설비가 계속 서 있던 것이므로 같은 고장이다. 해소가 끝났어도 아직 안 돌았으면 묶인다.
    /// </para>
    /// <para>
    /// ★미해소(래치) 예외 — 안 꺼지는 알람은 사건이 영영 안 닫혀 뒤따르는 진짜 고장을 삼킨다.
    /// 실측 <c>#셔틀</c> 이 그랬다(12:07:24 미해소 · 12:30:01 미해소 · 12:56:26 진짜 고장). 그래서 미해소
    /// 사건의 흡수 경계는 <b>발생 이후 첫 재가동</b>이다 — 설비가 한 번이라도 돌면 더는 삼키지 않는다.
    /// 방향이 중요하다: 삼키면 고장 건수가 줄어 <b>가장 나쁜 설비가 가장 좋아 보인다</b>.
    /// </para>
    /// </summary>
    /// <param name="alertsAsc">한 디바이스의 알람, 발생 오름차순.</param>
    /// <param name="restartsAsc">그 디바이스가 쓰이는 flow 들의 사이클 시작 합집합, 오름차순.</param>
    public static List<FaultEvent> BuildEvents(
        IReadOnlyList<AlertInput> alertsAsc, IReadOnlyList<long> restartsAsc, long nowMs)
    {
        var events = new List<FaultEvent>();
        FaultEvent? cur = null;

        for (var i = 0; i < alertsAsc.Count; i++)
        {
            var a = alertsAsc[i];

            if (cur is not null)
            {
                // 해소된 사건 = 해소 이후 첫 재가동까지 흡수 / 미해소 사건 = 발생 이후 첫 재가동까지만.
                var boundary = cur.ClearedMs is { } c
                    ? FirstAfter(restartsAsc, c)
                    : FirstAfter(restartsAsc, cur.OnsetMs);

                if (boundary is null || a.OccurredMs <= boundary)
                {
                    cur.Members.Add(i);
                    // 하나라도 미해소면 사건은 미해소다 — 조건이 아직 걸려 있다는 뜻이므로.
                    if (a.ClearedMs is { } ac) { if (cur.ClearedMs is { } cc && ac > cc) cur.ClearedMs = ac; }
                    else cur.ClearedMs = null;
                    continue;
                }
            }

            cur = new FaultEvent(a.OccurredMs, a.ClearedMs, [i]);
            events.Add(cur);
        }

        // 사건 단위로 회복을 판정한다 — 규칙은 알람 1건일 때와 같다(Resolve 재사용).
        foreach (var e in events)
        {
            var window = alertsAsc[e.Members[0]].WindowMs;
            e.Recovery = Resolve(new AlertInput(e.OnsetMs, e.ClearedMs, window), restartsAsc, nowMs);
        }
        return events;
    }

    /// <summary>
    /// 사건이 집계에 들어가는지 판정해 <see cref="FaultEvent.Stop"/> · <see cref="FaultEvent.Skip"/> 를 채운다.
    /// 순서가 뜻을 가진다 — 스냅샷이 먼저다(재접속 순간에는 리듬 판정 자체가 의미 없다).
    /// </summary>
    public static void Classify(
        IReadOnlyList<FaultEvent> events, IReadOnlyList<long> startsAsc, double rhythmMs,
        IReadOnlyList<long> linkAtAsc, bool changedOp = false,
        double multiple = StopRhythmMultiple, long graceMs = LinkSnapshotGraceMs)
    {
        foreach (var e in events)
        {
            if (changedOp) { e.Stop = StopVerdict.Unknown; e.Skip = SkipCause.ChangedOp; continue; }

            if (IsLinkSnapshot(e.OnsetMs, linkAtAsc, graceMs))
            {
                e.Stop = StopVerdict.Unknown;
                e.Skip = SkipCause.LinkSnapshot;
                continue;
            }

            e.Stop = JudgeStop(e.OnsetMs, startsAsc, rhythmMs, multiple);
            e.Skip = e.Stop switch
            {
                StopVerdict.Stopped => SkipCause.None,
                StopVerdict.NonStopWarning => SkipCause.NonStopWarning,
                _ => SkipCause.UnknownStop,
            };
        }
    }

    // ── 집계 ──────────────────────────────────────────────────────────────────

    /// <summary>표본 하한 — doc/30 의 K 규약과 같은 수. 미달이면 숫자 대신 "표본 부족".</summary>
    public const int MinSample = 3;

    /// <summary>
    /// 디바이스 1대의 지표. <b>eMTBF 는 간격의 평균이 아니라 가동시간 ÷ 고장 건수</b>다 — 간격 평균은
    /// 관측창을 넘는 값을 낼 수 없어(실측 11.6분 = 상한 12.7분의 91%) 설비가 아니라 창 길이를 재게 된다.
    /// T/N 형태라야 라인 롤업과도 맞물린다(1/MTBF_line = Σ 1/MTBF_d).
    /// </summary>
    public readonly record struct DeviceSummary(
        string System,
        string Device,
        int FaultCount,
        int RecoveredCount,
        int InProgressCount,
        int AwaitingRestartCount,
        int RestartUnconfirmedCount,
        int NonStopWarningCount,
        int LinkSnapshotCount,
        int UnknownStopCount,
        long OperatingMs,
        long TotalDownMs,
        double? EMtbfMs,
        double? EMttrMs);

    /// <summary>디바이스 1대의 사건들을 지표로 모은다. <paramref name="operatingMs"/> 는 eMTBF 의 분모다.</summary>
    public static DeviceSummary AggregateDevice(
        string system, string device, IReadOnlyList<FaultEvent> events, long operatingMs,
        int minSample = MinSample)
    {
        int fault = 0, recovered = 0, inProg = 0, awaiting = 0, unconfirmed = 0;
        int warn = 0, snapshot = 0, unknown = 0;
        long downTotal = 0;

        foreach (var e in events)
        {
            switch (e.Skip)
            {
                case SkipCause.NonStopWarning: warn++; continue;
                case SkipCause.LinkSnapshot: snapshot++; continue;
                case SkipCause.UnknownStop: unknown++; continue;
                case SkipCause.ChangedOp: continue;
            }

            fault++;                                     // eMTBF 분모 = 발생 전체(미복구 포함)
            switch (e.Recovery.State)
            {
                case RecoveryState.InProgress: inProg++; break;
                case RecoveryState.AwaitingRestart: awaiting++; break;
                case RecoveryState.RestartUnconfirmed: unconfirmed++; break;
                case RecoveryState.Recovered:
                    if (e.DownMs is { } d) { recovered++; downTotal += d; }
                    break;
            }
        }

        return new DeviceSummary(
            System: system,
            Device: device,
            FaultCount: fault,
            RecoveredCount: recovered,
            InProgressCount: inProg,
            AwaitingRestartCount: awaiting,
            RestartUnconfirmedCount: unconfirmed,
            NonStopWarningCount: warn,
            LinkSnapshotCount: snapshot,
            UnknownStopCount: unknown,
            OperatingMs: operatingMs,
            TotalDownMs: downTotal,
            EMtbfMs: fault >= minSample && operatingMs > 0 ? (double)operatingMs / fault : null,
            EMttrMs: recovered >= minSample ? (double)downTotal / recovered : null);
    }

    /// <summary>라인/시스템 값 — 디바이스에서 굴려 올린 것. 화면의 KPI 카드 두 장이 이것이다.</summary>
    public readonly record struct Summary(
        int FaultCount,
        int RecoveredCount,
        int InProgressCount,
        int AwaitingRestartCount,
        int RestartUnconfirmedCount,
        int NonStopWarningCount,
        int LinkSnapshotCount,
        int UnknownStopCount,
        /// <summary>
        /// 디바이스 가동시간의 <b>단순 합</b>. eMTBF 의 분모가 아니다(그쪽은 1/Σλ 라 겹친 시간을 한 번만
        /// 센다) — 커버리지 감각용 표시 값이므로 화면도 "디바이스 가동시간 합" 이라고 밝혀야 한다.
        /// </summary>
        long OperatingMs,
        double? EMttrMs,
        double? EMtbfMs)
    {
        /// <summary>표본이 K 에 못 미쳐 숫자를 내지 않은 상태인지 — 화면이 "표본 부족 (n=3)" 을 띄운다.</summary>
        public bool MttrBelowSample => EMttrMs is null;
        public bool MtbfBelowSample => EMtbfMs is null;
    }

    /// <summary>
    /// 라인 롤업 — <b>직렬 합산</b>이다. λ_line = Σ λ_d 이므로 eMTBF_line = 1 / Σ(고장_d / 가동_d).
    /// <para>
    /// ★디바이스들이 같은 시간대에 돌았으면 그 가동시간은 <b>한 번만</b> 센다 — λ_d = N_d/T 이므로
    /// Σλ = ΣN/T 이고 1/Σλ = <b>T/ΣN</b> 이다(ΣT/ΣN 이 아니다). 가동시간을 더해 나누면 라인이 디바이스
    /// 수만큼 좋아 보인다. 이 식은 그 함정을 구조적으로 피한다("어디가 서든 라인이 선다").
    /// </para>
    /// eMTTR 은 덧셈으로 굴러가지 않으므로 <b>건수 가중 pooled</b> 평균 그대로다.
    /// <para>표본 미달 디바이스도 λ 합에 넣는다 — 빼면 고장이 과소 계상되어 라인 eMTBF 가 부풀어 오른다.</para>
    /// </summary>
    public static Summary RollUp(IReadOnlyList<DeviceSummary> devices, int minSample = MinSample)
    {
        int fault = 0, recovered = 0, inProg = 0, awaiting = 0, unconfirmed = 0;
        int warn = 0, snapshot = 0, unknown = 0;
        long downTotal = 0, operating = 0;
        double lambda = 0;

        foreach (var d in devices)
        {
            fault += d.FaultCount;
            recovered += d.RecoveredCount;
            inProg += d.InProgressCount;
            awaiting += d.AwaitingRestartCount;
            unconfirmed += d.RestartUnconfirmedCount;
            warn += d.NonStopWarningCount;
            snapshot += d.LinkSnapshotCount;
            unknown += d.UnknownStopCount;
            downTotal += d.TotalDownMs;
            operating += d.OperatingMs;
            if (d.OperatingMs > 0 && d.FaultCount > 0)
                lambda += (double)d.FaultCount / d.OperatingMs;
        }

        return new Summary(
            FaultCount: fault,
            RecoveredCount: recovered,
            InProgressCount: inProg,
            AwaitingRestartCount: awaiting,
            RestartUnconfirmedCount: unconfirmed,
            NonStopWarningCount: warn,
            LinkSnapshotCount: snapshot,
            UnknownStopCount: unknown,
            OperatingMs: operating,
            EMttrMs: recovered >= minSample ? (double)downTotal / recovered : null,
            EMtbfMs: fault >= minSample && lambda > 0 ? 1.0 / lambda : null);
    }
}
