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

        _dispatcher.BeginInvoke(new Action(() =>
        {
            ToggleLlmChat();
            if (App.StartupMeasurePrompt == null) return;

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
        }), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 측정 자동화: LlmChatViewModel.IsReady 가 true 되면 Input 에 prompt 적용 + SendCommand 실행.
    /// thenExit=true 이면 LlmChatViewModel.TurnCompleted (= 1 turn 종료, ApplyTurnPlan 까지 완료) 후 MainWindow.Close().
    /// Closing 의 dirty check 는 autostart 에서 skip.
    /// </summary>
    private void ScheduleMeasurePrompt(LlmChatViewModel vm, string prompt, bool thenExit)
    {
        PropertyChangedEventHandler? readyHandler = null;
        readyHandler = (_, e) =>
        {
            if (e.PropertyName != nameof(LlmChatViewModel.IsReady) || !vm.IsReady) return;
            vm.PropertyChanged -= readyHandler;
            vm.Input = prompt;
            if (!vm.SendCommand.CanExecute(null))
            {
                // SendCommand 실행 불가 → TurnCompleted 영영 안 옴 → thenExit hang. 측정 모드 fail-fast.
                Log.Fatal($"autostart-llm 측정 모드에서 SendCommand.CanExecute=false (prompt 길이={prompt.Length}). shutdown({App.MeasureExitSendCommandUnavailable}).");
                Application.Current?.Shutdown(App.MeasureExitSendCommandUnavailable);
                return;
            }
            vm.SendCommand.Execute(null);

            if (!thenExit) return;

            // TurnCompleted = SendAsync 의 finally 끝(IsSending=false + ApplyTurnPlan 완료) 직후 1회 발생.
            // 응답 마무리(last AssistantDelta flush + Authoring "Executed" 로그)의 background priority 작업이
            // 끝난 후 close 하도록 ApplicationIdle 로 BeginInvoke → log4net flush 충분.
            EventHandler? turnHandler = null;
            turnHandler = (_, _) =>
            {
                vm.TurnCompleted -= turnHandler;
                _dispatcher.BeginInvoke(new Action(() =>
                    Application.Current?.MainWindow?.Close()), DispatcherPriority.ApplicationIdle);
            };
            vm.TurnCompleted += turnHandler;
        };
        vm.PropertyChanged += readyHandler;
    }
}
