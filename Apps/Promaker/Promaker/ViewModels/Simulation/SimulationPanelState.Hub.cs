using System;
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
            if (IsHubHost)
            {
                _hubHost = BackendHost.start(ParseHubPort());
                IsHubHosting = true;
                AddSimLog($"SignalR Hub 호스팅 시작 (port={ParseHubPort()})", LogSeverity.System);
            }

            var hubUrl = BuildHubUrl();
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(hubUrl)
                .WithAutomaticReconnect()
                .Build();

            _hubConnection.On<string, string, string>(HubMethod.OnTagChanged, OnHubTagChanged);
            _hubConnection.Closed += OnHubDisconnected;
            _hubConnection.Reconnecting += _ => { _dispatcher.BeginInvoke(() => SimStatusText = "Hub 재연결 중..."); return Task.CompletedTask; };
            _hubConnection.Reconnected += _ => { _dispatcher.BeginInvoke(() => { SimStatusText = IsSimulating ? "Hub 재연결 완료" : SimText.Stopped; AddSimLog("Hub 재연결 완료", LogSeverity.System); }); return Task.CompletedTask; };

            // 비동기 연결 시도 (UI 안 막음)
            _ = ConnectHubAsync(hubUrl);
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

    private async Task ConnectHubAsync(string hubUrl)
    {
        var retryDelayMs = 1000;
        const int maxDelayMs = 10000;

        while (_hubConnection is { State: HubConnectionState.Disconnected } conn)
        {
            try
            {
                _dispatcher.BeginInvoke(() =>
                {
                    SimStatusText = "Hub 연결 시도 중...";
                    _setStatusText($"Hub 연결 시도 중... ({hubUrl})");
                });

                await conn.StartAsync();

                _dispatcher.BeginInvoke(() =>
                {
                    AddSimLog($"SignalR Hub 연결 완료 ({hubUrl})", LogSeverity.System);
                    var isPassive = SelectedRuntimeMode is RuntimeMode.VirtualPlant or RuntimeMode.Monitoring;
                    var statusMsg = isPassive ? "Hub 신호 대기 중..." : $"{SelectedRuntimeMode} 동작 중";
                    SimStatusText = statusMsg;
                    _setStatusText(statusMsg);
                });
                return;
            }
            catch
            {
                _dispatcher.BeginInvoke(() =>
                    AddSimLog($"Hub 연결 실패 — {retryDelayMs / 1000.0:F1}초 후 재시도", LogSeverity.Warn));

                await Task.Delay(retryDelayMs);
                retryDelayMs = Math.Min(retryDelayMs * 2, maxDelayMs);
            }
        }
    }

    private Task OnHubDisconnected(Exception? ex)
    {
        if (!IsSimulating) return Task.CompletedTask;
        _dispatcher.BeginInvoke(() =>
        {
            AddSimLog($"Hub 연결 끊김{(ex is not null ? $": {ex.Message}" : "")}", LogSeverity.Warn);
            SimStatusText = "Hub 연결 끊김";
        });
        return Task.CompletedTask;
    }

    private void StopHub()
    {
        if (_hubConnection is not null)
        {
            var conn = _hubConnection;
            _hubConnection = null;
            conn.Closed -= OnHubDisconnected;
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

    private void OnHubTagChanged(string address, string value, string source)
    {
        _dispatcher.BeginInvoke(() =>
            AddSimLog($"[Hub수신] {address}={value} from={source}", LogSeverity.Info));

        if (_simEngine is null || _runtimeSession is null)
            return;
        // 자기 모드의 source는 무시 (순환 방지)
        if (_runtimeSession.ShouldIgnoreHubSource(source))
            return;

        var effects = _runtimeSession.HandleHubTag(address, value, source);
        ApplyRuntimeHubEffects(effects);
    }
}
