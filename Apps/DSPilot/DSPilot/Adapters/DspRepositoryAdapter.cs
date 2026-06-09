using Dapper;
using DSPilot.Infrastructure;
using DSPilot.Models.Dsp;
using DSPilot.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DSPilot.Adapters;

/// <summary>dspCall.State='Going' 행의 식별 정보 — 상태 self-heal reconcile 용.</summary>
public sealed record GoingCallInfo(Guid CallId, string CallName, string FlowName);

/// <summary>비가동-제외 사이클 1건의 측정 삼중쌍(ms). 라이브 롤링 평균 윈도우(<c>FlowCycleState.Recent</c>) 단위.</summary>
public readonly record struct CycleSample(int MT, int WT, int CT);

/// <summary>AASX 변경 이력 항목. 연표 다이얼로그에서 선택 기준으로 사용.</summary>
public sealed record AasxChangeLogEntry(long Id, DateTime ChangedAtLocal, string Source, string? Notes);

/// <summary>
/// DSP 실시간 DB 저장소 (pure C# Dapper 구현).
/// 기존 F# DspRepository에서 이관. DI 등록 이름 유지를 위해 클래스명은 Adapter 유지.
/// </summary>
public class DspRepositoryAdapter : IDspRepository
{
    private const string HistoryTable = "dspFlowHistory";

    private readonly DatabasePaths _paths;
    private readonly ILogger<DspRepositoryAdapter> _logger;
    private readonly bool _enabled;
    private readonly string _connectionString;
    private readonly string _flowTable;
    private readonly string _callTable;

    public DspRepositoryAdapter(DatabasePaths paths, ILogger<DspRepositoryAdapter> logger)
    {
        _paths = paths;
        _logger = logger;
        _enabled = paths.DspTablesEnabled;
        _connectionString = $"Data Source={paths.SharedDbPath};Mode=ReadWriteCreate;Default Timeout=20";
        _flowTable = paths.GetFlowTableName();
        _callTable = paths.GetCallTableName();

        if (!_enabled)
        {
            _logger.LogInformation("DspTables:Enabled=false, DspRepositoryAdapter will operate in no-op mode.");
        }
    }

    // ===== Connection helpers =====

    private async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<bool> FlowAndCallTablesExistAsync(SqliteConnection conn, string flowTable, string callTable)
    {
        const string sql = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN (@flowTable, @callTable)";
        var count = await conn.ExecuteScalarAsync<long>(sql, new { flowTable, callTable });
        return count >= 2;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string tableName)
    {
        const string sql = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@tableName";
        var count = await conn.ExecuteScalarAsync<long>(sql, new { tableName });
        return count > 0;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string tableName, string columnName)
    {
        var sql = $"SELECT name FROM pragma_table_info('{tableName}')";
        var names = await conn.QueryAsync<string>(sql);
        return names.Any(n => n == columnName);
    }

    /// <summary>
    /// 테이블에 컬럼이 없으면 ALTER TABLE ADD COLUMN 으로 추가. 옛 스키마 호환용.
    /// </summary>
    private async Task EnsureColumnAsync(SqliteConnection conn, string table, string column, string definition)
    {
        try
        {
            var existsSql = $"SELECT name FROM pragma_table_info('{table}')";
            var names = await conn.QueryAsync<string>(existsSql);
            if (names.Any(n => string.Equals(n, column, StringComparison.OrdinalIgnoreCase)))
                return;

            await conn.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
            _logger.LogInformation("Added missing column {Column} {Definition} to {Table}", column, definition, table);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EnsureColumn failed for {Table}.{Column}", table, column);
        }
    }

    private async Task EnsureIsIdleColumnAsync(SqliteConnection conn)
    {
        var exists = await ColumnExistsAsync(conn, HistoryTable, "IsIdle");
        if (exists) return;
        try
        {
            await conn.ExecuteAsync($"ALTER TABLE {HistoryTable} ADD COLUMN IsIdle INTEGER NOT NULL DEFAULT 0");
            _logger.LogInformation("Added IsIdle column to {Table} table", HistoryTable);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add IsIdle column (may already exist)");
        }
    }

    // ===== IDspRepository =====

