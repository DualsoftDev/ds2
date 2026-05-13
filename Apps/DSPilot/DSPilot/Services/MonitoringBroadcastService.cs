using System.Reactive.Linq;
using Microsoft.AspNetCore.SignalR;
using DSPilot.Hubs;
using DSPilot.Models;

namespace DSPilot.Services;

/// <summary>
/// Subscribes to CallStateNotificationService and broadcasts updates via SignalR.
/// 짧은 윈도우(<see cref="BufferWindowMs"/>)로 이벤트를 묶어 한 SignalR 프레임 으로 전송 →
/// 고빈도 변화 시 브라우저 측 메시지 처리/재렌더 비용 감소.
/// </summary>
public class MonitoringBroadcastService : BackgroundService
{
    /// <summary>여러 CallState 변경을 묶는 윈도우(ms). 짧을수록 latency↓ batch 효율↓.</summary>
    private const int BufferWindowMs = 50;

    private readonly ILogger<MonitoringBroadcastService> _logger;
    private readonly CallStateNotificationService _notificationService;
    private readonly IHubContext<MonitoringHub> _hubContext;
    private IDisposable? _subscription;

    public MonitoringBroadcastService(
        ILogger<MonitoringBroadcastService> logger,
        CallStateNotificationService notificationService,
        IHubContext<MonitoringHub> hubContext)
    {
        _logger = logger;
        _notificationService = notificationService;
        _hubContext = hubContext;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MonitoringBroadcastService starting...");

        _subscription = _notificationService.StateChanges
            .Buffer(TimeSpan.FromMilliseconds(BufferWindowMs))
            .Where(batch => batch.Count > 0)
            .Subscribe(
                onNext: batch => _ = BroadcastBatchAsync(batch, stoppingToken),
                onError: ex => _logger.LogError(ex, "State change stream error"),
                onCompleted: () => _logger.LogInformation("State change stream completed"));

        return Task.CompletedTask;
    }

    private async Task BroadcastBatchAsync(IList<CallStateChangedEvent> batch, CancellationToken ct)
    {
        try
        {
            // 묶음 전송 — N개 변화를 1개 프레임으로. 브라우저는 배열을 받아 일괄 처리.
            var payload = batch.Select(e => new
            {
                e.CallName,
                e.PreviousState,
                e.NewState,
                e.Timestamp,
            }).ToArray();

            await _hubContext.Clients.All.SendAsync("CallStateChangedBatch", payload, ct);

            // 호환성: 기존 구독자(개별 CallStateChanged) 도 동일 묶음에서 dispatch.
            // call-specific group 은 묶음이 의미 없으므로 건별 송신 유지.
            foreach (var evt in batch)
            {
                await _hubContext.Clients.Group($"call:{evt.CallName}")
                    .SendAsync("StateChanged", new
                    {
                        evt.PreviousState,
                        evt.NewState,
                        evt.Timestamp
                    }, ct);
            }

            _logger.LogDebug("📡 SignalR batch broadcast — count={Count}", batch.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast state change batch (count={Count})", batch.Count);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MonitoringBroadcastService stopping...");
        _subscription?.Dispose();
        await base.StopAsync(cancellationToken);
    }
}
