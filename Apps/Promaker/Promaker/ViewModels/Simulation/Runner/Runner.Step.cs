using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Engine.Core;
using Microsoft.FSharp.Core;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    private bool _pendingFirstStepAfterStart;
    private bool _stepPrimingDone;

    [RelayCommand(CanExecute = nameof(CanStepSimulation))]
    private async Task StepSimulationAsync()
    {
        // 시뮬 시작 안 한 상태면 STEP 으로 Start.
        if (!IsSimulating)
        {
            // Homing 페이즈가 있는 모델이면 OnHomingPhaseCompleted 가 끝난 후 자동으로
            // PauseSimulation + STEP 재호출 하도록 flag set. (polling/Sleep 안 씀)
            _pendingFirstStepAfterStart = true;
            StartSimulation();
            if (_simEngine is null)
            {
                _pendingFirstStepAfterStart = false;
                return;
            }
            if (IsHomingPhase)
                return;  // OnHomingPhaseCompleted 가 이어서 처리

            _pendingFirstStepAfterStart = false;
            PauseSimulation();
        }

        if (_simEngine is null) return;
        var engine = _simEngine;
        SimStatusText = SimText.StepMode;

        var (selectedSourceGuid, autoStartSources) = GetStepAdvanceSelection();

        // Source priming — *첫 STEP* 에만 호출. 이후 STEP 에선 자연 cascade/cycle 따라감.
        // 매 STEP 호출 시 NewFlow.NewWork 가 cycle 끝나서 Ready 로 돌아오는 시점마다 다시
        // ForceWorkState(Going) 호출되면 NewFlow1 안 끝났는데 NewFlow 가 또 시작하는 race 발생.
        if (!_stepPrimingDone)
        {
            var action = StepPrimingPlan.decide(
                FuncConvert.FromFunc<Guid, bool>(g => SimIndexModule.isTokenSource(engine.Index, g)),
                FuncConvert.FromFunc<Guid, bool>(g => engine.GetWorkToken(g) is not null),
                FuncConvert.FromFunc<Guid, FSharpOption<Status4>>(g => engine.GetWorkState(g)),
                selectedSourceGuid,
                autoStartSources);

            ApplyStepPrimingAction(engine, action);
            _stepPrimingDone = true;
        }

        Guid[] batch;

        try
        {
            batch = engine.BeginStepBatch(selectedSourceGuid, autoStartSources);
        }
        catch (Exception ex)
        {
            SimLog.Error("Simulation step failed", ex);
            _setStatusText(SimText.SimulationError(ex.Message));
            return;
        }

        var progressed = false;

        // batch 의 unit 모두 finish 까지 단계적 advance + wait.
        // 각 nextEventTime 까지 real-time wait 후 그 시점에 AdvanceSimulationTo 호출 →
        // 이벤트들이 그 시점에 발화 → GanttChart 가 시간선 따라 표시 (점프 X).
        GanttChart.IsRunning = true;
        try
        {
            var guard = 0;
            while (engine.IsStepBatchActive(batch) && guard < 256)
            {
                guard++;
                var nextEventTime = engine.NextEventTimeMs;
                if (nextEventTime is null) break;

                var simDelta = nextEventTime.Value - engine.CurrentTimeMs;
                if (simDelta > 0)
                {
                    var speed = Math.Max(0.000001, engine.SpeedMultiplier);
                    var realWaitMs = (int)Math.Ceiling(simDelta / speed);
                    if (realWaitMs > 0)
                        await Task.Delay(realWaitMs);
                }
                engine.AdvanceSimulationTo(nextEventTime.Value);
                progressed = true;
            }
        }
        catch (Exception ex)
        {
            SimLog.Error("Simulation step advance failed", ex);
            _setStatusText(SimText.SimulationError(ex.Message));
        }
        finally
        {
            engine.EndStep();
            GanttChart.IsRunning = false;
            GanttChart.CurrentTime = ToGanttTimestamp(engine.State.Clock);
        }

        AddSimLog(progressed ? "STEP 실행" : "STEP 진행 없음", LogSeverity.System);
        RefreshSimulationProgressUi();
        SimStatusText = SimText.Paused;
    }

    // STEP 은 Simulation 모드 전용. Control 은 외부 Hub 신호로 진행되어 단계적 advance 의미 없음,
    // VP/Monitoring 도 외부 신호 owner 라 STEP 부적절.
    private bool CanStepSimulation() =>
        RuntimeCommandPolicy.canStepSimulation(
            IsSimulating,
            IsSimPaused,
            IsHomingPhase,
            SelectedRuntimeMode);

    private static void ApplyStepPrimingAction(ISimulationEngine engine, StepPrimingAction action)
    {
        switch (action)
        {
            case var _ when action.IsStartAllSourcesWithoutToken:
                foreach (var sg in engine.Index.TokenSourceGuids)
                    if (engine.GetWorkToken(sg) is null)
                        engine.StartSourceWork(sg);
                break;
            case StepPrimingAction.StartSelectedSource s:
                if (engine.GetWorkToken(s.Item) is null)
                    engine.StartSourceWork(s.Item);
                break;
            case StepPrimingAction.ForceSelectedReadyToGoing f:
                engine.ForceWorkState(f.Item, Status4.Going);
                break;
            // NoAction: no-op
        }
    }
}
