// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Adapters;
using DSPilot.Hubs;
using DSPilot.Models.Dsp;
using DSPilot.Repositories;
using Microsoft.AspNetCore.SignalR;

namespace DSPilot.Services;

/// <summary>
/// Head/Tail 사이클 경계를 바꿔 "적용(저장)" 했을 때, 과거 dspFlowHistory 를 <b>새 경계 기준으로 재도출</b>해
/// 덮어쓰는 파이프라인.
///
/// <para>정본(결정): 원시 plcTagLog 의 PLC 엣지(Head OutTag↑ 시작 / Tail InTag↑ 완료) 를 history 의 단일
/// 진실원본으로 삼는다. 사이클 분해·유휴 필터는 화면(<see cref="CallTestController"/>)과 동일한
/// <see cref="CycleDerivation"/> 를 공유하므로 측정 정의가 일치한다(같은 범위·경계에서 수치 일치; 대시보드=전체
/// 이력 vs 화면=윈도우 범위 차이와 정수 ms sub-ms 절단 차이는 정의상 잔존).</para>
///
/// <para>저장 정책(결정): dspFlowHistory 는 원시 로그의 파생 캐시 → 대상 구간을 제자리 덮어쓰기
/// (<see cref="DspRepositoryAdapter.ReplaceFlowHistoryRangeAsync"/>). 원시 plcTagLog 가 진짜 아카이브라 비파괴.</para>
///
/// <para>재정합: 재기록 후 <see cref="DspRepositoryAdapter.RecomputeAveragesFromCurrentBoundaryAsync"/>
/// (dspFlow.Avg*/MT/WT/CT) + <see cref="IFlowMetricsService.ReseedCycleStatesFromCurrentBoundaryAsync"/>
/// (라이브 in-memory Welford) + <see cref="DspDbService.Reset"/> + SignalR "DatabaseRebuilt".
/// 이는 기존 InvalidateCachesAsync 의 평균-복원 단계를 그대로 재사용한다(전역 NULL/Call통계 reset 은 생략 —
/// 바뀐 flow 의 행만 새 경계로 재박제되어 매칭되므로 다른 flow 는 영향 없음).</para>
///
/// <para>OEE 는 범위 밖(결정): OEE Performance 는 idealCT×count/runtime 으로 Avg CT 를 소비하지 않는다.</para>
/// </summary>
public sealed class CycleRecomputeService
{
    private readonly IPlcRepository _plc;
    private readonly PlcToCallMapperService _mapper;
    private readonly DspRepositoryAdapter _dsp;
    private readonly IFlowMetricsService _flowMetrics;
    private readonly DspDbService _dspDb;
    private readonly AppSettingsService _settings;
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly ILogger<CycleRecomputeService> _logger;

    // 전체 이력(백그라운드) 잡은 한 번에 하나만. 증분(동기)은 이 게이트와 무관.
    private readonly SemaphoreSlim _fullGate = new(1, 1);
    private volatile RecomputeJobStatus _status = RecomputeJobStatus.Idle;

    public CycleRecomputeService(
        IPlcRepository plc,
        PlcToCallMapperService mapper,
        DspRepositoryAdapter dsp,
        IFlowMetricsService flowMetrics,
        DspDbService dspDb,
        AppSettingsService settings,
        IHubContext<MonitoringHub> hub,
        ILogger<CycleRecomputeService> logger)
    {
        _plc = plc;
        _mapper = mapper;
        _dsp = dsp;
        _flowMetrics = flowMetrics;
        _dspDb = dspDb;
        _settings = settings;
        _hub = hub;
        _logger = logger;
    }

    public RecomputeJobStatus Status => _status;

