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
    private CancellationTokenSource? _hubConnectionCts;
    private CancellationTokenSource? _reconnectStabilizationCts;
    private int _hubGeneration;

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

    private int ParseHubPort() =>
        HubAddress.Split(':') is { Length: >= 2 } parts && int.TryParse(parts[^1], out var p) ? p : 5050;

    private string BuildHubUrl() => IsHubHost
        ? BackendHost.getHubUrl(ParseHubPort())
        : $"http://{HubAddress}/hub/signal";

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
                _hubHost = BackendHost.start(ParseHubPort());
                IsHubHosting = true;
                AddSimLog($"SignalR Hub 호스팅 시작 (port={ParseHubPort()})", LogSeverity.System);
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

        if (_hubConnection is not null)
        {
            var conn = _hubConnection;
            var connectedAtStop = conn.State == HubConnectionState.Connected;
            _hubConnection = null;

            // 자기 hub host 라면 다음 PLAY 가 BackendHost.start 새로 띄우기 전에
            // 동기적으로 캐시 클리어 — Stop 비동기 cleanup 의 race 방지.
            if (_hubHost is not null)
                SignalHub.ClearTagCache();

            // 비동기 정리 — UI 안 막음. 끊기 전에 자기가 쓴 OUT tag 모두 false 로
            // broadcast 해서 attached client 들도 stale "true" 잔존 안 하게 cleanup.
            _ = Task.Run(async () =>
            {
                if (connectedAtStop)
                    await BroadcastClearOwnOutputsAsync(conn);
                try { await conn.StopAsync(); } catch { /* ignore */ }
                try { await conn.DisposeAsync(); } catch { /* ignore */ }
            });
        }

        if (_hubHost is not null)
        {
            var host = _hubHost;
            _hubHost = null;
            IsHubHosting = false;
            _ = Task.Run(() =>
            {
                try { BackendHost.stop(host); } catch { /* ignore */ }
            });
        }
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

        var outAddresses = ioMap.TxWorkToOutAddresses
            .SelectMany(kv => kv.Value)
            .Where(addr => !string.IsNullOrWhiteSpace(addr))
            .Distinct()
            .ToArray();

        var source = ResolveRuntimeHubSource() + ":stop";
        foreach (var address in outAddresses)
        {
            try
            {
                await conn.InvokeAsync(HubMethod.WriteTag, address, "false", source);
            }
            catch
            {
                // hub 가 이미 끊어졌거나 race — 다음 PLAY 의 ClearTagCache 가 정리한다.
            }
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

        if (_simEngine is null || _runtimeSession is null)
            return;
        // 자기 모드의 source는 무시 (순환 방지)
        if (_runtimeSession.ShouldIgnoreHubSource(source))
            return;

        var effects = _runtimeSession.HandleHubTag(address, value, source);
        ApplyRuntimeHubEffects(effects);
    }
}
