using Dapper;
using DSPilot.Infrastructure;
using DSPilot.Models.Oee;
using DSPilot.Repositories;
using Microsoft.Data.Sqlite;

namespace DSPilot.Services;

/// <summary>
/// 무사이클 기반 정지 onset/clear 자동 판정 (doc/21 §5).
///
/// 1차 소스 = "무사이클 N초". dspFlowHistory(plc.db) 의 flow별 마지막 사이클 RecordedAt 을 주기적으로 보고,
/// 마지막 사이클 후 N초 이상 신규 사이클이 없으면 open 정지이벤트(detectSource='nocycle', category/reason NULL,
/// isFailure 0)를 생성한다. 사이클이 재개되면(마지막 사이클이 정지 startAt 이후로 갱신) 해당 open 이벤트를 마감한다.
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
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDatabasePathResolver _pathResolver;
    private readonly DsProjectService _project;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OeeDowntimeStateMachine> _logger;

    public OeeDowntimeStateMachine(
        IServiceScopeFactory scopeFactory,
        IDatabasePathResolver pathResolver,
        DsProjectService project,
        IConfiguration configuration,
        ILogger<OeeDowntimeStateMachine> logger)
    {
        _scopeFactory = scopeFactory;
        _pathResolver = pathResolver;
        _project = project;
        _configuration = configuration;
        _logger = logger;
    }

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
            "OeeDowntimeStateMachine starting (poll={Poll}s, nocycle threshold={Threshold}s)",
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
        var thresholdMs = NoCycleSeconds * 1000L;

        // 1) plc.db 에서 flow별 마지막 사이클 시각(UTC) 조회.
        var lastCycleByFlow = await ReadLastCyclePerFlowAsync();
        if (lastCycleByFlow.Count == 0) return;

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
                        IsFailure = 0,
                        DetectSource = DetectSource,
                        SourceLogId = null,
                        Note = null,
                    }, ct);
                    _logger.LogInformation(
                        "[OEE] nocycle onset: flow='{Flow}' idle={IdleSec:F0}s (last cycle {Last:u})",
                        flowName, idleMs / 1000.0, lastCycleUtc);
                }
            }
            else
            {
                // 사이클 정상 진행 중. open 이벤트가 있고, 그 후로 새 사이클이 돌았으면 마감.
                if (hasOpen && open!.StartAt <= lastCycleUtc)
                {
                    // endAt = 사이클 재개(=마지막 사이클) 시점. durationMs 는 repo 가 계산.
                    var closed = await repo.CloseDowntimeAsync(open.Id, lastCycleUtc, ct);
                    if (closed > 0)
                        _logger.LogInformation(
                            "[OEE] nocycle clear: flow='{Flow}' event#{Id} cycle resumed at {Last:u}",
                            flowName, open.Id, lastCycleUtc);
                }
            }
        }
    }

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
            await using var conn = new SqliteConnection(
                $"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync();

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
