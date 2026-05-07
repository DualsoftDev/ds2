using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using log4net;
using log4net.Config;
using Promaker.Presentation;

namespace Promaker;

public partial class App : Application
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(App));

    /// <summary>더블클릭 등으로 전달된 파일 경로 (첫 번째 인자).</summary>
    internal static string? StartupFilePath { get; set; }

    /// <summary>
    /// `--autostart-llm` 인자 — Pass 1.5 측정 자동화용. 시작 시 LLM Chat panel 자동 활성화 →
    /// McpHostService 가 StartAsync 되어 mcp config 파일이 즉시 작성됨. 수동 모드에서는 사용 안 함.
    /// </summary>
    internal static bool StartupAutoOpenLlm { get; set; }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);

    /// <summary>
    /// Windows 기본 system timer 해상도(15.6ms)는 Control 모드처럼 ms 단위 외부 PLC 연동 시
    /// WaitHandle.WaitAny timeout / Task.Delay / Thread.Sleep 의 정밀도가 부족함.
    /// 1ms 로 강제해서 simulationLoop 의 wakeSignal 이 timeout 으로 깰 때도 ms 단위 보장.
    /// Linux 는 커널 timer 가 기본 1ms 라 별도 설정 불필요.
    /// </summary>
    private const uint TimerPeriodMs = 1u;
    private bool _timerPeriodSet;

    protected override void OnStartup(StartupEventArgs e)
    {
        foreach (var arg in e.Args)
        {
            if (arg == "--autostart-llm")
                StartupAutoOpenLlm = true;
            else if (StartupFilePath == null && File.Exists(arg))
                StartupFilePath = arg;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (TimeBeginPeriod(TimerPeriodMs) == 0)
                    _timerPeriodSet = true;
            }
            catch
            {
                // winmm 호출 실패해도 동작은 가능 (정밀도만 보장 안 됨)
            }
        }
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

        // 1d-5 — 비정상 종료한 이전 Promaker 인스턴스가 남긴 stale .mcp-config 정리.
        // 자기 sessionId + dead pid 또는 mtime > 5분 조건만 (자기 자신 / 다른 user session 보호).
        Promaker.LlmAgent.McpConfigWriter.SweepStale();

        Log.Info("=== Promaker startup ===");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_timerPeriodSet && OperatingSystem.IsWindows())
        {
            try { TimeEndPeriod(TimerPeriodMs); } catch { }
        }
        Log.Info("=== Promaker shutdown ===");
        base.OnExit(e);
    }
}
