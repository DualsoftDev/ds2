namespace Promaker.ViewModels;

using Ds2.Core;

public partial class MainViewModel
{
    private const string SimulationEditBlockedMessage =
        "시뮬레이션 중에는 화살표 연결 수정만 가능합니다.\n\n계속하려면 시뮬레이션을 종료해야 합니다.";

    /// <summary>
    /// Control / VirtualPlant / Monitoring 모드에서는 외부 PLC / 시뮬레이터와 동작 중이라
    /// 화살표 연결/타입/방향/삭제 변경을 허용하면 시뮬레이션 일관성이 깨질 수 있음.
    /// Simulation 모드에서만 화살표 변경 허용.
    /// </summary>
    internal bool GuardArrowEditByRuntimeMode(string editName)
    {
        if (Simulation.SelectedRuntimeMode == RuntimeMode.Simulation)
            return true;
        StatusText = $"[차단] {Simulation.SelectedRuntimeMode} 모드에서는 화살표 변경 불가 ({editName}) — Simulation 모드에서만 가능";
        return false;
    }

    private bool GuardSimulationSemanticEdit(string editName)
    {
        if (!Simulation.IsSimulating)
            return true;

        var fullMessage = $"{SimulationEditBlockedMessage}\n\n대상: {editName}";
        var proceedAfterStop = TryStopSimulationViaWarning(fullMessage);

        StatusText = proceedAfterStop
            ? $"시뮬레이션 종료 → '{editName}' 변경 진행"
            : $"시뮬레이션 중 '{editName}' 변경이 차단되었습니다.";

        return proceedAfterStop;
    }

    /// <summary>
    /// 시뮬레이션 중 편집 차단 경고를 표시하고, 사용자가 "시뮬레이션 종료"를 선택하면
    /// 시뮬레이션을 정지한 뒤 true를 반환합니다. 그 외에는 false.
    /// </summary>
    internal bool TryStopSimulationViaWarning(string message)
    {
        if (!Simulation.IsSimulating)
            return true;

        var stopChosen = _dialogService.WarnSimulationEditBlocked(message);
        if (!stopChosen)
            return false;

        Simulation.StopSimulationCommand.Execute(null);
        // Stop이 실패해 IsSimulating이 여전히 true인 경우 방어
        return !Simulation.IsSimulating;
    }
}
