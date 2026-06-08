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
/// DSPilot 서비스 재시작. OnTagChanged 신호는 <see cref="HubSignalProcessor"/> 채널 큐에 enqueue →
/// 단일 컨슈머가 순차적으로 SimulationEngineService 로 위임. SignalR client 의 콜백 동시 진입 가능성에
/// 대비한 defense-in-depth + Promaker idle 시 30초 server-timeout 으로 인한 잡음 reconnect 차단.
/// </summary>
public sealed class HubSubscriberService : BackgroundService
{
    private readonly ILogger<HubSubscriberService> _logger;
    private readonly IConfiguration _configuration;
    private readonly SimulationEngineService _engineService;
    private readonly PlcConnectionStatusTracker _plcStatusTracker;

    private HubConnection? _connection;
    private HubSignalProcessor? _processor;

    // StartAsync 호출 직렬화 — 시작용 retry loop 와 NudgeConnectAsync 가 동시에 StartAsync 를
    // 호출하면 SignalR client 가 InvalidOperationException("Cannot start the connection while it
    // is in the Connecting state.") 던진다.
    private readonly SemaphoreSlim _startGate = new(1, 1);

    // spec §SignalR — drop rate metric 임계. 누적 drop ≥ 100 + 마지막 Error 후 10s 경과 시 LogError.
    private long _lastDropReportTicks;
    private const long DropReportIntervalTicks = 10L * TimeSpan.TicksPerSecond;
    private const long DropReportThreshold = 100;
    private const int HandleHubTagMaxRetries = 3;

    /// <summary>
    /// 현재 Promaker hub 연결 상태. UI 헤더가 표시용으로 읽음.
    /// _connection 이 아직 생성되지 않은 부팅 초기 구간은 Disconnected 로 본다.
    /// </summary>
    public HubConnectionState CurrentStatus =>
        _connection?.State ?? HubConnectionState.Disconnected;

    /// <summary>현재까지 누적 channel drop count. 메트릭/진단용.</summary>
    public long ChannelDropCount => _processor?.DropCount ?? 0;

    /// <summary>현재까지 누적 dead-letter count. 메트릭/진단용.</summary>
    public long HandleHubDeadLetters => _processor?.DeadLetterCount ?? 0;

    /// <summary>
    /// 연결 상태 전이 시 발화. Blazor 헤더 배지가 구독해 StateHasChanged 호출.
    /// SignalR 의 Reconnecting/Reconnected/Closed 이벤트 + 우리 retry-loop / nudge 의 StartAsync
    /// 성공 시점 5개소에서 발화 — Connected 진입은 SignalR 의 Reconnected 가 "재연결" 만 커버하므로
    /// 첫 연결 성공도 수동으로 함께 발화해야 누락 없음.
    /// </summary>
    public event Action<HubConnectionState>? StatusChanged;

    /// <summary>v12 abnormal(SensorOpen/Short/ActionOver/Under) 수신 시 발화. UI 알림/패널이 구독.</summary>
    public event Action<AbnormalPayload>? AbnormalReceived;

    private void RaiseStatusChanged()
    {
        try { StatusChanged?.Invoke(CurrentStatus); }
        catch (Exception ex) { _logger.LogDebug(ex, "[Hub] StatusChanged subscriber threw"); }
    }

