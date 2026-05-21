using System;
using System.IO;
using System.Threading;
using System.Windows;
using log4net;
using log4net.Config;

namespace Promaker.AgentTray;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private AgentTrayHost? _trayHost;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 단일 인스턴스 가드 — HKCU\Run 자동시작 + 사용자 수동 실행이 겹쳐도 1개만.
        _singleInstanceMutex = new Mutex(initiallyOwned: true,
            name: "Global\\Promaker.AgentTray.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }

        InitializeLogging();
        var log = LogManager.GetLogger("Promaker.AgentTray");
        log.Info("================ Promaker.AgentTray starting ================");

        _trayHost = new AgentTrayHost();
        _trayHost.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayHost?.Dispose();
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* not owned */ }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void InitializeLogging()
    {
        var cfg = new FileInfo(Path.Combine(AppContext.BaseDirectory, "log4net.config"));
        if (cfg.Exists)
        {
            var repo = LogManager.GetRepository(typeof(App).Assembly);
            XmlConfigurator.Configure(repo, cfg);
        }
    }
}
