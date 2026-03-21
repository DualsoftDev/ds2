using System.Threading.Channels;
using DSPilot.Models;
using DSPilot.Models.Dsp;
using DSPilot.Models.Plc;
using DSPilot.Repositories;
using Ev2.Backend.Common;

namespace DSPilot.Services;

/// <summary>
/// PLC 데이터 실시간 읽기 백그라운드 서비스
/// EV2의 GlobalCommunication.SubjectC2S 이벤트를 구독하여 실시간으로 PLC 태그 변경을 감지합니다.
/// </summary>
public class PlcDataReaderService : BackgroundService
{
    private readonly ILogger<PlcDataReaderService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IDatabasePathResolver _pathResolver;
    private readonly IFlowMetricsService _flowMetricsService;
    private readonly DspDbService _dspDbService;
    private readonly ChannelWriter<CallStateChangedEvent> _eventWriter;
    private readonly bool _simulationMode;
    private readonly TimeSpan _simulationWindow = TimeSpan.FromSeconds(1);

    // Services needed for log processing (injected directly to avoid scope disposal issues)
    private readonly PlcToCallMapperService _mapper;
    private readonly PlcTagStateTrackerService _stateTracker;
    private readonly CallStatisticsService _statistics;
    private readonly IDspRepository _dspRepo;
    private readonly IPlcRepository _plcRepo;

    private DateTime _lastReadTime;
    private DateTime? _simulationReplayEndTime;
    private bool _simulationReplayCompleted;
    private int _totalLogsRead;
    private IDisposable? _ev2Subscription;
    private Dictionary<string, int> _tagAddressToIdMap = new();
    private CancellationTokenSource? _serviceCts;

    public PlcDataReaderService(
        ILogger<PlcDataReaderService> logger,
        IConfiguration configuration,
        IDatabasePathResolver pathResolver,
        IFlowMetricsService flowMetricsService,
        DspDbService dspDbService,
        PlcToCallMapperService mapper,
        PlcTagStateTrackerService stateTracker,
        CallStatisticsService statistics,
        IDspRepository dspRepo,
        IPlcRepository plcRepo)
    {
        _logger = logger;
        _configuration = configuration;
        _pathResolver = pathResolver;
        _flowMetricsService = flowMetricsService;
        _dspDbService = dspDbService;
        _eventWriter = dspDbService.EventWriter;
        _simulationMode = _configuration.GetValue<bool>("PlcDatabase:SimulationMode");
        _mapper = mapper;
        _stateTracker = stateTracker;
        _statistics = statistics;
        _dspRepo = dspRepo;
        _plcRepo = plcRepo;

        _lastReadTime = DateTime.Now;
    }

    // ──────────────────────────────────────────────
    //  Lifecycle
    // ──────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("========================================");
            _logger.LogInformation("PLC Data Reader Service starting...");
            _logger.LogInformation("Simulation Mode Config: {SimMode}", _simulationMode);
            _logger.LogInformation("========================================");

            // Create service-level cancellation token source
            _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            // Retry initialization if database schema is not ready yet
            if (!await InitializeWithRetryAsync(stoppingToken)) return;
            if (!await WaitForMapperInitializationAsync(stoppingToken)) return;

            await PostInitializeAsync();

            // EV2 이벤트 기반 모드
            var useEv2Events = _configuration.GetValue<bool>("PlcConnection:UseEv2Events", true);

