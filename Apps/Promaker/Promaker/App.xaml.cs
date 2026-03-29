using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using log4net;
using log4net.Config;
using Promaker.Presentation;
using Promaker.ViewModels;

namespace Promaker;

public partial class App : Application
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(App));

    /// <summary>더블클릭 등으로 전달된 파일 경로 (첫 번째 인자).</summary>
    internal static string? StartupFilePath { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
            StartupFilePath = e.Args[0];

        // Shift 키를 누른 채 실행하면 3D 뷰 활성화
        MainViewModel.Is3DViewEnabled = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
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

        ThemeManager.ApplySavedTheme();

        Log.Info("=== Promaker startup ===");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("=== Promaker shutdown ===");
        base.OnExit(e);
    }
}
