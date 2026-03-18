using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using DSPilot.Models;

namespace DSPilot.Services;

public class DspDbService : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<DspDbService> _logger;
    private readonly PeriodicTimer _timer;
    private readonly PeriodicTimer _progressTimer;
    private readonly CancellationTokenSource _cts = new();
    private DspDbSnapshot _snapshot = new();

    // Going 상태 추적: CallName → Going 시작 시간
    private readonly Dictionary<string, DateTime> _goingStartTimes = new();

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
        _progressTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
        _ = PollLoopAsync(_cts.Token);
        _ = ConsumeChannelAsync(_cts.Token);
        _ = ProgressUpdateLoopAsync(_cts.Token);
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

        // Going 상태 전환 시 시작 시간 기록
        if (evt.NewState == "Going")
        {
            _goingStartTimes[evt.CallName] = DateTime.Now;
        }
        else if (evt.NewState == "Ready" || evt.NewState == "Finish")
        {
            // Going 종료 시 시작 시간 제거
            _goingStartTimes.Remove(evt.CallName);
        }

        var updatedCall = new CallState
        {
            Id               = oldCall.Id,
            CallName         = oldCall.CallName,
            FlowName         = oldCall.FlowName,
            WorkName         = oldCall.WorkName,
            State            = evt.NewState,
            ProgressRate     = CalculateProgressRate(evt.NewState, oldCall.AverageGoingTime, null, evt.CallName),
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

    /// <summary>
    /// State 기반으로 ProgressRate 계산
    /// Ready: 0.0, Going: 경과시간/평균시간 비율, Finish: 1.0
    /// </summary>
    private double CalculateProgressRate(string state, double? averageGoingTime, int? previousGoingTime, string callName)
    {
        return state switch
        {
            "Ready" => 0.0,
            "Going" => CalculateGoingProgress(callName, averageGoingTime),
            "Finish" => 1.0,
            _ => 0.0
        };
    }

    /// <summary>
    /// Going 상태의 실시간 진행률 계산 (경과시간 / 평균시간)
    /// </summary>
    private double CalculateGoingProgress(string callName, double? averageGoingTime)
    {
        if (!_goingStartTimes.TryGetValue(callName, out var startTime))
        {
            return 0.5; // 시작 시간 없으면 50%로 표시
        }

        var elapsedMs = (DateTime.Now - startTime).TotalMilliseconds;

        if (averageGoingTime == null || averageGoingTime <= 0)
        {
            // 평균 시간 없으면 경과 시간 기반 추정 (10초 기준)
            return Math.Min(elapsedMs / 10000.0, 0.99);
        }

        // 경과시간 / 평균시간 (최대 99%까지만)
        var progress = elapsedMs / averageGoingTime.Value;
        return Math.Min(progress, 0.99);
    }

    /// <summary>
    /// DB 폴링으로 Going 상태를 다시 읽었을 때도 진행률이 뒤로 가지 않도록
    /// 기존 진행률과 평균시간을 기준으로 시작 시각을 역산해 이어 붙인다.
    /// </summary>
    private void EnsureGoingStartTime(CallState call, CallState? previousCall)
    {
        if (_goingStartTimes.ContainsKey(call.CallName))
        {
            return;
        }

        var seedProgress = previousCall?.State == "Going"
            ? Math.Max(previousCall.ProgressRate, call.ProgressRate)
            : call.ProgressRate;

        if (call.AverageGoingTime is > 0 && seedProgress > 0)
        {
            var estimatedElapsedMs = Math.Min(seedProgress, 0.99) * call.AverageGoingTime.Value;
            _goingStartTimes[call.CallName] = DateTime.Now - TimeSpan.FromMilliseconds(estimatedElapsedMs);
            return;
        }

        _goingStartTimes[call.CallName] = DateTime.Now;
    }

    private static CallState CloneCallWithProgress(CallState call, double progressRate)
    {
        return new CallState
        {
            Id = call.Id,
            CallName = call.CallName,
            FlowName = call.FlowName,
            WorkName = call.WorkName,
            State = call.State,
            ProgressRate = progressRate,
            GoingCount = call.GoingCount,
            AverageGoingTime = call.AverageGoingTime,
            Device = call.Device,
            ErrorText = call.ErrorText
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

    /// <summary>
    /// 300ms마다 Going 상태 Call의 ProgressRate를 실시간 업데이트
    /// </summary>
    private async Task ProgressUpdateLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _progressTimer.WaitForNextTickAsync(ct))
            {
                UpdateGoingProgress();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    /// <summary>
    /// Going 상태의 모든 Call에 대해 ProgressRate 재계산 및 스냅샷 업데이트
    /// </summary>
    private void UpdateGoingProgress()
    {
        if (_goingStartTimes.Count == 0)
            return; // Going 상태 Call이 없으면 skip

        var oldSnapshot = _snapshot;
        var hasUpdate = false;

        var updatedCalls = oldSnapshot.Calls.Select(call =>
        {
            if (call.State == "Going" && _goingStartTimes.ContainsKey(call.CallName))
            {
                var newProgress = CalculateGoingProgress(call.CallName, call.AverageGoingTime);

                // 진행률이 변경되었으면 업데이트
                if (Math.Abs(newProgress - call.ProgressRate) > 0.001)
                {
                    hasUpdate = true;
                    return new CallState
                    {
                        Id = call.Id,
                        CallName = call.CallName,
                        FlowName = call.FlowName,
                        WorkName = call.WorkName,
                        State = call.State,
                        ProgressRate = newProgress,
                        GoingCount = call.GoingCount,
                        AverageGoingTime = call.AverageGoingTime,
                        Device = call.Device,
                        ErrorText = call.ErrorText
                    };
                }
            }
            return call;
        }).ToList();

        if (!hasUpdate)
            return; // 변경 사항 없으면 skip

        _snapshot = new DspDbSnapshot
        {
            Flows = oldSnapshot.Flows,
            Calls = updatedCalls,
            CallsByFlow = updatedCalls
                .GroupBy(c => c.FlowName)
                .ToDictionary(g => g.Key, g => g.ToList()),
            Timestamp = DateTimeOffset.UtcNow,
        };

        OnDataChanged?.Invoke();
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

            var previousCallsById = _snapshot.Calls
                .DistinctBy(c => c.Id)
                .ToDictionary(c => c.Id, c => c);

            for (var i = 0; i < calls.Count; i++)
            {
                var call = calls[i];
                previousCallsById.TryGetValue(call.Id, out var previousCall);

                if (call.State == "Going")
                {
                    EnsureGoingStartTime(call, previousCall);

                    var recalculatedProgress = CalculateGoingProgress(call.CallName, call.AverageGoingTime);
                    var previousProgress = previousCall?.State == "Going" ? previousCall.ProgressRate : 0.0;
                    var stableProgress = Math.Max(previousProgress, recalculatedProgress);
                    calls[i] = CloneCallWithProgress(call, stableProgress);
                    continue;
                }

                _goingStartTimes.Remove(call.CallName);

                if (call.State == "Ready" && Math.Abs(call.ProgressRate) > 0.001)
                {
                    calls[i] = CloneCallWithProgress(call, 0.0);
                }
                else if (call.State == "Finish" && Math.Abs(call.ProgressRate - 1.0) > 0.001)
                {
                    calls[i] = CloneCallWithProgress(call, 1.0);
                }
            }

            // 실제 변경이 있을 때만 이벤트 발행 (불필요한 Blazor 렌더링 방지)
            // Dictionary 사용으로 O(N) 비교 (중첩 Any의 O(N²) 방지)
            var oldStateMap = _snapshot.Calls.DistinctBy(d=>d.Id).ToDictionary(c => c.Id, c => c.State);
            bool hasChanged =
                calls.Count != _snapshot.Calls.Count ||
                flows.Count != _snapshot.Flows.Count ||
                calls.Any(c => !oldStateMap.TryGetValue(c.Id, out var oldState) || oldState != c.State);

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
        _progressTimer.Dispose();
        _cts.Dispose();
    }
}
