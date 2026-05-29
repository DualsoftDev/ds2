namespace Promaker.ViewModels;

using Ds2.Core;

public partial class MainViewModel
{
    private const string SimulationEditBlockedMessage =
        "시뮬레이션 중에는 화살표 연결 수정만 가능합니다.\n\n계속하려면 시뮬레이션을 종료해야 합니다.";

    private const string MonitoringEditBlockedMessage =
        "모니터링 중에는 화살표 연결 수정만 가능합니다.\n\n계속하려면 모니터링을 종료해야 합니다.";

    /// <summary>현재 편집 차단 상황이 (가상) 시뮬레이션이 아니라 실 PLC 모니터링 세션인지.
    /// 다이얼로그 제목/문구/버튼 라벨("모니터링 종료" vs "시뮬레이션 종료") 결정에 사용.</summary>
    private bool IsMonitoringEditSession =>
        Simulation.SelectedRuntimeMode == RuntimeMode.Monitoring;

    /// <summary>
    /// PLAY 상태 (시뮬/모니터링 진행 중) 에서만 화살표 변경 차단. 그 외 (정지 / Pause) 시 모든 모드에서
    /// 변경 허용. 진행 중 사용자가 변경 시도하면 경고창 띄워 종료 선택 시 진행.
    /// </summary>
    internal bool GuardArrowEditByRuntimeMode(string editName)
    {
        if (!Simulation.IsSimulating)
            return true;

        var monitoring = IsMonitoringEditSession;
        var activity = monitoring ? "모니터링" : "시뮬레이션";
        var fullMessage = monitoring
            ? $"모니터링 중에는 화살표 변경이 불가합니다.\n\n대상: {editName}\n\n계속하려면 모니터링을 종료해야 합니다."
            : $"시뮬레이션 진행 중에는 화살표 변경이 불가합니다.\n\n대상: {editName}\n\n계속하려면 시뮬레이션을 종료해야 합니다.";
        var proceed = TryStopSimulationViaWarning(fullMessage);

        StatusText = proceed
            ? $"{activity} 종료 → '{editName}' 변경 진행"
            : $"{activity} 중 '{editName}' 변경이 차단되었습니다.";
        return proceed;
    }

    private bool GuardSimulationSemanticEdit(string editName)
    {
        if (!Simulation.IsSimulating)
            return true;

        var monitoring = IsMonitoringEditSession;
        var activity = monitoring ? "모니터링" : "시뮬레이션";
        var baseMessage = monitoring ? MonitoringEditBlockedMessage : SimulationEditBlockedMessage;
        var fullMessage = $"{baseMessage}\n\n대상: {editName}";
        var proceedAfterStop = TryStopSimulationViaWarning(fullMessage);

        StatusText = proceedAfterStop
            ? $"{activity} 종료 → '{editName}' 변경 진행"
            : $"{activity} 중 '{editName}' 변경이 차단되었습니다.";

        return proceedAfterStop;
    }

    /// <summary>
    /// 시뮬레이션/모니터링 중 편집 차단 경고를 표시하고, 사용자가 "시뮬레이션 종료"/"모니터링 종료"를 선택하면
    /// 시뮬레이션(모니터링)을 정지한 뒤 true를 반환합니다. "취소" 등 그 외에는 false.
    /// </summary>
    internal bool TryStopSimulationViaWarning(string message)
    {
        if (!Simulation.IsSimulating)
            return true;

        var stopChosen = _dialogService.WarnSimulationEditBlocked(message, IsMonitoringEditSession);
        if (!stopChosen)
            return false;

        Simulation.StopSimulationCommand.Execute(null);
        // Stop이 실패해 IsSimulating이 여전히 true인 경우 방어
        return !Simulation.IsSimulating;
    }
}