    public async Task<bool> CreateSchemaAsync()
    {
        if (!_enabled) return true;

        try
        {
            await using var conn = await OpenAsync();

            // SQLite journal_mode=WAL — write 트랜잭션이 read 를 차단하지 않도록.
            // PlcTagLogWriterService 가 250ms 마다 커밋하므로, 이걸 안 켜면 cycle-time-analysis
            // 등 시간 범위 read 쿼리가 매번 잠금 대기에 걸린다.
            // journal_mode 는 DB 파일에 영구 저장되는 속성이므로 한 번만 켜면 되고, plc.db 를 삭제 →
            // 재생성하는 경로(Settings 페이지의 DB 재초기화 + Program.cs 시작 시)에서도 항상 거치도록
            // CreateSchemaAsync 안에 둔다.
            try
            {
                var mode = await conn.ExecuteScalarAsync<string>("PRAGMA journal_mode=WAL");
                await conn.ExecuteAsync("PRAGMA synchronous=NORMAL");
                _logger.LogInformation("plc.db journal_mode={Mode}, synchronous=NORMAL", mode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set WAL pragma");
            }

            // 컬럼명은 EV2 unified schema 와 호환되도록 lowercase camelCase.
            // SQLite identifier 매칭은 case-insensitive 라 INSERT/UPDATE 의 PascalCase 도 동일 컬럼을 가리킨다.
            const string createFlow = @"
                CREATE TABLE IF NOT EXISTS dspFlow (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    flowName        NVARCHAR(128) NOT NULL UNIQUE,
                    mt              INTEGER,
                    wt              INTEGER,
                    ct              INTEGER,
                    avgMT           REAL,
                    avgWT           REAL,
                    avgCT           REAL,
                    state           NVARCHAR(128),
                    movingStartName NVARCHAR(128),
                    movingEndName   NVARCHAR(128),
                    createdAt       DATETIME DEFAULT (datetime('now')),
                    updatedAt       DATETIME DEFAULT (datetime('now'))
                )";

            const string createCall = @"
                CREATE TABLE IF NOT EXISTS dspCall (
                    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                    callId             TEXT,
                    callName           NVARCHAR(128) NOT NULL,
                    apiCall            NVARCHAR(128),
                    workName           NVARCHAR(128),
                    flowName           NVARCHAR(128) NOT NULL,
                    next               TEXT,
                    prev               TEXT,
                    autoPre            TEXT,
                    commonPre          TEXT,
                    state              NVARCHAR(128),
                    progressRate       REAL DEFAULT 0,
                    previousGoingTime  INTEGER,
                    averageGoingTime   REAL,
                    stdDevGoingTime    REAL,
                    goingCount         INTEGER DEFAULT 0,
                    device             NVARCHAR(128),
                    errorText          TEXT,
                    createdAt          DATETIME DEFAULT (datetime('now')),
                    updatedAt          DATETIME DEFAULT (datetime('now')),
                    UNIQUE (callName, flowName, workName)
                )";

            const string createFlowHistory = @"
                CREATE TABLE IF NOT EXISTS dspFlowHistory (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    flowName      NVARCHAR(128),
                    mt            INTEGER,
                    wt            INTEGER,
                    ct            INTEGER,
                    cycleNo       INTEGER,
                    recordedAt    DATETIME,
                    IsIdle        INTEGER NOT NULL DEFAULT 0,
                    headCallName  NVARCHAR(128),
                    tailCallName  NVARCHAR(128)
                )";

            // 모델 변경 이력 — Promaker 의 AASX 가 바뀔 때마다 audit row 1건.
            // 사후 분석에서 "이 사이클타임이 어느 모델 시점의 측정인지" 추적용.
            const string createAasxChangeLog = @"
                CREATE TABLE IF NOT EXISTS aasxChangeLog (
                    id               INTEGER PRIMARY KEY AUTOINCREMENT,
                    changedAt        DATETIME NOT NULL,
                    sha256Before     TEXT,
                    sha256After      TEXT NOT NULL,
                    source           TEXT NOT NULL,
                    flowsAddedJson   TEXT,
                    flowsRemovedJson TEXT,
                    pruneFlows       INTEGER NOT NULL DEFAULT 0,
                    pruneCalls       INTEGER NOT NULL DEFAULT 0,
                    pruneHistory     INTEGER NOT NULL DEFAULT 0,
                    notes            TEXT
                )";
            const string createAasxChangeLogIdx =
                "CREATE INDEX IF NOT EXISTS idx_aasxChangeLog_changedAt ON aasxChangeLog(changedAt)";

            // Flow head/tail boundary 변경 이력 — 사용자 override 또는 AASX 자동 갱신마다 row 1건.
            // dspFlowHistory 의 headCallName/tailCallName 박제와 함께 쓰면 boundary 변경 시점을 정확히 분리 가능.
            const string createFlowBoundaryChangeLog = @"
                CREATE TABLE IF NOT EXISTS flowBoundaryChangeLog (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    changedAt       DATETIME NOT NULL,
                    flowName        NVARCHAR(128) NOT NULL,
                    headBefore      NVARCHAR(128),
                    headAfter       NVARCHAR(128),
                    tailBefore      NVARCHAR(128),
                    tailAfter       NVARCHAR(128),
                    source          TEXT NOT NULL,
                    aasxChangeLogId INTEGER,
                    notes           TEXT
                )";
            const string createFlowBoundaryChangeLogIdxTime =
                "CREATE INDEX IF NOT EXISTS idx_flowBoundaryChangeLog_changedAt ON flowBoundaryChangeLog(changedAt)";
            const string createFlowBoundaryChangeLogIdxFlow =
                "CREATE INDEX IF NOT EXISTS idx_flowBoundaryChangeLog_flowName ON flowBoundaryChangeLog(flowName)";

            // plc / plcTag / plcTagLog — Hub 모니터링 모드에서 DsPilot 자체가 채움.
            // 컬럼 구성은 [PlcEntity](Apps/DSPilot/DSPilot/Models/Plc/PlcEntity.cs) 와 일치.
            // CycleTimeAnalysis 와 PlcDebug 가 이 테이블을 읽음.
            const string createPlc = @"
                CREATE TABLE IF NOT EXISTS plc (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    projectId  INTEGER,
                    name       NVARCHAR(128) NOT NULL UNIQUE,
                    connection TEXT
                )";

            const string createPlcTag = @"
                CREATE TABLE IF NOT EXISTS plcTag (
                    id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    plcId     INTEGER NOT NULL DEFAULT 1,
                    name      NVARCHAR(128) NOT NULL,
                    address   NVARCHAR(128) NOT NULL UNIQUE,
                    dataType  NVARCHAR(32)  NOT NULL DEFAULT 'BOOL'
                )";

            const string createPlcTagLog = @"
                CREATE TABLE IF NOT EXISTS plcTagLog (
                    id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    plcTagId  INTEGER NOT NULL,
                    dateTime  DATETIME NOT NULL,
                    value     TEXT NOT NULL,
                    FOREIGN KEY (plcTagId) REFERENCES plcTag(id)
                )";

            const string createPlcTagLogIdx =
                "CREATE INDEX IF NOT EXISTS idx_plcTagLog_dateTime ON plcTagLog(dateTime)";
            const string createPlcTagLogTagIdx =
                "CREATE INDEX IF NOT EXISTS idx_plcTagLog_plcTagId ON plcTagLog(plcTagId)";
            // 시간범위 쿼리 (cycle-time-analysis 메인 쿼리 등) 의 핵심 인덱스 —
            //   WHERE plcTagId IN (..) AND dateTime BETWEEN @start AND @end
            //   GROUP BY plcTagId 의 MAX(dateTime <= @at) (latest-before)
            // 둘 다 (plcTagId, dateTime) 복합 인덱스 위에서 태그당 1회 index seek 로 끝낼 수 있다.
            // 단일 인덱스 두 개로는 SQLite 가 풀스캔 또는 거대한 인메모리 필터로 처리해 시간범위 1분이라도
            // plcTagLog 전체에 비례한 비용이 든다.
            const string createPlcTagLogTagDateTimeIdx =
                "CREATE INDEX IF NOT EXISTS idx_plcTagLog_tagId_dateTime ON plcTagLog(plcTagId, dateTime)";
            const string createPlcTagAddressIdx =
                "CREATE INDEX IF NOT EXISTS idx_plcTag_address ON plcTag(address)";

            // UserTag 알림 raw 로그 — 매칭된 이벤트만 저장 (모든 plcTagLog 행을 다시 쓰지 않음).
            // occurredAt: ISO8601 UTC 문자열 (plcTagLog.dateTime 과 동일 포맷).
            const string createUserTagAlertLog = @"
                CREATE TABLE IF NOT EXISTS userTagAlertLog (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurredAt    TEXT     NOT NULL,
                    systemId      TEXT     NOT NULL,
                    systemName    TEXT     NOT NULL,
                    name          TEXT     NOT NULL,
                    logLevel      TEXT     NOT NULL,
                    tagAddress    TEXT     NOT NULL,
                    valueType     TEXT     NOT NULL,
                    matchOp       TEXT     NOT NULL,
                    matchValue    TEXT,
                    actualValue   TEXT     NOT NULL,
                    sourceLogId   INTEGER
                )";

            const string createUserTagAlertLogIdxTime =
                "CREATE INDEX IF NOT EXISTS idx_userTagAlertLog_occurredAt ON userTagAlertLog(occurredAt)";
            const string createUserTagAlertLogIdxNameTime =
                "CREATE INDEX IF NOT EXISTS idx_userTagAlertLog_name_time ON userTagAlertLog(name, occurredAt)";
            const string createUserTagAlertLogIdxLevelTime =
                "CREATE INDEX IF NOT EXISTS idx_userTagAlertLog_level_time ON userTagAlertLog(logLevel, occurredAt)";

            // 일별 사전집계 — 월/년 추세 쿼리용. 자정 backfill 잡이 채움.
            const string createUserTagAlertDaily = @"
                CREATE TABLE IF NOT EXISTS userTagAlertDaily (
                    bucketDate    TEXT     NOT NULL,
                    systemName    TEXT     NOT NULL,
                    name          TEXT     NOT NULL,
                    logLevel      TEXT     NOT NULL,
                    count         INTEGER  NOT NULL,
                    PRIMARY KEY (bucketDate, systemName, name, logLevel)
                )";

            const string createUserTagAlertDailyIdxDate =
                "CREATE INDEX IF NOT EXISTS idx_userTagAlertDaily_date ON userTagAlertDaily(bucketDate)";

            // dspFlowHistory 는 (flowName) 필터 + (recordedAt) 정렬/범위가 모든 조회·재집계의 공통 패턴인데
            // 인덱스가 전혀 없어 매번 풀스캔이었다. (flowName, recordedAt) 복합 인덱스로 per-flow seek + 범위
            // 스캔(history 조회 / 경계 재계산 delete-by-range / Recompute 상관 서브쿼리)을 모두 가속한다.
            const string createFlowHistoryIdxFlowTime =
                "CREATE INDEX IF NOT EXISTS idx_dspFlowHistory_flow_recordedAt ON dspFlowHistory(flowName, recordedAt)";

            await conn.ExecuteAsync(createFlow);
            await conn.ExecuteAsync(createCall);
            await conn.ExecuteAsync(createFlowHistory);
            await conn.ExecuteAsync(createFlowHistoryIdxFlowTime);
            await conn.ExecuteAsync(createPlc);
            await conn.ExecuteAsync(createPlcTag);
            await conn.ExecuteAsync(createPlcTagLog);
            await conn.ExecuteAsync(createPlcTagLogIdx);
            await conn.ExecuteAsync(createPlcTagLogTagIdx);
            await conn.ExecuteAsync(createPlcTagLogTagDateTimeIdx);
            await conn.ExecuteAsync(createPlcTagAddressIdx);
            await conn.ExecuteAsync(createUserTagAlertLog);
            await conn.ExecuteAsync(createUserTagAlertLogIdxTime);
            await conn.ExecuteAsync(createUserTagAlertLogIdxNameTime);
            await conn.ExecuteAsync(createUserTagAlertLogIdxLevelTime);
            await conn.ExecuteAsync(createUserTagAlertDaily);
            await conn.ExecuteAsync(createUserTagAlertDailyIdxDate);
            await conn.ExecuteAsync(createAasxChangeLog);
            await conn.ExecuteAsync(createAasxChangeLogIdx);
            await conn.ExecuteAsync(createFlowBoundaryChangeLog);
            await conn.ExecuteAsync(createFlowBoundaryChangeLogIdxTime);
            await conn.ExecuteAsync(createFlowBoundaryChangeLogIdxFlow);

            // 기본 plc 행 보장 (id=1) — plcTag.plcId 가 참조하는 단일 PLC
            await conn.ExecuteAsync(
                "INSERT INTO plc (id, name) VALUES (1, 'DSPilot') ON CONFLICT(name) DO NOTHING");

            // M2 — 옛 EV2 스키마 마이그레이션. CREATE TABLE IF NOT EXISTS 는 기존 테이블의 컬럼을
            // 추가하지 않으므로, 우리 코드가 쓰는 컬럼이 누락되어 있으면 SQL 에러가 fire-and-forget
            // 으로 흡수되어 통계가 영원히 0 으로 남는다. 누락된 컬럼만 ALTER 로 보충.
            await EnsureColumnAsync(conn, "dspCall", "previousGoingTime", "INTEGER");
            await EnsureColumnAsync(conn, "dspCall", "averageGoingTime",  "REAL");
            await EnsureColumnAsync(conn, "dspCall", "stdDevGoingTime",   "REAL");
            await EnsureColumnAsync(conn, "dspCall", "goingCount",        "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync(conn, "dspFlow", "movingStartName",   "NVARCHAR(128)");
            await EnsureColumnAsync(conn, "dspFlow", "movingEndName",     "NVARCHAR(128)");
            await EnsureColumnAsync(conn, "dspFlow", "avgMT",             "REAL");
            await EnsureColumnAsync(conn, "dspFlow", "avgWT",             "REAL");
            await EnsureColumnAsync(conn, "dspFlow", "avgCT",             "REAL");
            await EnsureColumnAsync(conn, "dspFlowHistory", "headCallName", "NVARCHAR(128)");
            await EnsureColumnAsync(conn, "dspFlowHistory", "tailCallName", "NVARCHAR(128)");

            _logger.LogInformation(
                "DSP/PLC schema ensured (dspFlow / dspCall / dspFlowHistory / plc / plcTag / plcTagLog)");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create DSP schema");
            return false;
        }
    }

    public async Task<int> BulkInsertFlowsAsync(List<DspFlowEntity> flows)
    {
        if (!_enabled) return 0;

        await using var conn = await OpenAsync();
        if (!await FlowAndCallTablesExistAsync(conn, _flowTable, _callTable))
        {
            _logger.LogWarning("Tables do not exist yet, cannot insert {Count} flows. Waiting for schema initialization.", flows.Count);
            return 0;
        }

        using var tx = conn.BeginTransaction();
        try
        {
            var sql = $@"
                INSERT INTO {_flowTable} (FlowName, MT, WT, CT, AvgMT, AvgWT, AvgCT, State, MovingStartName, MovingEndName)
                VALUES (@FlowName, @MT, @WT, @CT, @AvgMT, @AvgWT, @AvgCT, @State, @MovingStartName, @MovingEndName)
                ON CONFLICT (FlowName) DO UPDATE SET
                    MT = COALESCE(excluded.MT, {_flowTable}.MT),
                    WT = COALESCE(excluded.WT, {_flowTable}.WT),
                    CT = COALESCE(excluded.CT, {_flowTable}.CT),
                    AvgMT = COALESCE(excluded.AvgMT, {_flowTable}.AvgMT),
                    AvgWT = COALESCE(excluded.AvgWT, {_flowTable}.AvgWT),
                    AvgCT = COALESCE(excluded.AvgCT, {_flowTable}.AvgCT),
                    State = excluded.State,
                    MovingStartName = excluded.MovingStartName,
                    MovingEndName = excluded.MovingEndName,
                    UpdatedAt = datetime('now')";

            var count = await conn.ExecuteAsync(sql, flows, tx);
            tx.Commit();
            _logger.LogInformation("Inserted {Count} flows into DSP database (Table: {Table})", count, _flowTable);
            return count;
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.LogError(ex, "Failed to bulk insert flows");
            throw;
        }
    }

    public async Task<int> BulkInsertCallsAsync(List<DspCallEntity> calls)
    {
        if (!_enabled) return 0;

        await using var conn = await OpenAsync();
        if (!await FlowAndCallTablesExistAsync(conn, _flowTable, _callTable))
        {
            _logger.LogWarning("Tables do not exist yet, cannot insert {Count} calls. Waiting for schema initialization.", calls.Count);
            return 0;
        }

        using var tx = conn.BeginTransaction();
        try
        {
            // 중복 제거 (CallName, FlowName, WorkName 기준)
            var uniqueCalls = calls
                .GroupBy(c => (c.CallName, c.FlowName, c.WorkName))
                .Select(g => g.First())
                .ToList();

            if (uniqueCalls.Count < calls.Count)
            {
                _logger.LogWarning(
                    "Input data contains {DuplicateCount} duplicate calls (Total: {Total}, Unique: {Unique})",
                    calls.Count - uniqueCalls.Count, calls.Count, uniqueCalls.Count);
            }

            // Flow 존재 보장
            var flowNames = uniqueCalls.Select(c => c.FlowName).Distinct();
            foreach (var flowName in flowNames)
            {
                await conn.ExecuteAsync(
                    $"INSERT INTO {_flowTable} (FlowName) VALUES (@FlowName) ON CONFLICT (FlowName) DO NOTHING",
                    new { FlowName = flowName },
                    tx);
            }

            var sql = $@"
                INSERT INTO {_callTable} (CallId, CallName, ApiCall, WorkName, FlowName, Next, Prev, AutoPre, CommonPre, State, ProgressRate, Device)
                VALUES (@CallId, @CallName, @ApiCall, @WorkName, @FlowName, @Next, @Prev, @AutoPre, @CommonPre, @State, @ProgressRate, @Device)
                ON CONFLICT (CallName, FlowName, WorkName) DO UPDATE SET
                    CallId = excluded.CallId,
                    ApiCall = excluded.ApiCall,
                    Next = excluded.Next,
                    Prev = excluded.Prev,
                    AutoPre = excluded.AutoPre,
                    CommonPre = excluded.CommonPre,
                    State = excluded.State,
                    ProgressRate = excluded.ProgressRate,
                    Device = excluded.Device,
                    UpdatedAt = datetime('now')";

            var count = await conn.ExecuteAsync(sql, uniqueCalls, tx);
            tx.Commit();
            _logger.LogInformation("Inserted {Count} calls into DSP database", count);
            return count;
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.LogError(ex, "Failed to bulk insert calls");
            throw;
        }
    }

    /// <summary>
    /// 한 Flow 내 Going 상태 Call 수에 따라 dspFlow.state 를 'Going' / 'Ready' 로 자동 동기화.
    /// 단일 atomic UPDATE 로 race 없음.
    /// </summary>
    public async Task<bool> SyncFlowStateAsync(string flowName)
    {
        if (!_enabled) return false;
        if (string.IsNullOrEmpty(flowName)) return false;

        await using var conn = await OpenAsync();
        if (!await FlowAndCallTablesExistAsync(conn, _flowTable, _callTable))
            return false;

        var sql = $@"
            UPDATE {_flowTable}
            SET State = CASE WHEN EXISTS (
                    SELECT 1 FROM {_callTable}
                    WHERE FlowName = @FlowName AND State = 'Going'
                ) THEN 'Going' ELSE 'Ready' END,
                UpdatedAt = datetime('now')
            WHERE FlowName = @FlowName";

        var rows = await conn.ExecuteAsync(sql, new { FlowName = flowName });
        return rows > 0;
    }

    public async Task<bool> UpdateFlowStateAsync(string flowName, string state)
    {
        if (!_enabled) return false;

        await using var conn = await OpenAsync();
        if (!await FlowAndCallTablesExistAsync(conn, _flowTable, _callTable))
        {
            _logger.LogDebug("Tables do not exist yet, skipping update");
            return false;
        }

        var sql = $@"
            UPDATE {_flowTable}
            SET State = @State,
                UpdatedAt = datetime('now')
            WHERE FlowName = @FlowName";

        var result = await conn.ExecuteAsync(sql, new { State = state, FlowName = flowName });
        return result > 0;
    }

    public async Task<bool> HasGoingCallsInFlowAsync(string flowName)
    {
        if (!_enabled) return false;

        await using var conn = await OpenAsync();
        if (!await FlowAndCallTablesExistAsync(conn, _flowTable, _callTable))
        {
            _logger.LogDebug("Tables do not exist yet");
            return false;
        }

        var sql = $"SELECT COUNT(*) FROM {_callTable} WHERE FlowName = @FlowName AND State = 'Going'";
        var count = await conn.ExecuteScalarAsync<long>(sql, new { FlowName = flowName });
        return count > 0;
    }

    /// <summary>
    /// 현재 dspCall.State = 'Going' 인 모든 Call (CallId/CallName/FlowName). 상태 self-heal reconcile 용 —
    /// 호출 측이 엔진 in-memory 상태와 대조해 발산(DB=Going, 엔진=non-Going)을 교정한다.
    /// </summary>
    public async Task<List<GoingCallInfo>> GetGoingCallsAsync()
    {
        if (!_enabled) return new List<GoingCallInfo>();

        await using var conn = await OpenAsync();
        if (!await FlowAndCallTablesExistAsync(conn, _flowTable, _callTable))
            return new List<GoingCallInfo>();

        var sql = $"SELECT CallId, CallName, FlowName FROM {_callTable} WHERE State = 'Going'";
        var rows = await conn.QueryAsync<GoingCallInfo>(sql);
        return rows.ToList();
    }

    public async Task<bool> UpdateFlowMetricsAsync(
        string flowName,
        int? mt,
        int? wt,
        int? ct,
        string? movingStartName,
        string? movingEndName)
    {
        if (!_enabled) return false;

        await using var conn = await OpenAsync();

        var sql = $@"
            UPDATE {_flowTable}
            SET MT = @MT,
                WT = @WT,
                CT = @CT,
                MovingStartName = @MovingStartName,
                MovingEndName = @MovingEndName,
                UpdatedAt = datetime('now')
            WHERE FlowName = @FlowName";

        var result = await conn.ExecuteAsync(sql, new
        {
            MT = mt,
            WT = wt,
            CT = ct,
            MovingStartName = movingStartName,
            MovingEndName = movingEndName,
            FlowName = flowName,
        });
        return result > 0;
    }

    public async Task<bool> UpdateFlowCycleBoundariesAsync(
        string flowName,
        string? movingStartName,
        string? movingEndName)
    {
        if (!_enabled) return false;

        await using var conn = await OpenAsync();

        var sql = $@"
            UPDATE {_flowTable}
            SET MovingStartName = @MovingStartName,
                MovingEndName = @MovingEndName,
                UpdatedAt = datetime('now')
            WHERE FlowName = @FlowName";

        var result = await conn.ExecuteAsync(sql, new
        {
            MovingStartName = movingStartName,
            MovingEndName = movingEndName,
            FlowName = flowName,
        });
        return result > 0;
    }

    public async Task<bool> ClearAllDataAsync()
    {
        if (!_enabled) return true;

        try
        {
            await using var conn = await OpenAsync();
            using var tx = conn.BeginTransaction();

            var historyExists = await TableExistsAsync(conn, HistoryTable);
            if (historyExists)
            {
                await conn.ExecuteAsync($"DELETE FROM {HistoryTable}", transaction: tx);
            }

            await conn.ExecuteAsync($"DELETE FROM {_callTable}", transaction: tx);
            await conn.ExecuteAsync($"DELETE FROM {_flowTable}", transaction: tx);

            tx.Commit();

            _logger.LogInformation("Cleared all data from DSP database");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear DSP database");
            return false;
        }
    }

    public Task CleanupDatabaseAsync() => Task.CompletedTask;

    public async Task<List<CallStatisticsDto>> GetCallStatisticsAsync()
    {
        if (!_enabled) return new List<CallStatisticsDto>();

        await using var conn = await OpenAsync();
        if (!await FlowAndCallTablesExistAsync(conn, _flowTable, _callTable))
        {
            _logger.LogDebug("Tables do not exist yet, returning empty list");
            return new List<CallStatisticsDto>();
        }

        var sql = $@"
            SELECT
                CallId,
                CallName,
                FlowName,
                WorkName,
                AverageGoingTime,
                StdDevGoingTime,
                GoingCount
            FROM {_callTable}
            WHERE GoingCount > 0
              AND AverageGoingTime IS NOT NULL
              AND StdDevGoingTime IS NOT NULL
            ORDER BY FlowName, CallName";

        var results = await conn.QueryAsync<CallStatisticsDto>(sql);
        return results.ToList();
    }

    // ===== CallId 기반 API =====

    public async Task<string> GetCallStateAsync(Guid callId)
    {
        if (!_enabled) return "Ready";

        await using var conn = await OpenAsync();
        if (!await FlowAndCallTablesExistAsync(conn, _flowTable, _callTable))
        {
            _logger.LogDebug("Tables do not exist yet, returning default state");
            return "Ready";
        }

        var sql = $"SELECT State FROM {_callTable} WHERE CallId = @CallId LIMIT 1";
        var state = await conn.QueryFirstOrDefaultAsync<string>(sql, new { CallId = callId });
        return string.IsNullOrEmpty(state) ? "Ready" : state;
    }

    public async Task<(string WorkName, string FlowName)?> GetCallInfoAsync(Guid callId)
    {
        if (!_enabled) return null;

        await using var conn = await OpenAsync();
        if (!await FlowAndCallTablesExistAsync(conn, _flowTable, _callTable))
        {
            _logger.LogDebug("Tables do not exist yet");
            return null;
        }

        var sql = $"SELECT WorkName, FlowName FROM {_callTable} WHERE CallId = @CallId LIMIT 1";
        var result = await conn.QueryFirstOrDefaultAsync<CallInfoRow>(sql, new { CallId = callId });
        return result is null ? null : (result.WorkName, result.FlowName);
    }

    public async Task<DspCallEntity?> GetCallByIdAsync(Guid callId)
    {
        if (!_enabled) return null;

        await using var conn = await OpenAsync();
        if (!await FlowAndCallTablesExistAsync(conn, _flowTable, _callTable))
        {
            _logger.LogDebug("Tables do not exist yet");
            return null;
        }

        var sql = $@"
            SELECT CallId, CallName, ApiCall, WorkName, FlowName, Next, Prev, AutoPre, CommonPre,
                   State, ProgressRate, Device, PreviousGoingTime, AverageGoingTime, StdDevGoingTime, GoingCount
            FROM {_callTable}
            WHERE CallId = @CallId
            LIMIT 1";

        return await conn.QueryFirstOrDefaultAsync<DspCallEntity>(sql, new { CallId = callId });
    }

    public async Task<bool> UpdateCallStateAsync(Guid callId, string state)
    {
        if (!_enabled) return false;

        await using var conn = await OpenAsync();
        if (!await FlowAndCallTablesExistAsync(conn, _flowTable, _callTable))
        {
            _logger.LogDebug("Tables do not exist yet, skipping update");
            return false;
        }

        var sql = $@"
            UPDATE {_callTable}
            SET State = @State,
                UpdatedAt = datetime('now')
            WHERE CallId = @CallId";

        var result = await conn.ExecuteAsync(sql, new { State = state, CallId = callId });
        return result > 0;
    }

    public async Task<bool> UpdateCallWithStatisticsAsync(
        Guid callId,
        string state,
        int previousGoingTime,
        double averageGoingTime,
        double stdDevGoingTime)
    {
        if (!_enabled) return false;

        await using var conn = await OpenAsync();
        if (!await FlowAndCallTablesExistAsync(conn, _flowTable, _callTable))
        {
            _logger.LogDebug("Tables do not exist yet, skipping update");
            return false;
        }

        var sql = $@"
            UPDATE {_callTable}
            SET State = @State,
                PreviousGoingTime = @PreviousGoingTime,
                AverageGoingTime = @AverageGoingTime,
                StdDevGoingTime = @StdDevGoingTime,
                GoingCount = GoingCount + 1,
                UpdatedAt = datetime('now')
            WHERE CallId = @CallId";

        var result = await conn.ExecuteAsync(sql, new
        {
            State = state,
            PreviousGoingTime = previousGoingTime,
            AverageGoingTime = averageGoingTime,
            StdDevGoingTime = stdDevGoingTime,
            CallId = callId,
        });

        if (result > 0)
        {
            _logger.LogDebug(
                "Updated Call (CallId: {CallId}): State={State}, GoingTime={Time}ms, Avg={Avg:F0}ms, StdDev={StdDev:F0}ms",
                callId, state, previousGoingTime, averageGoingTime, stdDevGoingTime);
        }

        return result > 0;
    }

    // ===== Flow Metrics with Averages =====

    public async Task<bool> UpdateFlowWithAveragesAsync(
        string flowName,
        int mt,
        int wt,
        int ct,
        double avgMT,
        double avgWT,
        double avgCT,
        string? movingStartName,
        string? movingEndName)
    {
        if (!_enabled) return false;

        await using var conn = await OpenAsync();

        var sql = $@"
            UPDATE {_flowTable}
            SET MT = @MT,
                WT = @WT,
                CT = @CT,
                AvgMT = @AvgMT,
                AvgWT = @AvgWT,
                AvgCT = @AvgCT,
                MovingStartName = @MovingStartName,
                MovingEndName = @MovingEndName,
                UpdatedAt = datetime('now')
            WHERE FlowName = @FlowName";

        var result = await conn.ExecuteAsync(sql, new
        {
            MT = mt,
            WT = wt,
            CT = ct,
            AvgMT = avgMT,
            AvgWT = avgWT,
            AvgCT = avgCT,
            MovingStartName = movingStartName,
            MovingEndName = movingEndName,
            FlowName = flowName,
        });
        return result > 0;
    }

    // ===== Flow History =====

    public async Task<int> InsertFlowHistoryAsync(DspFlowHistoryEntity history)
    {
        if (!_enabled) return 0;

        await using var conn = await OpenAsync();

        if (!await TableExistsAsync(conn, HistoryTable))
        {
            _logger.LogWarning("{Table} table does not exist yet", HistoryTable);
            return 0;
        }

        await EnsureIsIdleColumnAsync(conn);

        try
        {
            var sql = $@"
                INSERT INTO {HistoryTable} (FlowName, MT, WT, CT, CycleNo, RecordedAt, IsIdle, HeadCallName, TailCallName)
                VALUES (@FlowName, @MT, @WT, @CT, @CycleNo, @RecordedAt, @IsIdle, @HeadCallName, @TailCallName)";

            var result = await conn.ExecuteAsync(sql, new
            {
                history.FlowName,
                history.MT,
                history.WT,
                history.CT,
                history.CycleNo,
                RecordedAt = history.RecordedAt == default ? DateTime.UtcNow : history.RecordedAt,
                history.IsIdle,
                history.HeadCallName,
                history.TailCallName,
            });

            _logger.LogDebug(
                "Inserted Flow history for '{FlowName}': Cycle={CycleNo}, MT={MT}ms, WT={WT}ms, CT={CT}ms, head={Head}, tail={Tail}",
                history.FlowName, history.CycleNo, history.MT, history.WT, history.CT,
                history.HeadCallName, history.TailCallName);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to insert Flow history for '{FlowName}'", history.FlowName);
            return 0;
        }
    }

    /// <summary>
    /// 한 Flow 의 [<paramref name="fromUtc"/>, <paramref name="toUtc"/>) (RecordedAt 기준, UTC) history 행을
    /// 통째로 <paramref name="rows"/> 로 교체한다(delete-by-range + bulk insert, 단일 트랜잭션).
    /// Head/Tail 경계 변경 시 원시 plcTagLog 로부터 재도출한 사이클을 과거 구간에 덮어쓰기 위한 용도.
    /// dspFlowHistory 는 원시 로그의 파생 캐시로 취급하므로(원시 로그 = 진짜 아카이브) 제자리 덮어쓰기가 안전.
    /// <para>주의: <paramref name="fromUtc"/>/<paramref name="toUtc"/> 와 각 row.RecordedAt 은 라이브 경로
    /// (DateTime.UtcNow 저장)와 동일하게 <b>UTC 벽시계(Kind=Utc)</b> 여야 비교/저장 포맷이 일치한다.</para>
    /// </summary>
    public async Task<(int Deleted, int Inserted)> ReplaceFlowHistoryRangeAsync(
        string flowName, DateTime fromUtc, DateTime toUtc, IReadOnlyList<DspFlowHistoryEntity> rows)
    {
        if (!_enabled) return (0, 0);
        if (string.IsNullOrWhiteSpace(flowName)) return (0, 0);

        await using var conn = await OpenAsync();
        if (!await TableExistsAsync(conn, HistoryTable)) return (0, 0);
        await EnsureIsIdleColumnAsync(conn);

        using var tx = conn.BeginTransaction();
        try
        {
            var deleted = await conn.ExecuteAsync(
                $@"DELETE FROM {HistoryTable}
                   WHERE FlowName = @FlowName
                     AND RecordedAt >= @FromUtc
                     AND RecordedAt <  @ToUtc",
                new { FlowName = flowName, FromUtc = fromUtc, ToUtc = toUtc }, transaction: tx);

            int inserted = 0;
            if (rows.Count > 0)
            {
                // Dapper: IEnumerable 파라미터 → 트랜잭션 안에서 항목당 1회 실행(배치).
                var insertSql = $@"
                    INSERT INTO {HistoryTable} (FlowName, MT, WT, CT, CycleNo, RecordedAt, IsIdle, HeadCallName, TailCallName)
                    VALUES (@FlowName, @MT, @WT, @CT, @CycleNo, @RecordedAt, @IsIdle, @HeadCallName, @TailCallName)";
                inserted = await conn.ExecuteAsync(insertSql, rows, transaction: tx);
            }

            tx.Commit();
            _logger.LogInformation(
                "ReplaceFlowHistoryRange '{Flow}' [{From:o}, {To:o}): deleted={Deleted}, inserted={Inserted}",
                flowName, fromUtc, toUtc, deleted, inserted);
            return (deleted, inserted);
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.LogError(ex, "ReplaceFlowHistoryRangeAsync 실패 ({Flow})", flowName);
            throw;
        }
    }

    public async Task<List<DspFlowHistoryEntity>> GetFlowHistoryAsync(string flowName, int limit)
    {
        if (!_enabled) return new List<DspFlowHistoryEntity>();

        await using var conn = await OpenAsync();

        if (!await TableExistsAsync(conn, HistoryTable))
            return new List<DspFlowHistoryEntity>();

        try
        {
            await EnsureIsIdleColumnAsync(conn);

            var sql = $@"
                SELECT Id, FlowName, MT, WT, CT, CycleNo, RecordedAt, COALESCE(IsIdle, 0) AS IsIdle,
                       HeadCallName, TailCallName
                FROM {HistoryTable}
                WHERE FlowName = @FlowName
                ORDER BY RecordedAt DESC
                LIMIT @Limit";

            var results = await conn.QueryAsync<DspFlowHistoryEntity>(sql, new { FlowName = flowName, Limit = limit });
            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Flow history for '{FlowName}'", flowName);
            return new List<DspFlowHistoryEntity>();
        }
    }

    public async Task<List<DspFlowHistoryEntity>> GetFlowHistoryByDaysAsync(string flowName, int days)
    {
        if (!_enabled) return new List<DspFlowHistoryEntity>();

        await using var conn = await OpenAsync();

        if (!await TableExistsAsync(conn, HistoryTable))
            return new List<DspFlowHistoryEntity>();

        try
        {
            await EnsureIsIdleColumnAsync(conn);

            var sql = $@"
                SELECT Id, FlowName, MT, WT, CT, CycleNo, RecordedAt, COALESCE(IsIdle, 0) AS IsIdle,
                       HeadCallName, TailCallName
                FROM {HistoryTable}
                WHERE FlowName = @FlowName
                  AND RecordedAt >= @SinceDate
                ORDER BY RecordedAt DESC";

            var sinceDate = DateTime.UtcNow.AddDays(-days);
            var results = await conn.QueryAsync<DspFlowHistoryEntity>(sql, new { FlowName = flowName, SinceDate = sinceDate });
            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Flow history by days for '{FlowName}'", flowName);
            return new List<DspFlowHistoryEntity>();
        }
    }

    public async Task<List<DspFlowHistoryEntity>> GetFlowHistoryByStartTimeAsync(string flowName, DateTime startTime)
    {
        if (!_enabled) return new List<DspFlowHistoryEntity>();

        await using var conn = await OpenAsync();

        if (!await TableExistsAsync(conn, HistoryTable))
            return new List<DspFlowHistoryEntity>();

        try
        {
            await EnsureIsIdleColumnAsync(conn);

            var sql = $@"
                SELECT Id, FlowName, MT, WT, CT, CycleNo, RecordedAt, COALESCE(IsIdle, 0) AS IsIdle,
                       HeadCallName, TailCallName
                FROM {HistoryTable}
                WHERE FlowName = @FlowName
                  AND RecordedAt >= @SinceDate
                ORDER BY RecordedAt DESC";

            var results = await conn.QueryAsync<DspFlowHistoryEntity>(sql, new { FlowName = flowName, SinceDate = startTime });
            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Flow history by start time for '{FlowName}'", flowName);
            return new List<DspFlowHistoryEntity>();
        }
    }

    /// <summary>
    /// dspCall 의 통계 컬럼만 reset (Welford 누적기 fresh 상태로).
    /// 사용처: Flow 히스토리 클리어 시 통계도 함께 초기화.
    /// </summary>
    public async Task<int> ResetCallStatisticsAsync()
    {
        if (!_enabled) return 0;

        try
        {
            await using var conn = await OpenAsync();
            if (!await TableExistsAsync(conn, _callTable))
                return 0;

            var sql = $@"
                UPDATE {_callTable}
                SET PreviousGoingTime = NULL,
                    AverageGoingTime = NULL,
                    StdDevGoingTime = NULL,
                    GoingCount = 0,
                    UpdatedAt = datetime('now')";
            var rows = await conn.ExecuteAsync(sql);
            _logger.LogInformation("Reset statistics on {Count} dspCall rows", rows);
            return rows;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset call statistics");
            return 0;
        }
    }

    /// <summary>
    /// retainFlowNames 에 없는 모든 dspFlow / dspCall / dspFlowHistory 행을 삭제.
    /// AASX 에서 사라진(삭제/리네임된) Flow 의 잔존 행 정리용 — 살아남은 Flow 의 통계 / 히스토리는 보존.
    /// retainFlowNames 가 비어 있으면(=실수 방지 안전장치) prune 을 수행하지 않고 (0,0,0) 을 반환한다.
    /// </summary>
    public async Task<(int Flows, int Calls, int History)> PruneByFlowNamesAsync(IEnumerable<string> retainFlowNames)
    {
        if (!_enabled) return (0, 0, 0);

        var retain = retainFlowNames?
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .ToList() ?? new List<string>();

        // 빈 retain 집합으로 전체 삭제하는 동작은 RebuildDatabaseAsync 의 영역. 여기서는 가드.
        if (retain.Count == 0)
        {
            _logger.LogDebug("PruneByFlowNamesAsync: retain 비어있음 — no-op");
            return (0, 0, 0);
        }

        await using var conn = await OpenAsync();
        if (!await FlowAndCallTablesExistAsync(conn, _flowTable, _callTable))
            return (0, 0, 0);

        using var tx = conn.BeginTransaction();
        try
        {
            var param = new { Names = retain };

            var callsDeleted = await conn.ExecuteAsync(
                $"DELETE FROM {_callTable} WHERE FlowName NOT IN @Names",
                param, transaction: tx);
            var flowsDeleted = await conn.ExecuteAsync(
                $"DELETE FROM {_flowTable} WHERE FlowName NOT IN @Names",
                param, transaction: tx);

            var historyDeleted = 0;
            if (await TableExistsAsync(conn, HistoryTable))
            {
                historyDeleted = await conn.ExecuteAsync(
                    $"DELETE FROM {HistoryTable} WHERE FlowName NOT IN @Names",
                    param, transaction: tx);
            }

            tx.Commit();

            if (flowsDeleted + callsDeleted + historyDeleted > 0)
                _logger.LogInformation("Pruned stale rows: dspFlow={Flows}, dspCall={Calls}, dspFlowHistory={History}",
                    flowsDeleted, callsDeleted, historyDeleted);

            return (flowsDeleted, callsDeleted, historyDeleted);
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.LogError(ex, "Failed to prune stale rows by Flow names");
            throw;
        }
    }

    public async Task<int> ClearFlowHistoryAsync()
    {
        if (!_enabled) return 0;

        await using var conn = await OpenAsync();

        if (!await TableExistsAsync(conn, HistoryTable))
        {
            _logger.LogWarning("{Table} table does not exist, nothing to clear", HistoryTable);
            return 0;
        }

        try
        {
            var deleted = await conn.ExecuteAsync($"DELETE FROM {HistoryTable}");
            _logger.LogInformation("Cleared {Count} rows from {Table}", deleted, HistoryTable);
            return deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear {Table}", HistoryTable);
            return 0;
        }
    }

    /// <summary>
    /// 비가동 임계값을 기존 히스토리/평균에 소급 적용.
    /// <para>
    /// 비가동 판정(IsIdle)은 원래 사이클이 기록되는 시점에 한 번만 계산되어 행에 박제되므로,
    /// 설정의 Max/MinCycleTimeMs 를 나중에 바꿔도 이미 저장된 행에는 반영되지 않는다.
    /// 이 메서드는 ① 모든 dspFlowHistory 행의 IsIdle 을 현재 임계값 기준으로 재계산하고,
    /// ② dspFlow 의 평균(AvgMT/WT/CT)과 현재값(MT/WT/CT)을 "비가동 제외" 기준으로 재집계한다.
    /// </para>
    /// 임계값이 0(비활성)이면 해당 방향 판정은 적용하지 않으므로, 둘 다 0이면 모든 행이 가동(IsIdle=0)으로 복원된다.
    /// </summary>
    /// <param name="maxCycleTimeMs">글로벌 기본 최대 CT(ms). per-flow override 가 없는 Flow 에 적용.</param>
    /// <param name="minCycleTimeMs">글로벌 기본 최소 CT(ms).</param>
    /// <summary>
    /// 상관 sub-select 로 "flowName 의 최근 <paramref name="lim"/>개 비가동 사이클 평균(<paramref name="col"/>)"을
    /// 내는 SQL 조각. <paramref name="predicate"/> 는 바깥 dspFlow 행을 참조한다
    /// (예: <c>h.flowName = dspFlow.flowName AND COALESCE(h.IsIdle,0)=0</c>).
    /// <paramref name="lim"/> &lt; 0 이면 전체 이력(SQLite <c>LIMIT -1</c> = 무제한). AVG 는 NULL 을 무시한다.
    /// </summary>
    private static string WindowedAvg(string col, string predicate, int lim) =>
        $@"(SELECT AVG(x.v) FROM (SELECT h.{col} AS v FROM {HistoryTable} h
                WHERE {predicate} ORDER BY h.recordedAt DESC, h.id DESC LIMIT {lim}) x)";

