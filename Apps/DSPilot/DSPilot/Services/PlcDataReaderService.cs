using System.Threading.Channels;
using DSPilot.Models;
using DSPilot.Models.Dsp;
using DSPilot.Models.Plc;
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// PLC 데이터 실시간 읽기 백그라운드 서비스
/// 1초마다 소스 DB에서 새로운 로그를 읽어옵니다.
/// </summary>
public class PlcDataReaderService : BackgroundService
{
    private readonly ILogger<PlcDataReaderService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IFlowMetricsService _flowMetricsService;
    private readonly DspDbService _dspDbService;
    private readonly ChannelWriter<CallStateChangedEvent> _eventWriter;
    private readonly bool _simulationMode;
    private readonly TimeSpan _simulationWindow = TimeSpan.FromSeconds(1);

    private DateTime _lastReadTime;
    private DateTime? _simulationReplayEndTime;
    private bool _simulationReplayCompleted;
    private int _totalLogsRead;

    public PlcDataReaderService(
        ILogger<PlcDataReaderService> logger,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IFlowMetricsService flowMetricsService,
        DspDbService dspDbService)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _flowMetricsService = flowMetricsService;
        _dspDbService = dspDbService;
        _eventWriter = dspDbService.EventWriter;
        _simulationMode = _configuration.GetValue<bool>("PlcDatabase:SimulationMode");

