// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Dapper;
using DSPilot.Infrastructure;
using DSPilot.Models.Oee;
using DSPilot.Repositories;
using Microsoft.Data.Sqlite;

namespace DSPilot.Services;

/// <summary>
/// 무사이클 기반 정지 onset/clear 자동 판정 (doc/21 §5, 임계는 doc/23 §6 Phase 1 로 개정).
///
/// 1차 소스 = "무사이클". dspFlowHistory(plc.db) 의 flow별 마지막 사이클 RecordedAt 을 주기적으로 보고,
/// 마지막 사이클 후 <b>flow별 임계</b> 이상 신규 사이클이 없으면 open 정지이벤트(detectSource='nocycle',
/// category/reason NULL)를 생성한다. 사이클이 재개되면(마지막 사이클이 정지 startAt 이후로 갱신) 마감한다.
///
/// 임계(doc/23 §6 폴백 체인, <see cref="OeeMath.ResolveNoCycleThresholdMs"/>):
///   ① max(3×gap', 30s) — gap' = 최근 14일 클린 WT(=ct−mt) 중앙값(오늘 제외, 가중 없음)
///   ② 3×14일평균CT — gap' 미학습 시
///   ③ NoCycleSeconds(120s) — 학습 전무(콜드스타트 부트스트랩)
/// 고정 120초 전역 적용은 폐기 — 주기가 긴(>120s) flow 가 정상 가동 중 매 사이클 거짓 onset 되던 결함 해소.
/// 임계는 <see cref="ThresholdTtl"/> 주기로 재학습(디스크 쿼리 절약, 15s tick 마다 재계산 안 함).
///
/// 보정 준수(doc/21 §10): 자동은 onset/clear 뿐. 정지원인 분류·불량·계획시간은 사람이 컨트롤러로 입력.
/// UserTag/태그 직접폴링은 1차에 미포함(무사이클만).
///
/// 중복 onset 가드: flow별 open 이벤트가 이미 있으면 새 onset 을 만들지 않는다(GetOpenEvents).
/// plc.db 는 read-only 로 직접 조회(DspDbService 와 동일 방식), oee.db 는 IOeeRepository(scoped) 로 write.
/// </summary>
public sealed class OeeDowntimeStateMachine : BackgroundService
{
    // detectSource = "nocycle" — clear 는 사이클 재개로만.
    private const string DetectSource = "nocycle";
    private const int DefaultNoCycleSeconds = 120;
    // 분류 휴리스틱(doc/21 §12 E): nocycle clear 시 지속시간으로 자동 분류 — 작업자 미분류 부담 완화.
    //   ≥ 5분  → 자동:고장(equipment_fault, unplanned, isFailure=1)
    //   ≥ 8시간 → 자동:점검(planned_maint, planned) — 계획정지로 보아 MTBF 분모에서 빠짐(추세상 점프 가능).
    //   < 5분  → 미분류 유지(짧은 정지 = 노이즈, 자동분류 안 함).
    // 임계/매핑은 OeeMath.ClassifyByDuration 단일 소스(테스트 가능). 수동 분류는 절대 덮지 않는다
    // (AutoClassifyHeuristicAsync 가 category NULL·classifySource≠'manual' 가드).
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    // flow별 임계 재학습 주기 — 14일 통계라 분 단위 신선도면 충분, 15s tick 마다 14일 쿼리 4회는 낭비.
    private static readonly TimeSpan ThresholdTtl = TimeSpan.FromMinutes(5);
    // 초고속 flow(gap' 수백 ms)의 잡음성 미세정지 onset 방지 하한 (doc/23 §7).
    private const double FloorMs = 30_000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDatabasePathResolver _pathResolver;
    private readonly DsProjectService _project;
    private readonly OeeCtStatsService _ctStats;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OeeDowntimeStateMachine> _logger;

    // flow별 학습 임계 캐시(ms). 미등재 flow = 학습 전무 → 부트스트랩(NoCycleSeconds).
    private Dictionary<string, double> _thresholdMsByFlow = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _thresholdRefreshedUtc = DateTime.MinValue;