    // ── 전체 이력 재도출(백그라운드) — 경계 변경 시 기본 동작 ───────────────────────────
    /// <summary>
    /// 해당 flow 의 원시 로그 전 구간을 새 경계로 재도출. 요청 스레드를 블로킹하지 않도록 백그라운드에서 실행하고
    /// "RecomputeProgress" SignalR 이벤트 + <see cref="Status"/>(폴링용) 로 진행률/완료를 통지한다.
    /// 이미 다른 전체-이력 잡이 진행 중이면 false 를 반환한다(중복 방지).
    /// </summary>
    public bool TryStartFullHistoryRecompute(string flowName, string? headCallName, string? tailCallName)
    {
        if (!_fullGate.Wait(0))
            return false;

        _status = RecomputeJobStatus.Begin(flowName);
        _ = Task.Run(async () =>
        {
            try
            {
                await RunFullHistoryAsync(flowName, headCallName, tailCallName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CycleRecompute] 전체 이력 재계산 실패 ({Flow})", flowName);
                _status = _status.Fail(ex.Message);
                await BroadcastProgressAsync();
            }
            finally
            {
                _fullGate.Release();
            }
        });
        return true;
    }

    private async Task RunFullHistoryAsync(string flowName, string? headCallName, string? tailCallName)
    {
        _status = _status.With(phase: "deriving");
        await BroadcastProgressAsync();

        var oldest = await _plc.GetOldestLogDateTimeAsync();
        var latest = await _plc.GetLatestLogDateTimeAsync();
        if (oldest is null || latest is null || latest <= oldest)
        {
            _status = _status.Complete(0, 0, 0);
            await BroadcastProgressAsync();
            return;
        }

        // 전 구간을 한 번에 재도출(엣지→사이클은 인덱스(plcTagId,dateTime) 범위스캔으로 태그 행수에 비례).
        // 사이클 행 수는 로그 행 수보다 훨씬 작으므로 단일 트랜잭션 교체로 충분히 짧다.
        // 상한은 latest+1ms(반열림) — 정확히 latest 시각에 완료된 마지막 사이클이 clamp 에서 누락되지 않게.
        var outcome = await RederiveAndReplaceAsync(
            flowName, headCallName, tailCallName, oldest.Value, latest.Value.AddMilliseconds(1));

        if (!outcome.TagResolved)
        {
            _status = _status.Fail("Head OutTag 주소를 해석할 수 없어 재계산을 건너뜀");
            await BroadcastProgressAsync();
            return;
        }

        _status = _status.With(phase: "reaggregating", cyclesFound: outcome.CyclesFound,
            deleted: outcome.Deleted, inserted: outcome.Inserted);
        await BroadcastProgressAsync();

        await RunConsistencyTailAsync();

        _status = _status.Complete(outcome.CyclesFound, outcome.Deleted, outcome.Inserted);
        await BroadcastProgressAsync();
        _logger.LogInformation(
            "[CycleRecompute] 전체 이력 재계산 완료 ({Flow}): cycles={Cycles}, deleted={Del}, inserted={Ins}",
            flowName, outcome.CyclesFound, outcome.Deleted, outcome.Inserted);
    }

