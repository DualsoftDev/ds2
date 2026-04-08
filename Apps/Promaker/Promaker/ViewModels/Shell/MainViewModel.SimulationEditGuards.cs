namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private const string SimulationEditBlockedMessage =
        "시뮬레이션 중에는 화살표 연결 수정만 가능합니다.\n현재 변경은 적용되지 않습니다.";

    private bool GuardSimulationSemanticEdit(string editName)
    {
        if (!Simulation.IsSimulating)
            return true;

        _dialogService.ShowWarning($"{SimulationEditBlockedMessage}\n\n대상: {editName}");
        StatusText = $"시뮬레이션 중 '{editName}' 변경이 차단되었습니다.";
        return false;
    }
}