    public OeeDowntimeStateMachine(
        IServiceScopeFactory scopeFactory,
        IDatabasePathResolver pathResolver,
        DsProjectService project,
        OeeCtStatsService ctStats,
        IConfiguration configuration,
        HistoryMirrorService mirror,
        ILogger<OeeDowntimeStateMachine> logger)
    {
        _scopeFactory = scopeFactory;
        _pathResolver = pathResolver;
        _project = project;
        _ctStats = ctStats;
        _configuration = configuration;
        _mirror = mirror;
        _logger = logger;
    }

    private readonly HistoryMirrorService _mirror;

    private int NoCycleSeconds
    {
        get
        {
            var v = _configuration.GetValue<int?>("Oee:NoCycleSeconds") ?? DefaultNoCycleSeconds;
            return v > 0 ? v : DefaultNoCycleSeconds;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OeeDowntimeStateMachine starting (poll={Poll}s, per-flow gap threshold, bootstrap={Bootstrap}s)",
            PollInterval.TotalSeconds, NoCycleSeconds);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try { await TickAsync(stoppingToken); }
                catch (Exception ex) { _logger.LogWarning(ex, "[OEE] state machine tick failed"); }

                try { await Task.Delay(PollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var bootstrapMs = NoCycleSeconds * 1000.0;

        // 1) plc.db 에서 flow별 마지막 사이클 시각(UTC) 조회.
        var lastCycleByFlow = await ReadLastCyclePerFlowAsync();
        if (lastCycleByFlow.Count == 0) return;

        // flow별 임계 재학습 (TTL 경과 시). 실패해도 기존 캐시/부트스트랩으로 tick 은 계속.
        if (nowUtc - _thresholdRefreshedUtc >= ThresholdTtl)
        {
            await RefreshThresholdsAsync(bootstrapMs);
            _thresholdRefreshedUtc = nowUtc;
        }

        // systemName 매핑 — AASX 로 flow→system 해석 (미로드 시 flow 이름으로 폴백).
        var systemByFlow = BuildFlowSystemMap();

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOeeRepository>();

        // 2) 현재 open 인 nocycle 이벤트를 flow 별로 인덱싱.
        var openEvents = await repo.GetOpenEventsAsync(ct: ct);
        var openByFlow = openEvents
            .Where(e => e.DetectSource == DetectSource && !string.IsNullOrEmpty(e.FlowName))
            .GroupBy(e => e.FlowName!)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.StartAt).First(), StringComparer.Ordinal);

        foreach (var (flowName, lastCycleUtc) in lastCycleByFlow)
        {
            var idleMs = (nowUtc - lastCycleUtc).TotalMilliseconds;
            var hasOpen = openByFlow.TryGetValue(flowName, out var open);
            // 폴백 체인 ③: 학습 임계 미등재(콜드스타트) flow 는 부트스트랩(NoCycleSeconds).
            var thresholdMs = _thresholdMsByFlow.TryGetValue(flowName, out var learned) ? learned : bootstrapMs;

            if (idleMs >= thresholdMs)
            {
                // 무사이클 임계 초과 → onset (중복 가드: 이미 open 이면 skip).
                if (!hasOpen)
                {
                    var systemName = systemByFlow.TryGetValue(flowName, out var sys) && !string.IsNullOrEmpty(sys)
                        ? sys
                        : flowName;
                    await repo.InsertDowntimeAsync(new OeeDowntimeEvent
                    {
                        SystemName = systemName,
                        FlowName = flowName,
                        DeviceName = null,
                        StartAt = lastCycleUtc, // 정지 시작 = 마지막 사이클 시각(무가동 시작 시점)
                        EndAt = null,
                        ReasonCode = null,
                        Category = null,
                        IsFailure = 1, // 기본 고장 — 사용자가 해제하면 유지보수(planned_maint)로 변경
                        DetectSource = DetectSource,
                        SourceLogId = null,
                        Note = null,
                    }, ct);
                    _logger.LogInformation(
                        "[OEE] nocycle onset: flow='{Flow}' idle={IdleSec:F0}s thr={ThrSec:F0}s (last cycle {Last:u})",
                        flowName, idleMs / 1000.0, thresholdMs / 1000.0, lastCycleUtc);
                }
            }
            else
            {
                // 사이클 정상 진행 중. open 이벤트가 있고, 그 후로 새 사이클이 돌았으면 마감.
                // ★StartAt 은 저장소가 UTC→로컬(Kind=Local)로 되돌려 준다(SqliteDateTimeHelpers.FromSqliteUtcString).
                //   lastCycleUtc(Kind=Utc)와 직접 비교하면 DateTime 이 Kind 무시하고 Ticks 만 비교 → KST(+9h) 만큼
                //   StartAt 이 '미래'로 보여 조건이 9시간 동안 거짓 → 사이클 재개해도 정지가 안 닫힘. UTC 로 정규화한다.
                var openStartUtc = ToUtc(open?.StartAt ?? default);
                if (hasOpen && openStartUtc <= lastCycleUtc)
                {
                    // endAt = 사이클 재개(=마지막 사이클) 시점. durationMs 는 repo 가 계산.
                    var closed = await repo.CloseDowntimeAsync(open!.Id, lastCycleUtc, ct);
                    if (closed > 0)
                    {
                        _logger.LogInformation(
                            "[OEE] nocycle clear: flow='{Flow}' event#{Id} cycle resumed at {Last:u}",
                            flowName, open.Id, lastCycleUtc);

                        // 분류 휴리스틱(신규 마감 건만 — 백필 금지). 수동 분류는 AutoClassifyHeuristicAsync 가드로 보존.
                        var durMs = (lastCycleUtc - openStartUtc).TotalMilliseconds;
                        var (rc, cat, isFail, should) = OeeMath.ClassifyByDuration(durMs);
                        if (should)
                            await repo.AutoClassifyHeuristicAsync(open.Id, rc, cat, isFail, ct);
                    }
                }
            }
        }
    }