    // ── 주기적 자동 재계산(전체 추적 flow) — PeriodicCycleRecomputeService 가 호출 ──────────
    /// <summary>
    /// 현재 추적 중인 모든 Flow 의 이력을, 각 Flow 의 유효 경계(override ?? 라벨 ?? 토폴로지)로
    /// 원시 plcTagLog 에서 재도출해 덮어쓴다. 라이브 기록기가 실시간에 놓친 tail 완료로 부풀려진 WT 를
    /// self-heal 하는 용도. 수동 "저장" 경로와 동일한 코어(<see cref="RederiveAndReplaceAsync"/>)와
    /// 재정합(<see cref="RunConsistencyTailAsync"/>)을 재사용하되, 진행률 broadcast/_status 는 건드리지
    /// 않아(조용한 백그라운드 동작) 수동 적용의 UI 폴링과 충돌하지 않는다.
    /// 다른 전체-이력 잡(수동/자동)이 진행 중이면 즉시 0 을 반환하고 다음 tick 에 재시도한다.
    ///
    /// <para><paramref name="sinceLocal"/> 가 주어지면 그 시각부터만 재도출(증분) — 라이브가 막 기록한
    /// 최근 구간만 self-heal 하여 쓰기 트랜잭션(락 점유)을 짧게 유지한다. 경계가 안 바뀌는 주기 케이스라
    /// 최근 구간만 교체해도 평균이 안 깨진다(과거 행은 직전 tick 들에서 이미 치유됨). null 이면 전 구간(첫 실행).
    /// straddle(경계에 걸친 사이클) 누락 방지는 호출 측이 충분한 overlap 을 빼서 넘기는 책임이다.</para>
    /// </summary>
    /// <returns>실제로 재도출된 Flow 수(0 이면 게이트 점유/데이터 없음으로 아무 작업 안 함).</returns>
    public async Task<int> RecomputeAllTrackedFlowsAsync(DateTime? sinceLocal = null, CancellationToken ct = default)
    {
        if (!_fullGate.Wait(0))
            return 0; // 수동/다른 자동 잡 진행 중 — 이번 tick 은 스킵

        try
        {
            var oldest = await _plc.GetOldestLogDateTimeAsync();
            var latest = await _plc.GetLatestLogDateTimeAsync();
            if (oldest is null || latest is null || latest <= oldest)
                return 0;

            // 증분이면 sinceLocal 부터, 전체면 oldest 부터. oldest 보다 이른 since 는 oldest 로 클램프.
            var fromLocal = (sinceLocal.HasValue && sinceLocal.Value > oldest.Value)
                ? sinceLocal.Value
                : oldest.Value;
            var toExclusive = latest.Value.AddMilliseconds(1); // 반열림 상한 — 마지막 사이클 누락 방지

            int recomputed = 0;
            foreach (var flowName in _flowMetrics.GetTrackedFlowNames())
            {
                if (ct.IsCancellationRequested) break;

                var (head, tail) = _flowMetrics.GetCycleBoundaryCallNames(flowName);
                if (string.IsNullOrEmpty(head)) continue; // 경계 없는(전부 Body·미추적) Flow 는 스킵

                try
                {
                    var outcome = await RederiveAndReplaceAsync(flowName, head, tail, fromLocal, toExclusive);
                    if (outcome.TagResolved) recomputed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[CycleRecompute] 자동 재계산 실패 ({Flow})", flowName);
                }
            }

            // 평균/스냅샷 재정합 + "DatabaseRebuilt" 는 실제 재도출이 있었을 때만 한 번.
            if (recomputed > 0 && !ct.IsCancellationRequested)
                await RunConsistencyTailAsync();

            return recomputed;
        }
        finally
        {
            _fullGate.Release();
        }
    }

