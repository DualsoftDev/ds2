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
        _runtimeSession = null;
        _passiveInference = null;
        ResetPassiveGanttClockAnchor();
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
        SimulationCommandFacade.IsAccepted(DecidePause());

    private SimulationCommandFacade.Decision DecidePause() =>
        SimulationCommandFacade.DecidePause(
            IsSimulating, IsSimPaused, IsHomingPhase, SelectedRuntimeMode, IsRealPlcConnected);

    [RelayCommand(CanExecute = nameof(CanStopSimulation))]
    private void StopSimulation()
    {
        AdvanceSimUiGeneration();
        // homing-only 세션 도중 사용자가 STOP 으로 빠져나오는 경우에도 플래그 리셋.
        _homingOnlyMode = false;
        // Agent 위임(Monitoring+실PLC)에선 _simEngine 이 원격 proxy 다. proxy.Stop() 은 RuntimeStop 을
        // Agent 로 보내 sticky monitoring 을 깨뜨리므로 호출하지 않는다 — "정지" 는 아래 Hub.Stop() 으로
        // Promaker 의 Hub 연결/화면만 정리하고 active.flag 는 유지되어 Agent 는 계속 모니터링한다.
        if (!IsAgentDelegationMode
            && _simEngine is not null
            && !TryWithSimEngine("Simulation stop", engine => engine.Stop()))
            return;
        if (_simEngine is not null)
            _simEngine.HomingPhaseCompleted -= OnHomingPhaseCompleted;
        IsHomingPhase = false;
        Hub.Stop();
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
        // 자동 박제는 Simulation 모드 한정 — VP/Control 은 외부 신호 기반이라 의도된 "Run" 경계가 없고
        // scenario 객체가 무거워(_stateChangeRecords 전체 + KPI + traversals) 누적 시 메모리 폭증.
        try
        {
            // 활성 traversal 들을 finalize → KPI 집계가 모든 토큰을 본다.
            // (분기 도중 stuck 된 branch 까지 포함; 완주 branch 가 있으면 그 max 시각으로 기록.)
            TokenTraversal.FinalizePending();
            if (SelectedRuntimeMode == RuntimeMode.Simulation)
                Report.TryCaptureScenario($"Run_{DateTime.Now:yyyyMMdd_HHmmss}");
        }
        catch { /* best-effort */ }

        // 토큰 traversal 누적 초기화 — 다음 Run 이 이전 완주 카운트/이력 위에 누적되지 않도록.
        // (Capture 가 _completedTraversals 를 사용하므로 반드시 capture 이후에 reset.)
        TokenTraversal.Reset();

        // 자동 줄자: 모니터링으로 학습된 device duration 이 있으면 정지 시 모델 반영 여부를 묻는다.
        TryApplyLearnedDurationsOnStop();
    }

    private bool CanStopSimulation() =>
        SimulationCommandFacade.IsAccepted(SimulationCommandFacade.DecideStop(IsSimulating));

    /// <summary>Agent 가 push 한 학습 duration 누적(UI 스레드). 정지 시 일괄 반영 대상.</summary>
    private void OnLearnedDurationReceived(Ds2.Backend.Common.LearnedDurationPayload p)
    {
        if (Guid.TryParse(p.WorkId, out var workGuid))
            _learnedDurations[workGuid] = (p.AvgMs, p.MinMs, p.MaxMs);
    }

    /// <summary>로컬 실측 duration 학습기 생성 — Simulation 모드는 plan 자체가 실행이라 학습 의미가 없어 제외.
    /// call(원본·참조 모두) → device Work(RxGuid) 매핑을 PLAY 시점 모델에서 빌드.</summary>
    private void InitDurationLearning()
    {
        if (SelectedRuntimeMode == RuntimeMode.Simulation)
        {
            _durationLearning = null;
            _healthBaseline = null;
            return;
        }

        var store = _storeProvider();
        var map = new System.Collections.Generic.Dictionary<Guid, Guid[]>();
        var activeWorks = new System.Collections.Generic.HashSet<Guid>();
        foreach (var call in store.Calls.Values)
        {
            var rxWorks = Queries.callRxWorkGuids(call.Id, store);
            if (rxWorks.Length > 0)
                map[call.Id] = System.Linq.Enumerable.ToArray(rxWorks);
            activeWorks.Add(call.ParentId);   // Call 을 가진 Work = Active Work, 자체 실측 대상
        }
        _durationLearning = new CallDurationLearning(map, activeWorks);

        // 건강 기준선 추적 — 학습기의 정상 샘플 스트림에 업혀 work 단위로 동결/드리프트를 본다.
        var workMaxMs = new System.Collections.Generic.Dictionary<Guid, double>();
        var workNames = new System.Collections.Generic.Dictionary<Guid, string>();
        foreach (var w in store.Works.Values)
        {
            workNames[w.Id] = w.Name;
            if (w.MaxDuration is { } maxOpt)   // F# option — None 은 null
                workMaxMs[w.Id] = maxOpt.Value.TotalMilliseconds;
        }
        _healthBaseline = new HealthBaselineTracker(workMaxMs, workNames);
        _durationLearning.SampleRecorded += OnHealthBaselineSample;
    }

    /// <summary>학습 샘플 1건 → 건강 기준선 추적 + 전이(동결/IQR 경보)만 로그로 승격.
    /// 드리프트 % 자체는 사이클마다 찍지 않는다 — 정지 시 요약과 경보가 사용자 접점.</summary>
    private void OnHealthBaselineSample(Guid workGuid, double spanMs)
    {
        if (_healthBaseline is not { } health) return;
        var r = health.OnSample(workGuid, spanMs, DateTime.Now);

        if (r.JustFrozen is { } frozen)
        {
            var how = r.FrozenByCap ? "상한 도달(수렴 미달) 동결" : "수렴 자동 동결";
            SimLog.Info($"[Health] {health.NameOf(workGuid)} 기준선 {how} — 중앙값 {frozen.MedianMs:F0}ms, IQR {frozen.IqrMs:F0}ms, 표본 {frozen.SampleCount}");
            AddSimLog($"[건강 기준선] {health.NameOf(workGuid)} 동결 — 중앙값 {frozen.MedianMs:F0}ms ({how}). 이후 드리프트를 추적합니다.", LogSeverity.System);
        }
        if (r.IqrAlarmRaised)
        {
            SimLog.Warn($"[Health] {health.NameOf(workGuid)} IQR 확대 경보 — 드리프트 {r.DriftPct:+0.0;-0.0}%");
            AddSimLog($"[건강 경보] {health.NameOf(workGuid)} 동작 변동 폭(IQR)이 기준선의 {HealthBaselineTracker.IqrAlarmRatio:F1}배를 넘었습니다 — 노화/이상 조기 신호일 수 있습니다.", LogSeverity.Warn);
        }
        else if (r.IqrAlarmCleared)
        {
            SimLog.Info($"[Health] {health.NameOf(workGuid)} IQR 경보 해제");
            AddSimLog($"[건강 경보 해제] {health.NameOf(workGuid)} 동작 변동 폭이 정상 범위로 돌아왔습니다.", LogSeverity.System);
        }
    }

    // 리본 "기준선 동결" 버튼은 제거됨(사용자 결정) — 동결의 본선은 자동 수렴이고,
    // 수동 동결은 DSPilot 설정 페이지 → hub FreezeHealthBaseline 브로드캐스트 경로만 남긴다.

    /// <summary>수동 "기준선 지금 동결" — 로컬 추적기 동결 + 로그. 허브 브로드캐스트(OnHealthBaselineFreeze) 수신용.</summary>
    internal void FreezeHealthBaselineNow(string origin)
    {
        if (_healthBaseline is not { } health)
        {
            AddSimLog("[건강 기준선] 추적 중이 아닙니다 — 비-Simulation 모드 PLAY 중에만 동결할 수 있습니다.", LogSeverity.Warn);
            return;
        }
        var frozen = health.FreezeNow(DateTime.Now);
        if (frozen.Count == 0)
        {
            AddSimLog($"[건강 기준선] 동결할 항목이 없습니다 — 이미 동결됐거나 표본이 {HealthBaselineTracker.MinManualFreezeSamples}사이클 미만입니다. ({origin})", LogSeverity.Info);
            return;
        }
        foreach (var (workId, b) in frozen)
            SimLog.Info($"[Health] {health.NameOf(workId)} 기준선 수동 동결({origin}) — 중앙값 {b.MedianMs:F0}ms, IQR {b.IqrMs:F0}ms, 표본 {b.SampleCount}");
        AddSimLog($"[건강 기준선] {frozen.Count}개 device 기준선을 수동 동결했습니다 ({origin}). 이후 드리프트를 추적합니다.", LogSeverity.System);
    }

    /// <summary>학습값 자동 반영 전 확인이 필요한가 — 정상 설비 가정(사용자 합의) 하에 조용히
    /// 자동 적용하되, 어떤 항목의 학습 범위가 비정상적으로 넓으면(상한이 중앙값의 2배 초과
    /// 또는 하한이 절반 미만) 워밍업 불안정/비정상 사이클 혼입 가능성이라 사용자에게 묻는다.</summary>
    internal static bool ShouldConfirmLearnedDurations(
        System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<Guid, (int avg, int min, int max)>> snapshot)
    {
        foreach (var kv in snapshot)
        {
            var (avg, min, max) = kv.Value;
            if (avg <= 0) return true;
            if (max > avg * 2 || min < avg / 2) return true;
        }
        return false;
    }

    // ※ 라이브 반영(동작 중 store 갱신 + engine.ReloadDurations)은 제거됨 — 2회 실측(11:34, 12:19)에서
    //   [Learn] 라이브 반영 직후 SensorShort 무더기 → 사이클 체인 단절 = 무개입 라인 정지를 유발.
    //   ratchet(경계 완화만)으로도 재발 → 경계 값이 아니라 동작 중 ReloadDurations 호출 자체가
    //   엔진 전이와 race 하는 것으로 추정. 동작 중 Reload 안전성이 규명되기 전까지 반영은 정지 시에만.

    /// <summary>정지 시 학습 duration 을 모델 Work 에 반영 + dirty.
    /// 소스 = Agent push(_learnedDurations) + 로컬 실측 학습(_durationLearning, Control/VP/Monitoring 공통).
    /// 둘 다 있으면 로컬 실측이 우선(최신 윈도우 기반). 학습값이 없으면 조용히 통과.
    /// 정상 범위면 묻지 않고 자동 적용(정상 설비 가정) — 학습값이 비정상적으로 흔들릴 때만 확인.
    /// 저장은 기존 Save 흐름이 AASX 로 영속.</summary>
    private void TryApplyLearnedDurationsOnStop()
    {
        // 건강 기준선 — 정지 시 work 별 드리프트/외삽 요약을 한 번 박고 세션 추적 종료.
        if (_healthBaseline is { HasFrozenBaseline: true } health)
        {
            foreach (var line in health.SummaryLines())
            {
                SimLog.Info($"[Health] {line}");
                AddSimLog($"[건강 요약] {line}", LogSeverity.System);
            }
        }
        _healthBaseline = null;

        if (_durationLearning is { HasSamples: true } learning)
        {
            foreach (var kv in learning.Snapshot())
                _learnedDurations[kv.Key] = kv.Value;
        }
        _durationLearning = null;

        if (_learnedDurations.Count == 0) return;
        var snapshot = System.Linq.Enumerable.ToArray(_learnedDurations);
        _learnedDurations.Clear();

        if (ShouldConfirmLearnedDurations(snapshot))
        {
            var ok = Promaker.Dialogs.DialogHelpers.Confirm(
                System.Windows.Application.Current?.MainWindow,
                $"학습된 device duration {snapshot.Length}건의 변동 폭이 비정상적으로 큽니다.\n(워밍업 불안정 또는 비정상 사이클 혼입 가능)\n그래도 모델에 반영할까요?",
                "학습 duration 확인");
            if (!ok) return;
        }

        var store = _storeProvider();
        var applied = 0;
        foreach (var kv in snapshot)
        {
            if (store.Works.TryGetValue(kv.Key, out var w))
            {
                var (avg, min, max) = kv.Value;
                w.Duration    = Microsoft.FSharp.Core.FSharpOption<TimeSpan>.Some(TimeSpan.FromMilliseconds(avg));
                w.MinDuration = Microsoft.FSharp.Core.FSharpOption<TimeSpan>.Some(TimeSpan.FromMilliseconds(min));
                w.MaxDuration = Microsoft.FSharp.Core.FSharpOption<TimeSpan>.Some(TimeSpan.FromMilliseconds(max));
                applied++;
            }
        }
        if (applied > 0)
        {
            MarkDirty?.Invoke();
            AddSimLog($"학습 duration {applied}건 자동 반영 — 저장하면 파일에 기록됩니다.", LogSeverity.System);
        }
    }

    private void InitSceneEventHandler()
    {
        _sceneEventHandler = new DeviceSceneEventHandler(ThreeD);
    }

    [RelayCommand(CanExecute = nameof(CanResetSimulation))]
    private void ResetSimulation()
    {
        AdvanceSimUiGeneration();
        // Agent 위임(Monitoring+실PLC proxy)에선 proxy.Reset() 이 RuntimeReset 을 Agent 로 보내
        // 단일 호스팅 engine 을 리셋해버린다 — Promaker 로컬 Reset 은 Agent 를 건드리지 않는다.
        if (!IsAgentDelegationMode
            && _simEngine is not null
            && !TryWithSimEngine("Simulation reset", engine => engine.Reset()))
            return;
        _simStartTime = DateTime.Now;
        ResetPassiveGanttClockAnchor();
        _durationLearning = null;   // 리셋 = 학습 폐기 (정지 시 반영 흐름을 안 탔으므로)
        _healthBaseline = null;
        ResetCommBlackout();
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