    /// <summary>
    /// flow별 무사이클 임계 재학습 (doc/23 §6 Phase 1). gap'(14일 클린 WT 중앙값)·14일평균CT 를 오늘 제외로
    /// 산출하고, 오늘 이전 데이터가 없는 신규 flow 는 오늘 포함 잠정값으로 폴백(TryAdd — 컨트롤러
    /// ResolveCtThresholdsAsync 와 동일 컨벤션). 체인 합성은 <see cref="OeeMath.ResolveNoCycleThresholdMs"/>.
    /// 실패 시 기존 캐시 유지(다음 TTL 에 재시도) — tick 을 막지 않는다.
    /// </summary>
    private async Task RefreshThresholdsAsync(double bootstrapMs)
    {
        try
        {
            var todayUtc = DateTime.Today.ToUniversalTime();
            var gap = await _ctStats.ComputeGapMedianAsync(excludeUntilUtc: todayUtc);
            foreach (var (k, v) in await _ctStats.ComputeGapMedianAsync())
                gap.TryAdd(k, v); // Day 0 폴백: 오늘 포함 잠정 gap'
            var ctThr = await _ctStats.ComputeCtThresholdAsync(excludeUntilUtc: todayUtc);
            foreach (var (k, v) in await _ctStats.ComputeCtThresholdAsync())
                ctThr.TryAdd(k, v); // Day 0 폴백: 오늘 포함 잠정 평균CT

            var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var flow in gap.Keys.Union(ctThr.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var gapMedian = gap.TryGetValue(flow, out var g) ? g.MedianMs : 0;
                var ctAvg = ctThr.TryGetValue(flow, out var c) ? c.AvgMs : 0;
                map[flow] = OeeMath.ResolveNoCycleThresholdMs(gapMedian, ctAvg, FloorMs, bootstrapMs);
            }
            _thresholdMsByFlow = map;
            _logger.LogDebug("[OEE] nocycle thresholds refreshed: {N} flows learned (bootstrap={Boot}s)",
                map.Count, bootstrapMs / 1000.0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] nocycle threshold refresh failed — keeping previous thresholds");
        }
    }

