using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ds2.Backend;
using Ds2.Backend.Common;
using Ds2.Core;
using Microsoft.AspNetCore.SignalR.Client;

namespace Promaker.ViewModels;

public sealed partial class SimulationHubBridge
{
    // ── URL / port 헬퍼 ──────────────────────────────────────────

    /// <summary>현재 모드/PLC 옵션에 해당하는 host:port. Monitoring + 실 PLC self-host 만 MonitoringHubAddress(5051),
    /// Monitoring 이라도 PLC 미연결이면 외부 Control hub 에 붙으므로 HubAddress(5050).</summary>
    private string ActiveAddress =>
        IsHubHost && _runtimeMode() == RuntimeMode.Monitoring ? _monitoringHubAddress() : _hubAddress();

    private int ParsePort()
    {
        var defaultPort = IsHubHost && _runtimeMode() == RuntimeMode.Monitoring ? 5051 : 5050;
        return ActiveAddress.Split(':') is { Length: >= 2 } parts && int.TryParse(parts[^1], out var p)
            ? p
            : defaultPort;
    }

    private string BuildUrl() => IsHubHost
        ? BackendHost.getHubUrl(ParsePort())
        : $"http://{ActiveAddress}/hub/signal";

    // ── Generation token 관리 ────────────────────────────────────

    private int StartNewGeneration()
    {
        _hubConnectionCts?.Cancel();
        _hubConnectionCts?.Dispose();
        _hubConnectionCts = new CancellationTokenSource();
        return Interlocked.Increment(ref _hubGeneration);
    }

    private void InvalidateGeneration()
    {
        Interlocked.Increment(ref _hubGeneration);
        _hubConnectionCts?.Cancel();
        _hubConnectionCts?.Dispose();
        _hubConnectionCts = null;
    }

    // ── Start ────────────────────────────────────────────────────