    // ── 코어: 재도출 + 구간 교체(재정합 없음) ───────────────────────────────────────────
    private async Task<RecomputeOutcome> RederiveAndReplaceAsync(
        string flowName, string? headCallName, string? tailCallName, DateTime fromLocal, DateTime toLocal)
    {
        if (string.IsNullOrWhiteSpace(flowName) || toLocal <= fromLocal)
            return RecomputeOutcome.Skipped;

        var (headOutTag, tailCompletion) = ResolveTags(flowName, headCallName, tailCallName);
        if (string.IsNullOrWhiteSpace(headOutTag))
        {
            // 시작 경계 태그를 못 찾으면 재도출 불가 — 기존 history 를 지우지 않고 안전하게 건너뛴다.
            _logger.LogWarning(
                "[CycleRecompute] '{Flow}' Head '{Head}' OutTag 주소 미해석 — 재계산 건너뜀 (history 보존)",
                flowName, headCallName);
            return RecomputeOutcome.Skipped;
        }

        // head==tail(단일 신호 Call)도 자기 OutTag↑→완료(InTag↑/OutTag↓)로 분해 — 화면(CallTestController)과 동일 규칙.

        var starts = (await _plc.FindRisingEdgesAsync(headOutTag!, fromLocal, toLocal))
            .OrderBy(t => t).ToList();

        // 시작 엣지가 0건이면(태그는 해석됐으나 구간에 데이터 없음 / 오매핑 / 부분기록 공백) 파괴적 삭제를 피하고
        // 기존 history 를 보존한다 — re-derive 가 충실해야만 "파생 캐시" 전제가 성립하므로.
        if (starts.Count == 0)
        {
            // 태그는 해석됐으나 새 경계 사이클 0건(헤드가 구간 내 미작동/오선택). 파괴적 삭제 없이 history 보존.
            // TagResolved=true 로 반환해 상위가 "해석 실패"가 아닌 "0건"으로 정상 처리(평균은 새 경계 매칭 0 → 비움).
            _logger.LogInformation(
                "[CycleRecompute] '{Flow}' [{From:o},{To:o}) Head OutTag rising edge 0건 — 삭제 없이 건너뜀 (history 보존)",
                flowName, fromLocal, toLocal);
            return new RecomputeOutcome(true, 0, 0, 0);
        }

        var tailEdges = !string.IsNullOrWhiteSpace(tailCompletion.Tag)
            ? (tailCompletion.Falling
                ? (await _plc.FindFallingEdgesAsync(tailCompletion.Tag!, fromLocal, toLocal)).OrderBy(t => t).ToList()
                : (await _plc.FindRisingEdgesAsync(tailCompletion.Tag!, fromLocal, toLocal)).OrderBy(t => t).ToList())
            : new List<DateTime>();

        var cycles = CycleDerivation.BuildCycles(starts, tailEdges, toLocal);

        var fromUtc = fromLocal.ToUniversalTime();
        var toUtc = toLocal.ToUniversalTime();
        var rows = BuildRows(flowName, headCallName, tailCallName, cycles, fromUtc, toUtc);

        var (deleted, inserted) = await _dsp.ReplaceFlowHistoryRangeAsync(flowName, fromUtc, toUtc, rows);
        return new RecomputeOutcome(true, rows.Count, deleted, inserted);
    }

