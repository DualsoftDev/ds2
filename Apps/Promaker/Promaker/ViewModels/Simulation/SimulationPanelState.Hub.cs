using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ds2.Backend;
using Ds2.Backend.Common;
using Ds2.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    private WebApplication? _hubHost;
    private HubConnection? _hubConnection;
    private HubTagBatchSender? _hubBatchSender;
    private CancellationTokenSource? _hubConnectionCts;
    private CancellationTokenSource? _reconnectStabilizationCts;
    private int _hubGeneration;

    /// <summary>현재 generation 의 batch sender — 없으면 null. WriteTag 송신은 모두 이 sender 경유.</summary>
    internal HubTagBatchSender? HubBatchSender => _hubBatchSender;

    private int CurrentHubGeneration => Volatile.Read(ref _hubGeneration);

    private bool IsCurrentHubGeneration(int generation) =>
        Volatile.Read(ref _hubGeneration) == generation;

    private bool IsCurrentHubConnection(int generation, HubConnection hub) =>
        IsCurrentHubGeneration(generation) && ReferenceEquals(_hubConnection, hub);

    /// <summary>
    /// Hub 연결 3-state 을 두 bool 한 쌍으로 set. Reconnecting 먼저 → Connected 나중 순서를
    /// 헬퍼 안에 고정해 두 bool 동시 true 모순 시점이 호출자에 의존하지 않도록 보장.
    /// </summary>
    private void SetHubStatus(bool connected, bool reconnecting)
    {
        IsHubReconnecting = reconnecting;
        IsHubConnected = connected;
    }

    private int StartNewHubGeneration()
    {
        _hubConnectionCts?.Cancel();
        _hubConnectionCts?.Dispose();
        _hubConnectionCts = new CancellationTokenSource();
        return Interlocked.Increment(ref _hubGeneration);
    }

    private void InvalidateHubGeneration()
    {
        Interlocked.Increment(ref _hubGeneration);
        _hubConnectionCts?.Cancel();
        _hubConnectionCts?.Dispose();
        _hubConnectionCts = null;
    }

    /// <summary>현재 모드에 해당하는 host:port 문자열. Monitoring 은 MonitoringHubAddress, 그 외는 HubAddress.</summary>
    private string ActiveHubAddress =>
        SelectedRuntimeMode == RuntimeMode.Monitoring ? MonitoringHubAddress : HubAddress;

    private int ParseHubPort()
    {
        var defaultPort = SelectedRuntimeMode == RuntimeMode.Monitoring ? 5051 : 5050;
        return ActiveHubAddress.Split(':') is { Length: >= 2 } parts && int.TryParse(parts[^1], out var p)
            ? p
            : defaultPort;
    }

    private string BuildHubUrl() => IsHubHost
        ? BackendHost.getHubUrl(ParseHubPort())
        : $"http://{ActiveHubAddress}/hub/signal";

    private bool TryStartHub()
    {
        if (SelectedRuntimeMode == RuntimeMode.Simulation)
            return true;

        try
        {
            var generation = StartNewHubGeneration();
            var cancellationToken = _hubConnectionCts?.Token ?? CancellationToken.None;

            if (IsHubHost)
            {
                var isMonitoring = SelectedRuntimeMode == RuntimeMode.Monitoring;
                // Monitoring 은 외부 PLC 데이터 캡처가 본질 — 실 PLC 미연결이면 빈 Hub 만 뜨는 무의미한 상태가 되므로 차단.
                // Control 은 수동 컨트롤러로 PLC 없이도 의미가 있어 허용 유지.
                if (isMonitoring && !IsRealPlcConnected)
                {
                    AddSimLog(
                        "Monitoring 모드는 실제 PLC 연결이 필요합니다. Runtime 설정에서 ‘실제 PLC 와 연결’ 체크 + PLC 설정을 완료해 주세요.",
                        LogSeverity.Error);
                    _setStatusText("Monitoring — PLC 미연결로 시작 불가");
                    return false;
                }
                if (IsRealPlcConnected)
                {
                    // 태그 매핑은 AASX IO 설정에서 자동 import.
                    var plcConfig = BuildPlcGatewayConfig(out var errors);
                    if (plcConfig is null)
                    {
                        var msg = "PLC 설정 검증 실패:\n  - " + string.Join("\n  - ", errors);
                        AddSimLog(msg, LogSeverity.Error);
                        _setStatusText("PLC 설정 오류 — Hub 시작 중단");
                        return false;
                    }
                    // Monitoring 은 readOnly 진입점으로 — SignalHub 가 클라이언트 WriteTag/WriteTags 거부.
                    _hubHost = isMonitoring
                        ? BackendHost.startWithPlcConfigReadOnly(ParseHubPort(), plcConfig)
                        : BackendHost.startWithPlcConfig(ParseHubPort(), plcConfig);
                    var roTag = isMonitoring ? " [read-only]" : "";
                    AddSimLog(
                        $"SignalR Hub + PLC 게이트웨이 시작{roTag} (port={ParseHubPort()}, vendor={PlcSettings.Vendor}, ip={PlcSettings.IpAddress}:{PlcSettings.Port})",
                        LogSeverity.System);
                }
                else
                {
                    _hubHost = BackendHost.start(ParseHubPort());
                    AddSimLog($"SignalR Hub 호스팅 시작 (port={ParseHubPort()})", LogSeverity.System);
                }
                IsHubHosting = true;
            }

            var hubUrl = BuildHubUrl();
            var hubConnection = new HubConnectionBuilder()
                .WithUrl(hubUrl)
                .WithAutomaticReconnect()
                .Build();
            _hubConnection = hubConnection;

            hubConnection.On<string, string, string>(
                HubMethod.OnTagChanged,
                (address, value, source) => OnHubTagChanged(generation, address, value, source));
            hubConnection.On<TagWrite[]>(
                HubMethod.OnTagsChanged,
                items =>
                {
                    if (items is null) return;
                    foreach (var it in items)
                        OnHubTagChanged(generation, it.Address, it.Value, it.Source);
                });

            // Batch sender — 이 generation 동안 모든 WriteTag 송신을 이 sender 가 coalesce.
            _hubBatchSender = new HubTagBatchSender(
                hubConnection,
                generation,
                (gen, hub) => IsCurrentHubConnection(gen, hub),
                (msg, ex) =>
                {
                    if (!IsCurrentHubConnection(generation, hubConnection)) return;
                    _ = _dispatcher.BeginInvoke(() =>
                    {
                        if (IsCurrentHubConnection(generation, hubConnection))
                            AddSimLog($"[Hub] {msg}", LogSeverity.Warn);
                    });
                });
            hubConnection.Closed += ex =>
            {
                // Closed 시 진행 중인 reconnect stabilization 취소 (false-positive "재연결 완료" 차단).
                _reconnectStabilizationCts?.Cancel();
                return OnHubDisconnected(generation, ex);
            };
            hubConnection.Reconnecting += ex =>
            {
                _reconnectStabilizationCts?.Cancel();
                if (IsCurrentHubConnection(generation, hubConnection))
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (!IsCurrentHubConnection(generation, hubConnection)) return;
                        SimStatusText = "Hub 재연결 시도 중...";
                        SetHubStatus(connected: false, reconnecting: true);
                        var detail = ex is null ? "" : $" ({ex.Message})";
                        AddSimLog($"Hub 연결 끊김 — 자동 재연결 시도 중{detail}", LogSeverity.Warn);
                    });
                return Task.CompletedTask;
            };
            hubConnection.Reconnected += _connectionId =>
            {
                if (!IsCurrentHubConnection(generation, hubConnection))
                    return Task.CompletedTask;

                // Reconnected event 가 발화돼도 즉시 다시 끊기는 short-lived Connected 가 있어
                // false-positive "재연결 완료" 가 뜨던 문제. 300ms stabilization 후 state 재검사 →
                // 그 사이 Closed/Reconnecting 가 오면 cts.Cancel 로 취소되어 "완료" 로그 안 뜸.
                _reconnectStabilizationCts?.Cancel();
                _reconnectStabilizationCts?.Dispose();
                _reconnectStabilizationCts = new CancellationTokenSource();
                var stabCt = _reconnectStabilizationCts.Token;

                _dispatcher.BeginInvoke(() =>
                {
                    if (IsCurrentHubConnection(generation, hubConnection))
                    {
                        SimStatusText = "Hub 재연결 안정화 중...";
                        AddSimLog("Hub 재연결 이벤트 발생 — 안정화 확인 중", LogSeverity.Info);
                    }
                });

                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(300, stabCt); }
                    catch (OperationCanceledException) { return; }

                    if (stabCt.IsCancellationRequested) return;
                    if (!IsCurrentHubConnection(generation, hubConnection)) return;

                    var stableState = hubConnection.State;
                    _ = _dispatcher.BeginInvoke(() =>
                    {
                        if (!IsCurrentHubConnection(generation, hubConnection)) return;
                        if (stableState != HubConnectionState.Connected)
                        {
                            AddSimLog(
                                $"Hub 재연결 안정화 실패 — state={stableState} (false-positive)",
                                LogSeverity.Warn);
                            SimStatusText = "Hub 연결 끊김";
                            SetHubStatus(connected: false, reconnecting: false);
                            return;
                        }
                        SimStatusText = IsSimulating ? "Hub 재연결 완료" : SimText.Stopped;
                        AddSimLog("Hub 재연결 완료", LogSeverity.System);
                        SetHubStatus(connected: true, reconnecting: false);
                    });
                });
                return Task.CompletedTask;
            };

            // 비동기 연결 시도 (UI 안 막음)
            _ = ConnectHubAsync(hubConnection, hubUrl, generation, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            SimLog.Error("Hub start failed", ex);
            _setStatusText($"Hub 시작 실패: {ex.Message}");
            StopHub();
            return false;
        }
    }

    private async Task ConnectHubAsync(
        HubConnection hubConnection,
        string hubUrl,
        int generation,
        CancellationToken cancellationToken)
    {
        var retryDelayMs = 1000;
        const int maxDelayMs = 10000;

        while (!cancellationToken.IsCancellationRequested
               && IsCurrentHubConnection(generation, hubConnection)
               && hubConnection.State == HubConnectionState.Disconnected)
        {
            try
            {
                _ = _dispatcher.BeginInvoke(() =>
                {
                    if (!IsCurrentHubConnection(generation, hubConnection))
                        return;
                    SimStatusText = "Hub 연결 시도 중...";
                    _setStatusText($"Hub 연결 시도 중... ({hubUrl})");
                });

                await hubConnection.StartAsync(cancellationToken);

                if (!IsCurrentHubConnection(generation, hubConnection))
                    return;

                _ = _dispatcher.BeginInvoke(() =>
                {
                    if (!IsCurrentHubConnection(generation, hubConnection))
                        return;
                    AddSimLog($"SignalR Hub 연결 완료 ({hubUrl})", LogSeverity.System);
                    SetHubStatus(connected: true, reconnecting: false);
                    var isPassive = SelectedRuntimeMode is RuntimeMode.VirtualPlant or RuntimeMode.Monitoring;
                    var statusMsg = isPassive ? "Hub 신호 대기 중..." : $"{SelectedRuntimeMode} 동작 중";
                    SimStatusText = statusMsg;
                    _setStatusText(statusMsg);
                });
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
                if (!IsCurrentHubConnection(generation, hubConnection))
                    return;

                _ = _dispatcher.BeginInvoke(() =>
                {
                    if (IsCurrentHubConnection(generation, hubConnection))
                        AddSimLog($"Hub 연결 실패 — {retryDelayMs / 1000.0:F1}초 후 재시도", LogSeverity.Warn);
                });

                try
                {
                    await Task.Delay(retryDelayMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                retryDelayMs = Math.Min(retryDelayMs * 2, maxDelayMs);
            }
        }
    }

    private Task OnHubDisconnected(int generation, Exception? ex)
    {
        if (!IsCurrentHubGeneration(generation)) return Task.CompletedTask;
        if (!IsSimulating) return Task.CompletedTask;
        _dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrentHubGeneration(generation)) return;
            AddSimLog($"Hub 연결 끊김{(ex is not null ? $": {ex.Message}" : "")}", LogSeverity.Warn);
            SimStatusText = "Hub 연결 끊김";
            SetHubStatus(connected: false, reconnecting: false);
        });
        return Task.CompletedTask;
    }

    private void StopHub()
    {
        InvalidateHubGeneration();
        _reconnectStabilizationCts?.Cancel();
        _reconnectStabilizationCts?.Dispose();
        _reconnectStabilizationCts = null;
        SetHubStatus(connected: false, reconnecting: false);

        var batchSender = _hubBatchSender;
        _hubBatchSender = null;

        // Cleanup 직렬화 — 클라이언트 → 호스트 순서 보장.
        // 병렬 Task.Run 으로 분리하면 host 가 client 보다 먼저 죽고, client 의 WithAutomaticReconnect 가
        // 죽은 host 에 connect 시도 → SocketException(ConnectionRefused) race 노이즈 발생.
        var conn = _hubConnection;
        var connectedAtStop = conn?.State == HubConnectionState.Connected;
        _hubConnection = null;

        // 자기 hub host 라면 다음 PLAY 가 BackendHost.start 새로 띄우기 전에
        // 동기적으로 캐시 클리어 — Stop 비동기 cleanup 의 race 방지.
        if (_hubHost is not null && conn is not null)
            SignalHub.ClearTagCache();

        var host = _hubHost;
        _hubHost = null;
        if (host is not null)
            IsHubHosting = false;

        if (conn is null && batchSender is null && host is null)
            return;

        _ = Task.Run(async () =>
        {
            // 1) batch sender flush (송신 미완료 패킷이 있으면 전송 후 dispose).
            if (batchSender is not null)
            {
                try { await batchSender.DisposeAsync(); } catch { /* ignore */ }
            }

            // 2) 자기가 쓴 OUT tag 들을 false 로 broadcast — attached client 의 stale "true" 잔존 cleanup.
            if (conn is not null && connectedAtStop)
            {
                try { await BroadcastClearOwnOutputsAsync(conn); } catch { /* ignore */ }
            }

            // 3) 클라이언트 먼저 정리 — auto-reconnect 가 멈추도록 StopAsync 후 DisposeAsync.
            //    이게 호스트보다 *반드시* 먼저 끝나야 reconnect race 가 발생하지 않음.
            if (conn is not null)
            {
                try { await conn.StopAsync(); } catch { /* ignore */ }
                try { await conn.DisposeAsync(); } catch { /* ignore */ }
            }

            // 4) 클라이언트 stop 완료 후에야 호스트 stop — 죽은 host 에 client 가 connect 시도하는 race 차단.
            if (host is not null)
            {
                try { BackendHost.stop(host); } catch { /* ignore */ }
            }
        });
    }

    /// <summary>
    /// Control 모드 종료 직전, 우리가 직접 작성하는 OUT(Tx) tag 들을 false 로 broadcast.
    /// attached client 의 stale "true" 잔존을 cleanup 하기 위함. Rx tag 는 외부 PLC 가
    /// 쓰는 영역이라 절대 건드리지 않는다.
    /// </summary>
    private async Task BroadcastClearOwnOutputsAsync(HubConnection conn)
    {
        if (SelectedRuntimeMode != RuntimeMode.Control) return;
        if (_simEngine?.IOMap is not { } ioMap) return;

        var source = ResolveRuntimeHubSource() + ":stop";
        var batch = ioMap.TxWorkToOutAddresses
            .SelectMany(kv => kv.Value)
            .Where(addr => !string.IsNullOrWhiteSpace(addr))
            .Distinct()
            .Select(addr => new TagWrite(addr, "false", source))
            .ToArray();

        if (batch.Length == 0) return;

        try
        {
            await conn.InvokeAsync(HubMethod.WriteTags, batch);
        }
        catch
        {
            // hub 가 이미 끊어졌거나 race — 다음 PLAY 의 ClearTagCache 가 정리한다.
        }
    }

    private void OnHubTagChanged(int generation, string address, string value, string source)
    {
        if (!IsCurrentHubGeneration(generation))
            return;

        _dispatcher.BeginInvoke(() =>
        {
            if (IsCurrentHubGeneration(generation))
                AddSimLog($"[Hub수신] {address}={value} from={source}", LogSeverity.Info);
        });

        // 수동 컨트롤러 다이얼로그 등 외부 구독자에게 broadcast — engine·session 상태와 무관히 항상 발화.
        try { HubTagBroadcast?.Invoke(address, value, source); }
        catch (Exception ex) { SimLog.Error("HubTagBroadcast subscriber threw", ex); }

        if (_simEngine is null || _runtimeSession is null)
            return;
        // 자기 모드의 source는 무시 (순환 방지)
        if (_runtimeSession.ShouldIgnoreHubSource(source))
            return;

        var effects = _runtimeSession.HandleHubTag(address, value, source);
        ApplyRuntimeHubEffects(effects);
    }

    /// <summary>외부 UI(수동 컨트롤러 다이얼로그) 가 hub 의 OnTagChanged 를 구독하기 위한 이벤트.
    /// (address, value, source) — engine/runtime session 과 무관히 hub 가 받는 모든 변화를 그대로 흘림.</summary>
    public event Action<string, string, string>? HubTagBroadcast;

    /// <summary>수동 컨트롤러 측에서 OUT 태그를 hub 로 쓰기 위한 진입점.
    /// 내부적으로 Control source 로 InvokeAsync — SignalHub 가 PLC 게이트웨이로 forward.
    /// hub 미연결이면 false 반환.</summary>
    public async Task<bool> WriteTagFromManualAsync(string address, string value)
    {
        var conn = _hubConnection;
        if (conn is null || conn.State != HubConnectionState.Connected)
            return false;
        try
        {
            await conn.InvokeAsync(HubMethod.WriteTag, address, value, HubSource.Control);
            return true;
        }
        catch (Exception ex)
        {
            SimLog.Error($"WriteTagFromManual failed {address}={value}", ex);
            return false;
        }
    }

    /// <summary>수동 컨트롤러 다이얼로그 초기 로드 시 hub 캐시에서 현재 값 한 번 조회.</summary>
    public async Task<string> QueryTagFromManualAsync(string address)
    {
        var conn = _hubConnection;
        if (conn is null || conn.State != HubConnectionState.Connected) return "";
        try { return await conn.InvokeAsync<string>(HubMethod.QueryTag, address); }
        catch { return ""; }
    }
}
