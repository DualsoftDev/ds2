using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// 1d-4 D — MainWindow 안 dock LLM Chat panel 의 ViewModel. 첫 토글 시 lazy 생성.
    /// MainWindow 가 IsLlmChatVisible PropertyChanged 를 구독해 column width 토글.
    /// </summary>
    [ObservableProperty] private LlmChatViewModel? _llmChatVm;
    [ObservableProperty] private bool _isLlmChatVisible;

    /// <summary>
    /// ENABLE_LLM 환경변수 가 설정된 경우에만 LLM 토글 버튼을 표시.
    /// </summary>
    public bool IsLlmEnabled =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ENABLE_LLM"));

    [RelayCommand]
    private void ToggleLlmChat()
    {
        if (LlmChatVm == null)
        {
            // 첫 활성화 — consent 검사 후 lazy 생성. 거부 시 visibility 변경 없음.
            if (!Promaker.LlmAgent.LlmConfig.EnsureGranted()) return;
            LlmChatVm = new LlmChatViewModel(_store);
        }
        IsLlmChatVisible = !IsLlmChatVisible;
    }

    /// <summary>
    /// MainWindow.Closing 시점에 LLM provider / MCP host 정리.
    /// </summary>
    public async ValueTask DisposeLlmChatAsync()
    {
        if (LlmChatVm != null)
        {
            await LlmChatVm.DisposeAsync();
            LlmChatVm = null;
        }
    }

    /// <summary>
    /// Pass 1.5 측정 자동화 — `--autostart-llm` 인자 시작 시 chat panel 자동 활성화.
    /// McpHostService 는 LlmChatViewModel ctor 에서 fire-and-forget 로 InitializeAsync → mcp config 가 비동기 작성됨.
    /// 추가: --measure-prompt 가 있으면 IsReady 후 자동 prompt 전송, --measure-then-exit 면 turn 끝 후 self-close.
    /// </summary>
    internal void InitLlmAutostart()
    {
        if (!App.StartupAutoOpenLlm) return;

        // ENABLE_LLM 미설정 시 LLM 자체가 비활성(토글 버튼 숨김) → autostart 무시. 측정 모드도 동일 (외부 스크립트가 환경변수 보장 책임).
        if (!IsLlmEnabled)
        {
            Log.Warn("autostart-llm 무시: ENABLE_LLM 환경변수 미설정.");
            return;
        }

        Log.Info($"autostart-llm: 시작 (measure-prompt={(App.StartupMeasurePrompt != null ? "set" : "unset")}, then-exit={App.StartupMeasureThenExit}).");

        _dispatcher.BeginInvoke(new Action(() =>
        {
            ToggleLlmChat();
            if (App.StartupMeasurePrompt == null)
            {
                Log.Info("autostart-llm: measure-prompt 없음 → 일반 autostart 모드 (LLM panel 만 활성화).");
                return;
            }

            if (LlmChatVm != null)
            {
                ScheduleMeasurePrompt(LlmChatVm, App.StartupMeasurePrompt, App.StartupMeasureThenExit);
            }
            else
            {
                // 측정 모드인데 consent 거부 등으로 LlmChatVm 미생성 → silent hang 회피 위해 fail-fast.
                Log.Fatal($"autostart-llm 측정 모드에서 LlmChatVm 생성 실패 (consent 거부?). shutdown({App.MeasureExitLlmVmMissing}).");
                Application.Current?.Shutdown(App.MeasureExitLlmVmMissing);
            }
            // v7 PR-2b — Loaded 가 아닌 ApplicationIdle 로 한 tick 더 미뤄야 dock layout 복원 + ReconcileAnchors 완료 보장.
            // `Apps/Promaker/Docs/todo-dock-layout.md` §3.3 Autostart race 해결.
        }), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 측정 자동화: LlmChatViewModel.IsReady 가 true 되면 Input 에 prompt 적용 + SendCommand 실행.
    /// thenExit=true 이면 LlmChatViewModel.TurnCompleted (= 1 turn 종료, ApplyTurnPlan 까지 완료) 후 MainWindow.Close().
    /// Closing 의 dirty check 는 autostart 에서 skip.
    /// IsReady 가 timeout 안에 true 가 안 되면 (InitializeAsync 실패/hang) fail-fast 로 종료.
    /// </summary>
    private const int MeasureReadyTimeoutSeconds = 60;

    private void ScheduleMeasurePrompt(LlmChatViewModel vm, string prompt, bool thenExit)
    {
        Log.Info($"autostart-llm: ScheduleMeasurePrompt 진입 (prompt 길이={prompt.Length}, thenExit={thenExit}, IsReady timeout={MeasureReadyTimeoutSeconds}s).");

        PropertyChangedEventHandler? readyHandler = null;
        DispatcherTimer? readyTimeoutTimer = null;

        readyHandler = (_, e) =>
        {
            if (e.PropertyName != nameof(LlmChatViewModel.IsReady) || !vm.IsReady) return;
            Log.Info("autostart-llm: IsReady=true → prompt 전송 준비.");
            vm.PropertyChanged -= readyHandler;
            readyTimeoutTimer?.Stop();
            readyTimeoutTimer = null;
            vm.Input = prompt;
            if (!vm.SendCommand.CanExecute(null))
            {
                // SendCommand 실행 불가 → TurnCompleted 영영 안 옴 → thenExit hang. 측정 모드 fail-fast.
                Log.Fatal($"autostart-llm 측정 모드에서 SendCommand.CanExecute=false (prompt 길이={prompt.Length}). shutdown({App.MeasureExitSendCommandUnavailable}).");
                Application.Current?.Shutdown(App.MeasureExitSendCommandUnavailable);
                return;
            }
            Log.Info("autostart-llm: SendCommand.Execute 호출 (turn 시작).");
            vm.SendCommand.Execute(null);

            if (!thenExit) return;

            // TurnCompleted = SendAsync 의 finally 끝(IsSending=false + ApplyTurnPlan 완료) 직후 1회 발생.
            // 응답 마무리(last AssistantDelta flush + Authoring "Executed" 로그)의 background priority 작업이
            // 끝난 후 close 하도록 ApplicationIdle 로 BeginInvoke → log4net flush 충분.
            EventHandler? turnHandler = null;
            turnHandler = (_, _) =>
            {
                vm.TurnCompleted -= turnHandler;
                Log.Info("autostart-llm: TurnCompleted → MainWindow.Close 예약 (ApplicationIdle).");
                _dispatcher.BeginInvoke(new Action(() =>
                    Application.Current?.MainWindow?.Close()), DispatcherPriority.ApplicationIdle);
            };
            vm.TurnCompleted += turnHandler;
        };
        vm.PropertyChanged += readyHandler;

        // IsReady 가 InitializeAsync 실패/hang 으로 영영 true 안 되는 경우의 안전망.
        // 정상 path 에서는 readyHandler 진입 시 timer.Stop() → Tick 호출 안 됨.
        readyTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(MeasureReadyTimeoutSeconds) };
        readyTimeoutTimer.Tick += (_, _) =>
        {
            readyTimeoutTimer.Stop();
            if (vm.IsReady) return; // race — 정상 path 가 같은 dispatcher tick 에서 진행 중
            vm.PropertyChanged -= readyHandler;
            Log.Fatal($"autostart-llm 측정 모드에서 IsReady 가 {MeasureReadyTimeoutSeconds}s 안에 true 안 됨. shutdown({App.MeasureExitInitTimeout}).");
            Application.Current?.Shutdown(App.MeasureExitInitTimeout);
        };
        readyTimeoutTimer.Start();
    }
}