    /// <param name="perFlowRangesMs">per-flow 유효범위(ms) override 맵 — 글로벌 재스탬프 위에 해당 Flow 만 덮어쓴다.</param>
    /// <param name="averageWindow">Avg 산출 시 집계할 최근 비가동 사이클 수(롤링 윈도우). 0/음수 = 전체 이력.</param>
    /// <returns>(재평가된 히스토리 행 수, 재집계된 Flow 수)</returns>
    public async Task<(int HistoryRestamped, int FlowsRecomputed)> ReapplyIdleThresholdsAsync(
        int maxCycleTimeMs,
        int minCycleTimeMs,
        IReadOnlyDictionary<string, (int MaxMs, int MinMs)>? perFlowRangesMs = null,
        int averageWindow = 0)
    {
        if (!_enabled) return (0, 0);

        await using var conn = await OpenAsync();
        if (!await TableExistsAsync(conn, HistoryTable))
            return (0, 0);

        await EnsureIsIdleColumnAsync(conn);

        using var tx = conn.BeginTransaction();
        try
        {
            // 1. 모든 히스토리 행의 IsIdle 을 글로벌 임계값 기준으로 재계산 (기본).
            var restampSql = $@"
                UPDATE {HistoryTable}
                SET IsIdle = CASE
                    WHEN (@MaxCT > 0 AND ct > @MaxCT) OR (@MinCT > 0 AND ct < @MinCT) THEN 1
                    ELSE 0
                END";
            var restamped = await conn.ExecuteAsync(
                restampSql, new { MaxCT = maxCycleTimeMs, MinCT = minCycleTimeMs }, tx);

            // 1-b. per-flow override 가 있는 Flow 는 그 유효범위로 다시 재계산(글로벌 위에 덮어쓰기).
            //      → "글로벌=기본, per-flow=override" 단일 유효범위를 IsIdle 에 박제. 명시 안 된 Flow 는 1 의 글로벌 유지.
            if (perFlowRangesMs is { Count: > 0 })
            {
                var overrideSql = $@"
                    UPDATE {HistoryTable}
                    SET IsIdle = CASE
                        WHEN (@MaxCT > 0 AND ct > @MaxCT) OR (@MinCT > 0 AND ct < @MinCT) THEN 1
                        ELSE 0
                    END
                    WHERE flowName = @FlowName";
                foreach (var kv in perFlowRangesMs)
                {
                    await conn.ExecuteAsync(
                        overrideSql,
                        new { MaxCT = kv.Value.MaxMs, MinCT = kv.Value.MinMs, FlowName = kv.Key },
                        tx);
                }
            }

            // 2. dspFlow 평균을 비가동 제외 후 재집계 (NULL = 가용 사이클 없음).
            //    averageWindow>0 이면 최근 N 사이클만 평균(롤링 윈도우) — 요약 대시보드가 현재 거동을 반영.
            int lim = averageWindow > 0 ? averageWindow : -1; // SQLite LIMIT -1 = 무제한(전체 이력)
            string fullPred = $"h.flowName = {_flowTable}.flowName AND COALESCE(h.IsIdle,0)=0";
            var avgSql = $@"
                UPDATE {_flowTable}
                SET AvgMT = {WindowedAvg("mt", fullPred, lim)},
                    AvgWT = {WindowedAvg("wt", fullPred, lim)},
                    AvgCT = {WindowedAvg("ct", fullPred, lim)},
                    UpdatedAt = datetime('now')";
            var flowsRecomputed = await conn.ExecuteAsync(avgSql, transaction: tx);

            // 3. dspFlow 현재값(MT/WT/CT)을 가장 최근 비가동 사이클로 갱신.
            //    가용 사이클이 하나도 없는 Flow 는 현재값을 건드리지 않는다.
            var lastSql = $@"
                UPDATE {_flowTable}
                SET MT = (SELECT mt FROM {HistoryTable} h WHERE h.flowName = {_flowTable}.flowName AND COALESCE(h.IsIdle,0)=0 ORDER BY h.recordedAt DESC, h.id DESC LIMIT 1),
                    WT = (SELECT wt FROM {HistoryTable} h WHERE h.flowName = {_flowTable}.flowName AND COALESCE(h.IsIdle,0)=0 ORDER BY h.recordedAt DESC, h.id DESC LIMIT 1),
                    CT = (SELECT ct FROM {HistoryTable} h WHERE h.flowName = {_flowTable}.flowName AND COALESCE(h.IsIdle,0)=0 ORDER BY h.recordedAt DESC, h.id DESC LIMIT 1),
                    UpdatedAt = datetime('now')
                WHERE EXISTS (SELECT 1 FROM {HistoryTable} h WHERE h.flowName = {_flowTable}.flowName AND COALESCE(h.IsIdle,0)=0)";
            await conn.ExecuteAsync(lastSql, transaction: tx);

            tx.Commit();

            _logger.LogInformation(
                "Reapplied idle thresholds (global Max={MaxCT}ms, Min={MinCT}ms, {Overrides} per-flow override(s)): {Rows} history rows restamped, {Flows} flows recomputed",
                maxCycleTimeMs, minCycleTimeMs, perFlowRangesMs?.Count ?? 0, restamped, flowsRecomputed);

            return (restamped, flowsRecomputed);
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.LogError(ex, "Failed to reapply idle thresholds");
            throw;
        }
    }

