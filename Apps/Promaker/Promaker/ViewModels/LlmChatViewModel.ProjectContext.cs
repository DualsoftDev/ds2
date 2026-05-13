namespace Promaker.ViewModels;

public partial class LlmChatViewModel
{
    /// <summary>마지막 닫힌 프로젝트 경로. 앱 세션 한정 (in-memory).
    /// 라이프사이클:
    ///   set   = OnProjectClosing 시점.
    ///   clear = (a) SendAsync 의 hint 주입 직후 (1회성 — 토큰 절약, session history 가 이미 LLM 에 인지시켜줌),
    ///           (b) OnProjectOpened (새 프로젝트 진입 — hint 무효화),
    ///           (c) Reset RelayCommand (사용자 명시 세션 초기화).</summary>
    public string? LastClosedProjectPath { get; private set; }

    /// <summary>
    /// MainViewModel.CloseFile 가 Reset() 직전에 호출.
    /// 책임 분담: 기존 ResetCommand 재활용 (Cancel/ClearSession/Turns/Attachments) + LastClosedProjectPath 캡처.
    /// </summary>
    public void OnProjectClosing(string? lastPath)
    {
        // 순서 의존: ResetCommand 본문이 LastClosedProjectPath = null 로 비우므로 set 은 *반드시* Reset 호출 이후.
        // Reset 자체의 책임 (사용자 명시 세션 초기화 시 hint 도 clear) 은 유지하고, 여기서는 닫기 직후 hint 캡처만 추가.
        ResetCommand.Execute(null);
        LastClosedProjectPath = lastPath;
        Log.Info($"LLM context cleared on project close (lastPath={lastPath ?? "(unsaved)"}).");
    }

    /// <summary>새 프로젝트가 열리거나 생성되면 LastClosedProjectPath 무효화.</summary>
    public void OnProjectOpened()
    {
        LastClosedProjectPath = null;
        Log.Info("LastClosedProjectPath cleared (new project opened).");
    }
}
