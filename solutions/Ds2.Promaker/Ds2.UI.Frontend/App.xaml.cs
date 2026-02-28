using log4net;
using log4net.Config;
using System.IO;
using System.Windows;

namespace Ds2.UI.Frontend;

public partial class App : Application
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(App));

    protected override void OnStartup(StartupEventArgs e)
    {
        var configFile = new FileInfo("log4net.config");
        if (configFile.Exists)
            XmlConfigurator.Configure(configFile);
        else
            System.Diagnostics.Trace.TraceWarning("log4net.config 파일을 찾을 수 없습니다. 로깅이 비활성화됩니다.");

        Log.Info("=== Ds2.Promaker 시작 ===");

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Fatal("처리되지 않은 예외", ex.Exception);
            ex.Handled = true;
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("=== Ds2.Promaker 종료 ===");
        base.OnExit(e);
    }
}
