using System;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    /// <summary>엔진의 자동 원위치 페이즈 완료 — Run 으로 넘어간다. STEP 으로 시작한 경우엔 Pause + STEP 재진입.</summary>
    private void OnHomingPhaseCompleted(object? sender, EventArgs e)
    {
        if (_simEngine is not null)
            _simEngine.HomingPhaseCompleted -= OnHomingPhaseCompleted;
        _dispatcher.BeginInvoke(() =>
        {
            IsHomingPhase = false;
            SimStatusText = SimText.Running;
            _setStatusText(SimText.Started);
            AddSimLog("시뮬레이션 자동 원위치 완료", LogSeverity.System);

            // STEP 으로 시뮬을 시작한 경우: Homing 완료 후 자동 PauseSimulation + STEP 재진입.
            if (_pendingFirstStepAfterStart)
            {
                _pendingFirstStepAfterStart = false;
                PauseSimulation();
                _ = StepSimulationAsync();
            }
        });
    }
}