    /// <summary>
    /// Flow 별 "비가동 제외" 최근 사이클(MT/WT/CT, 오래된→최신 순) — in-memory 롤링 평균 상태 재시드용.
    /// <paramref name="window"/> &gt; 0 이면 flow 당 최근 N 개만, 0/음수면 전체. MT/WT/CT 가 모두 채워진 행만
    /// (라이브 누산기가 정수 삼중쌍으로 평균을 굴리므로). <paramref name="byCurrentBoundary"/> 가 true 면 현재
    /// boundary 와 일치하는 행만(없으면 flow 별 전체로 폴백) — <see cref="RecomputeAveragesFromCurrentBoundaryAsync"/> 의
    /// 평균 폴백과 동일 기준이라 누산기와 dspFlow.Avg* 가 정합.
    /// </summary>
    public async Task<Dictionary<string, List<CycleSample>>> GetRecentNonIdleCyclesAsync(int window, bool byCurrentBoundary)
    {
        var result = new Dictionary<string, List<CycleSample>>(StringComparer.Ordinal);
        if (!_enabled) return result;

        await using var conn = await OpenAsync();
        if (!await TableExistsAsync(conn, HistoryTable)) return result;
        if (byCurrentBoundary && !await TableExistsAsync(conn, _flowTable)) return result;

        await EnsureIsIdleColumnAsync(conn);

        // ROW_NUMBER 로 flow 당 최신순 번호를 매겨 최근 N 개만 추린다(SQLite 3.25+ window function).
        // 최종 정렬은 rn DESC → flow 별 오래된→최신 순(Queue 에 enqueue 하면 최신이 tail).
        string boundaryJoin = byCurrentBoundary
            ? $@"INNER JOIN {_flowTable} f ON f.flowName = h.flowName"
            : "";
        // boundary 매칭: head/tail 이 NULL 이면 일치로 간주(박제 이전). 매칭 0행 flow 는 아래 폴백으로 채운다.
        string boundaryWhere = byCurrentBoundary
            ? @" AND (h.headCallName IS NULL OR f.movingStartName = (f.flowName || '.' || h.headCallName))
                 AND (h.tailCallName IS NULL OR f.movingEndName   = (f.flowName || '.' || h.tailCallName))"
            : "";

        string Build(string join, string extraWhere) => $@"
            SELECT FlowName, MT, WT, CT FROM (
                SELECT h.flowName AS FlowName, h.mt AS MT, h.wt AS WT, h.ct AS CT,
                       ROW_NUMBER() OVER (PARTITION BY h.flowName ORDER BY h.recordedAt DESC, h.id DESC) AS rn
                FROM {HistoryTable} h {join}
                WHERE COALESCE(h.IsIdle, 0) = 0
                  AND h.mt IS NOT NULL AND h.wt IS NOT NULL AND h.ct IS NOT NULL{extraWhere}
            )
            WHERE (@Win <= 0 OR rn <= @Win)
            ORDER BY FlowName, rn DESC";

        void Fill(IEnumerable<RecentCycleRow> rows, Dictionary<string, List<CycleSample>> into)
        {
            into.Clear();
            foreach (var r in rows)
            {
                if (string.IsNullOrEmpty(r.FlowName)) continue;
                if (!into.TryGetValue(r.FlowName, out var list))
                    into[r.FlowName] = list = new List<CycleSample>();
                list.Add(new CycleSample(r.MT, r.WT, r.CT));
            }
        }

        // 전체(경계 무관) — 폴백 베이스.
        Fill(await conn.QueryAsync<RecentCycleRow>(Build("", ""), new { Win = window }), result);

        // 경계매칭 — 행이 있는 flow 만 override(경계변경 의미 보존). 0행 flow 는 위 전체집계 유지.
        if (byCurrentBoundary)
        {
            var boundary = new Dictionary<string, List<CycleSample>>(StringComparer.Ordinal);
            Fill(await conn.QueryAsync<RecentCycleRow>(Build(boundaryJoin, boundaryWhere), new { Win = window }), boundary);
            foreach (var kv in boundary)
                if (kv.Value.Count > 0) result[kv.Key] = kv.Value;
        }
        return result;
    }