            try
            {
            if (useEv2Events)
            {
                _logger.LogInformation("Event-driven mode: Subscribing to EV2 GlobalCommunication.SubjectC2S");
                SubscribeToEv2Events();

                // 이벤트 기반이므로 무한 대기 (이벤트가 자동으로 처리됨)
                await WaitForCancellationAsync(stoppingToken);
            }
            else
            {
                // 폴링 모드 (기존 방식)
                var intervalMs = _configuration.GetValue<int>("PlcDatabase:ReadIntervalMs", 1000);
                _logger.LogInformation("Polling mode: Read interval {IntervalMs}ms", intervalMs);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await ReadAndProcessDataAsync(stoppingToken);
                        await Task.Delay(intervalMs, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error reading PLC data");
                        await Task.Delay(5000, stoppingToken);
                    }
                }
            }
        }
            catch (TaskCanceledException)
            {
                // Expected when application is shutting down (inner try-catch for delay)
                _logger.LogInformation("PLC Data Reader Service stopping gracefully");
            }

            _logger.LogInformation("PLC Data Reader Service stopped. Total events processed: {Count}", _totalLogsRead);
        }
        catch (TaskCanceledException)
        {
            // Expected when application is shutting down during initialization
            _logger.LogInformation("PLC Data Reader Service cancelled during startup");
        }
        catch (OperationCanceledException)
        {
            // Expected when application is shutting down
            _logger.LogInformation("PLC Data Reader Service operation cancelled");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PLC Data Reader Service is stopping gracefully...");

        // Cancel service-level token to stop event processing
        _serviceCts?.Cancel();

        // EV2 구독 해제
        _ev2Subscription?.Dispose();
        _ev2Subscription = null;

        // Dispose cancellation token source
        _serviceCts?.Dispose();
        _serviceCts = null;

        await base.StopAsync(cancellationToken);
    }

    // ──────────────────────────────────────────────
    //  EV2 Event Subscription
    // ──────────────────────────────────────────────

    private void SubscribeToEv2Events()
    {
        try
        {
            _ev2Subscription = GlobalCommunication.SubjectC2S.Subscribe(async info =>
            {
                try
                {
                    if (info.Tags == null || info.Tags.Length == 0) return;

                    // EV2 이벤트를 PlcTagLog로 변환
                    var logs = new List<PlcTagLogEntity>();
                    foreach (var (tagSpec, value) in info.Tags)
                    {
                        // 캐시된 태그 ID 조회
                        if (!_tagAddressToIdMap.TryGetValue(tagSpec.Address, out var tagId))
                        {
                            _logger.LogDebug("Tag ID not found in cache for address: {Address}", tagSpec.Address);
                            continue;
                        }

                        var log = new PlcTagLogEntity
                        {
                            PlcTagId = tagId,
                            Value = value.GetValue()?.ToString() ?? "",
                            DateTime = info.Timestamp
                        };
                        logs.Add(log);
                    }

                    if (logs.Count > 0)
                    {
                        // Check if service is still running
                        if (_serviceCts == null || _serviceCts.Token.IsCancellationRequested)
                        {
                            _logger.LogDebug("Service is stopping, ignoring event");
                            return;
                        }

                        // 기존 처리 로직 호출 (비동기)
                        await ProcessLogsAsync(logs, _serviceCts.Token);
                        Interlocked.Add(ref _totalLogsRead, logs.Count);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Service is stopping, ignore
                }
                catch (ObjectDisposedException)
                {
                    // Service is disposed, ignore
                    _logger.LogDebug("Service already disposed, ignoring event");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing EV2 event");
                }
            });

            _logger.LogInformation("Successfully subscribed to EV2 GlobalCommunication.SubjectC2S");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to subscribe to EV2 events");
            throw;
        }
    }

    // ──────────────────────────────────────────────
    //  Initialization
    // ──────────────────────────────────────────────

    private async Task<bool> WaitForDatabaseCreationAsync(CancellationToken stoppingToken)
    {
        var dbPath = _pathResolver.GetPlcDbPath();

        _logger.LogInformation("Waiting for database file to be created: {DbPath}", dbPath);

        var maxWaitTime = TimeSpan.FromSeconds(30);
        var startTime = DateTime.Now;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (File.Exists(dbPath))
            {
                _logger.LogInformation("Database file found: {DbPath}", dbPath);
                await Task.Delay(1000, stoppingToken);
                return true;
            }

            if (DateTime.Now - startTime > maxWaitTime)
            {
                _logger.LogError("Timeout waiting for database file: {DbPath}", dbPath);
                return false;
            }

            await Task.Delay(1000, stoppingToken);
        }

        return false;
    }

    private async Task<bool> InitializeWithRetryAsync(CancellationToken stoppingToken)
    {
        const int maxRetries = 5;
        const int delayMs = 2000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            _logger.LogInformation("Database initialization attempt {Attempt}/{MaxRetries}", attempt, maxRetries);

            if (await InitializeAsync())
            {
                _logger.LogInformation("Database initialization succeeded on attempt {Attempt}", attempt);
                return true;
            }

            if (attempt < maxRetries)
            {
                _logger.LogWarning("Database initialization failed on attempt {Attempt}/{MaxRetries}. Waiting {DelayMs}ms before retry...",
                    attempt, maxRetries, delayMs);
                await Task.Delay(delayMs, stoppingToken);
            }
        }


        _logger.LogError("Database initialization failed after {MaxRetries} attempts", maxRetries);
        return false;
    }

    private async Task<bool> InitializeAsync()
    {
        try
        {
            if (!await _plcRepo.TestConnectionAsync())
            {
                _logger.LogDebug("Database connection test failed (schema may not be ready yet)");
                return false;
            }

            var plcs = await _plcRepo.GetAllPlcsAsync();
            var tags = await _plcRepo.GetAllTagsAsync();
            var totalLogs = await _plcRepo.GetTotalLogCountAsync();

            // 태그 Address → ID 매핑 캐시 생성 (EV2 이벤트 처리용)
            _tagAddressToIdMap = tags.ToDictionary(t => t.Address, t => t.Id);
            _logger.LogInformation("Tag address cache built: {Count} tags", _tagAddressToIdMap.Count);

            _logger.LogInformation("Database initialized: {PlcCount} PLCs, {TagCount} tags, {LogCount} logs",
                plcs.Count, tags.Count, totalLogs);

            if (_simulationMode)
                await InitializeSimulationModeAsync(_plcRepo);
            else
            {
                _lastReadTime = DateTime.Now;
                _logger.LogInformation("Live mode enabled. Starting from {StartTime}", _lastReadTime);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database connection");
            return false;
        }
    }

    private async Task InitializeSimulationModeAsync(IPlcRepository repository)
    {
        var oldestLogTime = await repository.GetOldestLogDateTimeAsync();
        var latestLogTime = await repository.GetLatestLogDateTimeAsync();

        if (oldestLogTime == null || latestLogTime == null)
        {
            _simulationReplayCompleted = true;
            _logger.LogWarning("Simulation mode enabled, but PLC log data is empty");
            return;
        }

        _lastReadTime = oldestLogTime.Value.AddTicks(-1);
        _simulationReplayEndTime = latestLogTime.Value;
        _simulationReplayCompleted = false;

        _logger.LogInformation("Simulation mode: replaying from {Start} to {End}",
            oldestLogTime.Value, latestLogTime.Value);
    }

    private async Task<bool> WaitForMapperInitializationAsync(CancellationToken stoppingToken)
    {
        // Note: DsProjectService cannot be injected directly as it's not a service
        // We need to get it from scope, but this happens during initialization before disposal issues
        // For now, just check if mapper is initialized
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_mapper.IsInitialized)
            {
                _logger.LogInformation("DSP mapper initialized.");
                return true;
            }

            await Task.Delay(500, stoppingToken);
        }

        return false;
    }

    /// <summary>
    /// Mapper InTag 검증 + Flow MovingStart/End 캐시 로드
    /// </summary>
    private async Task PostInitializeAsync()
    {
        try
        {
            // InTag 매핑 검증: PLC에 없는 InTag 제거 → OutTag Falling으로 Finish 전이 가능
            var tags = await _plcRepo.GetAllTagsAsync();
            var tagMatchMode = _configuration.GetValue<string>("PlcDatabase:TagMatchMode") ?? "Address";
            var plcTagKeys = tags
                .Select(t => tagMatchMode.Equals("Address", StringComparison.OrdinalIgnoreCase) ? t.Address : t.Name)
                .ToHashSet();

            _mapper.ValidateWithPlcTags(plcTagKeys);

            _logger.LogInformation("Post-initialization completed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-initialization failed");
        }
    }

    // ──────────────────────────────────────────────
    //  Data reading
    // ──────────────────────────────────────────────

    private async Task ReadAndProcessDataAsync(CancellationToken stoppingToken)
    {
        if (_simulationMode && !_simulationReplayCompleted)
            await ReplayHistoricalWindowAsync(_plcRepo, stoppingToken);
        else
            await ReadLiveDataAsync(_plcRepo, stoppingToken);
    }

    private async Task ReadLiveDataAsync(IPlcRepository repository, CancellationToken stoppingToken)
    {
        var pollTime = DateTime.Now;
        var newLogs = await repository.GetNewLogsAsync(_lastReadTime);

        if (newLogs.Count > 0)
        {
            LogReadBatch($"[LIVE {pollTime:HH:mm:ss}]", newLogs);
            await ProcessLogsAsync(newLogs, stoppingToken);
            _totalLogsRead += newLogs.Count;
            _lastReadTime = newLogs.Max(log => log.DateTime);
        }
    }

    private async Task ReplayHistoricalWindowAsync(IPlcRepository repository, CancellationToken stoppingToken)
    {
        var windowStart = _lastReadTime;
        var windowEnd = windowStart + _simulationWindow;
        var replayLogs = await repository.GetLogsInRangeAsync(windowStart, windowEnd);

        if (replayLogs.Count > 0)
        {
            LogReadBatch($"[SIM {windowStart:HH:mm:ss.fff} ~ {windowEnd:HH:mm:ss.fff}]", replayLogs);
            await ProcessLogsAsync(replayLogs, stoppingToken);
            _totalLogsRead += replayLogs.Count;
        }

        _lastReadTime = windowEnd;

        if (_simulationReplayEndTime.HasValue && windowEnd >= _simulationReplayEndTime.Value)
        {
            _simulationReplayCompleted = true;
            _lastReadTime = _simulationReplayEndTime.Value;
            _logger.LogInformation("Simulation replay completed. Switching to live mode.");
        }
    }

    private void LogReadBatch(string prefix, List<PlcTagLogEntity> logs)
    {
        _logger.LogInformation("{Prefix} Read {Count} logs (Total: {Total})",
            prefix, logs.Count, _totalLogsRead + logs.Count);
    }

    // ──────────────────────────────────────────────
    //  Log processing pipeline
    // ──────────────────────────────────────────────

    private async Task ProcessLogsAsync(List<PlcTagLogEntity> logs, CancellationToken stoppingToken)
    {
        // Check if cancellation requested
        if (stoppingToken.IsCancellationRequested)
            return;

        // Check if mapper is initialized
        if (!_mapper.IsInitialized)
        {
            _logger.LogWarning("PlcToCallMapper not initialized. Skipping.");
            return;
        }

        int stateChangedCount = 0;

        foreach (var log in logs)
        {
            try
            {
                if (await ProcessSingleLogAsync(log, _mapper, _stateTracker, _statistics, _dspRepo, _plcRepo, stoppingToken))
                    stateChangedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing log ID {LogId}", log.Id);
            }
        }

        if (stateChangedCount > 0)
            _logger.LogDebug("Processed {Count} logs, {Changed} state changes", logs.Count, stateChangedCount);
    }

    /// <summary>
    /// 단일 PLC 로그 처리. 상태 변경이 발생하면 true 반환.
    /// </summary>
    private async Task<bool> ProcessSingleLogAsync(
        PlcTagLogEntity log,
        PlcToCallMapperService mapper,
        PlcTagStateTrackerService stateTracker,
        CallStatisticsService statistics,
        IDspRepository dspRepo,
        IPlcRepository plcRepo,
        CancellationToken stoppingToken)
    {
        // 1. 태그 조회
        var tag = await plcRepo.GetTagByIdAsync(log.PlcTagId);
        if (tag == null) return false;

        // 2. 엣지 감지
        var normalizedValue = NormalizeValue(log.Value);
        var edgeState = stateTracker.UpdateTagValue(tag.Name, normalizedValue);

        // 3. Call 매핑
        var callInfo = mapper.FindCallByTag(tag.Name, tag.Address);
        if (callInfo == null) return false;

        // 4. 상태 전이 결정
        var callId = callInfo.Call.Id;
        var currentState = await dspRepo.GetCallStateAsync(callId);
        var (newState, stateChanged) = mapper.DetermineCallState(tag.Name, tag.Address, edgeState, currentState);

        if (!stateChanged) return false;

        // 5. 상태별 처리
        if (newState == "Going")
            await HandleCallGoingAsync(callInfo, callId, statistics, dspRepo, tag, stoppingToken);
        else if (newState == "Finish")
            await HandleCallFinishAsync(callInfo, callId, statistics, dspRepo, tag, stoppingToken);

        return true;
    }

    // ──────────────────────────────────────────────
    //  State handlers
    // ──────────────────────────────────────────────

    private async Task HandleCallGoingAsync(
        CallMappingInfo callInfo, Guid callId,
        CallStatisticsService statistics, IDspRepository dspRepo,
        PlcTagEntity tag, CancellationToken stoppingToken)
    {
        var callName = callInfo.Call.Name;
        var flowName = callInfo.FlowName;
        var timestamp = DateTime.Now;

        await statistics.RecordGoingStartAsync(callId, callName);
        await dspRepo.UpdateCallStateAsync(callId, "Going");

        await WriteEventAsync(new CallStateChangedEvent { CallName = callName, NewState = "Going" }, stoppingToken);

        _flowMetricsService.OnCallGoingStarted(flowName, callName, timestamp);
        await TryUpdateFlowStateAsync(dspRepo, flowName, callName, "Going");

        _logger.LogInformation("Call '{CallName}' (Flow: {FlowName}): Ready → Going (Tag: {TagName})",
            callName, flowName, tag.Name);
    }

    private async Task HandleCallFinishAsync(
        CallMappingInfo callInfo, Guid callId,
        CallStatisticsService statistics, IDspRepository dspRepo,
        PlcTagEntity tag, CancellationToken stoppingToken)
    {
        var callName = callInfo.Call.Name;
        var flowName = callInfo.FlowName;

        // Going → Finish (통계 업데이트)
        var (_, finishTime, goingTime, avg, stdDev, goingCount) = statistics.RecordGoingFinish(callName);
        await dspRepo.UpdateCallWithStatisticsAsync(callId, "Finish", goingTime, avg, stdDev);

        await WriteEventAsync(new CallStateChangedEvent
        {
            CallName = callName,
            NewState = "Finish",
            PreviousGoingTime = goingTime,
            AverageGoingTime = avg,
            GoingCount = goingCount,
        }, stoppingToken);

        _flowMetricsService.OnCallFinished(flowName, callName, finishTime);
        await TryUpdateFlowStateAsync(dspRepo, flowName, callName, "Finish");

        _logger.LogInformation("Call '{CallName}' (Flow: {FlowName}): Going → Finish (Tag: {TagName}, Time: {Time}ms)",
            callName, flowName, tag.Name, goingTime);

        // Finish → Ready 자동 전환
        await Task.Delay(100, stoppingToken);
        await dspRepo.UpdateCallStateAsync(callId, "Ready");

        await WriteEventAsync(new CallStateChangedEvent
        {
            CallName = callName,
            NewState = "Ready",
            GoingCount = goingCount,
        }, stoppingToken);

        await TryUpdateFlowStateAsync(dspRepo, flowName, callName, "Ready");
    }

    // ──────────────────────────────────────────────
    //  Flow state
    // ──────────────────────────────────────────────

    /// <summary>
    /// MovingStartName/MovingEndName 기반 Flow 상태 업데이트.
    /// - MovingStart Call → Going: Flow "Going"
    /// - MovingEnd Call → Finish: Flow "Finish"
    /// - MovingEnd Call → Ready:  Flow "Ready"
    /// </summary>
    private async Task TryUpdateFlowStateAsync(IDspRepository dspRepo, string flowName, string callName, string callState)
    {
        var flow = _dspDbService.Snapshot.Flows.FirstOrDefault(f => f.FlowName == flowName);
        if (flow == null) return;

        var fullCallId = $"{flowName}.{callName}";

        var shouldUpdate = callState switch
        {
            "Going"  => fullCallId == flow.MovingStartName,
            "Finish" => fullCallId == flow.MovingEndName,
            "Ready"  => fullCallId == flow.MovingEndName,
            _ => false
        };

        if (shouldUpdate)
        {
            await dspRepo.UpdateFlowStateAsync(flowName, callState);
            _logger.LogInformation("Flow '{FlowName}' → {State} (by {CallId})", flowName, callState, fullCallId);
        }
    }

    // ──────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────

    private async Task WriteEventAsync(CallStateChangedEvent evt, CancellationToken ct)
    {
        try
        {
            await _eventWriter.WriteAsync(evt, ct);
        }
        catch (ChannelClosedException) { }
        catch (OperationCanceledException) { }
    }

    private static string NormalizeValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "0";

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" => "1",
            _ => "0"
        };
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>();
        using (cancellationToken.Register(() => tcs.TrySetResult(true)))
        {
            await tcs.Task;
        }
    }
}
