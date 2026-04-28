using Dapper;
using DSPilot.Infrastructure;
using DSPilot.Models.Dsp;
using DSPilot.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DSPilot.Adapters;

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

    public Task<bool> CreateSchemaAsync()
    {
        if (!_enabled) return Task.FromResult(true);
        _logger.LogInformation("Unified mode: Schema creation delegated to EV2 (no action needed)");
        return Task.FromResult(true);
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
                INSERT INTO {HistoryTable} (FlowName, MT, WT, CT, CycleNo, RecordedAt, IsIdle)
                VALUES (@FlowName, @MT, @WT, @CT, @CycleNo, @RecordedAt, @IsIdle)";

            var result = await conn.ExecuteAsync(sql, new
            {
                history.FlowName,
                history.MT,
                history.WT,
                history.CT,
                history.CycleNo,
                RecordedAt = history.RecordedAt == default ? DateTime.UtcNow : history.RecordedAt,
                history.IsIdle,
            });

            _logger.LogDebug(
                "Inserted Flow history for '{FlowName}': Cycle={CycleNo}, MT={MT}ms, WT={WT}ms, CT={CT}ms",
                history.FlowName, history.CycleNo, history.MT, history.WT, history.CT);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to insert Flow history for '{FlowName}'", history.FlowName);
            return 0;
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
                SELECT Id, FlowName, MT, WT, CT, CycleNo, RecordedAt, COALESCE(IsIdle, 0) AS IsIdle
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
                SELECT Id, FlowName, MT, WT, CT, CycleNo, RecordedAt, COALESCE(IsIdle, 0) AS IsIdle
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
                SELECT Id, FlowName, MT, WT, CT, CycleNo, RecordedAt, COALESCE(IsIdle, 0) AS IsIdle
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

    private sealed class CallInfoRow
    {
        public string WorkName { get; set; } = string.Empty;
        public string FlowName { get; set; } = string.Empty;
    }
}