    private sealed class RecentCycleRow
    {
        public string FlowName { get; set; } = string.Empty;
        public int MT { get; set; }
        public int WT { get; set; }
        public int CT { get; set; }
    }

    private sealed class CallInfoRow
    {
        public string WorkName { get; set; } = string.Empty;
        public string FlowName { get; set; } = string.Empty;
    }

    // ===== Cache invalidate (raw 보존, derived 만 reset) =====

    /// <summary>
    /// dspFlow 의 현재값/평균 + dspCall 의 누적 통계를 NULL/0 으로 reset.
    /// raw 테이블(plcTagLog / plcTag / userTagAlertLog / dspFlowHistory) 은 손대지 않음.
    /// head/tail boundary 가 바뀌었거나 모델이 크게 바뀌어 평균을 새 baseline 으로 다시 누적해야 할 때 사용.
    /// </summary>
    public async Task<(int FlowsReset, int CallsReset)> InvalidateRunningStatsAsync()
    {
        if (!_enabled) return (0, 0);

        await using var conn = await OpenAsync();
        using var tx = conn.BeginTransaction();
        try
        {
            var flowsReset = 0;
            if (await TableExistsAsync(conn, _flowTable))
            {
                var sql = $@"
                    UPDATE {_flowTable}
                    SET MT = NULL, WT = NULL, CT = NULL,
                        AvgMT = NULL, AvgWT = NULL, AvgCT = NULL,
                        UpdatedAt = datetime('now')";
                flowsReset = await conn.ExecuteAsync(sql, transaction: tx);
            }

            var callsReset = 0;
            if (await TableExistsAsync(conn, _callTable))
            {
                var sql = $@"
                    UPDATE {_callTable}
                    SET PreviousGoingTime = NULL,
                        AverageGoingTime = NULL,
                        StdDevGoingTime = NULL,
                        GoingCount = 0,
                        UpdatedAt = datetime('now')";
                callsReset = await conn.ExecuteAsync(sql, transaction: tx);
            }

            tx.Commit();
            _logger.LogInformation(
                "Invalidated running stats — Flow {Flows}, Call {Calls} (raw 보존, dspFlowHistory 도 보존)",
                flowsReset, callsReset);
            return (flowsReset, callsReset);
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.LogError(ex, "InvalidateRunningStatsAsync 실패");
            throw;
        }
    }

