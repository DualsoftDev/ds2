using Microsoft.AspNetCore.SignalR;

namespace DSPilot.Hubs;

/// <summary>
/// SignalR Hub for real-time monitoring updates
/// Broadcasts Call state changes, Flow metrics, and PLC tag updates
/// </summary>
public class MonitoringHub : Hub
{
    private readonly ILogger<MonitoringHub> _logger;

    public MonitoringHub(ILogger<MonitoringHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
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
