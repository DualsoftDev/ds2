using System.Threading.Channels;
using Ds2.Backend.Common;
using Microsoft.AspNetCore.SignalR.Client;

namespace DSPilot.Services;

/// <summary>
/// Promaker SignalHub(localhost:5050/hub/signal) 클라이언트.
/// OnTagChanged 신호를 채널 큐에 enqueue → 단일 컨슈머가 순차적으로 SimulationEngineService 로 위임.
/// SignalR client 의 콜백 동시 진입 가능성에 대비한 defense-in-depth + Promaker idle 시
/// 30초 server-timeout 으로 인한 잡음 reconnect 차단.
/// </summary>
public sealed class HubSubscriberService : BackgroundService
{
    private readonly ILogger<HubSubscriberService> _logger;
    private readonly IConfiguration _configuration;
    private readonly SimulationEngineService _engineService;

    private HubConnection? _connection;
    private HashSet<string> _acceptedSources = new(StringComparer.OrdinalIgnoreCase);

    private readonly Channel<HubSignal> _signalChannel = Channel.CreateUnbounded<HubSignal>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public HubSubscriberService(
        ILogger<HubSubscriberService> logger,
        IConfiguration configuration,
        SimulationEngineService engineService)
    {
        _logger = logger;
        _configuration = configuration;
        _engineService = engineService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hubUrl = _configuration["Hub:Url"] ?? "http://localhost:5050/hub/signal";
        var configuredSources = _configuration.GetSection("Hub:AcceptedSources").Get<string[]>()
            ?? new[] { HubSource.Control, HubSource.VirtualPlant, HubSource.Plc };
        _acceptedSources = new HashSet<string>(configuredSources, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "[Hub] Subscriber starting — url={Url}, acceptedSources={Sources}",
            hubUrl, string.Join(",", _acceptedSources));

        // 엔진 미리 초기화
        _engineService.TryEnsureInitialized();

        // 단일 컨슈머 시작 — Hub 콜백이 어떤 동시성으로 호출되든 직렬화 보장
        var consumerTask = Task.Run(() => ConsumeSignalsAsync(stoppingToken), stoppingToken);

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        // Promaker idle 시 30초 server-timeout reconnect 잡음 차단 — 5분으로 완화
        _connection.ServerTimeout = TimeSpan.FromMinutes(5);
        _connection.KeepAliveInterval = TimeSpan.FromMinutes(2);

        _connection.On<string, string, string>(HubMethod.OnTagChanged, OnHubTagChanged);

        _connection.Reconnecting += ex =>
        {
            _logger.LogWarning(ex, "[Hub] Reconnecting...");
            return Task.CompletedTask;
        };
        _connection.Reconnected += _ =>
        {
            _logger.LogInformation("[Hub] Reconnected");
            return Task.CompletedTask;
        };
        _connection.Closed += ex =>
        {
            _logger.LogWarning(ex, "[Hub] Closed");
            return Task.CompletedTask;
        };

        await ConnectWithRetryAsync(_connection, hubUrl, stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }

        _signalChannel.Writer.TryComplete();
        try { await consumerTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Hub] Subscriber stopping...");
        _signalChannel.Writer.TryComplete();
        if (_connection is not null)
        {
            try { await _connection.StopAsync(cancellationToken); } catch { /* ignore */ }
            try { await _connection.DisposeAsync(); } catch { /* ignore */ }
            _connection = null;
        }
        await base.StopAsync(cancellationToken);
    }

    private async Task ConnectWithRetryAsync(HubConnection connection, string hubUrl, CancellationToken ct)
    {
        var delayMs = 1000;
        const int maxDelayMs = 10_000;

        while (!ct.IsCancellationRequested && connection.State == HubConnectionState.Disconnected)
        {
            try
            {
                _logger.LogInformation("[Hub] Connecting to {Url}", hubUrl);
                await connection.StartAsync(ct);
                _logger.LogInformation("[Hub] Connected");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "[Hub] Connect failed ({Msg}) — retry in {DelayMs}ms",
                    ex.Message, delayMs);
                try { await Task.Delay(delayMs, ct); }
                catch (OperationCanceledException) { return; }
                delayMs = Math.Min(delayMs * 2, maxDelayMs);
            }
        }
    }

    private void OnHubTagChanged(string address, string value, string source)
    {
        if (!_acceptedSources.Contains(source))
        {
            _logger.LogTrace("[Hub] Ignored {Address}={Value} from={Source}", address, value, source);
            return;
        }
        // 채널에 enqueue만. SignalR 콜백 스레드는 즉시 반환 — 동시 진입해도 컨슈머가 직렬 처리.
        if (!_signalChannel.Writer.TryWrite(new HubSignal(address, value, source)))
            _logger.LogWarning("[Hub] Signal channel write dropped for {Address}", address);
    }

    private async Task ConsumeSignalsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var sig in _signalChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    _engineService.HandleHubTagChanged(sig.Address, sig.Value, sig.Source);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Hub] Failed to process {Address}={Value} from={Source}",
                        sig.Address, sig.Value, sig.Source);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private readonly record struct HubSignal(string Address, string Value, string Source);
}