    // ===== Audit logs =====

    public async Task<long> InsertAasxChangeLogAsync(
        string? sha256Before,
        string sha256After,
        string source,
        IEnumerable<string>? flowsAdded,
        IEnumerable<string>? flowsRemoved,
        int pruneFlows,
        int pruneCalls,
        int pruneHistory,
        string? notes = null)
    {
        if (!_enabled) return 0;

        try
        {
            await using var conn = await OpenAsync();
            if (!await TableExistsAsync(conn, "aasxChangeLog"))
                return 0;

            var sql = @"
                INSERT INTO aasxChangeLog
                  (changedAt, sha256Before, sha256After, source,
                   flowsAddedJson, flowsRemovedJson,
                   pruneFlows, pruneCalls, pruneHistory, notes)
                VALUES
                  (@ChangedAt, @ShaBefore, @ShaAfter, @Source,
                   @AddedJson, @RemovedJson,
                   @PruneFlows, @PruneCalls, @PruneHistory, @Notes);
                SELECT last_insert_rowid();";

            var id = await conn.ExecuteScalarAsync<long>(sql, new
            {
                ChangedAt = DateTime.UtcNow,
                ShaBefore = sha256Before,
                ShaAfter = sha256After,
                Source = source,
                AddedJson = flowsAdded is null ? null : System.Text.Json.JsonSerializer.Serialize(flowsAdded),
                RemovedJson = flowsRemoved is null ? null : System.Text.Json.JsonSerializer.Serialize(flowsRemoved),
                PruneFlows = pruneFlows,
                PruneCalls = pruneCalls,
                PruneHistory = pruneHistory,
                Notes = notes,
            });
            _logger.LogInformation(
                "[audit] aasxChangeLog#{Id} source={Source} sha {Before}→{After}",
                id, source, sha256Before ?? "<none>", sha256After);
            return id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InsertAasxChangeLogAsync 실패 (비중요)");
            return 0;
        }
    }

