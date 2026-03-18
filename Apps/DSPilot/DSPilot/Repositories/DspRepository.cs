using Dapper;
using DSPilot.Models.Dsp;
using Microsoft.Data.Sqlite;
using System.Data;

namespace DSPilot.Repositories;

/// <summary>
/// DSP 실시간 데이터베이스 저장소 - Dapper 기반 SQLite 구현
/// </summary>
public class DspRepository : IDspRepository
{
    private readonly string _connectionString;
    private readonly ILogger<DspRepository> _logger;

    public DspRepository(IConfiguration configuration, ILogger<DspRepository> logger)
    {
        _logger = logger;

        var dbPath = configuration["DspDatabase:Path"]
            ?? throw new InvalidOperationException("DspDatabase:Path is not configured");

        // 환경 변수 확장 (%APPDATA% 등)
        dbPath = Environment.ExpandEnvironmentVariables(dbPath);

        // 디렉토리 생성
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            _logger.LogInformation("Created database directory: {Directory}", directory);
        }

        _connectionString = $"Data Source={dbPath}";
        _logger.LogInformation("DSP Database path: {DbPath}", dbPath);
    }

    private SqliteConnection CreateConnection() => new SqliteConnection(_connectionString);

    /// <inheritdoc />
    public async Task<bool> CreateSchemaAsync()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            _logger.LogInformation("Creating DSP database schema...");

            // Schema validation and recreation for all tables
            await EnsureTableSchemaAsync(connection, "Flow", GetFlowColumns());
            await EnsureTableSchemaAsync(connection, "Call", GetCallColumns());

            // Create Flow table
            const string createFlowTable = @"
CREATE TABLE IF NOT EXISTS Flow (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    FlowName TEXT UNIQUE NOT NULL,
    MT INTEGER,
    WT INTEGER,
    CT INTEGER,
    State TEXT,
    MovingStartName TEXT,
    MovingEndName TEXT,
    CreatedAt TEXT DEFAULT (datetime('now')),
    UpdatedAt TEXT DEFAULT (datetime('now'))
);";
            await connection.ExecuteAsync(createFlowTable);

            // Create Call table (with statistics fields)
            const string createCallTable = @"
CREATE TABLE IF NOT EXISTS Call (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CallName TEXT NOT NULL,
    ApiCall TEXT NOT NULL,
    WorkName TEXT NOT NULL,
    FlowName TEXT NOT NULL,
    Next TEXT,
    Prev TEXT,
    AutoPre TEXT,
    CommonPre TEXT,
    State TEXT NOT NULL DEFAULT 'Ready',
    ProgressRate REAL DEFAULT 0.0,
    PreviousGoingTime INTEGER,
    AverageGoingTime REAL,
    StdDevGoingTime REAL,
    GoingCount INTEGER DEFAULT 0,
    Device TEXT,
    ErrorText TEXT,
    CreatedAt TEXT DEFAULT (datetime('now')),
    UpdatedAt TEXT DEFAULT (datetime('now')),
    UNIQUE(CallName, FlowName, WorkName),
    FOREIGN KEY(FlowName) REFERENCES Flow(FlowName) ON DELETE CASCADE
);";
            await connection.ExecuteAsync(createCallTable);

            // Clean up duplicate Call data if any exists
            await CleanupDuplicateCallsAsync(connection);

            // Create CallFlowView
            const string createView = @"
CREATE VIEW IF NOT EXISTS CallFlowView AS
SELECT
    c.Id,
    c.CallName,
    c.ApiCall,
    c.WorkName,
    c.FlowName,
    c.Next,
    c.Prev,
    c.AutoPre,
    c.CommonPre,
    c.State AS CallState,
    c.ProgressRate,
    c.PreviousGoingTime,
    c.AverageGoingTime,
    c.StdDevGoingTime,
    c.GoingCount,
    c.Device,
    c.ErrorText,
    f.MT,
    f.WT,
    f.CT,
    f.State AS FlowState,
    c.CreatedAt,
    c.UpdatedAt
