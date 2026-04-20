using Microsoft.AspNetCore.SignalR;
using DSPilot.Hubs;
using DSPilot.Repositories;
using DSPilot.Models.Plc;

namespace DSPilot.Services;

/// <summary>
/// PLC 데이터베이스를 모니터링하여 태그 변경사항을 SignalR로 실시간 브로드캐스트
/// PlcCaptureService가 DB에 저장하는 데이터를 주기적으로 polling하여 변경 감지
/// </summary>
public class PlcDatabaseMonitorService : BackgroundService
{
    private readonly ILogger<PlcDatabaseMonitorService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<MonitoringHub> _hubContext;
    private readonly PlcToCallMapperService _callMapper;

    private readonly Dictionary<string, string> _lastTagValues = new();
    private readonly int _pollIntervalMs = 500; // 500ms polling
    private long _lastCheckedMaxId;
    private int _changeCount;

    public PlcDatabaseMonitorService(
        ILogger<PlcDatabaseMonitorService> logger,
        IServiceScopeFactory scopeFactory,
        IHubContext<MonitoringHub> hubContext,
        PlcToCallMapperService callMapper)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _callMapper = callMapper;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PlcDatabaseMonitorService starting... (Poll interval: {Interval}ms)", _pollIntervalMs);

        // 초기화: 모든 태그의 현재 상태를 배치 쿼리로 로드
        await InitializeTagStatesAsync();

        _logger.LogInformation("Tag states initialized: {Count} tags, starting from log ID {MaxId}",
            _lastTagValues.Count, _lastCheckedMaxId);

        // 주기적으로 데이터베이스 polling (델타 기반)
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollDatabaseForChangesAsync(stoppingToken);
                await Task.Delay(_pollIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in database polling loop");
                await Task.Delay(1000, stoppingToken); // Error backoff
            }
        }

        _logger.LogInformation("PlcDatabaseMonitorService stopped. Total changes detected: {Count}", _changeCount);
    }

    /// <summary>
    /// 모든 태그의 현재 상태를 단일 배치 쿼리로 로드
    /// </summary>
    private async Task InitializeTagStatesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var plcRepo = scope.ServiceProvider.GetRequiredService<IPlcRepository>();

        try
        {
            var (tagValues, maxLogId) = await plcRepo.GetLatestValuePerTagAsync();

            foreach (var (address, value) in tagValues)
            {
                _lastTagValues[address] = value;
            }

            _lastCheckedMaxId = maxLogId;

            _logger.LogDebug("Initialized {Count} tag states from database (maxLogId: {MaxId})",
                _lastTagValues.Count, _lastCheckedMaxId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize tag states");
        }
    }

    /// <summary>
    /// 마지막 확인 이후 새로 추가된 로그만 조회하여 변경 감지 (델타 방식)
    /// 기존 N+1 쿼리 대신 단일 쿼리로 모든 변경사항을 감지
    /// </summary>
    private async Task PollDatabaseForChangesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var plcRepo = scope.ServiceProvider.GetRequiredService<IPlcRepository>();

        try
        {
            var newLogs = await plcRepo.GetLogsAfterIdAsync(_lastCheckedMaxId);

            if (newLogs.Count == 0)
                return;

            // 최대 ID 갱신
            _lastCheckedMaxId = newLogs.Max(l => l.Id);

            // 각 로그를 순서대로 처리하여 변경 감지
            foreach (var log in newLogs)
            {
                var address = log.Address;
                if (string.IsNullOrEmpty(address))
                    continue;

                var currentValue = log.Value ?? "0";
                var previousValue = _lastTagValues.GetValueOrDefault(address, "0");

                // 상태 업데이트 (모든 로그에 대해)
                _lastTagValues[address] = currentValue;

                // 값이 변경되었는지 확인
                if (currentValue == previousValue)
                    continue;

                _changeCount++;

                _logger.LogInformation("Tag changed: {Address} {Prev} -> {Current}",
                    address, previousValue, currentValue);

                // 값 정규화 (true/false → 1/0)
                var normalizedCurrent = NormalizeValue(currentValue);
                var normalizedPrev = NormalizeValue(previousValue);

                // Call 매핑 조회
                var mapping = _callMapper.FindCallByTag("", address);

                if (mapping == null)
                {
                    _logger.LogTrace("No mapping found for tag: {Address}", address);
                    continue;
                }

                // Rising/Falling edge 판단
                var isRisingEdge = normalizedPrev == "0" && normalizedCurrent == "1";
                var isFallingEdge = normalizedPrev == "1" && normalizedCurrent == "0";

                if (!isRisingEdge && !isFallingEdge)
                    continue;

                var edgeType = isRisingEdge ? "Rising" : "Falling";
                var newState = mapping.IsInTag
                    ? (isRisingEdge ? "Going" : "Ready")
                    : (isRisingEdge ? "Done" : "Going");

                var prevState = mapping.IsInTag
                    ? (isRisingEdge ? "Ready" : "Going")
                    : (isRisingEdge ? "Going" : "Done");

                _logger.LogInformation(
                    "Broadcasting: Call={CallName}, Tag={Address}, Edge={EdgeType}, {PrevState} -> {NewState}",
                    mapping.Call.Name, address, edgeType, prevState, newState);

                // SignalR로 브로드캐스트
                await _hubContext.Clients.All.SendAsync(
                    "CallStateChanged",
                    new
                    {
                        CallName = mapping.Call.Name,
                        PreviousState = prevState,
                        NewState = newState,
                        Timestamp = log.DateTime,
                        TagAddress = address,
                        EdgeType = edgeType,
                        IsInTag = mapping.IsInTag
                    },
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling database for changes");
        }
    }

    /// <summary>
    /// 값 정규화: true/false → 1/0
    /// </summary>
    private string NormalizeValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "0";

        var lower = value.ToLowerInvariant();
        if (lower == "true" || lower == "1")
            return "1";

        return "0";
    }
}
