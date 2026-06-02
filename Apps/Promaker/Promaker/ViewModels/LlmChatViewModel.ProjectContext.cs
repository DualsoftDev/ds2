namespace Promaker.ViewModels;

public partial class LlmChatViewModel
{
    /// <summary>마지막 닫힌 프로젝트 경로. 앱 세션 한정 (in-memory).
    /// 라이프사이클:
    ///   set   = OnProjectClosing 시점.
    ///   clear = (a) SendAsync 의 hint 주입 직후 (1회성 — 토큰 절약, session history 가 이미 LLM 에 인지시켜줌),
    ///           (b) OnProjectOpened (새 프로젝트 진입 — hint 무효화),
    ///           (c) ResetConversation (RestartCommand 재시작 / OnProjectClosing 의 세션 초기화).</summary>
    public string? LastClosedProjectPath { get; private set; }

    /// <summary>
    /// MainViewModel.CloseFile 가 호출. 프로젝트 닫기에는 MCP host / KB 세션 재시작이 불필요하므로 L3 RestartAsync 가
    /// 아니라 L1 <see cref="ResetConversation"/>(Cancel/ClearSession/Turns/Attachments) 만 재사용 + LastClosedProjectPath 캡처.
    /// </summary>
    public void OnProjectClosing(string? lastPath)
    {
        // 순서 의존: ResetConversation 이 LastClosedProjectPath = null 로 비우므로 set 은 *반드시* 호출 이후.
        // ResetConversation 자체의 책임 (세션 초기화 시 hint 도 clear) 은 유지하고, 여기서는 닫기 직후 hint 캡처만 추가.
        ResetConversation();
        // 구 Reset() 말미의 StatusText 갱신 보존 — ResetConversation 은 RestartAsync 와 공유라 StatusText 미변경
        // (재시작 중엔 "재시작 중…" 유지). 프로젝트 닫기 경로에서만 상태줄 명시 갱신 (직전 turn 문구 잔존 방지).
        StatusText = "세션 초기화 완료";
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
