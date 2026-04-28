namespace Ds2.Runtime.Engine

open System
open System.Threading
open Ds2.Core
open Ds2.Core.Store
open Ds2.Runtime.Model
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Engine.Scheduler

/// 이벤트 기반 시뮬레이션 엔진
/// H 상태 구현: F->H (내부 Call R 정리) -> H->R (최소 1ms)
type EventDrivenEngine(index: SimIndex, runtimeMode: RuntimeMode, writeTag: (string -> string -> unit) option) =
    let ioMap = Ds2.Runtime.IO.SignalIOMap.build index.Store
    let writeTagFn = defaultArg writeTag (fun _ _ -> ())
    let mutable status = Stopped
    let mutable isHomingPhase = false
    let homingPhaseCompletedEvent = Event<EventArgs>()
    let scheduler = EventScheduler()
    let mutable speedMultiplier = 1.0
    let mutable timeIgnore = false
    let mutable engineThread: Thread option = None
    let mutable cts: CancellationTokenSource option = None
    let processGate = obj()
    let engineThreadJoinTimeoutMs = 1000
    let getStatus () = Volatile.Read(&status)
    let setStatus newStatus = Volatile.Write(&status, newStatus)
    let stateManager = StateManager(index, index.TickMs)
    let durationTracker = DurationTracker(scheduler)
    let workStateChangedEvent = Event<WorkStateChangedArgs>()
    let callStateChangedEvent = Event<CallStateChangedArgs>()
    let callTimeoutEvent = Event<CallTimeoutArgs>()
    let simulationStatusChangedEvent = Event<SimulationStatusChangedArgs>()
    let tokenEventEvent = Event<TokenEventArgs>()
    let scheduleNow eventType priority =
        scheduler.ScheduleNow(eventType, priority) |> ignore
    let scheduleNowStateChange eventType = scheduleNow eventType ScheduledEvent.PriorityStateChange
    let scheduleNowDurationCheck eventType = scheduleNow eventType ScheduledEvent.PriorityDurationCheck
    let scheduleConditionEvaluation () =
        scheduleNow ScheduledEventType.EvaluateConditions ScheduledEvent.PriorityConditionEval
    let canStartWork workGuid = WorkConditionChecker.canStartWork index (stateManager.GetState()) workGuid
    let canStartCall callGuid = WorkConditionChecker.canStartCall index (stateManager.GetState()) callGuid
    let canCompleteCall callGuid = WorkConditionChecker.canCompleteCall index (stateManager.GetState()) callGuid
    let shouldSkipCall callGuid = WorkConditionChecker.shouldSkipCall index (stateManager.GetState()) callGuid
    let isActiveSystemWork workGuid =
        index.WorkSystemName
        |> Map.tryFind workGuid
        |> Option.map index.ActiveSystemNames.Contains
        |> Option.defaultValue false
    let tokenFlowContext : TokenFlow.Context = {
        Index = index
        StateManager = stateManager
        CurrentTimeMs = (fun () -> scheduler.CurrentTimeMs)
        TriggerTokenEvent = tokenEventEvent.Trigger
    }
    let workNameOf guid = TokenFlow.workNameOf tokenFlowContext guid
    let canReceiveToken workGuid = TokenFlow.canReceiveToken tokenFlowContext workGuid
    let emitTokenEvent kind token workGuid (targetGuid: Guid option) =
        TokenFlow.emitTokenEvent tokenFlowContext kind token workGuid targetGuid
    let shiftToken workGuid token = TokenFlow.shiftToken tokenFlowContext workGuid token
    let onWorkFinish workGuid = TokenFlow.onWorkFinish tokenFlowContext workGuid
    let workTransitionContext : WorkTransitions.Context = {
        Index = index
        StateManager = stateManager
        Scheduler = scheduler
        RuntimeMode = runtimeMode
        IsHomingPhase = (fun () -> isHomingPhase)
        TimeIgnore = (fun () -> timeIgnore)
        ScheduleConditionEvaluation = scheduleConditionEvaluation
        OnWorkFinish = onWorkFinish
        TriggerWorkStateChanged = workStateChangedEvent.Trigger
        OnDurationScheduled = fun workGuid eventId scheduledTimeMs ->
            durationTracker.OnDurationScheduled(workGuid, eventId, scheduledTimeMs)
    }
    let scheduleWorkIfReady workGuid targetState =
        WorkTransitions.scheduleWorkIfReady workTransitionContext workGuid targetState
    let applyWorkTransition workGuid newState =
        WorkTransitions.applyWorkTransition workTransitionContext workGuid newState
    let resolveWorkName workGuid =
        index.WorkName |> Map.tryFind workGuid |> Option.defaultValue (string workGuid)
    let resolveCallName callGuid =
        Queries.getCall callGuid index.Store
        |> Option.map (fun c -> c.Name)
        |> Option.defaultValue (string callGuid)
    let currentClock () = TimeSpan.FromMilliseconds(float scheduler.CurrentTimeMs)
    let setWorkStateDirect workGuid newState =
        if index.AllWorkGuids |> List.contains workGuid then
            let oldState = stateManager.GetWorkState(workGuid)
            if oldState <> newState then
                stateManager.ForceWorkState(workGuid, newState)
                workStateChangedEvent.Trigger({
                    WorkGuid = workGuid
                    WorkName = resolveWorkName workGuid
                    PreviousState = oldState
                    NewState = newState
                    Clock = currentClock ()
                })
    let setCallStateDirect callGuid newState =
        if index.AllCallGuids |> List.contains callGuid then
            let oldState = stateManager.GetCallState(callGuid)
            if oldState <> newState then
                stateManager.ForceCallState(callGuid, newState)
                callStateChangedEvent.Trigger({
                    CallGuid = callGuid
                    CallName = resolveCallName callGuid
                    PreviousState = oldState
                    NewState = newState
                    IsSkipped = false
                    Clock = currentClock ()
                })
    let forceWorkState workGuid newState =
        if index.AllWorkGuids |> List.contains workGuid then
            scheduleNowStateChange (ScheduledEventType.ForcedWorkTransition(workGuid, newState))
    let forceCallState callGuid newState =
        if index.AllCallGuids |> List.contains callGuid then
            scheduleNowStateChange (ScheduledEventType.ForcedCallTransition(callGuid, newState))
    let apiCallExecutionContext : ApiCallExecutionContext = {
        RuntimeMode = runtimeMode
        GetDeviceState = stateManager.GetWorkState
        GetDeviceName = resolveWorkName
        GetTxOutAddresses = fun deviceWorkGuid ->
            ioMap.TxWorkToOutAddresses |> Map.tryFind deviceWorkGuid |> Option.defaultValue []
        WriteTag = writeTagFn
        ForceWorkState = forceWorkState
    }
    let executeApiCall deviceWorkGuid =
        EventDrivenExecution.executeApiCall apiCallExecutionContext deviceWorkGuid
    let executeCallGoing callGuid =
        EventDrivenExecution.executeCallGoing index apiCallExecutionContext callGuid
    /// Call Homing 처리: allGoingTargets에 해당하는 TxWork를 직접 Going시켜 원위치 유도.
    /// 개별 Call의 Ready 전이는 하지 않음 — StartWithHomingPhase completion handler가 일괄 처리.
    let executeCallHoming (callGuid: Guid) (goingTargets: Set<Guid>) =
        EventDrivenExecution.executeCallHoming index apiCallExecutionContext callGuid goingTargets
    let callTransitionContext : CallTransitions.Context = {
        Index = index
        StateManager = stateManager
        Scheduler = scheduler
        ShouldSkipCall = shouldSkipCall
        TriggerCallStateChanged = callStateChangedEvent.Trigger
        ScheduleWorkIfReady = scheduleWorkIfReady
        ScheduleConditionEvaluation = scheduleConditionEvaluation
        DurationTracker = durationTracker
        TimeIgnore = fun () -> timeIgnore
        ExecuteCallGoing = executeCallGoing
    }
    let callTransitionApplyContext : CallTransitionApplyContext = {
        RuntimeMode = runtimeMode
        GetCallOutAddresses = fun callGuid ->
            ioMap.CallToMappings
            |> Map.tryFind callGuid
            |> Option.defaultValue []
            |> List.choose (fun mapping ->
                if String.IsNullOrEmpty mapping.OutAddress then None
                else Some mapping.OutAddress)
        WriteTag = writeTagFn
        GetCallState = stateManager.GetCallState
        ApplyCallTransitionCore = fun callGuid newState ->
            CallTransitions.applyCallTransition callTransitionContext callGuid newState
    }
    let applyCallTransition callGuid newState =
        EventDrivenExecution.applyCallTransition callTransitionApplyContext callGuid newState
    let conditionEvaluationContext : ConditionEvaluation.Context = {
        Index = index
        StateManager = stateManager
        Scheduler = scheduler
        IsWorkFrozen = stateManager.IsWorkFrozen
        CanStartWork = canStartWork
        CanStartCall = canStartCall
        CanCompleteCall = canCompleteCall
        ScheduleConditionEvaluation = scheduleConditionEvaluation
        ShiftToken = shiftToken
        EmitTokenEvent = emitTokenEvent
        CanReceiveToken = canReceiveToken
        ApplyWorkTransition = applyWorkTransition
    }
    /// Passive 모드 (VirtualPlant / Monitoring)는 상태 전이를 IO 이벤트 기반 외부 유추로만 처리.
    /// 엔진의 자체 조건 평가 루프를 끔 — 수동 관찰자 역할.
    let isPassiveMode =
        match runtimeMode with
        | RuntimeMode.VirtualPlant | RuntimeMode.Monitoring -> true
        | _ -> false

    let evaluateConditions () =
        if not isPassiveMode then
            ConditionEvaluation.evaluateConditions conditionEvaluationContext ()
    let transitionGuardsContext : TransitionGuards.Context = {
        Index = index
        StateManager = stateManager
        IsWorkFrozen = stateManager.IsWorkFrozen
        CanStartCall = canStartCall
        CanCompleteCall = canCompleteCall
        ApplyWorkTransition = applyWorkTransition
        ApplyCallTransition = applyCallTransition
    }
    let hasGoingCall () =
        index.AllCallGuids
        |> List.exists (fun callGuid -> stateManager.GetCallState(callGuid) = Status4.Going)
    let connectionReloadContext : ConnectionReload.Context = {
        Index = index
        StateManager = stateManager
        Scheduler = scheduler
        IsActiveSystemWork = isActiveSystemWork
        EmitTokenEvent = emitTokenEvent
        TriggerWorkStateChanged = workStateChangedEvent.Trigger
        PauseDuration = durationTracker.PauseDuration
        ResumePausedDuration = durationTracker.TryResumePausedDuration
        ClearPausedDuration = durationTracker.ClearPausedDuration
    }
    let durationCompleteContext : DurationCompleteContext = {
        IsPassiveMode = isPassiveMode
        RemoveDuration = durationTracker.Remove
        GetWorkState = stateManager.GetWorkState
        MarkMinDurationMet = stateManager.MarkMinDurationMet
        GetWorkCallGuids = fun workGuid -> SimIndex.findOrEmpty workGuid index.WorkCallGuids
        GetCallState = stateManager.GetCallState
        ApplyWorkTransition = applyWorkTransition
    }
    let handleDurationComplete workGuid =
        EventDrivenExecution.handleDurationComplete durationCompleteContext workGuid
    let runtimeContext : EventDrivenEngineRuntime.RuntimeContext = {
        ProcessGate = processGate
        Scheduler = scheduler
        GetStatus = getStatus
        SpeedMultiplier = (fun () -> speedMultiplier)
        UpdateClock = stateManager.UpdateClock
        GetWorkState = stateManager.GetWorkState
        GetWorkToken = stateManager.GetWorkToken
        ClearAndApplyWorkTransition = TransitionGuards.clearAndApplyWork transitionGuardsContext
        ClearAndApplyCallTransition = TransitionGuards.clearAndApplyCall transitionGuardsContext
        ForceAndApplyWorkTransition = TransitionGuards.forceAndApplyWork transitionGuardsContext
        ForceAndApplyCallTransition = TransitionGuards.forceAndApplyCall transitionGuardsContext
        ApplyWorkTransition = applyWorkTransition
        HandleDurationComplete = handleDurationComplete
        ShiftToken = shiftToken
        EmitTokenEvent = emitTokenEvent
        ScheduleConditionEvaluation = scheduleConditionEvaluation
        EvaluateConditions = evaluateConditions
        TriggerCallTimeout = callTimeoutEvent.Trigger
        GetCallState = stateManager.GetCallState
        GetCallName = fun callGuid ->
            Queries.getCall callGuid index.Store
            |> Option.map (fun c -> c.Name) |> Option.defaultValue (string callGuid)
        GetCallTimeoutMs = fun callGuid ->
            index.CallTimeoutMap |> Map.tryFind callGuid |> Option.map (fun ts -> int ts.TotalMilliseconds)
        CurrentTimeMs = fun () -> scheduler.CurrentTimeMs
    }
    let advanceStepRuntime targetTimeMs =
        EventDrivenEngineRuntime.advanceAndDrain runtimeContext targetTimeMs
    let startEngineThread (tokenSource: CancellationTokenSource) =
        let thread = Thread(ThreadStart(fun () -> EventDrivenEngineRuntime.simulationLoop runtimeContext tokenSource.Token))
        thread.IsBackground <- true
        thread.Name <- "EventDrivenEngine"
        thread.Start()
        engineThread <- Some thread
    let lifecycleContext : EngineLifecycle.Context = {
        EngineThreadJoinTimeoutMs = engineThreadJoinTimeoutMs
        GetStatus = getStatus
        SetStatus = setStatus
        GetEngineThread = (fun () -> engineThread)
        SetEngineThread = (fun thread -> engineThread <- thread)
        GetCts = (fun () -> cts)
        SetCts = (fun tokenSource -> cts <- tokenSource)
        TriggerStatusChanged = simulationStatusChangedEvent.Trigger
        ScheduleConditionEvaluation = scheduleConditionEvaluation
        StartEngineThread = startEngineThread
        AllWorkGuids = index.AllWorkGuids
        AllCallGuids = index.AllCallGuids
        GetWorkState = stateManager.GetWorkState
        GetCallState = stateManager.GetCallState
        ApplyWorkTransition = applyWorkTransition
        ForceWorkState = (fun workGuid newState -> stateManager.ForceWorkState(workGuid, newState))
        ForceCallState = (fun callGuid newState -> stateManager.ForceCallState(callGuid, newState))
        SchedulerClear = scheduler.Clear
        ResetState = stateManager.Reset
    }
    let flowContext : EngineFlowStep.FlowContext = {
        Index = index
        StateManager = stateManager
        DurationTracker = durationTracker
        ScheduleConditionEvaluation = scheduleConditionEvaluation
    }
    let stepBoundaryContext : EngineFlowStep.StepBoundaryContext = {
        GetState = stateManager.GetState
        CurrentTimeMs = (fun () -> scheduler.CurrentTimeMs)
        SetAllFlowStates = EngineFlowStep.setAllFlowStates flowContext
        AdvanceStepRuntime = advanceStepRuntime
        NextEventTime = (fun () -> scheduler.NextEventTime)
    }
    let reloadContext : EngineFlowStep.ReloadContext = {
        RemoveScheduledConditionEvents = (fun () -> ConnectionReload.removeScheduledConditionEvents connectionReloadContext)
        ClearConnectionTransientState = stateManager.ClearConnectionTransientState
        ReloadConnections = (fun () -> SimIndex.reloadConnections index)
        InvalidateOrphanedGoingWorks = (fun previousSnapshot currentSnapshot ->
            ConnectionReload.invalidateOrphanedGoingWorks connectionReloadContext previousSnapshot currentSnapshot)
        InvalidateOrphanedTokenHolders = (fun () -> ConnectionReload.invalidateOrphanedTokenHolders connectionReloadContext)
        ScheduleConditionEvaluation = scheduleConditionEvaluation
    }

    // ── ISimulationEngine 구현에 쓰이는 함수들 (let binding으로 한 번 정의, interface에서 참조) ──
    let applyInitialStates () =
        EngineLifecycle.applyInitialFinishStates index stateManager resolveWorkName workStateChangedEvent.Trigger

    let applyToken (workGuid, newValue, kind, token) =
        stateManager.SetWorkToken(workGuid, newValue)
        emitTokenEvent kind token workGuid None
        scheduleConditionEvaluation ()

    let seedToken (sourceWorkGuid: Guid) (value: TokenValue) =
        match stateManager.GetWorkToken(sourceWorkGuid) with
        | Some _ -> ()
        | None ->
            stateManager.SetTokenOrigin(value, TokenFlow.resolveSeedOriginLabel tokenFlowContext sourceWorkGuid)
            applyToken (sourceWorkGuid, Some value, Seed, value)

    let discardToken (workGuid: Guid) =
        match stateManager.GetWorkToken(workGuid) with
        | Some token -> applyToken (workGuid, None, Discard, token)
        | None -> ()

    let getTokenOrigin (token: TokenValue) =
        match token with
        | IntToken id -> stateManager.GetState().TokenOrigins |> Map.tryFind id

    let hasStartableWork () =
        EngineFlowStep.hasStartableWork index stateManager canStartWork stateManager.IsWorkFrozen

    let hasActiveDuration () =
        EngineFlowStep.hasActiveDuration index stateManager stateManager.IsWorkFrozen

    let getWorkStateOpt (workGuid: Guid) =
        if index.AllWorkGuids |> List.contains workGuid then
            Some (stateManager.GetWorkState(workGuid))
        else
            None

    let stepContext : EngineFlowStep.StepContext = {
        Index = index
        StateManager = stateManager
        HasStartableWork = hasStartableWork
        HasActiveDuration = hasActiveDuration
        HasGoingCall = hasGoingCall
        NextToken = stateManager.NextToken
        SeedToken = seedToken
        ForceWorkState = forceWorkState
        RunStepUntilBoundary = (fun () -> EngineFlowStep.runStepUntilBoundary stepBoundaryContext)
    }

    /// 자동 원위치 페이즈:
    /// 1. IsFinished → ApplyInitialStates 즉시 Finish
    /// 2. Active System Work/Call → Homing
    /// 3. ExecuteCallHoming per call (goingTargets로 Device 구분)
    /// 4. target Device Work Finish 완료 후 Active Call Ready
    /// 5. Active Call 전부 Ready 확인 후 Active Work Ready 복원 → homing 완료
    let startWithHomingPhase () : bool =
        let isFinishedGuids = SimIndex.findInitialFlagRxWorkGuids index
        applyInitialStates ()
        let plan = HomingPhaseSetup.computePlan index isFinishedGuids
        let homingContext : HomingPhaseContext = {
            AllGoingTargets = plan.AllGoingTargets
            DisplayHomingCallGuids = plan.DisplayHomingCallGuids
            ExecutionCallGuids = plan.ExecutionCallGuids
            ActiveWorkGuids = plan.ActiveWorkGuids
            IsHomingPhase = (fun () -> isHomingPhase)
            SetIsHomingPhase = (fun value -> isHomingPhase <- value)
            SetWorkStateDirect = setWorkStateDirect
            SetCallStateDirect = setCallStateDirect
            TriggerHomingPhaseCompleted = fun () -> homingPhaseCompletedEvent.Trigger(EventArgs.Empty)
            SubscribeWorkStateChanged = fun handler -> workStateChangedEvent.Publish.Subscribe(handler)
            StartEngine = fun () -> EngineLifecycle.start lifecycleContext
            ExecuteCallHoming = executeCallHoming
        }
        EventDrivenHoming.startWithHomingPhase homingContext

    let injectIOValueByAddress (address: string) (value: string) : bool =
        match ioMap.InAddressToMappings |> Map.tryFind address with
        | Some mappings ->
            for m in mappings do
                stateManager.SetIOValue(m.ApiCallGuid, value)
            scheduleConditionEvaluation ()
            true
        | None -> false

    let reloadDurations () =
        lock processGate (fun () ->
            SimIndex.reloadDurations index
                (index.AllWorkGuids
                 |> List.filter (fun wg -> stateManager.GetWorkState(wg) = Status4.Going)
                 |> Set.ofList))

    interface ISimulationEngine with
        member _.State = stateManager.GetState()
        member _.Status = getStatus ()
        member _.Index = index
        member _.Start() = EngineLifecycle.start lifecycleContext
        member _.Pause() = EngineLifecycle.pause lifecycleContext
        member _.Resume() = EngineLifecycle.resume lifecycleContext
        member _.Stop() = EngineLifecycle.stop lifecycleContext
        member _.Reset() = EngineLifecycle.reset lifecycleContext
        member _.ApplyInitialStates() = applyInitialStates ()
        member _.ForceWorkState(workGuid, newState) = forceWorkState workGuid newState
        member _.ForceCallState(callGuid, newState) = forceCallState callGuid newState
        member _.GetWorkState(workGuid) = getWorkStateOpt workGuid
        member _.GetCallState(callGuid) = stateManager.GetState().CallStates.TryFind(callGuid)
        member _.SetAllFlowStates(tag) = EngineFlowStep.setAllFlowStates flowContext tag
        member _.GetFlowState(flowGuid) = stateManager.GetFlowState(flowGuid)
        member _.HasStartableWork = hasStartableWork ()
        member _.HasActiveDuration = hasActiveDuration ()
        member _.Step() =
            lock processGate (fun () ->
                EngineFlowStep.runStepUntilBoundary stepBoundaryContext)
        member _.CanAdvanceStep(selectedSourceGuid, autoStartSources) =
            EngineFlowStep.canAdvanceStep stepContext selectedSourceGuid autoStartSources
        member _.StepWithSourcePriming(selectedSourceGuid, autoStartSources) =
            lock processGate (fun () ->
                EngineFlowStep.stepWithSourcePriming stepContext selectedSourceGuid autoStartSources)
        member _.ReloadConnections() =
            lock processGate (fun () -> EngineFlowStep.reloadConnections reloadContext)
        member _.ReloadDurations() = reloadDurations ()
        member _.SpeedMultiplier
            with get() = speedMultiplier
            and set(value) = speedMultiplier <- max 0.1 (min 1000.0 value)
        member _.TimeIgnore
            with get() = timeIgnore
            and set(isIgnored) =
                let previous = timeIgnore
                timeIgnore <- isIgnored
                if isIgnored && not previous && getStatus() = Running then
                    EngineFlowStep.rescheduleOnTimeIgnoreEnabled index stateManager scheduleNowDurationCheck scheduleNowStateChange
        member _.InjectIOValue(apiCallGuid, value) =
            stateManager.SetIOValue(apiCallGuid, value)
            scheduleConditionEvaluation ()
        member _.InjectIOValueByAddress(address, value) = injectIOValueByAddress address value
        member _.IOMap = ioMap
        member _.NextToken() = stateManager.NextToken()
        member _.SeedToken(sourceWorkGuid, value) = seedToken sourceWorkGuid value
        member _.DiscardToken(workGuid) = discardToken workGuid
        member _.GetWorkToken(workGuid) = stateManager.GetWorkToken(workGuid)
        member _.GetTokenOrigin(token) = getTokenOrigin token
        member _.StartWithHomingPhase() = startWithHomingPhase ()
        member _.IsHomingPhase = isHomingPhase
        [<CLIEvent>] member _.WorkStateChanged = workStateChangedEvent.Publish
        [<CLIEvent>] member _.CallStateChanged = callStateChangedEvent.Publish
        [<CLIEvent>] member _.SimulationStatusChanged = simulationStatusChangedEvent.Publish
        [<CLIEvent>] member _.TokenEvent = tokenEventEvent.Publish
        [<CLIEvent>] member _.CallTimeout = callTimeoutEvent.Publish
        [<CLIEvent>] member _.HomingPhaseCompleted = homingPhaseCompletedEvent.Publish
        member _.Dispose() = EngineLifecycle.stop lifecycleContext

    new(index: SimIndex, runtimeMode: RuntimeMode) = new EventDrivenEngine(index, runtimeMode, None)
