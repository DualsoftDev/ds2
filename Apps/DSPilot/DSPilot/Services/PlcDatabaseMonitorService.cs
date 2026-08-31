// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
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

    // ★키는 plcTagId — 이 서비스는 whole-model 폴러라 systemId 로 조회를 좁힐 수 없다.
    // 주소로 묶으면 같은 주소를 쓰는 두 PLC 의 값이 번갈아 덮여 가짜 변화가 브로드캐스트된다.
    // plcTagId 는 1단계에서 (plcId, address) 단위로 갈라졌으므로 그 자체가 PLC 별 유일 키다.
    private readonly Dictionary<int, string> _lastTagValues = new();
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

        // 초기화 성공 전에는 폴링을 시작하지 않는다 — 실패를 삼키고 진행하면 워터마크가 0인 채
        // GetLogsAfterIdAsync(0) 이 과거 로그 전체를 대상으로 도는 폭주 경로가 된다(부팅 시
        // SQLite 락 경합 1회로 진입 가능). UserTagAlertService 의 시드 가드와 동일 컨벤션.
        var initialized = false;

        // 주기적으로 데이터베이스 polling (델타 기반)
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!initialized)
                {
                    initialized = await InitializeTagStatesAsync();
                    if (!initialized)
                    {
                        await Task.Delay(5000, stoppingToken); // 초기화 재시도 backoff
                        continue;
                    }
                    _logger.LogInformation("Tag states initialized: {Count} tags, starting from log ID {MaxId}",
                        _lastTagValues.Count, _lastCheckedMaxId);
                }

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
    /// 모든 태그의 현재 상태를 단일 배치 쿼리로 로드. 실패 시 false — 호출측이 성공할 때까지
    /// 재시도하며, 그동안 폴링은 시작되지 않는다(워터마크 0 폭주 방지).
    /// </summary>
    private async Task<bool> InitializeTagStatesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var plcRepo = scope.ServiceProvider.GetRequiredService<IPlcRepository>();

        try
        {
            // 태그 상태 시드는 하지 않는다 — 주소 키 딕셔너리는 멀티 PLC 에서 collapse 되므로,
            // 첫 폴링의 로그(plcTagId 포함)로 상태를 채운다. 시드가 없으면 첫 변화 1회가
            // "직전값 없음"으로 흘러가는데, 이 서비스는 PlcDebug 화면 브로드캐스트 전용이라
            // 무해하다(판정·기록 경로 아님).
            _lastCheckedMaxId = await plcRepo.GetMaxLogIdAsync();

            _logger.LogDebug("Initialized delta-poll watermark from database (maxLogId: {MaxId})",
                _lastCheckedMaxId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize tag states — retrying before polling starts");
            return false;
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
                var previousValue = _lastTagValues.GetValueOrDefault(log.PlcTagId, "0");

                // 상태 업데이트 (모든 로그에 대해) — 키는 PLC 별로 유일한 plcTagId.
                _lastTagValues[log.PlcTagId] = currentValue;

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

                // 진영 B (PLC 기준): OutTag↑ = 명령 = Ready→Going(시작), InTag↑ = 응답 = Going→Done(완료).
                //   falling 은 역전이(복귀)로만 표시.
                var edgeType = isRisingEdge ? "Rising" : "Falling";
                var newState = mapping.IsInTag
                    ? (isRisingEdge ? "Done" : "Going")
                    : (isRisingEdge ? "Going" : "Ready");

                var prevState = mapping.IsInTag
                    ? (isRisingEdge ? "Going" : "Done")
                    : (isRisingEdge ? "Ready" : "Going");

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
