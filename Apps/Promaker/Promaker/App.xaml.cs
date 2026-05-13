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

    /// <summary>
    /// `--measure-prompt <text>` 인자 — 측정용 prompt 자동 전송. IsReady 후 LlmChatVm.Input 에 set + SendCommand 자동 실행.
    /// LlmTurnContext 가 정상 시작 → mutation tool 이 ImportPlanBuilder 누적 → turn end 시 ApplyImportPlan.
    /// </summary>
    internal static string? StartupMeasurePrompt { get; set; }

    /// <summary>
    /// `--measure-then-exit` 인자 — IsSending true→false transition (= turn 끝 + ApplyImportPlan 완료) 후 MainWindow 자동 close.
    /// MainWindow.Closing 의 dirty check 도 autostart 모드에서 skip. log4net flush 보장 + 외부 fsx 의 process.WaitForExit 자연 완료.
    /// </summary>
    internal static bool StartupMeasureThenExit { get; set; }

    /// <summary>
    /// `--dock-spike` 인자 — PR-1b 임시 모드. MainWindow 대신 DockSpikeWindow 만 띄움.
    /// PR-2a 진입 시 본 옵션 + Spike 폴더 함께 제거 예정.
    /// </summary>
    internal static bool StartupDockSpike { get; set; }

    // 측정 자동화 fail-fast exit codes — 외부 측정 스크립트(run-pass5.fsx 등)가 이 값으로 실패 원인을 분기 식별.
    // 변경 시 측정 스크립트 측도 함께 갱신 필요.
    internal const int MeasureExitSendCommandUnavailable = 2;
    internal const int MeasureExitLlmVmMissing = 3;
    internal const int MeasureExitInitTimeout = 4;

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
        // AAStoPLC.TagWizard 의 모든 family preset 자동 등록 (idempotent).
        // 새 family 추가 시 Bootstrap.fs 의 ensureRegistered 안에서 처리 — startup 무수정.
        AAStoPLC.TagWizard.Bootstrap.EnsureRegistered();

        for (int i = 0; i < e.Args.Length; i++)
        {
            var arg = e.Args[i];
            if (arg == "--autostart-llm")
                StartupAutoOpenLlm = true;
            else if (arg == "--measure-then-exit")
                StartupMeasureThenExit = true;
            else if (arg == "--measure-prompt" && i + 1 < e.Args.Length)
            {
                StartupMeasurePrompt = e.Args[i + 1];
                i++;   // skip next (consumed as prompt value)
            }
            else if (arg == "--dock-spike")
                StartupDockSpike = true;
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

        // GUI Log tab 의 AppLogState (singleton + ICollectionView) 를 UI thread 에서 강제 prefetch.
        // worker thread 의 첫 log 호출이 lazy 생성을 trigger 하면 CollectionView 가 worker SynchronizationContext
        // 에 묶여 이후 binding 시 NotSupportedException. fatal handler 등록 이후 시점이므로 ctor 예외 시 진단 가능.
        _ = Promaker.ViewModels.Logging.AppLogState.Instance;

        // 1d-5 — 비정상 종료한 이전 Promaker 인스턴스가 남긴 stale .mcp-config 정리.
        // 자기 sessionId + dead pid 또는 mtime > 5분 조건만 (자기 자신 / 다른 user session 보호).
        Promaker.LlmAgent.McpConfigWriter.SweepStale();

        // M1 — Codex CLI 임시 이미지 spool stale 정리 (mtime > 30분).
        // OnFinally cleanup 실패 / Promaker kill 등으로 남은 `%TEMP%\Promaker.LlmAgent\codex-img-*` 회수.
        Ds2.LlmAgent.CodexCliProvider.SweepStale();

        // CP949 (Windows-949) 등 legacy code page 활성화 — .NET Core/9 는 기본 미포함. LLM Chat 첨부 텍스트
        // 파일 인코딩 추정 (`AttachmentClassifier.detectEncoding`) 의 CP949 fallback 분기 활성화 (review F3).
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        Log.Info("=== Promaker startup ===");

        if (StartupDockSpike)
        {
            // PR-1b — MainWindow.xaml StartupUri 우회. ShutdownMode=OnMainWindowClose 라
            // MainWindow=spike 설정 후 spike close 시 종료.
            var spike = new Promaker.Spike.DockSpikeWindow();
            MainWindow = spike;
            spike.Show();
            return;
        }

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
