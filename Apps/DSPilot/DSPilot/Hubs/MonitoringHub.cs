using DSPilot.Services;
using Microsoft.AspNetCore.SignalR;

namespace DSPilot.Hubs;

/// <summary>
/// SignalR Hub for real-time monitoring updates
/// Broadcasts Call state changes, Flow metrics, and PLC tag updates
/// </summary>
public class MonitoringHub : Hub
{
    private readonly ILogger<MonitoringHub> _logger;
    private readonly HubSubscriberService _hubSubscriber;

    public MonitoringHub(ILogger<MonitoringHub> logger, HubSubscriberService hubSubscriber)
    {
        _logger = logger;
        _hubSubscriber = hubSubscriber;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);

        // 사용자가 dspilot 페이지를 열어 모니터링 hub 에 붙은 시점 — Promaker(5051) 연결이
        // Disconnected 라면 다음 주기 대기를 건너뛰고 한 번 즉시 시도. fire-and-forget 으로
        // negotiate 응답을 막지 않음.
        _ = _hubSubscriber.NudgeConnectAsync();

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Client subscribes to specific Call updates
    /// </summary>
    public async Task SubscribeToCall(string callName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"call:{callName}");
        _logger.LogDebug("Client {ConnectionId} subscribed to call:{CallName}", Context.ConnectionId, callName);
    }

    /// <summary>
    /// Client unsubscribes from Call updates
    /// </summary>
    public async Task UnsubscribeFromCall(string callName)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"call:{callName}");
        _logger.LogDebug("Client {ConnectionId} unsubscribed from call:{CallName}", Context.ConnectionId, callName);
    }

    /// <summary>
    /// Client subscribes to specific Flow updates
    /// </summary>
    public async Task SubscribeToFlow(string flowName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"flow:{flowName}");
        _logger.LogDebug("Client {ConnectionId} subscribed to flow:{FlowName}", Context.ConnectionId, flowName);
    }

    /// <summary>
    /// Client unsubscribes from Flow updates
    /// </summary>
    public async Task UnsubscribeFromFlow(string flowName)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"flow:{flowName}");
        _logger.LogDebug("Client {ConnectionId} unsubscribed from flow:{FlowName}", Context.ConnectionId, flowName);
    }

    /// <summary>
    /// Send test event (for debugging)
    /// </summary>
    public async Task SendTestEvent()
    {
        _logger.LogInformation("Test event requested by client {ConnectionId}", Context.ConnectionId);

        await Clients.All.SendAsync("CallStateChanged", new
        {
            CallName = "TestCall_" + DateTime.Now.ToString("HHmmss"),
            PreviousState = "Ready",
            NewState = "Going",
            Timestamp = DateTime.Now
        });
    }
}
