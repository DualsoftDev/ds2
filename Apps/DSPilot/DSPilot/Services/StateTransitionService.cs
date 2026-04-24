using Dapper;
using Ds2.Core;
using Microsoft.Data.Sqlite;

namespace DSPilot.Services;

/// <summary>
/// PLC 엣지 이벤트를 받아 dspCall/dspFlow 상태 전이를 DB에 반영.
/// F# StateTransition.processEdgeEvent의 pure C# 포팅.
/// </summary>
public sealed class StateTransitionService
{
    private readonly ILogger<StateTransitionService> _logger;

    // Welford-based per-call going-time tracker (process-wide, thread-safe)
    private readonly Dictionary<string, (double Count, double Mean, double M2, double Min, double Max, DateTime? LastStartAt)> _stats = new();
    private readonly object _statsSync = new();

    public StateTransitionService(ILogger<StateTransitionService> logger)
    {
        _logger = logger;
    }

    public Task ProcessEdgeEventAsync(string dbPath, string tagAddress, bool isInTag, EdgeType edgeType, DateTime timestamp, string callName)
    {
        return (isInTag, edgeType) switch
        {
            (true, EdgeType.RisingEdge) => HandleInTagRisingEdgeAsync(dbPath, callName, timestamp),
            (true, EdgeType.FallingEdge) => HandleInTagFallingEdgeAsync(dbPath, callName, timestamp),
            (false, EdgeType.RisingEdge) => HandleOutTagRisingEdgeAsync(dbPath, callName, timestamp),
            (false, EdgeType.FallingEdge) => HandleOutTagFallingEdgeAsync(dbPath, callName, timestamp),
            _ => Task.CompletedTask,
        };
    }

    // ===== Stats helpers (Welford) =====

    private void StatsRecordStart(string callName, DateTime timestamp)
    {
        lock (_statsSync)
        {
            if (!_stats.TryGetValue(callName, out var s))
                s = (0, 0, 0, double.MaxValue, double.MinValue, null);
            _stats[callName] = (s.Count, s.Mean, s.M2, s.Min, s.Max, timestamp);
        }
    }

    private double? StatsRecordFinish(string callName, DateTime timestamp)
    {
        lock (_statsSync)
        {
            if (!_stats.TryGetValue(callName, out var s) || s.LastStartAt is null)
                return null;

            var duration = (timestamp - s.LastStartAt.Value).TotalMilliseconds;
            var newCount = s.Count + 1;
            var delta = duration - s.Mean;
            var newMean = s.Mean + delta / newCount;
            var delta2 = duration - newMean;
            var newM2 = s.M2 + delta * delta2;
            var newMin = Math.Min(s.Min, duration);
            var newMax = Math.Max(s.Max, duration);

            _stats[callName] = (newCount, newMean, newM2, newMin, newMax, null);
            return duration;
        }
    }

    // ===== Direction parsing =====

    private static CallDirection ParseDirection(string? directionStr) => directionStr switch
    {
        "InOut" => CallDirection.InOut,
        "InOnly" => CallDirection.InOnly,
        "OutOnly" => CallDirection.OutOnly,
        _ => CallDirection.None,
    };

    private sealed class CallInfoRow
    {
        public string FlowName { get; set; } = string.Empty;
        public string CallName { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string? LastStartAt { get; set; }
        public string? Direction { get; set; }
    }

    // ===== Edge handlers =====

    private async Task HandleInTagRisingEdgeAsync(string dbPath, string callName, DateTime timestamp)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        const string sql = "SELECT FlowName, CallName, State, LastStartAt, Direction FROM dspCall WHERE CallName = @CallName";
        var call = await conn.QueryFirstOrDefaultAsync<CallInfoRow>(sql, new { CallName = callName });
        if (call is null) return;

        var direction = ParseDirection(call.Direction);
        var utcNow = DateTime.UtcNow.ToString("o");
        var tsIso = timestamp.ToString("o");

