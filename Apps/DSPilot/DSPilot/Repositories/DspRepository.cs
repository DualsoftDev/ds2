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

            // Create Flow table
            const string createFlowTable = @"
CREATE TABLE IF NOT EXISTS Flow (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    FlowName TEXT UNIQUE NOT NULL,
    MT INTEGER,
    WT INTEGER,
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
    UNIQUE(CallName, FlowName),
    FOREIGN KEY(FlowName) REFERENCES Flow(FlowName) ON DELETE CASCADE
);";
            await connection.ExecuteAsync(createCallTable);

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

    /// <inheritdoc />
    public async Task<int> BulkInsertFlowsAsync(List<DspFlowEntity> flows)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        try
        {
            const string sql = @"
INSERT INTO Flow (FlowName, MT, WT, State, MovingStartName, MovingEndName)
VALUES (@FlowName, @MT, @WT, @State, @MovingStartName, @MovingEndName)
ON CONFLICT (FlowName) DO UPDATE SET
    MT = COALESCE(excluded.MT, Flow.MT),
    WT = COALESCE(excluded.WT, Flow.WT),
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
            // Ensure Flows exist
            var flowNames = calls.Select(c => c.FlowName).Distinct().ToList();
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
ON CONFLICT (CallName, FlowName) DO UPDATE SET
    ApiCall = excluded.ApiCall,
    WorkName = excluded.WorkName,
    Next = excluded.Next,
    Prev = excluded.Prev,
    AutoPre = excluded.AutoPre,
    CommonPre = excluded.CommonPre,
    State = excluded.State,
    ProgressRate = excluded.ProgressRate,
    Device = excluded.Device,
    UpdatedAt = datetime('now')";

            var count = await connection.ExecuteAsync(sql, calls, transaction);
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
