using System;
using System.Globalization;
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
    internal static DateTimeOffset RunStartedAt { get; } = DateTimeOffset.Now;

    internal static string RunId { get; } =
        $"{RunStartedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-pid{Environment.ProcessId}";

    /// <summary>더블클릭 등으로 전달된 파일 경로 (첫 번째 인자).</summary>
    internal static string? StartupFilePath { get; set; }

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
        // 3rd-party 테마 (AvalonDock / MahApps IconPacks) 내부의 GeometryDrawing.Brush={Binding Fill}
        // 류는 DataContext 상속 불가 위치에서 raise 되어 무해한 BindingExpression 경고를 발생시킨다.
        // Critical 만 유지하여 실 오류는 보존, severity 2 (Warning) 잡음만 제거.
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level =
            System.Diagnostics.SourceLevels.Critical;

        // AAStoPLC.TagWizard 의 모든 family preset 자동 등록 (idempotent).
        // 새 family 추가 시 Bootstrap.fs 의 ensureRegistered 안에서 처리 — startup 무수정.
        AAStoPLC.TagWizard.Bootstrap.EnsureRegistered();

        for (int i = 0; i < e.Args.Length; i++)
        {
            var arg = e.Args[i];
            if (StartupFilePath == null && File.Exists(arg))
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
        // PR-A1 (DX → AvalonDock) — AvalonDock 은 일반 NuGet 어셈블리라 AppDomain.AssemblyResolve hook 불필요.
        // 호출은 외부 API 호환을 위해 유지 (DockHost.RegisterAssemblyResolve 는 no-op 박제).
        Promaker.Dock.DockHost.RegisterAssemblyResolve();

        // process working dir 가 exe 폴더가 아닐 수 있어 (단축키 / dotnet run / 다른 cwd 에서 실행)
        // AppContext.BaseDirectory (exe 폴더) 기준 절대 경로로 명시 — log4net Configure silent skip 회피.
        GlobalContext.Properties["PromakerRunId"] = RunId;
        var configFile = new FileInfo(Path.Combine(AppContext.BaseDirectory, "log4net.config"));
        if (configFile.Exists)
            XmlConfigurator.Configure(configFile);
        else
            System.Diagnostics.Trace.TraceWarning($"log4net.config not found at {configFile.FullName}. Logging may be disabled.");

        Log.Info(
            $"PROMAKER_RUN_BEGIN runId={RunId} pid={Environment.ProcessId} " +
            $"startedAt={RunStartedAt.ToString("O", CultureInfo.InvariantCulture)} baseDir=\"{AppContext.BaseDirectory}\"");

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

        // AvalonDock 테마 — Promaker 라이트/다크에 연동 (dark=Vs2013DarkTheme, light=Vs2013LightTheme).
        // 격리 helper 호출 (AvalonDock type 외부 노출 0건 유지). 초기 1회 + 테마 전환 시 갱신.
        Promaker.Dock.DockHost.SetTheme(ThemeManager.CurrentTheme == AppTheme.Dark);
        ThemeManager.ThemeChanged += t => Promaker.Dock.DockHost.SetTheme(t == AppTheme.Dark);

        // GUI Log tab 의 AppLogState (singleton + ICollectionView) 를 UI thread 에서 강제 prefetch.
        // worker thread 의 첫 log 호출이 lazy 생성을 trigger 하면 CollectionView 가 worker SynchronizationContext
        // 에 묶여 이후 binding 시 NotSupportedException. fatal handler 등록 이후 시점이므로 ctor 예외 시 진단 가능.
        _ = Promaker.ViewModels.Logging.AppLogState.Instance;

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_timerPeriodSet && OperatingSystem.IsWindows())
        {
            try { TimeEndPeriod(TimerPeriodMs); } catch { }
        }

        var uptimeMs = (DateTimeOffset.Now - RunStartedAt).TotalMilliseconds;
        Log.Info(
            $"PROMAKER_RUN_END runId={RunId} pid={Environment.ProcessId} " +
            $"exitCode={e.ApplicationExitCode} uptimeMs={uptimeMs:F0}");
        base.OnExit(e);
    }
}
