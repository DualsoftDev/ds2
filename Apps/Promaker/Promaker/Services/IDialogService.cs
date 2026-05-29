using System;
using System.Windows;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.Services;

/// <summary>Cross-flow Call 이동/복사 시 사용자에게 device system 처리 모드를 묻는 컨텍스트.</summary>
/// <param name="CallCount">옮길/복사할 Call 개수</param>
/// <param name="IsCut">cut(이동) 인지 (false 면 copy)</param>
/// <param name="Store">F# helper 호출용 (rename 충돌 사전 검사 등)</param>
/// <param name="SourceCallIds">source Call ID 목록</param>
/// <param name="TargetWorkId">target Work ID (target Flow 식별용)</param>
public record CrossFlowDeviceModePromptContext(
    int CallCount,
    bool IsCut,
    DsStore Store,
    Guid[] SourceCallIds,
    Guid TargetWorkId);

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
    /// 시뮬레이션/모니터링 중 편집 차단 경고 + "시뮬레이션 종료"/"모니터링 종료" 옵션 다이얼로그.
    /// </summary>
    /// <param name="message">경고 메시지</param>
    /// <param name="isMonitoring">실 PLC 모니터링 세션이면 true — 제목/버튼을 "모니터링" 문구로 표시</param>
    /// <returns>사용자가 종료를 선택했으면 true, "취소" 등 그 외 false</returns>
    bool WarnSimulationEditBlocked(string message, bool isMonitoring);

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

    /// <summary>
    /// Cross-flow Call 이동/복사 시 device system 처리 모드를 묻는 다이얼로그.
    /// CloneSystem(복사) / RenameSourceSystem(prefix 교체) / KeepReferences(참조 유지) 중 선택.
    /// RenameSourceSystem 은 충돌(공유 device or name taken)이 있으면 라디오 비활성.
    /// </summary>
    /// <returns>사용자가 선택한 mode, 취소 시 null</returns>
    CrossFlowDeviceMode? PromptCrossFlowDeviceMode(CrossFlowDeviceModePromptContext context);
}
