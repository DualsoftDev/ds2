using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using log4net;
using Promaker.Shared;

namespace Promaker.AgentTray;

/// <summary>
/// Promaker.Agent 의 사용자 컨텍스트 트레이 — 모니터링 상태 노출 + 시작/정지/자동재시작 토글.
/// 무대화면 없음 (창 미생성), 트레이 아이콘 + 컨텍스트 메뉴만.
///
/// 상태 소스 3개:
///   - Promaker.Agent Windows Service 의 ServiceController 상태 (Running / Stopped / NotInstalled)
///   - <see cref="Promaker.Shared.SharedPaths.AgentActiveFlagPath"/> 존재 여부 (Active vs Idle)
///   - SignalR Hub TCP probe(localhost:5051) — 옵션, 향후 추가
/// </summary>
public sealed class AgentTrayHost : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AgentTrayHost));
    private const string ServiceName = "PromakerAgentService";
    private const int HubPort = 5051;
    private const int PollIntervalMs = 5000;
    private const int TcpProbeTimeoutMs = 800;

    private TaskbarIcon? _icon;
    private MenuItem? _statusItem;
    private MenuItem? _hubProbeItem;
    private MenuItem? _startItem;
    private MenuItem? _stopItem;
    private MenuItem? _autoStartItem;
    private FileSystemWatcher? _flagWatcher;
    private Timer? _pollTimer;
    private bool _disposed;
    private volatile bool _refreshInFlight;

    public void Start()
    {
        Directory.CreateDirectory(SharedPaths.AgentDirectory);

        var icon = new TaskbarIcon
        {
            ToolTipText = "Promaker Agent",
            Visibility = Visibility.Visible,
        };

        try
        {
            var uri = new Uri("pack://application:,,,/Promaker.AgentTray;component/Assets/Promaker.ico",
                UriKind.Absolute);
            icon.IconSource = new BitmapImage(uri);
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray icon load failed: {ex.Message}");
        }

        icon.ContextMenu = BuildMenu();
        try { icon.ForceCreate(); }
        catch (Exception ex) { Log.Warn($"TaskbarIcon.ForceCreate failed: {ex.Message}"); }

        _icon = icon;

        // 파일 + 폴링 양쪽으로 상태 추적 — FileSystemWatcher 가 놓치는 경우(원격 마운트 등) 폴링이 백업.
        _flagWatcher = new FileSystemWatcher(SharedPaths.AgentDirectory,
            Path.GetFileName(SharedPaths.AgentActiveFlagPath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _flagWatcher.Created += (_, _) => RefreshOnUi();
        _flagWatcher.Deleted += (_, _) => RefreshOnUi();
        _flagWatcher.Changed += (_, _) => RefreshOnUi();

        _pollTimer = new Timer(_ => RefreshOnUi(), null, 0, PollIntervalMs);

        Log.Info("AgentTray started.");
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        _statusItem = new MenuItem { Header = "Promaker Agent — 확인 중...", IsEnabled = false };
        menu.Items.Add(_statusItem);

        // Service + flag 와 별도로, 실제 Hub(5051) 가 응답하는지 — 진짜 PLC 데이터가 흐르고 있는지 1차 지표.
        // Service Running + active.flag + 5051 응답 셋 다 만족이어야 정상 모니터링 상태.
        _hubProbeItem = new MenuItem { Header = "Hub 5051 — 확인 중...", IsEnabled = false };
        menu.Items.Add(_hubProbeItem);
        menu.Items.Add(new Separator());

        _startItem = new MenuItem { Header = "모니터링 시작" };
        _startItem.Click += (_, _) => OnStartMonitoring();
        menu.Items.Add(_startItem);

        _stopItem = new MenuItem { Header = "모니터링 정지" };
        _stopItem.Click += (_, _) => OnStopMonitoring();
        menu.Items.Add(_stopItem);

        menu.Items.Add(new Separator());

        _autoStartItem = new MenuItem { Header = "재부팅 시 자동 실행: ?" };
        _autoStartItem.Click += (_, _) => OnToggleAutoStart();
        menu.Items.Add(_autoStartItem);

        var openLogItem = new MenuItem { Header = "Agent 로그 열기" };
        openLogItem.Click += (_, _) => OnOpenAgentLog();
        menu.Items.Add(openLogItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "AgentTray 종료 (Agent 서비스는 계속 동작)" };
        exitItem.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exitItem);

        return menu;
    }

    // ── 상태 갱신 ────────────────────────────────────────────────

    private void RefreshOnUi()
    {
        if (_disposed) return;
        if (_refreshInFlight) return;   // TCP probe 가 끝나기 전 중복 호출 차단.
        _refreshInFlight = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var svc = QueryServiceStatus();
                var active = AgentSession.IsActive();
                // TCP probe 는 svc 가 Running 이고 active 일 때만 의미 — 그 외엔 굳이 시도 안 함 (불필요한 800ms 대기 회피).
                var hubResponsive = (svc is { Installed: true, Status: ServiceControllerStatus.Running } && active)
                    ? await ProbeHubAsync().ConfigureAwait(false)
                    : (bool?)null;

                try
                {
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        UpdateMenuLabels(svc, active, hubResponsive);
                        UpdateTooltip(svc, active, hubResponsive);
                    }));
                }
                catch { /* shutdown race */ }
            }
            finally
            {
                _refreshInFlight = false;
            }
        });
    }

    /// <summary>localhost:5051 TCP 연결 가능 여부를 800ms 안에 판정.
    /// 연결 성공 = Hub 가 listening. 실패 = 서비스 Running 라 해도 Hub bind 안 됐거나
    /// (예: 다른 프로세스가 5051 점유, 첫 BackendHost 시작 race) 데이터 흐르지 않음.</summary>
    private static async Task<bool> ProbeHubAsync()
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TcpProbeTimeoutMs);
            await client.ConnectAsync(IPAddress.Loopback, HubPort, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static ServiceStatus QueryServiceStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            // Status 접근 시 예외가 나면 서비스 미설치.
            var status = sc.Status;
            var startType = sc.StartType;
            return new ServiceStatus(true, status, startType);
        }
        catch
        {
            return new ServiceStatus(false, ServiceControllerStatus.Stopped, ServiceStartMode.Disabled);
        }
    }

    private void UpdateMenuLabels(ServiceStatus svc, bool active, bool? hubResponsive)
    {
        if (_statusItem is null) return;

        // 최상위 상태 — Service + flag 만으로 판단 (5051 응답은 별도 메뉴 항목).
        var statusText = svc switch
        {
            { Installed: false } => "Promaker Agent — 서비스 미설치 (installer 재실행 필요)",
            { Status: ServiceControllerStatus.Running } when active => "Promaker Agent — ● 모니터링 중",
            { Status: ServiceControllerStatus.Running }              => "Promaker Agent — ○ 대기 (idle)",
            { Status: ServiceControllerStatus.Stopped }              => "Promaker Agent — ✗ 정지됨",
            _                                                         => $"Promaker Agent — {svc.Status}",
        };
        _statusItem.Header = statusText;

        if (_hubProbeItem is not null)
        {
            _hubProbeItem.Header = hubResponsive switch
            {
                true  => "Hub 5051 — ● 응답 (데이터 흐름 정상)",
                false => "Hub 5051 — ⚠ 응답 없음 (서비스는 Running, Hub bind 실패 의심)",
                null  => svc switch
                {
                    { Installed: false } => "Hub 5051 — — (서비스 미설치)",
                    { Status: ServiceControllerStatus.Running } when !active
                        => "Hub 5051 — — (대기 중, 모니터링 비활성)",
                    _ => "Hub 5051 — — (서비스 정지)",
                },
            };
        }

        if (_startItem is not null)
        {
            _startItem.IsEnabled = svc.Installed && !active;
            _startItem.Header = active ? "모니터링 시작 (이미 활성)" : "모니터링 시작";
        }
        if (_stopItem is not null)
        {
            _stopItem.IsEnabled = svc.Installed && active;
        }

        if (_autoStartItem is not null)
        {
            var on = svc.StartType == ServiceStartMode.Automatic;
            _autoStartItem.IsEnabled = svc.Installed;
            _autoStartItem.Header = on
                ? "재부팅 시 자동 실행: ON  (클릭하여 OFF)"
                : "재부팅 시 자동 실행: OFF (클릭하여 ON)";
        }
    }

    private void UpdateTooltip(ServiceStatus svc, bool active, bool? hubResponsive)
    {
        if (_icon is null) return;
        // 툴팁은 한 줄 — 가장 강한 신호 표시. 모니터링 정상 = Service Running + flag + Hub 응답 셋 다.
        _icon.ToolTipText = (svc.Installed, svc.Status, active, hubResponsive) switch
        {
            (false, _, _, _)                                          => "Promaker Agent: 서비스 미설치",
            (true, ServiceControllerStatus.Stopped, _, _)             => "Promaker Agent: 서비스 정지됨",
            (true, ServiceControllerStatus.Running, true, true)       => "Promaker Agent: ● 모니터링 중 (5051 응답)",
            (true, ServiceControllerStatus.Running, true, false)      => "Promaker Agent: ⚠ 활성 — 5051 응답 없음",
            (true, ServiceControllerStatus.Running, true, null)       => "Promaker Agent: 모니터링 중",
            (true, ServiceControllerStatus.Running, false, _)         => "Promaker Agent: ○ 대기 (idle)",
            _                                                          => $"Promaker Agent: {svc.Status}",
        };
    }

    // ── 메뉴 액션 ────────────────────────────────────────────────

    private void OnStartMonitoring()
    {
        // 마지막 세션이 있으면 그걸 그대로, 없으면 기본 경로로.
        var session = AgentSession.TryLoad() ?? AgentSession.ForCurrentDefaults(requestedBy: "agenttray");
        // ActivatedAtUtc 만 새로 갱신 — 가장 최근 활성화 시점 추적.
        session.ActivatedAtUtc = DateTime.UtcNow.ToString("o");
        session.RequestedBy = "agenttray";
        if (!session.TryWrite())
        {
            MessageBox.Show("active.flag 기록 실패 — 공유 폴더 권한을 확인하세요.",
                "Promaker Agent", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshOnUi();
    }

    private void OnStopMonitoring()
    {
        var ok = AgentSession.TryDeactivate();
        if (!ok)
            MessageBox.Show("active.flag 삭제 실패.",
                "Promaker Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshOnUi();
    }

    private void OnToggleAutoStart()
    {
        var svc = QueryServiceStatus();
        if (!svc.Installed) return;
        var desired = svc.StartType == ServiceStartMode.Automatic ? "disabled" : "auto";
        // sc config 는 관리자 권한 필요 — UAC elevation 으로 별도 프로세스 spawn.
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"config {ServiceName} start= {desired}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };
            var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Log.Warn($"sc config 실패: {ex.Message}");
            MessageBox.Show("관리자 권한 elevation 이 취소되었거나 실패했습니다.",
                "Promaker Agent", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        RefreshOnUi();
    }

    private void OnOpenAgentLog()
    {
        // 설치된 환경에서는 {app}\Agent\logs\promaker-agent.log. 개발에서는 publish 디렉터리.
        // AgentTray 의 BaseDirectory 상위 (..\Agent\logs) 또는 형제 ..\Agent 가 일반적.
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "..", "Agent", "logs", "promaker-agent.log"),
            Path.Combine(baseDir, "logs", "promaker-agent.log"),
        };
        foreach (var path in candidates)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
            {
                try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Warn($"Open agent log failed: {ex.Message}"); }
                return;
            }
        }
        MessageBox.Show("Agent 로그 파일을 찾을 수 없습니다.\n경로 확인: " + string.Join("\n", candidates),
            "Promaker Agent", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── lifecycle ─────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer?.Dispose();
        try { _flagWatcher?.Dispose(); } catch { }
        try { _icon?.Dispose(); } catch { }
        _icon = null;
        Log.Info("AgentTray stopped.");
    }

    private readonly record struct ServiceStatus(bool Installed, ServiceControllerStatus Status, ServiceStartMode StartType);
}
