using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using log4net;
using log4net.Config;

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
            System.Diagnostics.Trace.TraceWarning("log4net.config was not found. Logging may be disabled.");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Fatal("Unhandled AppDomain exception", ex);
            else
                Log.Fatal($"Unhandled AppDomain exception (non-Exception): {args.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal("Unhandled dispatcher exception", args.Exception);
            MessageBox.Show(
                $"A fatal UI error occurred and the app will stop.\n\n{args.Exception.Message}",
                "Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = false;
        };

        Log.Info("=== Ds2.Promaker startup ===");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("=== Ds2.Promaker shutdown ===");
        base.OnExit(e);
    }
}
