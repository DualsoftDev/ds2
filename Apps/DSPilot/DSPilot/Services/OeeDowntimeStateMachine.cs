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
/// 임계 = <b>14일 평균 CT × 비가동 배수</b>(사용자 설정, 기본 2.5×) — 집계의 정지 판정과 <b>같은 값</b>.
/// <para>2026-08-21 통일. 종전엔 감지만 별도 체인(3×gap' ▸ 3×평균CT ▸ 120s)을 써서 집계 기준과 어긋났다
/// (실측: 감지 109초 vs 집계 계상 213초 → 그 사이 정지는 <b>로그엔 뜨는데 고장 건수엔 없는</b> 구간이 됐다).
/// 이제 화면의 '정지·비생산 판정 기준' 슬라이더가 감지 시점까지 함께 움직인다 — 사용자가 보는 숫자가 하나다.</para>
/// <para>평균 CT 미학습(클린샘플 0) flow 는 <b>감지하지 않는다</b>. 그 상태는 가용성도 '산출 불가'이므로
/// 감지만 켜두면 근거 없는 정지를 만들어낸다 — 그 설비는 '데이터 무결성' 카드가 측정 불가로 보고한다.</para>
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
    // 부트스트랩 상수 폐기(2026-08-21) — 평균 CT 가 없으면 감지하지 않는다(위 주석 참조).
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

    /// <summary>
    /// 비가동 판정 배수 — 화면 '정지·비생산 판정 기준' 슬라이더 값. 집계(ComputeCycleAggregateAsync)와
    /// 같은 소스(<see cref="OeeManualSettings.ResolveCtMultipliers"/>)를 읽어 감지와 계상 기준을 일치시킨다.
    /// </summary>
    private double ReadIdleMultiplier()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<AppSettingsService>();
            var (idle, _) = settings.LoadSettings().OeeManual.ResolveCtMultipliers();
            return idle > 0 ? idle : Services.OeeMath.IdleCtMultiplierDefault;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "비가동 배수 조회 실패 — 기본값 사용");
            return Services.OeeMath.IdleCtMultiplierDefault;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OeeDowntimeStateMachine starting (poll={Poll}s, 임계 = flow별 14일 평균CT × 비가동 배수)",
            PollInterval.TotalSeconds);

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


        // 1) plc.db 에서 flow별 마지막 사이클 시각(UTC) 조회.
        var lastCycleByFlow = await ReadLastCyclePerFlowAsync();
        if (lastCycleByFlow.Count == 0) return;

        // flow별 임계 재학습 (TTL 경과 시). 실패해도 기존 캐시/부트스트랩으로 tick 은 계속.
        if (nowUtc - _thresholdRefreshedUtc >= ThresholdTtl)
        {
            await RefreshThresholdsAsync();
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

        foreach (var (flowName, mirrorLastCycleUtc) in lastCycleByFlow)
        {
            var lastCycleUtc = mirrorLastCycleUtc;
            var hasOpen = openByFlow.TryGetValue(flowName, out var open);
            // 평균 CT 미학습 flow 는 감지 대상이 아니다(임계 미등재 = 건너뜀).
            if (!_thresholdMsByFlow.TryGetValue(flowName, out var thresholdMs) || thresholdMs <= 0) continue;
            var idleMs = (nowUtc - lastCycleUtc).TotalMilliseconds;

            // ★미러 tail 지연 방어 — "정지"로 보이는 flow 만 파일(SSOT)로 마지막 사이클을 재확인한다.
            //   ReadLastCyclePerFlowAsync 는 63일 인메모리 미러로 라우팅되는데, 미러 write-through 는
            //   쓰기 락 경합 시 조용히 스킵되므로 tail 이 수 분 뒤처질 수 있다. 그 stale 값으로 판정하면
            //   사이클이 정상 유입 중인데도 무사이클 정지가 열린 채 남고(마감 조건 미충족), 집계가 그 구간을
            //   비생산으로 승격시켜 가동시간이 0 으로 나온다(2026-07-29 실측: 정상 사이클 196건/5분 구간이
            //   가동 0·비생산 100%). 가동 중에는 이 분기에 들어오지 않으므로 추가 쿼리는 실제 정지 구간에서만
            //   발생하고, (flowName, recordedAt) 인덱스 시크라 tick 당 비용이 무시할 수준이다.
            if (idleMs >= thresholdMs)
            {
                var fileLast = await ReadLastCycleFromFileAsync(flowName);
                if (fileLast.HasValue && fileLast.Value > lastCycleUtc)
                {
                    _logger.LogDebug(
                        "[OEE] nocycle: flow='{Flow}' 미러 tail 지연 — 미러 {Mirror:u} → 파일 {File:u} ({LagSec:F0}s)",
                        flowName, lastCycleUtc, fileLast.Value, (fileLast.Value - lastCycleUtc).TotalSeconds);
                    lastCycleUtc = fileLast.Value;
                    idleMs = (nowUtc - lastCycleUtc).TotalMilliseconds;
                }
            }

            // ① 마감 우선 — 열린 정지의 시작 이후 새 사이클이 돌았으면 *idle 여부와 무관하게* 닫는다.
            //   종전엔 마감이 "idle < 임계" 분기 안에만 있어, tick 이 그 짧은 창에 못 들어가거나 조회가
            //   stale 하면 사이클 재개에도 정지가 영구 open 으로 남았다(우진 현장: 하루 종일 open).
            //   ★StartAt 은 저장소가 UTC→로컬(Kind=Local)로 되돌려 준다(SqliteDateTimeHelpers.FromSqliteUtcString).
            //   lastCycleUtc(Kind=Utc)와 직접 비교하면 DateTime 이 Kind 무시하고 Ticks 만 비교 → KST(+9h) 만큼
            //   StartAt 이 '미래'로 보여 조건이 9시간 동안 거짓 → 사이클 재개해도 정지가 안 닫힘. UTC 로 정규화한다.
            var openStartUtc = hasOpen ? ToUtc(open!.StartAt) : default;
            // 판정 규칙은 OeeMath.ResolveNoCycleActions 단일 소스(순수·테스트 가능).
            var (shouldClose, shouldOpen) = OeeMath.ResolveNoCycleActions(
                hasOpen, openStartUtc, lastCycleUtc, idleMs, thresholdMs);

            if (shouldClose)
            {
                // endAt = 사이클 재개(=마지막 사이클) 시점. durationMs 는 repo 가 계산.
                var closed = await repo.CloseDowntimeAsync(open!.Id, lastCycleUtc, ct);
                if (closed > 0)
                {
                    _logger.LogInformation(
                        "[OEE] nocycle clear: flow='{Flow}' event#{Id} cycle resumed at {Last:u} (정지 {DurSec:F0}s)",
                        flowName, open.Id, lastCycleUtc, (lastCycleUtc - openStartUtc).TotalSeconds);

                    // 분류 휴리스틱(신규 마감 건만 — 백필 금지). 수동 분류는 AutoClassifyHeuristicAsync 가드로 보존.
                    var durMs = (lastCycleUtc - openStartUtc).TotalMilliseconds;
                    var (rc, cat, isFail, should) = OeeMath.ClassifyByDuration(durMs);
                    if (should)
                        await repo.AutoClassifyHeuristicAsync(open.Id, rc, cat, isFail, ct);
                }
            }

            // ② onset — 무사이클 임계 초과 + (마감 반영 후) 열린 정지 없음. 마감과 같은 tick 에서 다시 열릴 수
            //    있다: 그 사이클 뒤로 또 임계를 넘겼다는 뜻이므로 정상(startAt = 그 사이클 시각).
            if (shouldOpen)
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
    }

    /// <summary>
    /// flow별 무사이클 임계 재학습 (doc/23 §6 Phase 1). gap'(14일 클린 WT 중앙값)·14일평균CT 를 오늘 제외로
    /// 산출하고, 오늘 이전 데이터가 없는 신규 flow 는 오늘 포함 잠정값으로 폴백(TryAdd — 컨트롤러
    /// ResolveCtThresholdsAsync 와 동일 컨벤션). 체인 합성은 <see cref="OeeMath.ResolveNoCycleThresholdMs"/>.
    /// 실패 시 기존 캐시 유지(다음 TTL 에 재시도) — tick 을 막지 않는다.
    /// </summary>
    private async Task RefreshThresholdsAsync()
    {
        try
        {
            var todayUtc = DateTime.Today.ToUniversalTime();
            var ctThr = await _ctStats.ComputeCtThresholdAsync(excludeUntilUtc: todayUtc);
            foreach (var (k, v) in await _ctStats.ComputeCtThresholdAsync())
                ctThr.TryAdd(k, v); // Day 0 폴백: 오늘 포함 잠정 평균CT

            // 집계와 같은 배수를 쓴다 — 사용자가 슬라이더로 조절하면 감지 시점도 함께 움직인다.
            var idleMult = ReadIdleMultiplier();
            var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (flow, c) in ctThr)
                if (c.AvgMs > 0) map[flow] = c.AvgMs * idleMult;
            _thresholdMsByFlow = map;
            _logger.LogDebug("[OEE] nocycle thresholds refreshed: {N} flows (평균CT × {Mult}배)",
                map.Count, idleMult);
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
    /// 파일(SSOT)에서 한 flow 의 마지막 사이클 시각(UTC). 미러 tail 지연 재확인 전용 —
    /// idx_dspFlowHistory_flow_recordedAt 인덱스 시크라 정지 구간에서만 호출되면 비용이 무시할 수준이다.
    /// 실패/미존재 시 null → 호출측이 미러 값을 그대로 쓴다(보수: 판정을 바꾸지 않음).
    /// </summary>
    private async Task<DateTime?> ReadLastCycleFromFileAsync(string flowName)
    {
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!File.Exists(dbPath)) return null;
        try
        {
            await using var conn = new SqliteConnection(
                $"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync();
            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (exists == 0) return null;
            var raw = await conn.ExecuteScalarAsync<string?>(
                "SELECT MAX(recordedAt) FROM dspFlowHistory WHERE flowName = @Flow",
                new { Flow = flowName });
            return string.IsNullOrEmpty(raw) ? null : ParseRecordedAt(raw);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[OEE] nocycle: 파일 last-cycle 재확인 실패 flow='{Flow}'", flowName);
            return null;
        }
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
