using System.Windows;

namespace Promaker.Services;

/// <summary>
/// 다이얼로그 표시를 담당하는 서비스 인터페이스
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// 이름 입력 프롬프트 표시
    /// </summary>
    /// <param name="title">다이얼로그 제목</param>
    /// <param name="defaultName">기본 이름</param>
    /// <returns>입력된 이름, 취소 시 null</returns>
    string? PromptName(string title, string defaultName);

    /// <summary>
    /// 확인 다이얼로그 표시
    /// </summary>
    /// <param name="message">메시지</param>
    /// <param name="title">제목</param>
    /// <returns>사용자가 확인을 선택하면 true</returns>
    bool Confirm(string message, string title);

    /// <summary>
    /// 경고 메시지 표시
    /// </summary>
    /// <param name="message">경고 메시지</param>
    void ShowWarning(string message);

    /// <summary>
    /// 시뮬레이션 중 편집 차단 경고 + "시뮬레이션 종료" 옵션 다이얼로그.
    /// </summary>
    /// <param name="message">경고 메시지</param>
    /// <returns>사용자가 시뮬 종료를 선택했으면 true, 그 외 false</returns>
    bool WarnSimulationEditBlocked(string message);

    /// <summary>
    /// 에러 메시지 표시
    /// </summary>
    /// <param name="message">에러 메시지</param>
    void ShowError(string message);

    /// <summary>
    /// 정보 메시지 표시
    /// </summary>
    /// <param name="message">정보 메시지</param>
    void ShowInfo(string message);

    /// <summary>
    /// 저장 확인 다이얼로그 표시 (예/아니오/취소)
    /// </summary>
    /// <returns>MessageBoxResult</returns>
    MessageBoxResult AskSaveChanges();

    /// <summary>
    /// 파일 열기 다이얼로그 표시
    /// </summary>
    /// <param name="filter">파일 필터</param>
    /// <returns>선택된 파일 경로, 취소 시 null</returns>
    string? ShowOpenFileDialog(string filter);

    /// <summary>
    /// 파일 저장 다이얼로그 표시
    /// </summary>
    /// <param name="filter">파일 필터</param>
    /// <param name="defaultFileName">기본 파일 이름</param>
    /// <returns>선택된 파일 경로, 취소 시 null</returns>
    string? ShowSaveFileDialog(string filter, string? defaultFileName = null);

    /// <summary>
    /// 커스텀 다이얼로그 표시
    /// </summary>
    /// <typeparam name="T">다이얼로그 결과 타입</typeparam>
    /// <param name="dialog">표시할 다이얼로그</param>
    /// <returns>다이얼로그 결과, 취소 시 null</returns>
    T? ShowDialog<T>(Window dialog) where T : class;

    /// <summary>
    /// 커스텀 다이얼로그 표시 (bool 결과)
    /// </summary>
    /// <param name="dialog">표시할 다이얼로그</param>
    /// <returns>다이얼로그 결과</returns>
    bool? ShowDialog(Window dialog);
}
