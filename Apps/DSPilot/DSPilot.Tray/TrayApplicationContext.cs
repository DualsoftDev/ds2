using System.Diagnostics;
using System.Text.Json;

namespace DSPilot.Tray;

internal class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly string _dashboardUrl;

    public TrayApplicationContext()
    {
        _dashboardUrl = ResolveDashboardUrl();

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("웹 대시보드 열기", null, OnOpenDashboard);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("서비스 재시작", null, OnRestartService);
        contextMenu.Items.Add("서비스 종료", null, OnStopService);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("트레이 종료", null, OnExit);

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "DSPilot",
            Visible = true,
            ContextMenuStrip = contextMenu
        };

        _notifyIcon.DoubleClick += OnOpenDashboard;
    }

    private static Icon LoadIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "DSPilot.ico");
        if (File.Exists(iconPath))
            return new Icon(iconPath);

        return SystemIcons.Application;
    }

    private static string ResolveDashboardUrl()
    {
        // appsettings.Production.json에서 포트 읽기
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.Production.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                var json = File.ReadAllText(settingsPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Urls", out var urlsProp))
                {
                    var urls = urlsProp.GetString(); // e.g. "http://*:8080"
                    if (urls != null)
                    {
                        var port = ExtractPort(urls);
                        if (port != null && port != "80")
                            return $"http://localhost:{port}";
                    }
                }
            }
            catch { }
        }

        return "http://localhost";
    }

    private static string? ExtractPort(string urls)
    {
        // "http://*:8080" or "http://+:8080" or "http://0.0.0.0:8080"
        var lastColon = urls.LastIndexOf(':');
        if (lastColon >= 0)
        {
            var portStr = urls[(lastColon + 1)..];
            if (int.TryParse(portStr, out _))
                return portStr;
        }
        return null;
    }

    private void OnOpenDashboard(object? sender, EventArgs e)
    {
        Process.Start(new ProcessStartInfo(_dashboardUrl) { UseShellExecute = true });
    }

    private void OnRestartService(object? sender, EventArgs e)
    {
        RunServiceAction("서비스 재시작", ServiceManager.RestartService);
    }

    private void OnStopService(object? sender, EventArgs e)
    {
        RunServiceAction("서비스 종료", ServiceManager.StopService);
    }

    private void RunServiceAction(string actionName, Action action)
    {
        try
        {
            action();
            _notifyIcon.ShowBalloonTip(2000, "DSPilot", $"{actionName} 완료", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(3000, "DSPilot", $"{actionName} 실패: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
