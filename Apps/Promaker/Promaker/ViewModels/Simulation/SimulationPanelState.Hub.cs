using System;
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
    private int _hubGeneration;

    private int CurrentHubGeneration => Volatile.Read(ref _hubGeneration);

    private bool IsCurrentHubGeneration(int generation) =>
        Volatile.Read(ref _hubGeneration) == generation;

    private bool IsCurrentHubConnection(int generation, HubConnection hub) =>
        IsCurrentHubGeneration(generation) && ReferenceEquals(_hubConnection, hub);

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
            hubConnection.Closed += ex => OnHubDisconnected(generation, ex);
            hubConnection.Reconnecting += _ =>
            {
                if (IsCurrentHubConnection(generation, hubConnection))
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (IsCurrentHubConnection(generation, hubConnection))
                            SimStatusText = "Hub 재연결 중...";
                    });
                return Task.CompletedTask;
            };
            hubConnection.Reconnected += _ =>
            {
                if (IsCurrentHubConnection(generation, hubConnection))
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (!IsCurrentHubConnection(generation, hubConnection))
                            return;
                        SimStatusText = IsSimulating ? "Hub 재연결 완료" : SimText.Stopped;
                        AddSimLog("Hub 재연결 완료", LogSeverity.System);
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
        });
        return Task.CompletedTask;
    }

    private void StopHub()
    {
        InvalidateHubGeneration();

        if (_hubConnection is not null)
        {
            var conn = _hubConnection;
            _hubConnection = null;
            // 비동기 정리 — UI 안 막음
            _ = Task.Run(async () =>
            {
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
