using Microsoft.Data.Sqlite;
using DSPilot.Models;

namespace DSPilot.Services;

public class DspDbService : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<DspDbService> _logger;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private DspDbSnapshot _snapshot = new();

    public DspDbSnapshot Snapshot => _snapshot;

    public event Action? OnDataChanged;

    public DspDbService(IConfiguration configuration, ILogger<DspDbService> logger)
    {
        _logger = logger;

        var configPath = configuration["DspDatabase:Path"];
        _dbPath = !string.IsNullOrEmpty(configPath)
            ? Environment.ExpandEnvironmentVariables(configPath)
            : Path.Combine(
                  Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                  "DSPilot", "dsp.db");

        _timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _ = PollLoopAsync(_cts.Token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        TryRefresh();

        try
        {
            while (await _timer.WaitForNextTickAsync(ct))
            {
                TryRefresh();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private void TryRefresh()
    {
        try
        {
            if (!File.Exists(_dbPath))
            {
                _logger.LogDebug("DB file not found: {Path}", _dbPath);
                return;
            }

            var flows = new List<FlowState>();
            var calls = new List<CallState>();

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            using var conn = new SqliteConnection(connStr);
            conn.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT Id, FlowName, MT, WT, State, MovingStartName, MovingEndName FROM Flow";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    flows.Add(new FlowState
                    {
                        Id = reader.GetInt32(0),
                        FlowName = reader.GetString(1),
                        MT = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                        WT = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        State = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        MovingStartName = reader.IsDBNull(5) ? null : reader.GetString(5),
                        MovingEndName = reader.IsDBNull(6) ? null : reader.GetString(6),
                    });
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT Id, CallName, FlowName, WorkName, State, ProgressRate,
                    GoingCount, AverageGoingTime, Device, ErrorText FROM Call";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    calls.Add(new CallState
                    {
                        Id = reader.GetInt32(0),
                        CallName = reader.GetString(1),
                        FlowName = reader.GetString(2),
                        WorkName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        State = reader.IsDBNull(4) ? "Ready" : reader.GetString(4),
                        ProgressRate = reader.IsDBNull(5) ? 0.0 : reader.GetDouble(5),
                        GoingCount = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                        AverageGoingTime = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                        Device = reader.IsDBNull(8) ? null : reader.GetString(8),
                        ErrorText = reader.IsDBNull(9) ? null : reader.GetString(9),
                    });
                }
            }

            var callsByFlow = calls
                .GroupBy(c => c.FlowName)
                .ToDictionary(g => g.Key, g => g.ToList());

            _snapshot = new DspDbSnapshot
            {
                Flows = flows,
                Calls = calls,
                CallsByFlow = callsByFlow,
                Timestamp = DateTimeOffset.UtcNow,
            };

            OnDataChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read dsp.db");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        _cts.Dispose();
    }
}
