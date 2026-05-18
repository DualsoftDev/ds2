using System.Threading.Channels;
using Ds2.Backend.Common;
using Microsoft.AspNetCore.SignalR.Client;

namespace DSPilot.Services;

/// <summary>
/// SignalR 클라이언트 무한 재연결 정책. 기본 WithAutomaticReconnect 는 4회(0/2/10/30s) 후 포기 →
/// Promaker 가 42초 넘게 다운되면 DSPilot 영구 disconnected. 운영상 Promaker 가 임의 시점에 재시작될 수
/// 있으므로 절대 포기하지 않는 정책 사용 — 초기엔 짧은 간격, 이후 1분 간격으로 계속 시도.
/// </summary>
internal sealed class InfinitePromakerRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
        retryContext.PreviousRetryCount switch
        {
            0 => TimeSpan.FromSeconds(0),
            1 => TimeSpan.FromSeconds(2),
            2 => TimeSpan.FromSeconds(5),
            3 => TimeSpan.FromSeconds(10),
            4 => TimeSpan.FromSeconds(30),
            _ => TimeSpan.FromSeconds(60),
        };
}

/// <summary>
/// Promaker SignalHub 클라이언트. 기본 URL = http://localhost:5051/hub/signal (Promaker Monitoring 모드).
/// Control 모드(5050) 또는 원격 인스턴스 구독으로 전환하려면 Settings 페이지 'Hub' 섹션에서 URL 변경 후
/// DSPilot 서비스 재시작. OnTagChanged 신호는 채널 큐에 enqueue → 단일 컨슈머가 순차적으로
/// SimulationEngineService 로 위임. SignalR client 의 콜백 동시 진입 가능성에 대비한 defense-in-depth
/// + Promaker idle 시 30초 server-timeout 으로 인한 잡음 reconnect 차단.
/// </summary>
public sealed class HubSubscriberService : BackgroundService
{
    private readonly ILogger<HubSubscriberService> _logger;
    private readonly IConfiguration _configuration;
    private readonly SimulationEngineService _engineService;

    private HubConnection? _connection;
    private HashSet<string> _acceptedSources = new(StringComparer.OrdinalIgnoreCase);

    // StartAsync 호출 직렬화 — 시작용 retry loop 와 NudgeConnectAsync 가 동시에 StartAsync 를
    // 호출하면 SignalR client 가 InvalidOperationException("Cannot start the connection while it
    // is in the Connecting state.") 던진다.
    private readonly SemaphoreSlim _startGate = new(1, 1);

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
        var hubUrl = _configuration["Hub:Url"] ?? "http://localhost:5051/hub/signal";
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

        // 무한 재시도 정책 — Promaker 가 언제 재시작되어도 자동으로 다시 붙도록.
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new InfinitePromakerRetryPolicy())
            .Build();

        // Promaker idle 시 30초 server-timeout reconnect 잡음 차단 — 5분으로 완화
        _connection.ServerTimeout = TimeSpan.FromMinutes(5);
        _connection.KeepAliveInterval = TimeSpan.FromMinutes(2);

        _connection.On<string, string, string>(HubMethod.OnTagChanged, OnHubTagChanged);
        // Batch 변형 — 송신측이 짧은 윈도우 내 변경을 묶어 1프레임으로 보낸다. 동일 처리 경로를 재사용.
        _connection.On<TagWrite[]>(HubMethod.OnTagsChanged, OnHubTagsChanged);

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
            await _startGate.WaitAsync(ct);
            try
            {
                if (connection.State != HubConnectionState.Disconnected) return;
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
            }
            finally
            {
                _startGate.Release();
            }

            try { await Task.Delay(delayMs, ct); }
            catch (OperationCanceledException) { return; }
            delayMs = Math.Min(delayMs * 2, maxDelayMs);
        }
    }

    /// <summary>
    /// 브라우저 클라이언트가 모니터링 페이지에 접속했을 때 호출 — 현재 Disconnected 상태면
    /// 다음 주기 대기를 건너뛰고 즉시 StartAsync 한 번 시도. 이미 Connected/Connecting 이거나
    /// SignalR auto-reconnect 가 백오프 중(Reconnecting)이면 no-op. 동시 호출은 게이트로 직렬화.
    /// </summary>
    public async Task NudgeConnectAsync(CancellationToken ct = default)
    {
        var conn = _connection;
        if (conn is null) return;

        var state = conn.State;
        if (state != HubConnectionState.Disconnected) return;

        // 게이트가 이미 점유 중이면(= 다른 StartAsync 가 in-flight) 그쪽이 처리하도록 양보.
        if (!await _startGate.WaitAsync(0, ct)) return;
        try
        {
            // 게이트 획득 후 재확인 — 대기 중 다른 경로가 상태를 바꿨을 수 있음.
            if (conn.State != HubConnectionState.Disconnected) return;

            _logger.LogInformation("[Hub] Connect nudged by client visit");
            try
            {
                await conn.StartAsync(ct);
                _logger.LogInformation("[Hub] Connected (via client-visit nudge)");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Hub] Nudge connect failed — periodic retry continues");
            }
        }
        finally
        {
            _startGate.Release();
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

    private void OnHubTagsChanged(TagWrite[] items)
    {
        if (items is null || items.Length == 0) return;
        var writer = _signalChannel.Writer;
        foreach (var it in items)
        {
            if (!_acceptedSources.Contains(it.Source))
            {
                _logger.LogTrace("[Hub] Ignored {Address}={Value} from={Source}", it.Address, it.Value, it.Source);
                continue;
            }
            if (!writer.TryWrite(new HubSignal(it.Address, it.Value, it.Source)))
                _logger.LogWarning("[Hub] Signal channel write dropped for {Address}", it.Address);
        }
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