        switch (direction)
        {
            case CallDirection.InOut when call.State == "Going":
            {
                var statsDuration = StatsRecordFinish(callName, timestamp);
                var durationMs = statsDuration ?? TryParseStart(call.LastStartAt)?.Let(start => (timestamp - start).TotalMilliseconds);

                const string updateCallSql = @"
                    UPDATE dspCall
                    SET State = @State,
                        LastFinishAt = @LastFinishAt,
                        LastDurationMs = @LastDurationMs,
                        CycleCount = CycleCount + 1,
                        UpdatedAt = @UpdatedAt
                    WHERE CallName = @CallName";

                await conn.ExecuteAsync(updateCallSql, new
                {
                    State = "Finish",
                    LastFinishAt = tsIso,
                    LastDurationMs = durationMs,
                    UpdatedAt = utcNow,
                    CallName = callName,
                });
                break;
            }

            case CallDirection.InOnly when call.State == "Ready":
            {
                StatsRecordStart(callName, timestamp);
                StatsRecordFinish(callName, timestamp);

                const string updateCallSql = @"
                    UPDATE dspCall
                    SET State = @State,
                        LastStartAt = @LastStartAt,
                        LastFinishAt = @LastFinishAt,
                        LastDurationMs = @LastDurationMs,
                        CycleCount = CycleCount + 1,
                        UpdatedAt = @UpdatedAt
                    WHERE CallName = @CallName";

                await conn.ExecuteAsync(updateCallSql, new
                {
                    State = "Finish",
                    LastStartAt = tsIso,
                    LastFinishAt = tsIso,
                    LastDurationMs = 0.0,
                    UpdatedAt = utcNow,
                    CallName = callName,
                });

                const string updateFlowSql = @"
                    UPDATE dspFlow
                    SET ActiveCallCount = ActiveCallCount + 1,
                        State = 'Going',
                        UpdatedAt = @UpdatedAt
                    WHERE FlowName = @FlowName";

                await conn.ExecuteAsync(updateFlowSql, new { UpdatedAt = utcNow, FlowName = call.FlowName });
                break;
            }

            default:
                _logger.LogWarning("InTag Rising for Call with invalid Direction: {CallName} ({Direction})", callName, direction);
                break;
        }
    }

    private async Task HandleInTagFallingEdgeAsync(string dbPath, string callName, DateTime timestamp)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        const string sql = "SELECT FlowName, State, Direction FROM dspCall WHERE CallName = @CallName";
        var call = await conn.QueryFirstOrDefaultAsync<CallInfoRow>(sql, new { CallName = callName });
        if (call is null) return;

        var direction = ParseDirection(call.Direction);
        if ((direction != CallDirection.InOut && direction != CallDirection.InOnly) || call.State != "Finish")
            return;

        var utcNow = DateTime.UtcNow.ToString("o");

        const string updateCallSql = @"
            UPDATE dspCall
            SET State = @State,
                UpdatedAt = @UpdatedAt
            WHERE CallName = @CallName";

        await conn.ExecuteAsync(updateCallSql, new
        {
            State = "Ready",
            UpdatedAt = utcNow,
            CallName = callName,
        });

        const string updateFlowSql = @"
            UPDATE dspFlow
            SET ActiveCallCount = MAX(0, ActiveCallCount - 1),
                UpdatedAt = @UpdatedAt
            WHERE FlowName = @FlowName";

        await conn.ExecuteAsync(updateFlowSql, new { UpdatedAt = utcNow, FlowName = call.FlowName });

        const string countSql = "SELECT ActiveCallCount FROM dspFlow WHERE FlowName = @FlowName";
        var activeCount = await conn.ExecuteScalarAsync<long>(countSql, new { FlowName = call.FlowName });
        var newFlowState = activeCount > 0 ? "Going" : "Ready";

        const string updateStateSql = @"
            UPDATE dspFlow
            SET State = @State,
                UpdatedAt = @UpdatedAt
            WHERE FlowName = @FlowName";