    /// <summary>
    /// 사이클 분해 → dspFlowHistory 엔티티. MT=활성, CT=주기, WT=CT−MT(라이브 가산정의와 동일).
    /// RecordedAt 은 라이브 경로와 동일하게 <b>사이클 끝(= Start + Period = 다음 head start)</b> 시각을 UTC 로
    /// 저장한다 — 라이브는 다음 head start 처리 중 UtcNow 로 찍으므로 같은 시각이고, OEE 의 [rec−ct, rec]
    /// 복원이 정확히 [Start, 다음 Start] 가 된다. (2026-08-19 이전엔 완료(tail) 시각을 우선해 복원 구간이
    /// WT 만큼 과거로 밀렸다 — 장기정지 행은 며칠 단위 오정렬. 규약 변경 배포 시 기존 이력 재계산 필요.)
    /// IsIdle 은 현재 유효 비가동 범위(글로벌 + per-flow override)로 재판정(과거를 새 기준으로 다시 가동/비가동 분류).
    /// 삽입 행은 반드시 [fromUtc, toUtc) 안에 들도록 clamp → delete-range 와 정확히 일치(중복/누락 방지).
    /// </summary>
    private List<DspFlowHistoryEntity> BuildRows(
        string flowName, string? headCallName, string? tailCallName,
        IReadOnlyList<CycleDerivation.CycleRecord> cycles, DateTime fromUtc, DateTime toUtc)
    {
        var (maxCT, minCT) = _settings.GetEffectiveCycleRangeMs(flowName);

        var rows = new List<DspFlowHistoryEntity>(cycles.Count);
        int cycleNo = 0;
        foreach (var c in cycles)
        {
            if (!c.PeriodMs.HasValue && !c.ActiveMs.HasValue)
                continue; // 주기도 활성도 없는 마지막 빈 사이클은 기록할 게 없음

            // 라이브 엔진과 동일한 절단 순서로 분해: MT=(int)활성, WT=(int)(주기−활성), CT=MT+WT.
            //   → 재도출 행이 라이브가 같은 엣지로 기록했을 값과 일치(같은 history 안에서 seam 없음), CT=MT+WT 보존.
            //   (ClampMs: ms 가 int 범위를 넘는 비정상 gap 은 상한 클램프 — 어차피 IsIdle 로 분류됨.)
            int? mt = c.ActiveMs.HasValue ? ClampMs(c.ActiveMs.Value) : (int?)null;
            int? wt = null, ct = null;
            if (c.PeriodMs.HasValue && c.ActiveMs.HasValue)
            {
                wt = ClampMs(c.PeriodMs.Value - c.ActiveMs.Value);
                ct = mt + wt;
            }
            else if (c.PeriodMs.HasValue)
            {
                ct = ClampMs(c.PeriodMs.Value); // tail(완료) 미검출: 주기만 기록
            }

            // 기록 시각(Local) — 사이클 끝(= 다음 head start) 우선 = 라이브 규약. Period 없는(미완료 마지막)
            // 행만 완료/시작 폴백 — 그 행은 ct=null → 아래에서 IsIdle=1 로 집계 제외되므로 정렬 무관.
            DateTime recordedLocal = c.PeriodMs.HasValue
                ? c.Start.AddMilliseconds(c.PeriodMs.Value)
                : (c.Complete ?? c.Start);
            var recordedUtc = recordedLocal.ToUniversalTime();
            if (recordedUtc < fromUtc || recordedUtc >= toUtc)
                continue; // delete-range 와 정확히 일치하도록 경계 밖 행은 제외

            // ct 가 없으면(주기 미검출 = 미완료 사이클) 유효 CT 가 없으므로 비가동으로 분류해 평균에서 제외.
            // 행 자체는 보존되되 IsIdle=1 → AVG/누산기에서 빠진다. (과거엔 ct=NULL 이 비-idle 로 남아 누산기
            // COUNT 만 부풀리고 boundary AVG 와 어긋나 평균을 끌어내렸다.)
            bool isIdle = !ct.HasValue || (maxCT > 0 && ct > maxCT) || (minCT > 0 && ct < minCT);

            rows.Add(new DspFlowHistoryEntity
            {
                FlowName = flowName,
                MT = mt,
                WT = wt,
                CT = ct,
                CycleNo = ++cycleNo,
                RecordedAt = DateTime.SpecifyKind(recordedUtc, DateTimeKind.Utc),
                IsIdle = isIdle,
                HeadCallName = headCallName,
                TailCallName = tailCallName,
            });
        }
        return rows;
    }

    /// <summary>ms(double) → 음수 0, int 초과는 상한 클램프, 그 외 절단. dspFlowHistory 의 int 컬럼 안전 변환.</summary>
    private static int ClampMs(double ms)
        => ms <= 0 ? 0 : (ms >= int.MaxValue ? int.MaxValue : (int)ms);