        _lastReadTime = DateTime.Now;
    }

    // ──────────────────────────────────────────────
    //  Lifecycle
    // ──────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("PLC Data Reader Service starting...");
        _logger.LogInformation("Simulation Mode Config: {SimMode}", _simulationMode);
        _logger.LogInformation("========================================");

        if (!await WaitForDatabaseCreationAsync(stoppingToken)) return;
        if (!await InitializeAsync()) return;
        if (!await WaitForMapperInitializationAsync(stoppingToken)) return;

        await PostInitializeAsync();

        var intervalMs = _configuration.GetValue<int>("PlcDatabase:ReadIntervalMs", 1000);
        _logger.LogInformation("Read interval: {IntervalMs}ms", intervalMs);

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

        _logger.LogInformation("PLC Data Reader Service stopped. Total logs read: {Count}", _totalLogsRead);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PLC Data Reader Service is stopping gracefully...");
        await base.StopAsync(cancellationToken);
    }

    // ──────────────────────────────────────────────
    //  Initialization
    // ──────────────────────────────────────────────

    private async Task<bool> WaitForDatabaseCreationAsync(CancellationToken stoppingToken)
    {
        var dbPath = _configuration["PlcDatabase:SourceDbPath"] ?? "sample/db/DsDB.sqlite3";
        dbPath = Environment.ExpandEnvironmentVariables(dbPath);
        dbPath = dbPath.Replace('/', Path.DirectorySeparatorChar);

        if (!Path.IsPathRooted(dbPath))
            dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dbPath);

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

    private async Task<bool> InitializeAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IPlcRepository>();

            if (!await repository.TestConnectionAsync())
            {
                _logger.LogError("Database connection test failed");
                return false;
            }

            var plcs = await repository.GetAllPlcsAsync();
            var tags = await repository.GetAllTagsAsync();
            var totalLogs = await repository.GetTotalLogCountAsync();

            _logger.LogInformation("Database initialized: {PlcCount} PLCs, {TagCount} tags, {LogCount} logs",
                plcs.Count, tags.Count, totalLogs);

            if (_simulationMode)
                await InitializeSimulationModeAsync(repository);
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
        using var scope = _scopeFactory.CreateScope();
        var projectService = scope.ServiceProvider.GetRequiredService<DsProjectService>();
        var mapper = scope.ServiceProvider.GetRequiredService<PlcToCallMapperService>();

        if (!projectService.IsLoaded)
        {
            _logger.LogWarning("DS project is not loaded. PLC logs will not update dsp.db.");
            return true;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (mapper.IsInitialized)
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
            using var scope = _scopeFactory.CreateScope();
            var plcRepo = scope.ServiceProvider.GetRequiredService<IPlcRepository>();
            var mapper = scope.ServiceProvider.GetRequiredService<PlcToCallMapperService>();

            // InTag 매핑 검증: PLC에 없는 InTag 제거 → OutTag Falling으로 Finish 전이 가능
            var tags = await plcRepo.GetAllTagsAsync();
            var tagMatchMode = _configuration.GetValue<string>("PlcDatabase:TagMatchMode") ?? "Address";
            var plcTagKeys = tags
                .Select(t => tagMatchMode.Equals("Address", StringComparison.OrdinalIgnoreCase) ? t.Address : t.Name)
                .ToHashSet();

            mapper.ValidateWithPlcTags(plcTagKeys);

            // Flow Tail Call PLC 매핑 교차 검증
            ValidateFlowTailCallMappings(mapper);

            _logger.LogInformation("Post-initialization completed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-initialization failed");
        }
    }

    /// <summary>
    /// Flow의 MovingEndName(Tail) Call이 PLC 태그 매핑이 올바른지 교차 검증.
    /// 문제가 있는 Flow를 경고 로그로 출력.
    /// </summary>
    private void ValidateFlowTailCallMappings(PlcToCallMapperService mapper)
    {
        var flows = _dspDbService.Snapshot.Flows;
        int warningCount = 0;

        foreach (var flow in flows)
        {
            if (string.IsNullOrEmpty(flow.MovingEndName)) continue;

            // MovingEndName은 "FlowName.CallName" 형식
            var parts = flow.MovingEndName.Split('.', 2);
            if (parts.Length < 2) continue;
            var tailCallName = parts[1];

            var tagInfo = mapper.GetCallTags(tailCallName);
            if (tagInfo == null)
            {
                _logger.LogWarning(
                    "[DIAG] Flow '{FlowName}': Tail Call '{TailCall}' has NO PLC tag mapping. Flow will never reach Finish.",
                    flow.FlowName, tailCallName);
                warningCount++;
            }
            else
            {
                var (inTag, outTag) = tagInfo.Value;
                var hasInTag = !string.IsNullOrEmpty(inTag);
                if (hasInTag)
                {
                    _logger.LogWarning(
                        "[DIAG] Flow '{FlowName}': Tail Call '{TailCall}' has InTag='{InTag}'. " +
                        "Finish requires InTag Rising. If InTag never fires, Flow will be stuck in Going.",
                        flow.FlowName, tailCallName, inTag);
                    warningCount++;
                }
            }
        }

        if (warningCount > 0)
        {
            _logger.LogWarning("[DIAG] {Count} Flow(s) may have Tail Call mapping issues. Check logs above.", warningCount);
        }
        else
        {
            _logger.LogInformation("[DIAG] All Flow Tail Calls have valid PLC tag mappings (no InTag issues).");
        }
    }

    // ──────────────────────────────────────────────
    //  Data reading
    // ──────────────────────────────────────────────

    private async Task ReadAndProcessDataAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPlcRepository>();

        if (_simulationMode && !_simulationReplayCompleted)
            await ReplayHistoricalWindowAsync(repository, stoppingToken);
        else
            await ReadLiveDataAsync(repository, stoppingToken);
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
        using var scope = _scopeFactory.CreateScope();
        var mapper = scope.ServiceProvider.GetRequiredService<PlcToCallMapperService>();
        var stateTracker = scope.ServiceProvider.GetRequiredService<PlcTagStateTrackerService>();
        var statistics = scope.ServiceProvider.GetRequiredService<CallStatisticsService>();
        var dspRepo = scope.ServiceProvider.GetRequiredService<IDspRepository>();
        var plcRepo = scope.ServiceProvider.GetRequiredService<IPlcRepository>();

        if (!mapper.IsInitialized)
        {
            _logger.LogWarning("PlcToCallMapper not initialized. Skipping.");
            return;
        }

        int stateChangedCount = 0;

        foreach (var log in logs)
        {
            try
            {
                if (await ProcessSingleLogAsync(log, mapper, stateTracker, statistics, dspRepo, plcRepo, stoppingToken))
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
        var callKey = callInfo.ToCallKey();
        var currentState = await dspRepo.GetCallStateAsync(callKey);
        var (newState, stateChanged) = mapper.DetermineCallState(tag.Name, tag.Address, edgeState, currentState);

        if (!stateChanged) return false;

        // 5. 상태별 처리
        if (newState == "Going")
            await HandleCallGoingAsync(callInfo, callKey, statistics, dspRepo, tag, stoppingToken);
        else if (newState == "Finish")
            await HandleCallFinishAsync(callInfo, callKey, statistics, dspRepo, tag, stoppingToken);

        return true;
    }

    // ──────────────────────────────────────────────
    //  State handlers
    // ──────────────────────────────────────────────

    private async Task HandleCallGoingAsync(
        CallMappingInfo callInfo, CallKey callKey,
        CallStatisticsService statistics, IDspRepository dspRepo,
        PlcTagEntity tag, CancellationToken stoppingToken)
    {
        var callName = callInfo.Call.Name;
        var flowName = callInfo.FlowName;
        var timestamp = DateTime.Now;

        await statistics.RecordGoingStartAsync(callName);
        await dspRepo.UpdateCallStateAsync(callKey, "Going");

        await WriteEventAsync(new CallStateChangedEvent { CallName = callName, NewState = "Going" }, stoppingToken);

        _flowMetricsService.OnCallGoingStarted(flowName, callName, timestamp);
        await TryUpdateFlowStateAsync(dspRepo, flowName, callName, "Going");

        _logger.LogInformation("Call '{CallName}' (Flow: {FlowName}): Ready → Going (Tag: {TagName})",
            callName, flowName, tag.Name);
    }

    private async Task HandleCallFinishAsync(
        CallMappingInfo callInfo, CallKey callKey,
        CallStatisticsService statistics, IDspRepository dspRepo,
        PlcTagEntity tag, CancellationToken stoppingToken)
    {
        var callName = callInfo.Call.Name;
        var flowName = callInfo.FlowName;

        // Going → Finish (통계 업데이트)
        var (_, finishTime, goingTime, avg, stdDev, goingCount) = statistics.RecordGoingFinish(callName);
        await dspRepo.UpdateCallWithStatisticsAsync(callKey, "Finish", goingTime, avg, stdDev);

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
        await dspRepo.UpdateCallStateAsync(callKey, "Ready");

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
}