FROM Call c
LEFT JOIN Flow f ON c.FlowName = f.FlowName
ORDER BY c.Id;";
            await connection.ExecuteAsync(createView);

            // Create Indexes
            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_flow_flowname ON Flow(FlowName);");
            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_flow_state ON Flow(State);");
            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_call_flowname ON Call(FlowName);");
            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_call_callname ON Call(CallName);");
            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_call_state ON Call(State);");

            // Create Triggers
            const string createFlowTrigger = @"
CREATE TRIGGER IF NOT EXISTS update_flow_updated_at
    AFTER UPDATE ON Flow
    FOR EACH ROW
    BEGIN
        UPDATE Flow SET UpdatedAt = datetime('now') WHERE Id = NEW.Id;
    END;";
            await connection.ExecuteAsync(createFlowTrigger);

            const string createCallTrigger = @"
CREATE TRIGGER IF NOT EXISTS update_call_updated_at
    AFTER UPDATE ON Call
    FOR EACH ROW
    BEGIN
        UPDATE Call SET UpdatedAt = datetime('now') WHERE Id = NEW.Id;
    END;";
            await connection.ExecuteAsync(createCallTrigger);

            _logger.LogInformation("DSP database schema created successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create DSP database schema");
            return false;
        }
    }

    /// <summary>
    /// 테이블 스키마 검증 및 필요시 재생성
    /// </summary>
    private async Task EnsureTableSchemaAsync(SqliteConnection connection, string tableName, HashSet<string> expectedColumns)
    {
        var tableExists = await connection.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@TableName",
            new { TableName = tableName }) > 0;

        if (!tableExists)
        {
            _logger.LogInformation("Table '{TableName}' does not exist, will be created", tableName);
            return;
        }

        // 기존 테이블의 컬럼 조회
        var columns = await connection.QueryAsync<dynamic>($"PRAGMA table_info({tableName})");
        var actualColumns = columns.Select(c => ((string)c.name).ToLowerInvariant()).ToHashSet();

        // 스키마 비교
        var expectedLower = expectedColumns.Select(c => c.ToLowerInvariant()).ToHashSet();

        bool needsRecreation = false;
        string reason = "";

        if (!actualColumns.SetEquals(expectedLower))
        {
            needsRecreation = true;
            reason = $"Column mismatch. Expected: [{string.Join(", ", expectedColumns)}], Actual: [{string.Join(", ", actualColumns)}]";
        }

        // Call 테이블의 UNIQUE 제약조건 체크
        if (tableName == "Call" && !needsRecreation)
        {
            var createTableSql = await connection.QueryFirstOrDefaultAsync<string>(
                "SELECT sql FROM sqlite_master WHERE type='table' AND name=@TableName",
                new { TableName = tableName });

            if (createTableSql != null)
            {
                // UNIQUE 제약조건 체크: CallName, FlowName, WorkName
                if (!createTableSql.Contains("UNIQUE(CallName, FlowName, WorkName)", StringComparison.OrdinalIgnoreCase) &&
                    !createTableSql.Contains("UNIQUE (CallName, FlowName, WorkName)", StringComparison.OrdinalIgnoreCase))
                {
                    needsRecreation = true;
                    reason = "UNIQUE constraint changed to (CallName, FlowName, WorkName)";
                }
            }
        }

        if (needsRecreation)
        {
            _logger.LogWarning(
                "Table '{TableName}' needs recreation. Reason: {Reason}. Dropping and recreating...",
                tableName, reason);

            // Drop table and related objects
            await DropTableAndDependenciesAsync(connection, tableName);
        }
        else
        {
            _logger.LogDebug("Table '{TableName}' schema is up to date", tableName);
        }
    }

    /// <summary>
    /// 테이블 및 종속 객체 삭제
    /// </summary>
    private async Task DropTableAndDependenciesAsync(SqliteConnection connection, string tableName)
    {
        // Drop triggers
        var triggers = await connection.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='trigger' AND tbl_name=@TableName",
            new { TableName = tableName });

        foreach (var trigger in triggers)
        {
            await connection.ExecuteAsync($"DROP TRIGGER IF EXISTS {trigger}");
            _logger.LogInformation("Dropped trigger '{TriggerName}'", trigger);
        }

        // Drop indexes
        var indexes = await connection.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name=@TableName AND name NOT LIKE 'sqlite_%'",
            new { TableName = tableName });

        foreach (var index in indexes)
        {
            await connection.ExecuteAsync($"DROP INDEX IF EXISTS {index}");
            _logger.LogInformation("Dropped index '{IndexName}'", index);
        }

        // Drop views that depend on this table
        if (tableName == "Flow" || tableName == "Call")
        {
            await connection.ExecuteAsync("DROP VIEW IF EXISTS CallFlowView");
            _logger.LogInformation("Dropped view 'CallFlowView'");
        }

        // Drop table
        await connection.ExecuteAsync($"DROP TABLE IF EXISTS {tableName}");
        _logger.LogInformation("Dropped table '{TableName}'", tableName);
    }

    /// <summary>
    /// 중복된 Call 데이터 정리 (CallName, FlowName, WorkName 기준으로 최신 것만 남김)
    /// </summary>
    private async Task CleanupDuplicateCallsAsync(SqliteConnection connection)
    {
        try
        {
            // 1. UNIQUE 제약조건 위반 중복 확인 (CallName, FlowName, WorkName)
            var duplicateByUnique = await connection.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) - COUNT(DISTINCT CallName || '|' || FlowName || '|' || WorkName)
                FROM Call");

            if (duplicateByUnique > 0)
            {
                _logger.LogWarning("Found {Count} records violating UNIQUE(CallName, FlowName, WorkName), cleaning up...",
                    duplicateByUnique);

                // 중복 제거: CallName, FlowName, WorkName이 같은 레코드 중 Id가 큰 것만 유지
                var deletedCount = await connection.ExecuteAsync(@"
                    DELETE FROM Call
                    WHERE Id NOT IN (
                        SELECT MAX(Id)
                        FROM Call
                        GROUP BY CallName, FlowName, WorkName
                    )");

                _logger.LogInformation("Deleted {Count} duplicate Call records (UNIQUE constraint violation)", deletedCount);
            }

            // 2. PRIMARY KEY 중복 확인 (Id) - 이론적으로 불가능하지만 DB 손상 시 발생 가능
            var duplicateById = await connection.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) - COUNT(DISTINCT Id)
                FROM Call");

            if (duplicateById > 0)
            {
                _logger.LogError("Found {Count} records with duplicate Id (PRIMARY KEY violation)! Database integrity issue detected.",
                    duplicateById);

                // Id 중복 확인 상세 로그
                var duplicateIds = await connection.QueryAsync<int>(@"
                    SELECT Id
                    FROM Call
                    GROUP BY Id
                    HAVING COUNT(*) > 1
                    LIMIT 10");

                _logger.LogError("Duplicate Ids: {Ids}", string.Join(", ", duplicateIds));

                // 임시 테이블로 복구: Id 중복 시 임의로 재번호 부여
                await connection.ExecuteAsync(@"
                    CREATE TEMP TABLE Call_Backup AS
                    SELECT * FROM Call;

                    DELETE FROM Call;

                    INSERT INTO Call (CallName, ApiCall, WorkName, FlowName, Next, Prev, AutoPre, CommonPre,
                                     State, ProgressRate, PreviousGoingTime, AverageGoingTime, StdDevGoingTime,
                                     GoingCount, Device, ErrorText, CreatedAt, UpdatedAt)
                    SELECT CallName, ApiCall, WorkName, FlowName, Next, Prev, AutoPre, CommonPre,
                           State, ProgressRate, PreviousGoingTime, AverageGoingTime, StdDevGoingTime,
                           GoingCount, Device, ErrorText, CreatedAt, UpdatedAt
                    FROM Call_Backup
                    GROUP BY CallName, FlowName, WorkName;

                    DROP TABLE Call_Backup;
                ");

                _logger.LogWarning("Rebuilt Call table with new Ids to fix PRIMARY KEY violation");
            }
            else
            {
                _logger.LogDebug("No duplicate Call records found (DB integrity OK)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup duplicate Call records");
        }
    }

    /// <summary>
    /// Flow 테이블 예상 컬럼 목록
    /// </summary>
    private HashSet<string> GetFlowColumns() => new()
    {
        "Id", "FlowName", "MT", "WT", "CT", "State",
        "MovingStartName", "MovingEndName", "CreatedAt", "UpdatedAt"
    };

    /// <summary>
    /// Call 테이블 예상 컬럼 목록
    /// </summary>
    private HashSet<string> GetCallColumns() => new()
    {
        "Id", "CallName", "ApiCall", "WorkName", "FlowName",
        "Next", "Prev", "AutoPre", "CommonPre", "State", "ProgressRate",
        "PreviousGoingTime", "AverageGoingTime", "StdDevGoingTime", "GoingCount",
        "Device", "ErrorText", "CreatedAt", "UpdatedAt"
    };

    /// <inheritdoc />
    public async Task<int> BulkInsertFlowsAsync(List<DspFlowEntity> flows)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        try
        {
            const string sql = @"
INSERT INTO Flow (FlowName, MT, WT, CT, State, MovingStartName, MovingEndName)
VALUES (@FlowName, @MT, @WT, @CT, @State, @MovingStartName, @MovingEndName)
ON CONFLICT (FlowName) DO UPDATE SET
    MT = COALESCE(excluded.MT, Flow.MT),
    WT = COALESCE(excluded.WT, Flow.WT),
    CT = COALESCE(excluded.CT, Flow.CT),
    State = excluded.State,
    MovingStartName = excluded.MovingStartName,
    MovingEndName = excluded.MovingEndName,
    UpdatedAt = datetime('now')";

            var count = await connection.ExecuteAsync(sql, flows, transaction);
            transaction.Commit();

            _logger.LogInformation("Inserted {Count} flows into DSP database", count);
            return count;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<int> BulkInsertCallsAsync(List<DspCallEntity> calls)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        try
        {
            // 입력 데이터에서 중복 제거 (CallName, FlowName, WorkName 기준)
            var uniqueCalls = calls
                .GroupBy(c => new { c.CallName, c.FlowName, c.WorkName })
                .Select(g => g.First())
                .ToList();

            if (uniqueCalls.Count < calls.Count)
            {
                _logger.LogWarning(
                    "Input data contains {DuplicateCount} duplicate calls (Total: {Total}, Unique: {Unique})",
                    calls.Count - uniqueCalls.Count, calls.Count, uniqueCalls.Count);
            }

            // Ensure Flows exist
            var flowNames = uniqueCalls.Select(c => c.FlowName).Distinct().ToList();
            foreach (var flowName in flowNames)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO Flow (FlowName) VALUES (@FlowName) ON CONFLICT (FlowName) DO NOTHING",
                    new { FlowName = flowName },
                    transaction);
            }

            // Insert Calls
            const string sql = @"
INSERT INTO Call (CallName, ApiCall, WorkName, FlowName, Next, Prev, AutoPre, CommonPre, State, ProgressRate, Device)
VALUES (@CallName, @ApiCall, @WorkName, @FlowName, @Next, @Prev, @AutoPre, @CommonPre, @State, @ProgressRate, @Device)
ON CONFLICT (CallName, FlowName, WorkName) DO UPDATE SET
    ApiCall = excluded.ApiCall,
    Next = excluded.Next,
    Prev = excluded.Prev,
    AutoPre = excluded.AutoPre,
    CommonPre = excluded.CommonPre,
    State = excluded.State,
    ProgressRate = excluded.ProgressRate,
    Device = excluded.Device,
    UpdatedAt = datetime('now')";

            var count = await connection.ExecuteAsync(sql, uniqueCalls, transaction);
            transaction.Commit();

            _logger.LogInformation("Inserted {Count} calls into DSP database", count);
            return count;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string> GetCallStateAsync(string callName)
    {
        using var connection = CreateConnection();

        const string sql = "SELECT State FROM Call WHERE CallName = @CallName LIMIT 1";
        var state = await connection.QueryFirstOrDefaultAsync<string>(sql, new { CallName = callName });

        return state ?? "Ready";
    }

    /// <inheritdoc />
    public async Task<(string WorkName, string FlowName)?> GetCallInfoAsync(string callName)
    {
        using var connection = CreateConnection();

        const string sql = "SELECT WorkName, FlowName FROM Call WHERE CallName = @CallName LIMIT 1";
        var result = await connection.QueryFirstOrDefaultAsync<(string WorkName, string FlowName)>(sql, new { CallName = callName });

        return result.WorkName != null && result.FlowName != null ? result : null;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateCallStateAsync(string callName, string state)
    {
        using var connection = CreateConnection();

        const string sql = @"
UPDATE Call
SET State = @State,
    UpdatedAt = datetime('now')
WHERE CallName = @CallName";

        var result = await connection.ExecuteAsync(sql, new { State = state, CallName = callName });
        return result > 0;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateCallWithStatisticsAsync(
        string callName,
        string state,
        int previousGoingTime,
        double averageGoingTime,
        double stdDevGoingTime)
    {
        using var connection = CreateConnection();

        const string sql = @"
UPDATE Call
SET State = @State,
    PreviousGoingTime = @PreviousGoingTime,
    AverageGoingTime = @AverageGoingTime,
    StdDevGoingTime = @StdDevGoingTime,
    GoingCount = GoingCount + 1,
    UpdatedAt = datetime('now')
WHERE CallName = @CallName";

        var result = await connection.ExecuteAsync(sql, new
        {
            State = state,
            PreviousGoingTime = previousGoingTime,
            AverageGoingTime = averageGoingTime,
            StdDevGoingTime = stdDevGoingTime,
            CallName = callName
        });

        if (result > 0)
        {
            _logger.LogDebug(
                "Updated Call '{CallName}': State={State}, GoingTime={Time}ms, Avg={Avg:F0}ms, StdDev={StdDev:F0}ms",
                callName, state, previousGoingTime, averageGoingTime, stdDevGoingTime);
        }

        return result > 0;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateFlowStateAsync(string flowName, string state)
    {
        using var connection = CreateConnection();

        const string sql = @"
UPDATE Flow
SET State = @State,
    UpdatedAt = datetime('now')
WHERE FlowName = @FlowName";

        var result = await connection.ExecuteAsync(sql, new { State = state, FlowName = flowName });
        return result > 0;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateFlowMetricsAsync(
        string flowName,
        int? mt,
        int? wt,
        int? ct,
        string? movingStartName,
        string? movingEndName)
    {
        using var connection = CreateConnection();

        const string sql = @"
UPDATE Flow
SET MT = @MT,
    WT = @WT,
    CT = @CT,
    MovingStartName = @MovingStartName,
    MovingEndName = @MovingEndName,
    UpdatedAt = datetime('now')
WHERE FlowName = @FlowName";

        var result = await connection.ExecuteAsync(sql, new
        {
            MT = mt,
            WT = wt,
            CT = ct,
            MovingStartName = movingStartName,
            MovingEndName = movingEndName,
            FlowName = flowName
        });

        return result > 0;
    }

    /// <inheritdoc />
    public async Task<bool> ClearAllDataAsync()
    {
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync();

            using var transaction = connection.BeginTransaction();

            await connection.ExecuteAsync("DELETE FROM Call", transaction: transaction);
            await connection.ExecuteAsync("DELETE FROM Flow", transaction: transaction);

            transaction.Commit();

            _logger.LogInformation("Cleared all data from DSP database");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear DSP database");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task CleanupDatabaseAsync()
    {
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync();

            _logger.LogInformation("Running database cleanup...");
            await CleanupDuplicateCallsAsync(connection);

            // VACUUM으로 DB 최적화
            await connection.ExecuteAsync("VACUUM");

            _logger.LogInformation("Database cleanup completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup database");
        }
    }

    /// <inheritdoc />
    public async Task<List<CallStatisticsDto>> GetCallStatisticsAsync()
    {
        using var connection = CreateConnection();

        const string sql = @"
SELECT
    CallName,
    FlowName,
    WorkName,
    AverageGoingTime,
    StdDevGoingTime,
    GoingCount
FROM Call
WHERE GoingCount > 0
  AND AverageGoingTime IS NOT NULL
  AND StdDevGoingTime IS NOT NULL
ORDER BY FlowName, CallName";

        var results = await connection.QueryAsync<CallStatisticsDto>(sql);
        return results.ToList();
    }

}
