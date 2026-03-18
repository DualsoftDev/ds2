using System.Threading.Channels;
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

    // 인메모리 이벤트 채널: PlcDataReaderService가 상태 변경 즉시 여기에 쓴다
    private readonly Channel<CallStateChangedEvent> _channel =
        Channel.CreateBounded<CallStateChangedEvent>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

    /// <summary>PlcDataReaderService가 이벤트를 발행하는 채널 Writer</summary>
    public ChannelWriter<CallStateChangedEvent> EventWriter => _channel.Writer;

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
        _ = ConsumeChannelAsync(_cts.Token);
    }

    /// <summary>
    /// 채널에서 CallStateChangedEvent를 읽어 DB 폴링 없이 스냅샷을 즉시 갱신한다.
    /// 동시에 쌓인 이벤트를 모두 처리한 후 OnDataChanged를 한 번만 발행하여
    /// 불필요한 Blazor 다중 렌더링을 방지한다.
    /// </summary>
    private async Task ConsumeChannelAsync(CancellationToken ct)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct))
            {
                // 채널에 쌓인 이벤트를 모두 소진 (동시 다발 전환 시 렌더 1회로 합산)
                while (_channel.Reader.TryRead(out var evt))
                {
                    ApplyEventToSnapshot(evt);
                }
                OnDataChanged?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
    }

    /// <summary>
    /// DB 읽기 없이 이벤트 정보로 인메모리 스냅샷을 패치한다.
    /// </summary>
    private void ApplyEventToSnapshot(CallStateChangedEvent evt)
    {
        var oldSnapshot = _snapshot;
        var oldCall = oldSnapshot.Calls.FirstOrDefault(c => c.CallName == evt.CallName);
        if (oldCall == null) return;  // 알 수 없는 Call — 다음 DB 폴링에서 복구됨

        var updatedCall = new CallState
        {
            Id               = oldCall.Id,
            CallName         = oldCall.CallName,
            FlowName         = oldCall.FlowName,
            WorkName         = oldCall.WorkName,
            State            = evt.NewState,
            ProgressRate     = oldCall.ProgressRate,
            GoingCount       = evt.GoingCount ?? oldCall.GoingCount,
            AverageGoingTime = evt.AverageGoingTime ?? oldCall.AverageGoingTime,
            Device           = oldCall.Device,
            ErrorText        = oldCall.ErrorText,
        };

        var updatedCalls = oldSnapshot.Calls
            .Select(c => c.CallName == evt.CallName ? updatedCall : c)
            .ToList();

        _snapshot = new DspDbSnapshot
        {
            Flows       = oldSnapshot.Flows,
            Calls       = updatedCalls,
            CallsByFlow = updatedCalls
                .GroupBy(c => c.FlowName)
                .ToDictionary(g => g.Key, g => g.ToList()),
            Timestamp   = evt.OccurredAt,
        };
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

            // 실제 변경이 있을 때만 이벤트 발행 (불필요한 Blazor 렌더링 방지)
            // Dictionary 사용으로 O(N) 비교 (중첩 Any의 O(N²) 방지)
            var oldStateMap = _snapshot.Calls.ToDictionary(c => c.CallName, c => c.State);
            bool hasChanged =
                calls.Count != _snapshot.Calls.Count ||
                flows.Count != _snapshot.Flows.Count ||
                calls.Any(c => !oldStateMap.TryGetValue(c.CallName, out var oldState) || oldState != c.State);

            if (!hasChanged) return;

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
        _channel.Writer.Complete();
        _timer.Dispose();
        _cts.Dispose();
    }
}