    public bool TryStart()
    {
        if (_runtimeMode() == RuntimeMode.Simulation)
            return true;

        try
        {
            var generation = StartNewGeneration();
            var cancellationToken = _hubConnectionCts?.Token ?? CancellationToken.None;

            if (IsHubHost && !TryStartHost())
                return false;

            var hubUrl = BuildUrl();
            var hubConnection = new HubConnectionBuilder()
                .WithUrl(hubUrl)
                .WithAutomaticReconnect()
                .Build();
            _hubConnection = hubConnection;

            WireHubReceivers(hubConnection, generation);
            _hubBatchSender = CreateBatchSender(hubConnection, generation);
            WireHubLifecycleEvents(hubConnection, generation);

            _ = ConnectAsync(hubConnection, hubUrl, generation, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            SimLog.Error("Hub start failed", ex);
            _setStatusText($"Hub 시작 실패: {ex.Message}");
            Stop();
            return false;
        }
    }

    /// <summary>Host 띄우기 — PLC 게이트웨이 동반 (실 PLC) 또는 idle. PLC 검증 실패 시 false.</summary>
    private bool TryStartHost()
    {
        var isMonitoring = _runtimeMode() == RuntimeMode.Monitoring;
        if (_isRealPlcConnected())
        {
            var plcConfig = _buildPlcGatewayConfig(out var errors);
            if (plcConfig is null)
            {
                var msg = "PLC 설정 검증 실패:\n  - " + string.Join("\n  - ", errors);
                _addSimLog(msg, LogSeverity.Error);
                _setStatusText("PLC 설정 오류 — Hub 시작 중단");
                return false;
            }
            // Monitoring 은 readOnly 진입점으로 — SignalHub 가 클라이언트 WriteTag/WriteTags 거부.
            _hubHost = isMonitoring
                ? BackendHost.startWithPlcConfigReadOnly(ParsePort(), plcConfig)
                : BackendHost.startWithPlcConfig(ParsePort(), plcConfig);
            var roTag = isMonitoring ? " [read-only]" : "";
            var ps = _plcSettings();
            _addSimLog(
                $"SignalR Hub + PLC 게이트웨이 시작{roTag} (port={ParsePort()}, vendor={ps.Vendor}, ip={ps.IpAddress}:{ps.Port})",
                LogSeverity.System);
        }
        else
        {
            _hubHost = BackendHost.start(ParsePort());
            _addSimLog($"SignalR Hub 호스팅 시작 (port={ParsePort()})", LogSeverity.System);
        }
        IsHosting = true;
        return true;
    }

    private void WireHubReceivers(HubConnection hubConnection, int generation)
    {
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
    }

    private HubTagBatchSender CreateBatchSender(HubConnection hubConnection, int generation) =>
        new(hubConnection,
            generation,
            (gen, hub) => IsCurrentConnection(gen, hub),
            (msg, ex) =>
            {
                if (!IsCurrentConnection(generation, hubConnection)) return;
                _ = _dispatcher.BeginInvoke(() =>
                {
                    if (IsCurrentConnection(generation, hubConnection))
                        _addSimLog($"[Hub] {msg}", LogSeverity.Warn);
                });
            });

    private void WireHubLifecycleEvents(HubConnection hubConnection, int generation)
    {
        hubConnection.Closed += ex =>
        {
            _reconnectStabilizationCts?.Cancel();
            return OnDisconnected(generation, ex);
        };
        hubConnection.Reconnecting += ex => OnReconnecting(hubConnection, generation, ex);
        hubConnection.Reconnected  += _ => OnReconnected(hubConnection, generation);
    }

    private Task OnReconnecting(HubConnection hubConnection, int generation, Exception? ex)
    {
        _reconnectStabilizationCts?.Cancel();
        if (IsCurrentConnection(generation, hubConnection))
            _dispatcher.BeginInvoke(() =>
            {
                if (!IsCurrentConnection(generation, hubConnection)) return;
                _setSimStatusText("Hub 재연결 시도 중...");
                SetStatus(false, true);
                var detail = ex is null ? "" : $" ({ex.Message})";
                _addSimLog($"Hub 연결 끊김 — 자동 재연결 시도 중{detail}", LogSeverity.Warn);
            });
        return Task.CompletedTask;
    }

    /// <summary>Reconnected event 가 발화돼도 즉시 다시 끊기는 short-lived Connected 가 있어 false-positive
    /// "재연결 완료" 가 뜨던 문제. 300ms stabilization 후 state 재검사 → 그 사이 Closed/Reconnecting 가 오면
    /// cts.Cancel 로 취소되어 "완료" 로그 안 뜸.</summary>
    private Task OnReconnected(HubConnection hubConnection, int generation)
    {
        if (!IsCurrentConnection(generation, hubConnection))
            return Task.CompletedTask;

        _reconnectStabilizationCts?.Cancel();
        _reconnectStabilizationCts?.Dispose();
        _reconnectStabilizationCts = new CancellationTokenSource();
        var stabCt = _reconnectStabilizationCts.Token;

        _dispatcher.BeginInvoke(() =>
        {
            if (IsCurrentConnection(generation, hubConnection))
            {
                _setSimStatusText("Hub 재연결 안정화 중...");
                _addSimLog("Hub 재연결 이벤트 발생 — 안정화 확인 중", LogSeverity.Info);
            }
        });

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(300, stabCt); }
            catch (OperationCanceledException) { return; }

            if (stabCt.IsCancellationRequested) return;
            if (!IsCurrentConnection(generation, hubConnection)) return;

            var stableState = hubConnection.State;
            _ = _dispatcher.BeginInvoke(() =>
            {
                if (!IsCurrentConnection(generation, hubConnection)) return;
                if (stableState != HubConnectionState.Connected)
                {
                    _addSimLog($"Hub 재연결 안정화 실패 — state={stableState} (false-positive)", LogSeverity.Warn);
                    _setSimStatusText("Hub 연결 끊김");
                    SetStatus(false, false);
                    return;
                }
                _setSimStatusText(_isSimulating() ? "Hub 재연결 완료" : "시뮬레이션 정지 됨");
                _addSimLog("Hub 재연결 완료", LogSeverity.System);
                SetStatus(true, false);
            });
        });
        return Task.CompletedTask;
    }

    // ── Connect (initial async retry loop) ───────────────────────

    private async Task ConnectAsync(
        HubConnection hubConnection,
        string hubUrl,
        int generation,
        CancellationToken cancellationToken)
    {
        var retryDelayMs = 1000;
        const int maxDelayMs = 10000;

        while (!cancellationToken.IsCancellationRequested
               && IsCurrentConnection(generation, hubConnection)
               && hubConnection.State == HubConnectionState.Disconnected)
        {
            try
            {
                _ = _dispatcher.BeginInvoke(() =>
                {
                    if (!IsCurrentConnection(generation, hubConnection)) return;
                    _setSimStatusText("Hub 연결 시도 중...");
                    _setStatusText($"Hub 연결 시도 중... ({hubUrl})");
                });

                await hubConnection.StartAsync(cancellationToken);

                if (!IsCurrentConnection(generation, hubConnection))
                    return;

                _ = _dispatcher.BeginInvoke(() =>
                {
                    if (!IsCurrentConnection(generation, hubConnection)) return;
                    _addSimLog($"SignalR Hub 연결 완료 ({hubUrl})", LogSeverity.System);
                    SetStatus(true, false);
                    var isPassive = _runtimeMode() is RuntimeMode.VirtualPlant or RuntimeMode.Monitoring;
                    var statusMsg = isPassive ? "Hub 신호 대기 중..." : $"{_runtimeMode()} 동작 중";
                    _setSimStatusText(statusMsg);
                    _setStatusText(statusMsg);
                });
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) { return; }
            catch
            {
                if (!IsCurrentConnection(generation, hubConnection))
                    return;

                _ = _dispatcher.BeginInvoke(() =>
                {
                    if (IsCurrentConnection(generation, hubConnection))
                        _addSimLog($"Hub 연결 실패 — {retryDelayMs / 1000.0:F1}초 후 재시도", LogSeverity.Warn);
                });

                try { await Task.Delay(retryDelayMs, cancellationToken); }
                catch (OperationCanceledException) { return; }
                retryDelayMs = Math.Min(retryDelayMs * 2, maxDelayMs);
            }
        }
    }

    private Task OnDisconnected(int generation, Exception? ex)
    {
        if (!IsCurrentGeneration(generation)) return Task.CompletedTask;
        if (!_isSimulating()) return Task.CompletedTask;
        _dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrentGeneration(generation)) return;
            _addSimLog($"Hub 연결 끊김{(ex is not null ? $": {ex.Message}" : "")}", LogSeverity.Warn);
            _setSimStatusText("Hub 연결 끊김");
            SetStatus(false, false);
        });
        return Task.CompletedTask;
    }

    // ── Stop / cleanup ───────────────────────────────────────────

    public void Stop()
    {
        InvalidateGeneration();
        _reconnectStabilizationCts?.Cancel();
        _reconnectStabilizationCts?.Dispose();
        _reconnectStabilizationCts = null;
        SetStatus(false, false);

        var batchSender = _hubBatchSender;
        _hubBatchSender = null;

        // Cleanup 직렬화 — 클라이언트 → 호스트 순서 보장.
        var conn = _hubConnection;
        var connectedAtStop = conn?.State == HubConnectionState.Connected;
        _hubConnection = null;

        // 자기 hub host 라면 다음 PLAY 가 BackendHost.start 새로 띄우기 전에 동기 캐시 클리어.
        if (_hubHost is not null && conn is not null)
            SignalHub.ClearTagCache();

        var host = _hubHost;
        _hubHost = null;
        if (host is not null)
            IsHosting = false;

        if (conn is null && batchSender is null && host is null)
            return;

        _ = Task.Run(async () => await DisposeHubResourcesAsync(batchSender, conn, host, connectedAtStop));
    }

    private async Task DisposeHubResourcesAsync(
        HubTagBatchSender? batchSender,
        HubConnection?     conn,
        Microsoft.AspNetCore.Builder.WebApplication? host,
        bool               connectedAtStop)
    {
        // 1) batch sender flush
        if (batchSender is not null)
        {
            try { await batchSender.DisposeAsync(); } catch { /* ignore */ }
        }

        // 2) 자기가 쓴 OUT tag 들을 false 로 broadcast — attached client 의 stale "true" 잔존 cleanup.
        if (conn is not null && connectedAtStop)
        {
            try { await BroadcastClearOwnOutputsAsync(conn); } catch { /* ignore */ }
        }

        // 3) 클라이언트 먼저 정리
        if (conn is not null)
        {
            try { await conn.StopAsync(); } catch { /* ignore */ }
            try { await conn.DisposeAsync(); } catch { /* ignore */ }
        }

        // 4) 클라이언트 stop 완료 후에야 호스트 stop
        if (host is not null)
        {
            try { BackendHost.stop(host); } catch { /* ignore */ }
        }
    }

    /// <summary>Control 모드 종료 직전, 우리가 직접 작성하는 OUT(Tx) tag 들을 false 로 broadcast.</summary>
    private async Task BroadcastClearOwnOutputsAsync(HubConnection conn)
    {
        if (_runtimeMode() != RuntimeMode.Control) return;
        if (!_hasIoMap()) return;

        var source = _resolveRuntimeHubSource() + ":stop";
        var batch = _txOutAddresses()
            .Where(addr => !string.IsNullOrWhiteSpace(addr))
            .Distinct()
            .Select(addr => new TagWrite(addr, "false", source))
            .ToArray();

        if (batch.Length == 0) return;

        try { await conn.InvokeAsync(HubMethod.WriteTags, batch); }
        catch { /* hub 가 이미 끊어졌거나 race — 다음 PLAY 의 ClearTagCache 가 정리 */ }
    }
}