    public async Task InsertFlowBoundaryChangeLogAsync(
        string flowName,
        string? headBefore, string? headAfter,
        string? tailBefore, string? tailAfter,
        string source,
        long? aasxChangeLogId = null,
        string? notes = null)
    {
        if (!_enabled) return;
        if (headBefore == headAfter && tailBefore == tailAfter) return; // no-op

        try
        {
            await using var conn = await OpenAsync();
            if (!await TableExistsAsync(conn, "flowBoundaryChangeLog"))
                return;

            await conn.ExecuteAsync(@"
                INSERT INTO flowBoundaryChangeLog
                  (changedAt, flowName, headBefore, headAfter, tailBefore, tailAfter, source, aasxChangeLogId, notes)
                VALUES
                  (@ChangedAt, @FlowName, @HeadB, @HeadA, @TailB, @TailA, @Source, @AasxId, @Notes)",
                new
                {
                    ChangedAt = DateTime.UtcNow,
                    FlowName = flowName,
                    HeadB = headBefore,
                    HeadA = headAfter,
                    TailB = tailBefore,
                    TailA = tailAfter,
                    Source = source,
                    AasxId = aasxChangeLogId,
                    Notes = notes,
                });
            _logger.LogInformation(
                "[audit] flowBoundaryChangeLog '{Flow}' head {HB}→{HA} tail {TB}→{TA} source={Source}",
                flowName, headBefore ?? "<none>", headAfter ?? "<none>",
                tailBefore ?? "<none>", tailAfter ?? "<none>", source);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InsertFlowBoundaryChangeLogAsync 실패 (비중요)");
        }
    }

    /// <summary>AASX 변경 이력 조회 (최신순). 연표 다이얼로그용.</summary>
    public async Task<IReadOnlyList<AasxChangeLogEntry>> GetAasxChangeLogAsync(int limit = 100)
    {
        if (!_enabled) return Array.Empty<AasxChangeLogEntry>();

        await using var conn = await OpenAsync();
        if (!await TableExistsAsync(conn, "aasxChangeLog"))
            return Array.Empty<AasxChangeLogEntry>();

        var rows = await conn.QueryAsync<AasxChangeLogRow>(
            "SELECT id, changedAt, source, notes FROM aasxChangeLog ORDER BY changedAt DESC LIMIT @Limit",
            new { Limit = limit });

        return rows.Select(r =>
        {
            var dt = SqliteDateTimeHelpers.FromSqliteUtcString(r.ChangedAt) ?? DateTime.MinValue;
            return new AasxChangeLogEntry(r.Id, dt, r.Source ?? "", r.Notes);
        }).ToList();
    }

