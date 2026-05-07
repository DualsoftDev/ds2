using System;
using System.Threading.Tasks;
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
}