    public HubSubscriberService(
        ILogger<HubSubscriberService> logger,
        IConfiguration configuration,
        SimulationEngineService engineService,
        PlcConnectionStatusTracker plcStatusTracker)
    {
        _logger = logger;
        _configuration = configuration;
        _engineService = engineService;
        _plcStatusTracker = plcStatusTracker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hubUrl = _configuration["Hub:Url"] ?? "http://localhost:5051/hub/signal";
        // spec §SignalR — DefaultAcceptedSources 가 권위 default. config override 가능.
        var configuredSources = _configuration.GetSection("Hub:AcceptedSources").Get<string[]>()
            ?? HubSource.DefaultAcceptedSources;

        _processor = new HubSignalProcessor(
            acceptedSources: configuredSources,
            handleSignal: (addr, val, src) => _engineService.HandleHubTagChanged(addr, val, src),
            maxRetries: HandleHubTagMaxRetries,
            onDrop: OnChannelDrop,
            onRetry: (addr, ex, attempt, max) =>
                _logger.LogWarning(ex, "[Hub] Retry {Attempt}/{Max} for {Address}", attempt, max, addr),
            onDeadLetter: (addr, val, src, ex, dl) =>
                _logger.LogError(ex,
                    "[Hub] Dead-letter — {Address}={Value} from={Source} (총 dead-letter={DL})",
                    addr, val, src, dl));

        _logger.LogInformation(
            "[Hub] Subscriber starting — url={Url}, acceptedSources={Sources}",
            hubUrl, string.Join(",", configuredSources));

        // 엔진 미리 초기화
        _engineService.TryEnsureInitialized();

        // 단일 컨슈머 시작 — Hub 콜백이 어떤 동시성으로 호출되든 직렬화 보장
        var consumerTask = Task.Run(() => _processor.ConsumeAsync(stoppingToken), stoppingToken);

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
        // PLC 어댑터 연결 상태. 부팅 직후 OnConnectedAsync 가 현재 알려진 모든 어댑터 상태를 caller 전용 cast 로 push,
        // 이후엔 PlcGateway 의 connect/reconnect 전이마다 broadcast.
        _connection.On<PlcConnectionStatus>(HubMethod.OnPlcConnectionStatus, _plcStatusTracker.Apply);
        // v12 abnormal — Promaker(Control/Monitoring)가 발행한 경로이탈 이벤트 수신.
        _connection.On<AbnormalPayload>(HubMethod.OnAbnormal, OnHubAbnormal);

        _connection.Reconnecting += ex =>
        {
            _logger.LogWarning(ex, "[Hub] Reconnecting...");
            RaiseStatusChanged();
            return Task.CompletedTask;
        };
        _connection.Reconnected += _ =>
        {
            _logger.LogInformation("[Hub] Reconnected");
            RaiseStatusChanged();
            return Task.CompletedTask;
        };
        _connection.Closed += ex =>
        {
            _logger.LogWarning(ex, "[Hub] Closed");
            // Hub 가 끊긴 동안 stale PLC 상태로 사용자 오해 유발 방지 — 캐시 비움.
            // 재연결 시 SignalHub.OnConnectedAsync 가 다시 snapshot push.
            _plcStatusTracker.ClearAll();
            RaiseStatusChanged();
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

        _processor.SignalChannel.Writer.TryComplete();
        try { await consumerTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Hub] Subscriber stopping...");
        _processor?.SignalChannel.Writer.TryComplete();
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
                RaiseStatusChanged();
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
                RaiseStatusChanged();
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
        if (_processor is null) return;
        var result = _processor.TryEnqueue(address, value, source);
        if (result == EnqueueResult.Ignored)
            _logger.LogTrace("[Hub] Ignored {Address}={Value} from={Source}", address, value, source);
        // Dropped 는 onDrop 콜백에서 LogWarning/Error 처리됨.
    }

    private void OnHubTagsChanged(TagWrite[] items)
    {
        if (items is null || items.Length == 0 || _processor is null) return;
        foreach (var it in items)
        {
            var result = _processor.TryEnqueue(it.Address, it.Value, it.Source);
            if (result == EnqueueResult.Ignored)
                _logger.LogTrace("[Hub] Ignored {Address}={Value} from={Source}", it.Address, it.Value, it.Source);
        }
    }

    /// <summary>abnormal 이벤트 수신 — 로그 + AbnormalReceived 발화(UI 구독).</summary>
    private void OnHubAbnormal(AbnormalPayload payload)
    {
        _logger.LogWarning(
            "[Hub] Abnormal {Kind}({KindValue}) call={CallId} api={ApiCallId} elapsed={Elapsed} observed={Observed} mode={Mode}",
            payload.Kind, payload.KindValue, payload.CallId, payload.ApiCallId, payload.ElapsedMs, payload.Observed, payload.Mode);
        try { AbnormalReceived?.Invoke(payload); }
        catch (Exception ex) { _logger.LogDebug(ex, "[Hub] AbnormalReceived subscriber threw"); }
    }

    /// <summary>Channel write 실패 시 호출 (HubSignalProcessor 의 onDrop 콜백).
    /// LogWarning 매 drop + 임계 초과 시 LogError 한 번 발화 (10s window CAS gate).</summary>
    private void OnChannelDrop(string address, long total)
    {
        _logger.LogWarning("[Hub] Signal channel write dropped for {Address} (total={Total})", address, total);

        if (total < DropReportThreshold) return;
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastDropReportTicks);
        if (now - last <= DropReportIntervalTicks) return;
        if (Interlocked.CompareExchange(ref _lastDropReportTicks, now, last) != last) return;

        var windowSec = DropReportIntervalTicks / TimeSpan.TicksPerSecond;
        _logger.LogError(
            "[Hub] Channel drop rate 임계 초과 — total={Total} (last report window {WindowSec}s)",
            total, windowSec);
    }
}