    /// <summary>
    /// 지정 시각 이전 raw 데이터 삭제 (plcTagLog, userTagAlertLog, userTagAlertDaily, dspFlowHistory).
    /// 해당 시각 이후 데이터는 보존. 반환: (plcTagLog삭제, userTagAlertLog삭제, flowHistory삭제) 건수.
    /// </summary>
    public async Task<(int PlcTagLog, int UserTagAlertLog, int FlowHistory)> DeleteRawDataBeforeAsync(DateTime cutoffUtc)
    {
        if (!_enabled) return (0, 0, 0);

        var cutoffStr = SqliteDateTimeHelpers.ToSqliteUtcString(cutoffUtc);
        var cutoffDate = cutoffUtc.ToString("yyyy-MM-dd");

        await using var conn = await OpenAsync();
        using var tx = conn.BeginTransaction();
        try
        {
            var plcDeleted = await TableExistsAsync(conn, "plcTagLog")
                ? await conn.ExecuteAsync("DELETE FROM plcTagLog WHERE dateTime < @Cutoff",
                    new { Cutoff = cutoffStr }, tx)
                : 0;

            var alertDeleted = await TableExistsAsync(conn, "userTagAlertLog")
                ? await conn.ExecuteAsync("DELETE FROM userTagAlertLog WHERE occurredAt < @Cutoff",
                    new { Cutoff = cutoffStr }, tx)
                : 0;

            if (await TableExistsAsync(conn, "userTagAlertDaily"))
                await conn.ExecuteAsync("DELETE FROM userTagAlertDaily WHERE bucketDate < @CutoffDate",
                    new { CutoffDate = cutoffDate }, tx);

            var histDeleted = await TableExistsAsync(conn, HistoryTable)
                ? await conn.ExecuteAsync($"DELETE FROM {HistoryTable} WHERE recordedAt < @Cutoff",
                    new { Cutoff = cutoffStr }, tx)
                : 0;

            tx.Commit();
            _logger.LogInformation(
                "[DeleteRawDataBefore] cutoff={Cutoff} plcTagLog={P} userTagAlertLog={A} history={H}",
                cutoffStr, plcDeleted, alertDeleted, histDeleted);
            return (plcDeleted, alertDeleted, histDeleted);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private sealed class AasxChangeLogRow
    {
        public long Id { get; set; }
        public string? ChangedAt { get; set; }
        public string? Source { get; set; }
        public string? Notes { get; set; }
    }

    /// <summary>dspFlow 의 현재 boundary 조회 (변경 비교용).</summary>
    public async Task<(string? MovingStartName, string? MovingEndName)> GetFlowBoundariesAsync(string flowName)
    {
        if (!_enabled) return (null, null);
        await using var conn = await OpenAsync();
        if (!await TableExistsAsync(conn, _flowTable)) return (null, null);

        // Dapper 가 ValueTuple 매핑을 silently null 처리 — 명시적 DTO.
        var row = await conn.QueryFirstOrDefaultAsync<FlowBoundaryRow>(
            $"SELECT MovingStartName, MovingEndName FROM {_flowTable} WHERE FlowName = @FlowName",
            new { FlowName = flowName });
        return (row?.MovingStartName, row?.MovingEndName);
    }

    private sealed class FlowBoundaryRow
    {
        public string? MovingStartName { get; set; }
        public string? MovingEndName { get; set; }
    }

    // ===== Cache rebuild from history (옵션 A — 즉시 재집계) =====

    /// <summary>
    /// 각 Flow 의 현재 boundary(MovingStartName/MovingEndName) 와 일치하는 dspFlowHistory 행으로
    /// Avg{MT,WT,CT} 와 현재값(MT/WT/CT) 을 즉시 재집계.
    /// <para>
    /// 매칭 규칙:
    /// <list type="bullet">
    /// <item>history.HeadCallName 이 NULL → 보수적으로 현재 boundary 와 일치하는 것으로 간주 (박제 이전 데이터).</item>
    /// <item>history.HeadCallName 이 있음 → dspFlow.MovingStartName == flowName||"."||history.headCallName 일치 검사.</item>
    /// <item>비가동(IsIdle=1) 행은 제외.</item>
    /// </list>
    /// </para>
    /// InvalidateCachesAsync 끝에서 호출되어, NULL 로 비운 Avg* 를 boundary 박제 컬럼 기반으로 즉시 복원한다.
    /// </summary>
    /// <param name="averageWindow">Avg 산출 시 집계할 최근 비가동 사이클 수(롤링 윈도우). 0/음수 = 전체 이력.</param>
    public async Task<(int FlowsRecomputed, int HistoryRowsUsed)> RecomputeAveragesFromCurrentBoundaryAsync(int averageWindow = 0)
    {
        if (!_enabled) return (0, 0);

        await using var conn = await OpenAsync();
        if (!await TableExistsAsync(conn, HistoryTable)) return (0, 0);
        if (!await TableExistsAsync(conn, _flowTable)) return (0, 0);

        await EnsureIsIdleColumnAsync(conn);

        using var tx = conn.BeginTransaction();
        try
        {
            // boundary 매칭 predicate (h=history, 대상=dspFlow): dspFlow.{MovingStartName,MovingEndName}
            // == flowName||'.'||h.{headCallName,tailCallName}. (SQLite UPDATE FROM 미지원 → 상관 sub-select.)
            var boundaryMatch = $@"(h.headCallName IS NULL
                               OR {_flowTable}.movingStartName = ({_flowTable}.flowName || '.' || h.headCallName))
                          AND (h.tailCallName IS NULL
                               OR {_flowTable}.movingEndName = ({_flowTable}.flowName || '.' || h.tailCallName))";

            // 경계매칭 부분집합이 0행이면(현재 boundary 가 history 의 head/tail 과 어긋난 경우 — 경계 메타 불일치)
            // 경계조건을 떼고 전체 비가동 평균으로 폴백한다. → stale/오염된 평균(예: 라이브 누산기가 남긴 수십 분
            // 값)이 박제되지 않고, 항상 이상치(IsIdle) 필터만 적용한 평균을 보인다. 매칭 행이 1개라도 있으면 그
            // 부분집합을 그대로 사용해 경계변경 의미(새 경계로 측정된 사이클만 집계)를 보존한다.
            // averageWindow>0 이면 경계매칭/전체 폴백 각각 "최근 N 사이클"만 평균(롤링 윈도우).
            int lim = averageWindow > 0 ? averageWindow : -1; // SQLite LIMIT -1 = 무제한(전체 이력)
            string boundaryPred = $"h.flowName = {_flowTable}.flowName AND COALESCE(h.IsIdle, 0) = 0 AND {boundaryMatch}";
            string fullPred = $"h.flowName = {_flowTable}.flowName AND COALESCE(h.IsIdle, 0) = 0";
            string AvgFallback(string col) => $@"COALESCE(
                        {WindowedAvg(col, boundaryPred, lim)},
                        {WindowedAvg(col, fullPred, lim)})";
            var avgMt = AvgFallback("mt");
            var avgWt = AvgFallback("wt");
            var avgCt = AvgFallback("ct");
            var avgSql = $@"
                UPDATE {_flowTable}
                SET AvgMT = {avgMt},
                    AvgWT = {avgWt},
                    AvgCT = {avgCt},
                    UpdatedAt = datetime('now')";
            var flowsRecomputed = await conn.ExecuteAsync(avgSql, transaction: tx);

            // 현재값(MT/WT/CT) 도 매칭되는 가장 최근 사이클로 복원. 경계매칭이 0행이면 평균과 동일하게
            // 경계조건 없이 가장 최근 비가동 사이클로 폴백(매칭 행 있으면 그 최근값 유지).
            string LastFallback(string col) => $@"COALESCE(
                        (SELECT h.{col} FROM {HistoryTable} h
                          WHERE h.flowName = {_flowTable}.flowName AND COALESCE(h.IsIdle, 0) = 0 AND {boundaryMatch}
                          ORDER BY h.recordedAt DESC, h.id DESC LIMIT 1),
                        (SELECT h.{col} FROM {HistoryTable} h
                          WHERE h.flowName = {_flowTable}.flowName AND COALESCE(h.IsIdle, 0) = 0
                          ORDER BY h.recordedAt DESC, h.id DESC LIMIT 1))";
            var lastMt = LastFallback("mt");
            var lastWt = LastFallback("wt");
            var lastCt = LastFallback("ct");
            var lastSql = $@"
                UPDATE {_flowTable}
                SET MT = {lastMt},
                    WT = {lastWt},
                    CT = {lastCt}";
            await conn.ExecuteAsync(lastSql, transaction: tx);

            // 사용된 history 행 수 — UI 메시지용. 같은 boundary 매칭 조건으로 COUNT.
            var countSql = $@"
                SELECT COUNT(*)
                FROM {HistoryTable} h
                INNER JOIN {_flowTable} f ON f.flowName = h.flowName
                WHERE COALESCE(h.IsIdle, 0) = 0
                  AND (h.headCallName IS NULL OR f.movingStartName = (f.flowName || '.' || h.headCallName))
                  AND (h.tailCallName IS NULL OR f.movingEndName = (f.flowName || '.' || h.tailCallName))";
            var historyRowsUsed = await conn.ExecuteScalarAsync<int>(countSql, transaction: tx);

            tx.Commit();

            _logger.LogInformation(
                "Recomputed Flow averages from current boundary: {Flows} flows updated, {Rows} history rows used",
                flowsRecomputed, historyRowsUsed);

            return (flowsRecomputed, historyRowsUsed);
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.LogError(ex, "RecomputeAveragesFromCurrentBoundaryAsync 실패");
            throw;
        }
    }

    /// <summary>전체 dspFlow 의 FlowName 집합 — added/removed 계산용.</summary>
    public async Task<List<string>> GetAllFlowNamesAsync()
    {
        if (!_enabled) return new List<string>();
        await using var conn = await OpenAsync();
        if (!await TableExistsAsync(conn, _flowTable)) return new List<string>();
        var rows = await conn.QueryAsync<string>($"SELECT FlowName FROM {_flowTable}");
        return rows.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
    }
}