    /// <summary>
    /// DateTime 을 UTC 순간으로 정규화. 저장소 read-back(FromSqliteUtcString)은 Kind=Local 을 준다
    /// (UTC 저장값을 ToLocalTime 으로 되돌림) → lastCycleUtc(Kind=Utc)와 비교 전 반드시 UTC 로 맞춰야 한다.
    /// Local=ToUniversalTime, Unspecified=이미 UTC 로 가정.
    /// </summary>
    private static DateTime ToUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
    };

    /// <summary>
    /// plc.db dspFlowHistory 에서 flow별 가장 최근 RecordedAt(UTC) 조회.
    /// RecordedAt 은 DspRepositoryAdapter 가 DateTime.UtcNow(DATETIME) 로 저장 — 읽을 때 UTC kind 로 취급.
    /// </summary>
    private async Task<Dictionary<string, DateTime>> ReadLastCyclePerFlowAsync()
    {
        var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!File.Exists(dbPath)) return result;

        try
        {
            // 미러 라우팅 — 15초 폴링의 flow별 MAX 풀스캔을 창 고정 비용으로. 시맨틱 편차 1건 수용:
            // 미러는 63일 창만 담으므로 63일 내 사이클이 전무한 flow 는 맵에서 빠진다 — nocycle 판정이
            // "그 flow 는 정지 추적 대상 아님" 쪽(보수)으로 기울 뿐 오탐을 만들지 않는다.
            var mirrorConn = await _mirror.TryOpenPlcReadAsync(DateTime.UtcNow.AddDays(-60), layerB: true);
            SqliteConnection conn;
            if (mirrorConn is not null) conn = mirrorConn;
            else
            {
                conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
                await conn.OpenAsync();
            }
            await using var _ = conn;

            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (exists == 0) return result;

            const string sql = @"
                SELECT flowName AS FlowName, MAX(recordedAt) AS LastRecorded
                FROM dspFlowHistory
                WHERE flowName IS NOT NULL
                GROUP BY flowName";

            var rows = await conn.QueryAsync<LastCycleRow>(sql);
            foreach (var r in rows)
            {
                if (string.IsNullOrEmpty(r.FlowName) || string.IsNullOrEmpty(r.LastRecorded)) continue;
                var dt = ParseRecordedAt(r.LastRecorded);
                if (dt.HasValue)
                    result[r.FlowName] = dt.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] failed reading dspFlowHistory last cycles");
        }
        return result;
    }

    /// <summary>
    /// recordedAt 파싱 → UTC. DspRepositoryAdapter 는 DateTime.UtcNow 를 DATETIME 컬럼에 넣으므로
    /// SQLite 는 "yyyy-MM-dd HH:mm:ss(.fffffff)" (Z 없음) 형태로 저장한다. AssumeUniversal 로 UTC 취급.
    /// SqliteDateTimeHelpers(Z suffix + 로컬변환)와 포맷이 달라 별도 파서를 둔다.
    /// </summary>
    private static DateTime? ParseRecordedAt(string s)
    {
        var trimmed = s.TrimEnd('Z');
        if (DateTime.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return null;
    }

    /// <summary>flow 이름 → 소속 system 이름. AASX 미로드 시 빈 맵.</summary>
    private Dictionary<string, string> BuildFlowSystemMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!_project.IsLoaded) return map;
            foreach (var sys in _project.GetActiveSystems())
                foreach (var f in _project.GetFlows(sys.Id))
                    map[f.Name] = sys.Name;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[OEE] flow→system map build failed");
        }
        return map;
    }

    private sealed class LastCycleRow
    {
        public string? FlowName { get; set; }
        public string? LastRecorded { get; set; }
    }
}
