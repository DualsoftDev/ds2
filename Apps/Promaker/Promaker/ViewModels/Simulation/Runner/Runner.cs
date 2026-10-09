using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
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
        ContinuousInjection.ClearCycle();

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
        // Pause 는 곧 단계 제어(STEP) 모드 진입.
        _isStepMode = true;
        SimStatusText = SimText.StepMode;
        ApplySimulationUiState(
            isSimPaused: true,
            statusText: SimText.Paused,
            logText: "단계 제어 모드 진입");
        RefreshSimulationProgressUi();
    }

    private bool CanPauseSimulation() =>
        SimulationCommandFacade.IsAccepted(DecidePause());

    private SimulationCommandFacade.Decision DecidePause() =>
        SimulationCommandFacade.DecidePause(
            IsSimulating, IsSimPaused, IsHomingPhase, RuntimeMode.Simulation, false);

    [RelayCommand(CanExecute = nameof(CanStopSimulation))]
    private void StopSimulation()
    {
        AdvanceSimUiGeneration();
        if (_simEngine is not null
            && !TryWithSimEngine("Simulation stop", engine => engine.Stop()))
            return;
        if (_simEngine is not null)
            _simEngine.HomingPhaseCompleted -= OnHomingPhaseCompleted;
        IsHomingPhase = false;
        ClearSimStateFromCanvas();
        ClearAllWarnings();
        ContinuousInjection.ClearCycle();
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
        try
        {
            // 활성 traversal 들을 finalize → KPI 집계가 모든 토큰을 본다.
            // (분기 도중 stuck 된 branch 까지 포함; 완주 branch 가 있으면 그 max 시각으로 기록.)
            TokenTraversal.FinalizePending();
            Report.TryCaptureScenario($"Run_{DateTime.Now:yyyyMMdd_HHmmss}");
        }
        catch { /* best-effort */ }

        // 토큰 traversal 누적 초기화 — 다음 Run 이 이전 완주 카운트/이력 위에 누적되지 않도록.
        // (Capture 가 _completedTraversals 를 사용하므로 반드시 capture 이후에 reset.)
        TokenTraversal.Reset();
    }

    private bool CanStopSimulation() =>
        SimulationCommandFacade.IsAccepted(SimulationCommandFacade.DecideStop(IsSimulating));

    private void InitSceneEventHandler()
    {
        // 3D 배치 뷰 슬롯만 새 인스턴스로 교체 — 그래픽 정보뷰 등 다른 구독은 유지한다.
        _sceneEventHandler.ReplaceSingleton(new DeviceSceneEventHandler(ThreeD));
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
        SimulationCommandFacade.IsAccepted(SimulationCommandFacade.DecideReset(IsSimulating));

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
        Report.Clear();
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
        ContinuousInjection.ClearCycle();
        ClearSimStateFromCanvas();

        if (clearCollections)
        {
            SimNodes.Clear();
            _simNodeByGuid.Clear();
            SimWorkItems.Clear();
            TokenSourceWorks.Clear();
            SelectedTokenSource = null;
            return;
        }

        foreach (var row in SimNodes)
        {
            row.State = Status4.Ready;
            row.TokenDisplay = "";
        }
    }
}
