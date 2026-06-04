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

    // ── 코어: 재도출 + 구간 교체(재정합 없음) ───────────────────────────────────────────
    private async Task<RecomputeOutcome> RederiveAndReplaceAsync(
        string flowName, string? headCallName, string? tailCallName, DateTime fromLocal, DateTime toLocal)
    {
        if (string.IsNullOrWhiteSpace(flowName) || toLocal <= fromLocal)
            return RecomputeOutcome.Skipped;

        var (headOutTag, tailInTag) = ResolveTags(flowName, headCallName, tailCallName);
        if (string.IsNullOrWhiteSpace(headOutTag))
        {
            // 시작 경계 태그를 못 찾으면 재도출 불가 — 기존 history 를 지우지 않고 안전하게 건너뛴다.
            _logger.LogWarning(
                "[CycleRecompute] '{Flow}' Head '{Head}' OutTag 주소 미해석 — 재계산 건너뜀 (history 보존)",
                flowName, headCallName);
            return RecomputeOutcome.Skipped;
        }

        // 단일 Call Flow(head==tail): 화면(ResolveEffectiveHeadTail)은 tail 을 null 로 강제해 활성(MT)을 비운다.
        // 재계산도 tail 엣지를 쓰지 않아야 화면과 일치(MT null, CT=주기만).
        bool singleCall = headCallName != null
            && string.Equals(headCallName, tailCallName, StringComparison.OrdinalIgnoreCase);
        var effTailIn = singleCall ? null : tailInTag;

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

        var tailEdges = !string.IsNullOrWhiteSpace(effTailIn)
            ? (await _plc.FindRisingEdgesAsync(effTailIn!, fromLocal, toLocal)).OrderBy(t => t).ToList()
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
    /// RecordedAt 은 라이브 경로(UtcNow 저장)와 맞추기 위해 완료(없으면 사이클 끝) 시각을 UTC 로 변환해 저장한다.
    /// IsIdle 은 현재 설정의 Max/MinCycleTimeMs 로 재판정(과거를 새 임계로 다시 가동/비가동 분류).
    /// 삽입 행은 반드시 [fromUtc, toUtc) 안에 들도록 clamp → delete-range 와 정확히 일치(중복/누락 방지).
    /// </summary>
    private List<DspFlowHistoryEntity> BuildRows(
        string flowName, string? headCallName, string? tailCallName,
        IReadOnlyList<CycleDerivation.CycleRecord> cycles, DateTime fromUtc, DateTime toUtc)
    {
        var hv = _settings.LoadSettings().HistoryView;
        int maxCT = hv.MaxCycleTimeMs;
        int minCT = hv.MinCycleTimeMs;

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

            // 기록 시각(Local) — 완료가 있으면 완료, 없으면 사이클 끝(= 다음 시작), 그것도 없으면 시작.
            DateTime recordedLocal =
                c.Complete ?? (c.PeriodMs.HasValue
                    ? c.Start.AddMilliseconds(c.PeriodMs.Value)
                    : c.Start);
            var recordedUtc = recordedLocal.ToUniversalTime();
            if (recordedUtc < fromUtc || recordedUtc >= toUtc)
                continue; // delete-range 와 정확히 일치하도록 경계 밖 행은 제외

            bool isIdle = ct.HasValue && ((maxCT > 0 && ct > maxCT) || (minCT > 0 && ct < minCT));

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

    /// <summary>flow + Call 이름 → (Head OutTag 주소, Tail InTag 주소). 진영 B 폴라리티.</summary>
    private (string? HeadOutTag, string? TailInTag) ResolveTags(
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

        string? tailIn = tailCallName is null ? null : pairs
            .Where(p => string.Equals(p.FlowName, flowName, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(p.CallName, tailCallName, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.InTag)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

        return (headOut, tailIn);
    }

    /// <summary>재기록 후 파생값/라이브 상태/UI 스냅샷 재정합 — InvalidateCachesAsync 의 평균-복원 단계 재사용.</summary>
    private async Task RunConsistencyTailAsync()
    {
        try
        {
            await _dsp.RecomputeAveragesFromCurrentBoundaryAsync();
            await _flowMetrics.ReseedCycleStatesFromCurrentBoundaryAsync();
            _dspDb.Reset();
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
