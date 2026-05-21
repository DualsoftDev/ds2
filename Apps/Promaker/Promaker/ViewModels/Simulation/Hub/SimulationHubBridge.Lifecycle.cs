using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ds2.Backend;
using Ds2.Backend.Common;
using Ds2.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Promaker.Shared;

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
        Interlocked.Exchange(ref _reconnectAttempt, 0);
        return Interlocked.Increment(ref _hubGeneration);
    }

    private void InvalidateGeneration()
    {
        Interlocked.Increment(ref _hubGeneration);
        Interlocked.Exchange(ref _reconnectAttempt, 0);
        _hubConnectionCts?.Cancel();
        _hubConnectionCts?.Dispose();
        _hubConnectionCts = null;
    }

    /// <summary>SignalR WithAutomaticReconnect() 의 default policy 기반 다음 재시도 추정 ms.
    /// 정확한 값은 SDK 내부 — UI 표시용 추정치. policy: [0s, 2s, 10s, 30s] (4회) 후 Closed.</summary>
    private static int EstimateNextReconnectDelayMs(int attempt) => attempt switch
    {
        <= 1 => 0,
        2    => 2000,
        3    => 10000,
        _    => 30000,
    };

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
            // v10 모니터링 진단: ex 분류 후 한국어 라벨 표시.
            var diagnostic = Ds2.Backend.Common.HubConnectionDiagnostic.ClassifyException(ex);
            var label = Ds2.Backend.Common.HubConnectionDiagnostic.DiagnosticLabel(diagnostic);
            _setStatusText($"Hub 시작 실패: {label}");
            Stop();
            return false;
        }
    }

    /// <summary>Host 띄우기 — PLC 게이트웨이 동반 (실 PLC) 또는 idle. PLC 검증 실패 시 false.
    /// Monitoring + 실 PLC + Promaker.Agent 설치 환경에서는 자체 BackendHost 대신 active.flag 를 써서
    /// Agent (Windows Service) 에 5051 호스팅을 위임. 그 외(Control / PLC 미연결) 경로는 기존 그대로.</summary>
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

            // ── Monitoring + 실 PLC + Agent 설치됨 → Agent 위임 경로 ──
            // BackendHost 자체 호스팅 대신 active.flag/session.json 만 쓰고, 클라이언트로 5051 에 접속.
            // Agent 서비스가 변화 감지 → AASX 로드 → BackendHost.startReadOnly(5051) → PLC 스캔 시작.
            if (isMonitoring && IsAgentAvailable)
            {
                // PLC 설정은 다이얼로그 OK 시점에 이미 SharedPaths.PlcConnectionFilePath 에 저장됨 (PlcSettings.Save).
                // 명시적으로 한 번 더 호출해 race / 첫 PLAY 누락 방지.
                _plcSettings().Save();

                var session = AgentSession.ForCurrentDefaults(requestedBy: "promaker");
                if (!session.TryWrite())
                {
                    _addSimLog("Agent active.flag 기록 실패 — 공유 폴더 권한을 확인하세요.", LogSeverity.Error);
                    _setStatusText("Agent 위임 실패");
                    return false;
                }
                _delegatedToAgent = true;
                _hubHost = null;       // 우리가 host 가 아님.
                IsHosting = false;
                var ps = _plcSettings();
                _addSimLog(
                    $"Promaker.Agent 에 모니터링 위임 (5051, vendor={ps.Vendor}, ip={ps.IpAddress}:{ps.Port}). " +
                    "Agent 서비스가 미실행 상태면 'sc start PromakerAgentService' 로 시작하세요.",
                    LogSeverity.System);
                return true;
            }

            // Monitoring 은 readOnly 진입점으로 — SignalHub 가 클라이언트 WriteTag/WriteTags 거부.
            _hubHost = isMonitoring
                ? BackendHost.startWithPlcConfigReadOnly(ParsePort(), plcConfig)
                : BackendHost.startWithPlcConfig(ParsePort(), plcConfig);
            var roTag = isMonitoring ? " [read-only]" : "";
            var ps2 = _plcSettings();
            _addSimLog(
                $"SignalR Hub + PLC 게이트웨이 시작{roTag} (port={ParsePort()}, vendor={ps2.Vendor}, ip={ps2.IpAddress}:{ps2.Port})",
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
                // v10 모니터링 진단: ex 가 있으면 분류 라벨 부착 (silent 에러 가시화).
                var detail = ex is null
                    ? msg
                    : $"{msg} — {Ds2.Backend.Common.HubConnectionDiagnostic.DiagnosticLabel(Ds2.Backend.Common.HubConnectionDiagnostic.ClassifyException(ex))}";
                _ = _dispatcher.BeginInvoke(() =>
                {
                    if (IsCurrentConnection(generation, hubConnection))
                        _addSimLog($"[Hub] {detail}", LogSeverity.Warn);
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
        if (!IsCurrentConnection(generation, hubConnection))
            return Task.CompletedTask;

        var attempt = Interlocked.Increment(ref _reconnectAttempt);
        var etaMs = EstimateNextReconnectDelayMs(attempt);
        _dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrentConnection(generation, hubConnection)) return;
            // v10 모니터링 진단: ex 분류 + Reconnecting attempt/ETA 라벨.
            var cause = ex is null
                ? Ds2.Backend.Common.HubConnectionDiagnostic.Diagnostic.NewInternalError("연결 끊김")
                : Ds2.Backend.Common.HubConnectionDiagnostic.ClassifyException(ex);
            var causeLabel = Ds2.Backend.Common.HubConnectionDiagnostic.DiagnosticLabel(cause);
            var retryLabel = Ds2.Backend.Common.HubConnectionDiagnostic.DiagnosticLabel(
                Ds2.Backend.Common.HubConnectionDiagnostic.Diagnostic.NewReconnecting(attempt, etaMs));
            _setSimStatusText($"Hub 재연결 시도 #{attempt}...");
            SetStatus(false, true);
            _addSimLog($"Hub 연결 끊김 — {causeLabel}. {retryLabel}", LogSeverity.Warn);
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

        Interlocked.Exchange(ref _reconnectAttempt, 0);
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
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested
               && IsCurrentConnection(generation, hubConnection)
               && hubConnection.State == HubConnectionState.Disconnected)
        {
            attempt++;
            try
            {
                _ = _dispatcher.BeginInvoke(() =>
                {
                    if (!IsCurrentConnection(generation, hubConnection)) return;
                    _setSimStatusText($"Hub 연결 시도 중... (#{attempt})");
                    _setStatusText($"Hub 연결 시도 중... #{attempt} ({hubUrl})");
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
            catch (Exception ex)
            {
                if (!IsCurrentConnection(generation, hubConnection))
                    return;

                // v10 모니터링 진단: 예외 분류 → 한국어 라벨 + Reconnecting attempt/ETA.
                var diagnostic = Ds2.Backend.Common.HubConnectionDiagnostic.ClassifyException(ex);
                var causeLabel = Ds2.Backend.Common.HubConnectionDiagnostic.DiagnosticLabel(diagnostic);
                var retryLabel = Ds2.Backend.Common.HubConnectionDiagnostic.DiagnosticLabel(
                    Ds2.Backend.Common.HubConnectionDiagnostic.Diagnostic.NewReconnecting(attempt, retryDelayMs));

                _ = _dispatcher.BeginInvoke(() =>
                {
                    if (IsCurrentConnection(generation, hubConnection))
                    {
                        _addSimLog($"Hub 연결 실패 — {causeLabel}. {retryLabel}", LogSeverity.Warn);
                        _setStatusText($"Hub 연결 실패: {causeLabel}");
                    }
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
        // v10 모니터링 진단: ex 분류 후 사유 라벨 추가.
        var diagnostic = ex is null
            ? Ds2.Backend.Common.HubConnectionDiagnostic.Diagnostic.NewInternalError("연결 끊김")
            : Ds2.Backend.Common.HubConnectionDiagnostic.ClassifyException(ex);
        var diagnosticLabel = Ds2.Backend.Common.HubConnectionDiagnostic.DiagnosticLabel(diagnostic);
        if (!_isSimulating()) return Task.CompletedTask;
        _dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrentGeneration(generation)) return;
            _addSimLog($"Hub 연결 끊김 — {diagnosticLabel}", LogSeverity.Warn);
            _setSimStatusText($"Hub 연결 끊김: {diagnosticLabel}");
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

        // Agent 위임 상태였다면 active.flag 삭제 — Agent 가 supervisor watch 로 idle 전이.
        // session.json 은 유지해서 다음 PLAY 시 동일 설정 자동 복원 (AgentSession.TryDeactivate 의 의도).
        if (_delegatedToAgent)
        {
            try { AgentSession.TryDeactivate(); }
            catch (Exception ex) { SimLog.Warn("AgentSession.TryDeactivate failed", ex); }
            _delegatedToAgent = false;
        }

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
