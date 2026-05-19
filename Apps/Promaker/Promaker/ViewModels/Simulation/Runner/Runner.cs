using System;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Engine.Core;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    private bool TryWithSimEngine(string operationName, Action<ISimulationEngine> action)
    {
        if (_simEngine is null)
            return false;

        try
        {
            action(_simEngine);
            return true;
        }
        catch (Exception ex)
        {
            SimLog.Error($"{operationName} failed", ex);
            _setStatusText(SimText.SimulationError(ex.Message));
            return false;
        }
    }

    private bool TryDisposeCurrentEngine(string operationName)
    {
        if (_simEngine is null)
            return true;

        AdvanceSimUiGeneration();
        var engine = _simEngine;
        _simEngine = null;
        _runtimeSession = null;
        _passiveInference = null;
        ClearContinuousSourceCycle();

        try
        {
            engine.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            SimLog.Error($"{operationName} failed", ex);
            _setStatusText(SimText.SimulationError(ex.Message));
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPauseSimulation))]
    private void PauseSimulation()
    {
        // Pause = 시간 정지:
        // 1) SetAllFlowStates(Pause) — work transition 막기 + SyncCurrentTime 으로 sim 시계 elapsed 한 번 반영
        // 2) engine.Pause() — status=Paused, simulation thread 종료 → RuntimeClock 멈춤
        // 3) GanttChart.IsRunning=false — 간트차트의 real-time AdjustedNow 도 정지 (안 그러면 sim 시계는
        //    멈췄어도 간트차트 자체 시계가 real-time 따라 흘러서 시각적으로 시간 흐르는 듯이 보임)
        _simEngine?.SetAllFlowStates(FlowTag.Pause);
        _simEngine?.Pause();
        GanttChart.IsRunning = false;
        // STEP 모드는 Simulation 모드 전용. Control/VP/Monitoring 은 단순 일시 정지.
        var isStepEligible = SelectedRuntimeMode == RuntimeMode.Simulation;
        _isStepMode = isStepEligible;
        SimStatusText = isStepEligible ? SimText.StepMode : SimText.Paused;
        ApplySimulationUiState(
            isSimPaused: true,
            statusText: SimText.Paused,
            logText: isStepEligible ? "단계 제어 모드 진입" : "시뮬레이션 일시 정지");
        RefreshSimulationProgressUi();
    }

    // VP/Monitoring 은 외부 Hub 신호로 진행되어 일시정지 자체가 의미 없음 → 버튼 비활성.
    // Control + 실 PLC 연결 시에도 Pause 비활성 — Pause 는 엔진만 freeze 하고
    // 이미 송출된 OUT 코일은 그대로 유지되므로 PLC 측 액추에이터 모션이 멈추지 않음.
    // 사용자가 "Pause = 라인 멈춤" 으로 오해하는 안전 위험 차단. 실 라인 정지는 STOP 사용
    // (BroadcastClearOwnOutputsAsync 로 모든 OUT 을 false 송출 → 솔레노이드 OFF).
    private bool CanPauseSimulation() =>
        RuntimeCommandPolicy.canPauseSimulation(
            IsSimulating,
            IsSimPaused,
            IsHomingPhase,
            SelectedRuntimeMode,
            IsRealPlcConnected);

    [RelayCommand(CanExecute = nameof(CanStopSimulation))]
    private void StopSimulation()
    {
        AdvanceSimUiGeneration();
        // homing-only 세션 도중 사용자가 STOP 으로 빠져나오는 경우에도 플래그 리셋.
        _homingOnlyMode = false;
        if (_simEngine is not null
            && !TryWithSimEngine("Simulation stop", engine => engine.Stop()))
            return;
        if (_simEngine is not null)
            _simEngine.HomingPhaseCompleted -= OnHomingPhaseCompleted;
        IsHomingPhase = false;
        StopHub();
        ClearSimStateFromCanvas();
        ClearAllWarnings();
        ClearContinuousSourceCycle();
        HasWorkGoing = false;
        HasGoingCall = false;
        _isStepMode = false;
        _stepPrimingDone = false;

        SimStatusText = SimText.Stopped;
        _sceneEventHandler?.Reset();
        ApplySimulationUiState(
            ganttRunning: false,
            isSimulating: false,
            isSimPaused: false,
            statusText: SimText.Stopped,
            logText: SimText.Stopped);

        // 시뮬 종료 시 결과 시나리오 자동 박제 (TechnicalData.SimulationResults).
        // CapturedRuns 에 누적되어 "시뮬레이션 결과 보기" 다이얼로그에 표시된다.
        // 자동 박제는 Simulation 모드 한정 — VP/Control 은 외부 신호 기반이라 의도된 "Run" 경계가 없고
        // scenario 객체가 무거워(_stateChangeRecords 전체 + KPI + traversals) 누적 시 메모리 폭증.
        try
        {
            // 활성 traversal 들을 finalize → KPI 집계가 모든 토큰을 본다.
            // (분기 도중 stuck 된 branch 까지 포함; 완주 branch 가 있으면 그 max 시각으로 기록.)
            FinalizePendingTraversals();
            if (SelectedRuntimeMode == RuntimeMode.Simulation)
                TryCaptureScenario($"Run_{DateTime.Now:yyyyMMdd_HHmmss}");
        }
        catch { /* best-effort */ }

        // 토큰 traversal 누적 초기화 — 다음 Run 이 이전 완주 카운트/이력 위에 누적되지 않도록.
        // (Capture 가 _completedTraversals 를 사용하므로 반드시 capture 이후에 reset.)
        ResetTraversalTracking();

        // 트레이 모드였다면 윈도우 복원 + 아이콘 제거. 평소 모드면 no-op.
        FireTrayRestore();
    }

    private bool CanStopSimulation() =>
        RuntimeCommandPolicy.canStopSimulation(IsSimulating);

    private void InitSceneEventHandler()
    {
        _sceneEventHandler = new DeviceSceneEventHandler(ThreeD);
    }

    [RelayCommand(CanExecute = nameof(CanResetSimulation))]
    private void ResetSimulation()
    {
        AdvanceSimUiGeneration();
        if (_simEngine is not null
            && !TryWithSimEngine("Simulation reset", engine => engine.Reset()))
            return;
        _simStartTime = DateTime.Now;
        ApplySimulationResetUiState(clearCollections: false);
        GanttChart.Reset(_simStartTime);
        InitGanttEntries();
        HasWorkGoing = false;
        HasGoingCall = false;
        _isStepMode = false;
        _stepPrimingDone = false;
        SimStatusText = SimText.Reset;
        ApplySimulationUiState(
            statusText: SimText.Reset,
            logText: SimText.ResetLog);
    }

    private bool CanResetSimulation() =>
        RuntimeCommandPolicy.canResetSimulation(IsSimulating);

    private void DisposeSimEngine()
    {
        TryDisposeCurrentEngine("Simulation dispose");
        ClearSimStateFromCanvas();
        IsSimulating = false;
        IsSimPaused = false;
        _stateCache.Clear();
    }

    private void ApplySimulationResetUiState(bool clearCollections)
    {
        GanttChart.IsRunning = false;
        _stateChangeRecords.Clear();
        HasReportData = false;
        SimClock = SimText.ClockZero;
        SelectedSimWork = null;
        IsSimulating = false;
        IsSimPaused = false;
        _isStepMode = false;
        SimSpeed = 1.0;
        SimTimeIgnore = false;
        SimStatusText = SimText.Stopped;
        _stateCache.Clear();
        _suppressedWarnings.Clear();
        ClearContinuousSourceCycle();
        ClearSimStateFromCanvas();

        if (clearCollections)
        {
            SimNodes.Clear();
            SimEventLog.Clear();
            SimWorkItems.Clear();
            TokenSourceWorks.Clear();
            SelectedTokenSource = null;
            return;
        }

        SimEventLog.Clear();
        foreach (var row in SimNodes)
        {
            row.State = Status4.Ready;
            row.TokenDisplay = "";
        }
    }
}
