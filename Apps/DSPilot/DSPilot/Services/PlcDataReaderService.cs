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
    private readonly ChannelWriter<CallStateChangedEvent> _eventWriter;
    private readonly bool _simulationMode;
    private readonly TimeSpan _simulationWindow = TimeSpan.FromSeconds(1);

    private DateTime _lastReadTime;
    private DateTime? _simulationReplayEndTime;
    private bool _simulationReplayCompleted;
    private int _totalLogsRead = 0;

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
        _eventWriter = dspDbService.EventWriter;
        _simulationMode = _configuration.GetValue<bool>("PlcDatabase:SimulationMode");

        _lastReadTime = DateTime.Now;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("PLC Data Reader Service starting...");
        _logger.LogInformation("Simulation Mode Config: {SimMode}", _simulationMode);
        _logger.LogInformation("========================================");

        // 초기화 및 연결 테스트
        if (!await InitializeAsync())
        {
            _logger.LogError("Failed to initialize PLC Data Reader Service");
            return;
        }

        _logger.LogInformation("InitializeAsync completed successfully");

        if (!await WaitForMapperInitializationAsync(stoppingToken))
        {
            _logger.LogError("PLC Data Reader Service stopped before DSP mapper initialization");
            return;
        }

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
                _logger.LogInformation("PLC Data Reader Service is stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading PLC data");
                await Task.Delay(5000, stoppingToken); // 오류 시 5초 대기
            }
        }

        _logger.LogInformation("PLC Data Reader Service stopped. Total logs read: {Count}", _totalLogsRead);
    }

    /// <summary>
    /// 초기화 및 연결 테스트
    /// </summary>
    private async Task<bool> InitializeAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IPlcRepository>();

            // 연결 테스트
            if (!await repository.TestConnectionAsync())
            {
                _logger.LogError("Database connection test failed");
                return false;
            }

            // 초기 정보 로드
            var plcs = await repository.GetAllPlcsAsync();
            var tags = await repository.GetAllTagsAsync();
            var totalLogs = await repository.GetTotalLogCountAsync();

            _logger.LogInformation("========================================");
            _logger.LogInformation("Database initialized successfully");
            _logger.LogInformation("Found {PlcCount} PLCs, {TagCount} tags, {LogCount} total logs",
                plcs.Count, tags.Count, totalLogs);

            // PLC 목록 출력
            foreach (var plc in plcs)
            {
                _logger.LogInformation("PLC: {PlcName} (ID: {PlcId}, Project: {ProjectId})",
                    plc.Name, plc.Id, plc.ProjectId);
            }

            _logger.LogInformation("Simulation Mode: {Mode}", _simulationMode);
            _logger.LogInformation("========================================");

            if (_simulationMode)
            {
                var oldestLogTime = await repository.GetOldestLogDateTimeAsync();
                var latestLogTime = await repository.GetLatestLogDateTimeAsync();

                _logger.LogInformation("Oldest log time: {Oldest}, Latest log time: {Latest}",
                    oldestLogTime?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "NULL",
                    latestLogTime?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "NULL");

                if (oldestLogTime == null || latestLogTime == null)
                {
                    _simulationReplayCompleted = true;
                    _logger.LogWarning("Simulation mode enabled, but PLC log data is empty (oldest={Oldest}, latest={Latest})",
                        oldestLogTime, latestLogTime);
                }
                else
                {
                    _lastReadTime = oldestLogTime.Value.AddTicks(-1);
                    _simulationReplayEndTime = latestLogTime.Value;
                    _simulationReplayCompleted = false;

                    _logger.LogInformation(
                        "Simulation mode enabled. Replaying PLC logs from {Start} to {End} in 1-second windows.",
                        oldestLogTime.Value,
                        latestLogTime.Value);
                }
            }
            else
            {
                _lastReadTime = DateTime.Now;
                _logger.LogInformation("Live mode enabled. Starting incremental read from {StartTime}", _lastReadTime);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database connection");
            return false;
        }
    }

    /// <summary>
    /// 데이터 읽기 및 처리
    /// </summary>
    private async Task ReadAndProcessDataAsync(CancellationToken stoppingToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPlcRepository>();

        if (_simulationMode && !_simulationReplayCompleted)
        {
            await ReplayHistoricalWindowAsync(repository, stoppingToken);
            return;
        }

        await ReadLiveDataAsync(repository, stoppingToken);
    }

    private async Task ReadLiveDataAsync(IPlcRepository repository, CancellationToken stoppingToken = default)
    {
        var pollTime = DateTime.Now;
        var newLogs = await repository.GetNewLogsAsync(_lastReadTime);

        if (newLogs.Count > 0)
        {
            LogReadBatch($"[LIVE {pollTime:HH:mm:ss}]", newLogs);
            await SaveToDspDatabaseAsync(newLogs, stoppingToken);
            _totalLogsRead += newLogs.Count;
            _lastReadTime = newLogs.Max(log => log.DateTime);
        }
    }

    /// <summary>
    /// 과거 데이터를 1초 윈도우로 재생
    /// </summary>
    private async Task ReplayHistoricalWindowAsync(IPlcRepository repository, CancellationToken stoppingToken = default)
    {
        var windowStartExclusive = _lastReadTime;
        var windowEndInclusive = windowStartExclusive + _simulationWindow;
        var replayLogs = await repository.GetLogsInRangeAsync(windowStartExclusive, windowEndInclusive);

        if (replayLogs.Count > 0)
        {
            LogReadBatch(
                $"[SIM {windowStartExclusive:yyyy-MM-dd HH:mm:ss.fff} ~ {windowEndInclusive:yyyy-MM-dd HH:mm:ss.fff}]",
                replayLogs);
            await SaveToDspDatabaseAsync(replayLogs, stoppingToken);
            _totalLogsRead += replayLogs.Count;
        }

        _lastReadTime = windowEndInclusive;

        if (_simulationReplayEndTime.HasValue && windowEndInclusive >= _simulationReplayEndTime.Value)
        {
            _simulationReplayCompleted = true;
            _lastReadTime = _simulationReplayEndTime.Value;

            _logger.LogInformation(
                "Simulation replay completed at {ReplayEnd}. Switching to live incremental mode.",
                _simulationReplayEndTime.Value);
        }
    }

    private void LogReadBatch(string prefix, List<PlcTagLogEntity> logs)
    {
        _logger.LogInformation(
            "{Prefix} Read {Count} logs (Total: {Total})",
            prefix,
            logs.Count,
            _totalLogsRead + logs.Count);

        foreach (var log in logs.Take(3))
        {
            _logger.LogDebug(
                "  Log #{LogId}: TagId={TagId}, DateTime={DateTime}, Value={Value}",
                log.Id,
                log.PlcTagId,
                log.DateTime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                log.Value);
        }
    }

    /// <summary>
    /// dsp.db 저장 준비가 끝날 때까지 대기
    /// </summary>
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
                _logger.LogInformation("PLC Data Reader Service detected initialized DSP mapper.");
                return true;
            }

            _logger.LogInformation("Waiting for DSP mapper initialization...");
            await Task.Delay(500, stoppingToken);
        }

        return false;
    }

    /// <summary>
    /// dsp.db에 저장 (엣지 감지 및 상태 전환 로직 포함)
    /// </summary>
    private async Task SaveToDspDatabaseAsync(List<PlcTagLogEntity> logs, CancellationToken stoppingToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var mapper = scope.ServiceProvider.GetRequiredService<PlcToCallMapperService>();
        var stateTracker = scope.ServiceProvider.GetRequiredService<PlcTagStateTrackerService>();
        var statistics = scope.ServiceProvider.GetRequiredService<CallStatisticsService>();
        var dspRepo = scope.ServiceProvider.GetRequiredService<IDspRepository>();
        var plcRepo = scope.ServiceProvider.GetRequiredService<IPlcRepository>();

        if (!mapper.IsInitialized)
        {
            _logger.LogWarning("PlcToCallMapper not initialized. Skipping dsp.db save.");
            return;
        }

        int processedCount = 0;
        int mappedCount = 0;
        int stateChangedCount = 0;

        foreach (var log in logs)
        {
            try
            {
                // 1. plcTag 정보 조회
                var tag = await plcRepo.GetTagByIdAsync(log.PlcTagId);
                if (tag == null)
                {
                    _logger.LogWarning("Tag ID {TagId} not found", log.PlcTagId);
                    continue;
                }

                processedCount++;

                // 2. 값 정규화 (boolean → "0"/"1")
                var normalizedValue = NormalizeValue(log.Value);

                // 3. 엣지 감지
                var edgeState = stateTracker.UpdateTagValue(tag.Name, normalizedValue);

                // 4. Call 매핑 확인 (Name 또는 Address 기준)
                var callInfo = mapper.FindCallByTag(tag.Name, tag.Address);
                if (callInfo == null)
                {
                    // 매핑되지 않은 태그 (정상, 로그 무시)
                    _logger.LogDebug("Tag '{TagName}' (Address: '{Address}') not mapped to any Call",
                        tag.Name, tag.Address);
                    continue;
                }

                mappedCount++;
                _logger.LogDebug("Tag '{TagName}' (Address: '{Address}') mapped to Call '{CallName}' (IsInTag: {IsInTag})",
                    tag.Name, tag.Address, callInfo.Value.Call.Name, callInfo.Value.IsInTag);

                var call = callInfo.Value.Call;

                // 5. 현재 Call 상태 조회
                var currentCallState = await dspRepo.GetCallStateAsync(call.Name);

                // 6. 새 상태 결정
                var (newState, stateChanged) = mapper.DetermineCallState(
                    tag.Name,
                    tag.Address,
                    edgeState,
                    currentCallState
                );

                if (!stateChanged)
                {
                    // 상태 변화 없음
                    _logger.LogDebug(
                        "No state change for Call '{CallName}' (Current: {State}, Tag: {TagName}, Edge: Rising={Rising}, Falling={Falling})",
                        call.Name, currentCallState, tag.Name, edgeState.IsRisingEdge(), edgeState.IsFallingEdge());
                    continue;
                }

                stateChangedCount++;

                // 7. 상태별 처리
                if (newState == "Going")
                {
                    // Going 시작 시간 기록
                    var timestamp = DateTime.Now;
                    statistics.RecordGoingStart(call.Name);
                    await dspRepo.UpdateCallStateAsync(call.Name, "Going");

                    // 채널을 통해 UI에 즉시 Going 상태 전달 (DB 폴링 대기 불필요)
                    await WriteEventAsync(new CallStateChangedEvent
                    {
                        CallName = call.Name,
                        NewState = "Going",
                    }, stoppingToken);

                    // FlowMetricsService 이벤트 발생
                    _flowMetricsService.OnCallGoingStarted(call.Name, timestamp);

                    _logger.LogInformation(
                        "Call '{CallName}': State changed Ready → Going (Tag: {TagName})",
                        call.Name, tag.Name);
                }
                else if (newState == "Finish")
                {
                    // Going 시간 계산 및 통계 업데이트
                    var (startTime, finishTime, goingTime, avg, stdDev, goingCount) = statistics.RecordGoingFinish(call.Name);

                    await dspRepo.UpdateCallWithStatisticsAsync(
                        call.Name,
                        "Finish",
                        goingTime,
                        avg,
                        stdDev
                    );

                    // 채널을 통해 UI에 즉시 Finish 상태 전달
                    await WriteEventAsync(new CallStateChangedEvent
                    {
                        CallName         = call.Name,
                        NewState         = "Finish",
                        PreviousGoingTime = goingTime,
                        AverageGoingTime = avg,
                        GoingCount       = goingCount,
                    }, stoppingToken);

                    // FlowMetricsService 이벤트 발생
                    _flowMetricsService.OnCallFinished(call.Name, finishTime);

                    _logger.LogInformation(
                        "Call '{CallName}': State changed Going → Finish (Tag: {TagName}, Time: {Time}ms)",
                        call.Name, tag.Name, goingTime);

                    // Finish → Ready 자동 전환 (100ms 후)
                    await Task.Delay(100, stoppingToken);
                    await dspRepo.UpdateCallStateAsync(call.Name, "Ready");

                    // 채널을 통해 UI에 즉시 Ready 상태 전달
                    await WriteEventAsync(new CallStateChangedEvent
                    {
                        CallName   = call.Name,
                        NewState   = "Ready",
                        GoingCount = goingCount,
                    }, stoppingToken);

                    _logger.LogDebug("Call '{CallName}': State changed Finish → Ready", call.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing log ID {LogId}", log.Id);
            }
        }

        _logger.LogDebug(
            "SaveToDspDatabase completed: {TotalLogs} logs, {ProcessedCount} processed, {MappedCount} mapped, {StateChangedCount} state changed",
            logs.Count, processedCount, mappedCount, stateChangedCount);
    }

    /// <summary>
    /// DspDbService 채널에 이벤트를 안전하게 발행한다.
    /// 채널이 닫혀 있으면 (앱 종료 중) 무시한다.
    /// </summary>
    private async Task WriteEventAsync(CallStateChangedEvent evt, CancellationToken ct)
    {
        try
        {
            await _eventWriter.WriteAsync(evt, ct);
        }
        catch (ChannelClosedException)
        {
            // 앱 종료 중 — 무시
        }
        catch (OperationCanceledException)
        {
            // 서비스 중지 중 — 무시
        }
    }

    /// <summary>
    /// PLC 값을 EdgeDetection이 인식할 수 있는 "0" 또는 "1"로 정규화
    /// </summary>
    private static string NormalizeValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "0";

        var lowerValue = value.Trim().ToLowerInvariant();

        return lowerValue switch
        {
            "1" or "true" or "on" => "1",
            "0" or "false" or "off" => "0",
            _ => "0" // 기본값
        };
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PLC Data Reader Service is stopping gracefully...");
        await base.StopAsync(cancellationToken);
    }
}