        await conn.ExecuteAsync(updateStateSql, new
        {
            State = newFlowState,
            UpdatedAt = utcNow,
            FlowName = call.FlowName,
        });
    }

    private async Task HandleOutTagRisingEdgeAsync(string dbPath, string callName, DateTime timestamp)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        const string sql = "SELECT FlowName, State, Direction FROM dspCall WHERE CallName = @CallName";
        var call = await conn.QueryFirstOrDefaultAsync<CallInfoRow>(sql, new { CallName = callName });
        if (call is null) return;

        var direction = ParseDirection(call.Direction);
        if ((direction != CallDirection.InOut && direction != CallDirection.OutOnly) || call.State != "Ready")
        {
            if (direction != CallDirection.InOut && direction != CallDirection.OutOnly)
                _logger.LogWarning("OutTag Rising for Call with invalid Direction: {CallName} ({Direction})", callName, direction);
            return;
        }

        StatsRecordStart(callName, timestamp);

        var utcNow = DateTime.UtcNow.ToString("o");
        var tsIso = timestamp.ToString("o");

        const string updateCallSql = @"
            UPDATE dspCall
            SET State = @State,
                LastStartAt = @LastStartAt,
                UpdatedAt = @UpdatedAt
            WHERE CallName = @CallName";

        await conn.ExecuteAsync(updateCallSql, new
        {
            State = "Going",
            LastStartAt = tsIso,
            UpdatedAt = utcNow,
            CallName = callName,
        });

        const string updateFlowSql = @"
            UPDATE dspFlow
            SET ActiveCallCount = ActiveCallCount + 1,
                State = 'Going',
                UpdatedAt = @UpdatedAt
            WHERE FlowName = @FlowName";

        await conn.ExecuteAsync(updateFlowSql, new { UpdatedAt = utcNow, FlowName = call.FlowName });
    }

    private async Task HandleOutTagFallingEdgeAsync(string dbPath, string callName, DateTime timestamp)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        const string sql = "SELECT FlowName, CallName, State, LastStartAt, Direction FROM dspCall WHERE CallName = @CallName";
        var call = await conn.QueryFirstOrDefaultAsync<CallInfoRow>(sql, new { CallName = callName });
        if (call is null) return;

        var direction = ParseDirection(call.Direction);
        if (direction != CallDirection.OutOnly || call.State != "Going")
            return;

        var statsDuration = StatsRecordFinish(callName, timestamp);
        var durationMs = statsDuration ?? TryParseStart(call.LastStartAt)?.Let(start => (timestamp - start).TotalMilliseconds);

        var utcNow = DateTime.UtcNow.ToString("o");
        var tsIso = timestamp.ToString("o");

        const string updateCallSql = @"
            UPDATE dspCall
            SET State = @State,
                LastFinishAt = @LastFinishAt,
                LastDurationMs = @LastDurationMs,
                CycleCount = CycleCount + 1,
                UpdatedAt = @UpdatedAt
            WHERE CallName = @CallName";

        await conn.ExecuteAsync(updateCallSql, new
        {
            State = "Ready",
            LastFinishAt = tsIso,
            LastDurationMs = durationMs,
            UpdatedAt = utcNow,
            CallName = callName,
        });

        const string updateFlowSql = @"
            UPDATE dspFlow
            SET ActiveCallCount = MAX(0, ActiveCallCount - 1),
                UpdatedAt = @UpdatedAt
            WHERE FlowName = @FlowName";

        await conn.ExecuteAsync(updateFlowSql, new { UpdatedAt = utcNow, FlowName = call.FlowName });

        const string countSql = "SELECT ActiveCallCount FROM dspFlow WHERE FlowName = @FlowName";
        var activeCount = await conn.ExecuteScalarAsync<long>(countSql, new { FlowName = call.FlowName });
        var newFlowState = activeCount > 0 ? "Going" : "Ready";

        const string updateStateSql = @"
            UPDATE dspFlow
            SET State = @State,
                UpdatedAt = @UpdatedAt
            WHERE FlowName = @FlowName";

        await conn.ExecuteAsync(updateStateSql, new
        {
            State = newFlowState,
            UpdatedAt = utcNow,
            FlowName = call.FlowName,
        });
    }

    private static DateTime? TryParseStart(string? lastStartAt)
    {
        if (string.IsNullOrEmpty(lastStartAt)) return null;
        return DateTime.TryParse(lastStartAt, out var dt) ? dt : null;
    }
}

internal static class NullableExtensions
{
    public static TOut? Let<TIn, TOut>(this TIn value, Func<TIn, TOut> f) where TIn : struct where TOut : struct
        => f(value);
}