    /// <summary>
    /// flow + Call 이름 → (Head OutTag 주소, Tail 완료 마커). 진영 B 폴라리티.
    /// Tail 완료 = InTag↑(있으면) else OutTag↓(OutOnly 추정) — 화면(CallTestController)과 동일 규칙(CycleCompletionResolver).
    /// </summary>
    private (string? HeadOutTag, CycleCompletionResolver.TailCompletion TailCompletion) ResolveTags(
        string flowName, string? headCallName, string? tailCallName)
    {
        if (!_mapper.IsInitialized)
            _mapper.Initialize();

        var pairs = _mapper.GetAllCallTagPairs();

        string? headOut = headCallName is null ? null : pairs
            .Where(p => string.Equals(p.FlowName, flowName, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(p.CallName, headCallName, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.OutTag)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

        string? tailIn = null, tailOut = null;
        if (tailCallName is not null)
        {
            var tailPairs = pairs
                .Where(p => string.Equals(p.FlowName, flowName, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(p.CallName, tailCallName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            tailIn = tailPairs.Select(p => p.InTag).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            tailOut = tailPairs.Select(p => p.OutTag).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        }

        return (headOut, CycleCompletionResolver.Resolve(tailIn, tailOut));
    }

    /// <summary>재기록 후 파생값/라이브 상태/UI 스냅샷 재정합 — InvalidateCachesAsync 의 평균-복원 단계 재사용.</summary>
    private async Task RunConsistencyTailAsync()
    {
        try
        {
            await _dsp.RecomputeAveragesFromCurrentBoundaryAsync(_settings.GetCycleAverageWindow());
            await _flowMetrics.ReseedCycleStatesFromCurrentBoundaryAsync();
            // Reset()(스냅샷 비움 → 다음 1초 폴링까지 대기) 대신 동기 재읽기로 제자리 교체.
            // DB 는 삭제되지 않고 평균만 바뀌었으므로 비울 필요가 없고, 비우면 그 빈 창에 대시보드가
            // 빈 Flows 를 읽어 모든 카드가 잠깐 "곧 시작"으로 초기화되어 보인다(이 메서드는 그 깜빡임 제거).
            _dspDb.RefreshNow();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CycleRecompute] 재정합(평균/스냅샷) 실패");
        }

        try { await _hub.Clients.All.SendAsync("DatabaseRebuilt"); }
        catch (Exception ex) { _logger.LogDebug(ex, "[CycleRecompute] DatabaseRebuilt 브로드캐스트 실패 (비중요)"); }
    }

    private async Task BroadcastProgressAsync()
    {
        try { await _hub.Clients.All.SendAsync("RecomputeProgress", _status); }
        catch (Exception ex) { _logger.LogDebug(ex, "[CycleRecompute] RecomputeProgress 브로드캐스트 실패 (비중요)"); }
    }
}

/// <summary>단일 재계산 호출 결과.</summary>
public readonly record struct RecomputeOutcome(bool TagResolved, int CyclesFound, int Deleted, int Inserted)
{
    public static RecomputeOutcome Skipped => new(false, 0, 0, 0);
}

/// <summary>전체-이력 백그라운드 잡 상태(폴링 + SignalR 페이로드 겸용). camelCase 직렬화.</summary>
public sealed record RecomputeJobStatus(
    bool Running,
    string? Flow,
    string Phase,        // idle | deriving | reaggregating | done | error
    int CyclesFound,
    int Deleted,
    int Inserted,
    bool Done,
    string? Error,
    DateTimeOffset UpdatedAt)
{
    public static RecomputeJobStatus Idle =>
        new(false, null, "idle", 0, 0, 0, false, null, DateTimeOffset.UtcNow);

    public static RecomputeJobStatus Begin(string flow) =>
        new(true, flow, "starting", 0, 0, 0, false, null, DateTimeOffset.UtcNow);

    public RecomputeJobStatus With(string? phase = null, int? cyclesFound = null, int? deleted = null, int? inserted = null) =>
        this with
        {
            Phase = phase ?? Phase,
            CyclesFound = cyclesFound ?? CyclesFound,
            Deleted = deleted ?? Deleted,
            Inserted = inserted ?? Inserted,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    public RecomputeJobStatus Complete(int cyclesFound, int deleted, int inserted) =>
        this with
        {
            Running = false, Done = true, Phase = "done",
            CyclesFound = cyclesFound, Deleted = deleted, Inserted = inserted,
            Error = null, UpdatedAt = DateTimeOffset.UtcNow,
        };

    public RecomputeJobStatus Fail(string error) =>
        this with { Running = false, Done = true, Phase = "error", Error = error, UpdatedAt = DateTimeOffset.UtcNow };
}
