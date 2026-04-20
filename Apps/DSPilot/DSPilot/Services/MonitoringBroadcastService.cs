using Microsoft.AspNetCore.SignalR;
using DSPilot.Hubs;
using DSPilot.Models;

namespace DSPilot.Services;

/// <summary>
/// Subscribes to CallStateNotificationService and broadcasts updates via SignalR
/// </summary>
public class MonitoringBroadcastService : BackgroundService
{
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

        _subscription = _notificationService.StateChanges.Subscribe(
            onNext: async evt =>
            {
                try
                {
                    // Broadcast to all clients
                    await _hubContext.Clients.All.SendAsync("CallStateChanged", new
                    {
                        evt.CallName,
                        evt.PreviousState,
                        evt.NewState,
                        evt.Timestamp
                    }, stoppingToken);

                    // Broadcast to call-specific group
                    await _hubContext.Clients.Group($"call:{evt.CallName}")
                        .SendAsync("StateChanged", new
                        {
                            evt.PreviousState,
                            evt.NewState,
                            evt.Timestamp
                        }, stoppingToken);

                    _logger.LogInformation("📡 SignalR broadcast sent: {CallName} {PrevState} → {NewState}",
                        evt.CallName, evt.PreviousState, evt.NewState);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to broadcast state change for {CallName}", evt.CallName);
                }
            },
            onError: ex =>
            {
                _logger.LogError(ex, "State change stream error");
            },
            onCompleted: () =>
            {
                _logger.LogInformation("State change stream completed");
            });

        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MonitoringBroadcastService stopping...");
        _subscription?.Dispose();
        await base.StopAsync(cancellationToken);
    }
}
