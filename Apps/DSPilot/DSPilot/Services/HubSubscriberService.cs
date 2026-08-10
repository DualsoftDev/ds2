// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
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
    private long _lastReportedDropTotal;
    private const long DropReportIntervalTicks = 60L * TimeSpan.TicksPerSecond;

    // resync 조기 스킵 상태 — 주소별 마지막 resync 값 + 60초 1회 전체 통과 게이트.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastResyncValueByAddress =
        new(StringComparer.OrdinalIgnoreCase);
    private long _lastResyncFullPassTicks;
    private const long ResyncFullPassIntervalTicks = 60L * TimeSpan.TicksPerSecond;
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

    /// <summary>Agent 의 현재 PLC 스캔 주기(ms) — 연결 직후 pull + OnScanIntervalChanged push 로 갱신.
    /// null = 아직 동기화 전 (미연결 등).</summary>
    public int? CurrentScanIntervalMs { get; private set; }

    /// <summary>스캔 주기 변경 수신 시 발화 — Promaker/다른 클라이언트가 바꾼 값도 들어온다.
    /// Program.cs 가 구독해 브라우저(/hubs/monitoring)로 재방송.</summary>
    public event Action<int>? ScanIntervalChanged;

    /// <summary>Agent hub 에서 현재 스캔 주기 pull. 미연결이면 null.</summary>
    public async Task<int?> GetScanIntervalMsAsync(CancellationToken ct = default)
    {
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected) return CurrentScanIntervalMs;
        try
        {
            var ms = await conn.InvokeAsync<int>("GetScanIntervalMs", ct);
            CurrentScanIntervalMs = ms;
            return ms;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Hub] GetScanIntervalMs failed");
            return CurrentScanIntervalMs;
        }
    }

    /// <summary>스캔 주기 변경 요청 — Agent 가 라이브 적용 + 영속화 + 전 클라이언트 브로드캐스트.</summary>
    public async Task<bool> SetScanIntervalMsAsync(int ms, CancellationToken ct = default)
    {
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected) return false;
        try
        {
            await conn.InvokeAsync("SetScanIntervalMs", ms, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Hub] SetScanIntervalMs({Ms}) failed", ms);
            return false;
        }
    }

    /// <summary>건강 기준선 수동 동결 요청 — hub 가 전 클라이언트(Promaker 등 학습 보유 인스턴스)에
    /// 브로드캐스트해 동시 동결한다. DSPilot 자체는 학습기가 없어 수신 처리는 없음(명령 발신만).</summary>
    public async Task<bool> FreezeHealthBaselineAsync(CancellationToken ct = default)
    {
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected) return false;
        try
        {
            await conn.InvokeAsync(HubMethod.FreezeHealthBaseline, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Hub] FreezeHealthBaseline failed");
            return false;
        }
    }

    /// <summary>Agent 의 현재 자동 duration 정합 ON/OFF — OnAutoCalibrateChanged push 로 갱신.</summary>
    public bool CurrentAutoCalibrate { get; private set; } = true;

    /// <summary>자동 정합 ON/OFF 변경 수신 시 발화 — settings 페이지가 구독해 체크박스 동기화.</summary>
    public event Action<bool>? AutoCalibrateChanged;

    private void OnHubAutoCalibrateChanged(bool on)
    {
        CurrentAutoCalibrate = on;
        try { AutoCalibrateChanged?.Invoke(on); }
        catch (Exception ex) { _logger.LogDebug(ex, "[Hub] AutoCalibrateChanged subscriber threw"); }
    }

    /// <summary>자동 정합 토글 요청 — hub 가 엔진 적용 + 전 클라이언트 broadcast.</summary>
    public async Task<bool> SetAutoCalibrateAsync(bool on, CancellationToken ct = default)
    {
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected) return false;
        try
        {
            await conn.InvokeAsync(HubMethod.SetAutoCalibrate, on, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Hub] SetAutoCalibrate({On}) failed", on);
            return false;
        }
    }

    private void OnHubScanIntervalChanged(int ms)
    {
        CurrentScanIntervalMs = ms;
        try { ScanIntervalChanged?.Invoke(ms); }
        catch (Exception ex) { _logger.LogDebug(ex, "[Hub] ScanIntervalChanged subscriber threw"); }
    }

    /// <summary>연결/재연결 직후 현재 스캔 주기 pull — "언제 연결되어도" 동기화 보장.</summary>
    private async Task SyncScanIntervalAsync()
    {
        var ms = await GetScanIntervalMsAsync();
        if (ms.HasValue)
            OnHubScanIntervalChanged(ms.Value);
        await SyncAutoCalibrateAsync();
    }

    /// <summary>연결/재연결 직후 Agent 의 현재 자동정합 상태 pull — 설정 페이지 체크박스 초기 동기.
    /// (PlcConnection.json SSOT 는 Agent 가 들고, DSPilot 은 hub pull/push 로만 동기 — 파일 직접 안 봄.)</summary>
    private async Task SyncAutoCalibrateAsync()
    {
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected) return;
        try
        {
            var on = await conn.InvokeAsync<bool>("GetAutoCalibrate");
            OnHubAutoCalibrateChanged(on);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "[Hub] GetAutoCalibrate failed"); }
    }

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
        // resync 는 정책 선택지가 아니라 프로토콜 내부 채널(재연결/주기 baseline 스냅샷) — 구버전
        // AppSettingsModel 이 자동 생성한 appsettings(Hub.AcceptedSources 에 resync 누락)가 코드
        // 기본값을 영구히 덮어 baseline 이 통째로 버려지던 사고 방지. config 로도 끌 수 없게 항상 포함.
        if (!configuredSources.Contains(HubSource.Resync, StringComparer.OrdinalIgnoreCase))
            configuredSources = [.. configuredSources, HubSource.Resync];

        _processor = new HubSignalProcessor(
            acceptedSources: configuredSources,
            handleSignal: (addr, val, src, wallMs) => _engineService.HandleHubTagChanged(addr, val, src, wallMs),
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

        // 통신 blackout — PLC 단절 전이 시 진행 중 래치 사이클을 즉시 abandon(미기록).
        // 단절 구간은 신호 신뢰가 없어 그 사이클이 나중에 완료돼도 통계/보정에 넣으면 안 된다.
        _plcStatusTracker.AdapterDisconnected += status =>
            _ = _engineService.AbandonActiveCyclesOnPlcBlackoutAsync(status.Name, status.LastError);

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
        // Agent ControlAbnormalAdapter 감지 결과 — MonitoringAbnormalAdapter 로컬 감지 대체.
        _connection.On<AbnormalPayload>(HubMethod.OnAbnormal, OnHubAbnormal);
        // PLC 스캔 주기 동기화 — 어느 클라이언트가 바꿔도 push 수신.
        _connection.On<int>(HubMethod.OnScanIntervalChanged, OnHubScanIntervalChanged);
        // 자동 duration 정합 ON/OFF 동기화.
        _connection.On<bool>(HubMethod.OnAutoCalibrateChanged, OnHubAutoCalibrateChanged);

        _connection.Reconnecting += ex =>
        {
            _logger.LogWarning(ex, "[Hub] Reconnecting...");
            RaiseStatusChanged();
            return Task.CompletedTask;
        };
        _connection.Reconnected += connectionId =>
        {
            _logger.LogInformation("[Hub] Reconnected");
            RaiseStatusChanged();
            _ = SyncScanIntervalAsync();
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

        // 연결 자가복구 워치독 — ConnectWithRetryAsync 는 첫 연결 성공 시 1회성으로 끝나고,
        // SignalR 의 WithAutomaticReconnect 캠페인은 terminal Disconnected/Closed 로 떨어지면
        // 다시 살아나지 않는다. 특히 Agent 가 장시간 다운 후 재기동하는 순간은 negotiate(HTTP) 는
        // 응답하나 hub 핸드셰이크는 미준비인 "반쯤 뜬" 창이 있어 reconnect 가 Closed 로 종료되기 쉽다.
        // 그 박제 상태는 지금까지 브라우저가 모니터링 페이지에 접속해 NudgeConnectAsync 를 호출할 때만
        // 풀렸다 — 헤드리스/무방문 구간이면 DSPilot 재시작 전까지 영구 Disconnected. 주기적으로
        // Disconnected 를 감지해 같은 nudge 경로로 재시도하면 브라우저 방문과 무관하게 자가복구된다.
        var watchdogInterval = TimeSpan.FromSeconds(20);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(watchdogInterval, stoppingToken);
                if (_connection.State == HubConnectionState.Disconnected)
                    await NudgeConnectAsync(stoppingToken);
            }
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
                _ = SyncScanIntervalAsync();
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
    /// 현재 Disconnected 상태면 다음 주기 대기를 건너뛰고 즉시 StartAsync 한 번 시도.
    /// 두 곳에서 호출 — (1) 브라우저가 모니터링 페이지에 접속한 시점(MonitoringHub), (2) ExecuteAsync
    /// 의 자가복구 워치독(20s 주기). 이미 Connected/Connecting 이거나 SignalR auto-reconnect 가
    /// 백오프 중(Reconnecting)이면 no-op. 동시 호출은 _startGate 로 직렬화.
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
                _logger.LogInformation("[Hub] Connected (via nudge)");
                RaiseStatusChanged();
                // ConnectWithRetryAsync / Reconnected 와 동일하게 연결 직후 스캔 주기·자동정합 pull.
                // 워치독/브라우저-방문 어느 경로로 다시 붙어도 동기화 보장.
                _ = SyncScanIntervalAsync();
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

        // resync(주기 10s baseline 스냅샷) 조기 스킵 — 태그 전수(≈19만 건/h)가 값 변화 없이
        // 채널·컨슈머를 통과하는 상시 부하가 큐 포화(8/6 실측 28만 drop)의 바탕이 됐다.
        // 값이 안 바뀐 resync 는 enqueue 전에 버리되, 60초에 1번은 전체를 통과시켜 엔진
        // baseline 정정(레벨 드리프트 자가치유)을 유지한다. plcTagLog 폭증은 SimulationEngineService
        // 의 기록 dedupe 가 이중 방어. (실변화·plc 등 다른 source 는 종전대로 전량 통과.)
        bool resyncFullPass = false, fullPassDecided = false;
        foreach (var it in items)
        {
            if (string.Equals(it.Source, HubSource.Resync, StringComparison.OrdinalIgnoreCase))
            {
                if (!fullPassDecided)
                {
                    fullPassDecided = true;
                    var now = DateTime.UtcNow.Ticks;
                    var last = Interlocked.Read(ref _lastResyncFullPassTicks);
                    resyncFullPass = now - last >= ResyncFullPassIntervalTicks
                        && Interlocked.CompareExchange(ref _lastResyncFullPassTicks, now, last) == last;
                }
                var unchanged = _lastResyncValueByAddress.TryGetValue(it.Address, out var lastVal)
                    && string.Equals(lastVal, it.Value, StringComparison.Ordinal);
                _lastResyncValueByAddress[it.Address] = it.Value;
                if (unchanged && !resyncFullPass) continue;
            }
            // WallClockMs = 원천 관측 시각(Pi5 스캔 직후 각인). plcTagLog 기록 시각으로 관통 —
            // 도착시각으로 찍으면 핑 두절→replay 신호가 복구 순간에 뭉쳐 그래프가 왜곡된다.
            var result = _processor.TryEnqueue(it.Address, it.Value, it.Source, it.WallClockMs);
            if (result == EnqueueResult.Ignored)
                _logger.LogTrace("[Hub] Ignored {Address}={Value} from={Source}", it.Address, it.Value, it.Source);
        }
    }

    /// <summary>abnormal 이벤트 수신 — AbnormalEventService 적재 + AbnormalReceived 발화(UI 구독).</summary>
    private void OnHubAbnormal(AbnormalPayload payload)
    {
        _logger.LogWarning(
            "[Hub] Abnormal {Kind}({KindValue}) call={CallId} api={ApiCallId} elapsed={Elapsed} observed={Observed} mode={Mode}",
            payload.Kind, payload.KindValue, payload.CallId, payload.ApiCallId, payload.ElapsedMs, payload.Observed, payload.Mode);
        _engineService.HandleHubAbnormal(payload);
        try { AbnormalReceived?.Invoke(payload); }
        catch (Exception ex) { _logger.LogDebug(ex, "[Hub] AbnormalReceived subscriber threw"); }
    }

    /// <summary>Channel write 실패 시 호출 (HubSignalProcessor 의 onDrop 콜백).
    /// ★건별 로그 금지 — 8/6 실측: drop 당 LogWarning 이 28만 줄/일로 journald 를 포화시켜
    /// 컨슈머를 더 늦추고 drop 을 재생산하는 눈덩이(로깅 피드백 루프)를 만들었다.
    /// 60초 1회로 묶어 그동안의 증가분만 보고한다(WarnRejectedTimestamp 와 동일 패턴).</summary>
    private void OnChannelDrop(string address, long total)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastDropReportTicks);
        if (now - last < DropReportIntervalTicks) return;
        if (Interlocked.CompareExchange(ref _lastDropReportTicks, now, last) != last) return;

        var delta = total - Interlocked.Exchange(ref _lastReportedDropTotal, total);
        _logger.LogWarning(
            "[Hub] Signal channel drops — 최근 60s 간 {Delta}건 (누계 {Total}, 예: {Address}) — 컨슈머 포화 신호",
            delta, total, address);
    }
}
