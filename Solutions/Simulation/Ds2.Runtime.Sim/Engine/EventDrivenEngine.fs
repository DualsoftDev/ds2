namespace Ds2.Runtime.Sim.Engine

open System
open System.Threading
open Ds2.Core
open Ds2.Core.Store
open Ds2.Runtime.Sim.Model
open Ds2.Runtime.Sim.Engine.Core
open Ds2.Runtime.Sim.Engine.Scheduler

/// 이벤트 기반 시뮬레이션 엔진
/// H 상태 구현: F->H (내부 Call R 정리) -> H->R (최소 1ms)
type EventDrivenEngine(index: SimIndex) =
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
    let simulationStatusChangedEvent = Event<SimulationStatusChangedArgs>()
    let tokenEventEvent = Event<TokenEventArgs>()
    let scheduleConditionEvaluation () =
        scheduler.ScheduleNow(ScheduledEventType.EvaluateConditions, ScheduledEvent.PriorityConditionEval) |> ignore
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
    let callTransitionContext : CallTransitions.Context = {
        Index = index
        StateManager = stateManager
        Scheduler = scheduler
        ShouldSkipCall = shouldSkipCall
        TriggerCallStateChanged = callStateChangedEvent.Trigger
        ScheduleWorkIfReady = scheduleWorkIfReady
        ScheduleConditionEvaluation = scheduleConditionEvaluation
        DurationTracker = durationTracker
    }
    let applyCallTransition callGuid newState =
        CallTransitions.applyCallTransition callTransitionContext callGuid newState
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
    let evaluateConditions () =
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
    let handleDurationComplete workGuid =
        durationTracker.Remove(workGuid)
        if stateManager.GetWorkState(workGuid) = Status4.Going then
            stateManager.MarkMinDurationMet(workGuid)
            let callGuids = SimIndex.findOrEmpty workGuid index.WorkCallGuids
            if callGuids.IsEmpty then
                applyWorkTransition workGuid Status4.Finish
            elif callGuids |> List.forall (fun callGuid -> stateManager.GetCallState(callGuid) = Status4.Finish) then
                applyWorkTransition workGuid Status4.Finish
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
    let createStepContext (self: EventDrivenEngine) : EngineFlowStep.StepContext = {
        Index = index
        StateManager = stateManager
        HasStartableWork = (fun () -> self.HasStartableWork)
        HasActiveDuration = (fun () -> self.HasActiveDuration)
        HasGoingCall = hasGoingCall
        NextToken = stateManager.NextToken
        SeedToken = (fun workGuid token -> self.SeedToken(workGuid, token))
        ForceWorkState = (fun workGuid newState -> self.ForceWorkState(workGuid, newState))
        RunStepUntilBoundary = (fun () -> EngineFlowStep.runStepUntilBoundary stepBoundaryContext)
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
    member _.State = stateManager.GetState()
    member _.Status = getStatus ()
    member _.Index = index
    [<CLIEvent>] member _.WorkStateChanged = workStateChangedEvent.Publish
    [<CLIEvent>] member _.CallStateChanged = callStateChangedEvent.Publish
    [<CLIEvent>] member _.SimulationStatusChanged = simulationStatusChangedEvent.Publish
    [<CLIEvent>] member _.TokenEvent = tokenEventEvent.Publish
    member _.Start() = EngineLifecycle.start lifecycleContext
    member _.Pause() = EngineLifecycle.pause lifecycleContext
    member _.Resume() = EngineLifecycle.resume lifecycleContext
    member _.Stop() = EngineLifecycle.stop lifecycleContext
    member _.Reset() = EngineLifecycle.reset lifecycleContext
    member _.ApplyInitialStates() =
        let initialRxWorkGuids = SimIndex.findInitialFlagRxWorkGuids index
        for workGuid in initialRxWorkGuids do
            stateManager.ForceWorkState(workGuid, Status4.Finish)
            let workName = index.WorkName |> Map.tryFind workGuid |> Option.defaultValue (string workGuid)
            workStateChangedEvent.Trigger({
                WorkGuid = workGuid
                WorkName = workName
                PreviousState = Status4.Ready
                NewState = Status4.Finish
                Clock = TimeSpan.Zero
            })
    member _.IsHomingPhase = isHomingPhase
    [<CLIEvent>] member _.HomingPhaseCompleted = homingPhaseCompletedEvent.Publish

    /// 자동 원위치 페이즈:
    /// 1. IsFinished 설정된 Work → ApplyInitialStates로 즉시 Finish (Going 불필요)
    /// 2. IsFinished 미설정 → computeAutoHomingTargets로 대상 계산 → Going시켜서 Finish 도달
    /// PLC 연동 시 실제 디바이스가 물리적으로 원위치 동작을 수행하는 것과 동일.
    member this.StartWithHomingPhase() : bool =
        // Phase 1: IsFinished 플래그가 있으면 즉시 적용
        let isFinishedGuids = SimIndex.findInitialFlagRxWorkGuids index
        this.ApplyInitialStates()

        // Phase 2: IsFinished 없는 Device Work → 자동 계산으로 Going 시작
        let autoHomingGuids = SimIndex.computeAutoHomingTargets index
        // IsFinished로 이미 Finish된 것은 제외
        let needsGoing = autoHomingGuids |> Set.filter (fun g -> not (isFinishedGuids.Contains g))

        if needsGoing.IsEmpty then
            // Going 시킬 대상 없음 → 바로 시작
            this.Start()
            false
        else
            isHomingPhase <- true
            let homingTargets = System.Collections.Generic.HashSet<Guid>(needsGoing)

            // WorkStateChanged 구독: 대상 Work가 Finish에 도달하면 원위치 완료
            let mutable handler : IDisposable = null
            handler <- workStateChangedEvent.Publish.Subscribe(fun args ->
                if isHomingPhase && args.NewState = Status4.Finish && homingTargets.Contains(args.WorkGuid) then
                    homingTargets.Remove(args.WorkGuid) |> ignore
                    if homingTargets.Count = 0 then
                        isHomingPhase <- false
                        handler.Dispose()
                        homingPhaseCompletedEvent.Trigger(EventArgs.Empty))

            // 엔진 시작
            this.Start()
            // 대상 Work를 강제 Going → Duration 소모 → Finish (실제 원위치 동작)
            for workGuid in needsGoing do
                this.ForceWorkState(workGuid, Status4.Going)
            true

    member _.ForceWorkState(workGuid, newState) =
        if index.AllWorkGuids |> List.contains workGuid then
            scheduler.ScheduleNow(
                ScheduledEventType.ForcedWorkTransition(workGuid, newState),
                ScheduledEvent.PriorityStateChange)
            |> ignore
    member _.ForceCallState(callGuid, newState) =
        if index.AllCallGuids |> List.contains callGuid then
            scheduler.ScheduleNow(
                ScheduledEventType.ForcedCallTransition(callGuid, newState),
                ScheduledEvent.PriorityStateChange)
            |> ignore
    member _.GetWorkState(workGuid) =
        if index.AllWorkGuids |> List.contains workGuid then
            Some (stateManager.GetWorkState(workGuid))
        else
            None
    member _.GetCallState(callGuid) =
        stateManager.GetState().CallStates.TryFind(callGuid)
    member _.SetAllFlowStates(tag: FlowTag) =
        EngineFlowStep.setAllFlowStates flowContext tag
    member _.GetFlowState(flowGuid: Guid) = stateManager.GetFlowState(flowGuid)
    member _.SpeedMultiplier
        with get() = speedMultiplier
        and set(multiplier) = speedMultiplier <- max 0.1 (min 1000.0 multiplier)
    member _.TimeIgnore
        with get() = timeIgnore
        and set(isIgnored) =
            let previous = timeIgnore
            timeIgnore <- isIgnored

            if isIgnored && not previous && getStatus() = Running then
                for workGuid in index.AllWorkGuids do
                    match stateManager.GetWorkState(workGuid) with
                    | Status4.Going when not (stateManager.IsMinDurationMet(workGuid)) ->
                        scheduler.ScheduleNow(
                            ScheduledEventType.DurationComplete workGuid,
                            ScheduledEvent.PriorityDurationCheck)
                        |> ignore
                    | Status4.Homing ->
                        scheduler.ScheduleNow(
                            ScheduledEventType.HomingComplete workGuid,
                            ScheduledEvent.PriorityStateChange)
                        |> ignore
                    | _ -> ()
    member _.InjectIOValue(apiCallGuid, value) =
        stateManager.SetIOValue(apiCallGuid, value)
        scheduleConditionEvaluation ()
    member private _.ApplyToken(workGuid: Guid, newValue: TokenValue option, kind: TokenEventKind, token: TokenValue) =
        stateManager.SetWorkToken(workGuid, newValue)
        emitTokenEvent kind token workGuid None
        scheduler.ScheduleNow(ScheduledEventType.EvaluateConditions, ScheduledEvent.PriorityConditionEval) |> ignore
    member this.SeedToken(sourceWorkGuid: Guid, value: TokenValue) =
        match stateManager.GetWorkToken(sourceWorkGuid) with
        | Some _ -> ()
        | None ->
            let canonicalSourceWorkGuid = SimIndex.canonicalWorkGuid index sourceWorkGuid
            let originLabel =
                Queries.getTokenSpecs index.Store
                |> List.tryFind (fun spec ->
                    spec.WorkId
                    |> Option.map (fun workGuid -> SimIndex.canonicalWorkGuid index workGuid = canonicalSourceWorkGuid)
                    |> Option.defaultValue false)
                |> Option.map (fun spec -> spec.Label)
                |> Option.defaultWith (fun () -> workNameOf canonicalSourceWorkGuid)
            stateManager.SetTokenOrigin(value, originLabel)
            this.ApplyToken(sourceWorkGuid, Some value, Seed, value)
    member this.DiscardToken(workGuid: Guid) =
        match stateManager.GetWorkToken(workGuid) with
        | Some token -> this.ApplyToken(workGuid, None, Discard, token)
        | None -> ()
    member _.GetWorkToken(workGuid: Guid) = stateManager.GetWorkToken(workGuid)
    member _.GetTokenOrigin(token: TokenValue) =
        match token with
        | IntToken id -> stateManager.GetState().TokenOrigins |> Map.tryFind id
    member _.NextToken() = stateManager.NextToken()
    member _.HasStartableWork = EngineFlowStep.hasStartableWork index stateManager canStartWork stateManager.IsWorkFrozen
    member _.HasActiveDuration = EngineFlowStep.hasActiveDuration index stateManager stateManager.IsWorkFrozen
    member _.Step() =
        lock processGate (fun () ->
            EngineFlowStep.runStepUntilBoundary stepBoundaryContext)
    member this.CanAdvanceStep(selectedSourceGuid, autoStartSources) =
        EngineFlowStep.canAdvanceStep (createStepContext this) selectedSourceGuid autoStartSources
    member this.StepWithSourcePriming(selectedSourceGuid, autoStartSources) =
        lock processGate (fun () ->
            EngineFlowStep.stepWithSourcePriming (createStepContext this) selectedSourceGuid autoStartSources)
    member _.ReloadConnections() =
        lock processGate (fun () ->
            EngineFlowStep.reloadConnections reloadContext)
    member _.ReloadDurations() =
        lock processGate (fun () ->
            let goingGuids =
                index.AllWorkGuids
                |> List.filter (fun wg -> stateManager.GetWorkState(wg) = Status4.Going)
                |> Set.ofList
            SimIndex.reloadDurations index goingGuids)
    interface ISimulationEngine with
        member this.State = this.State
        member this.Status = this.Status
        member this.Index = this.Index
        member this.Start() = this.Start()
        member this.Pause() = this.Pause()
        member this.Resume() = this.Resume()
        member this.Stop() = this.Stop()
        member this.Reset() = this.Reset()
        member this.ApplyInitialStates() = this.ApplyInitialStates()
        member this.ForceWorkState(workGuid, newState) = this.ForceWorkState(workGuid, newState)
        member this.ForceCallState(callGuid, newState) = this.ForceCallState(callGuid, newState)
        member this.GetWorkState(workGuid) = this.GetWorkState(workGuid)
        member this.GetCallState(callGuid) = this.GetCallState(callGuid)
        member this.SetAllFlowStates(tag) = this.SetAllFlowStates(tag)
        member this.GetFlowState(flowGuid) = this.GetFlowState(flowGuid)
        member this.HasStartableWork = this.HasStartableWork
        member this.HasActiveDuration = this.HasActiveDuration
        member this.Step() = this.Step()
        member this.CanAdvanceStep(selectedSourceGuid, autoStartSources) =
            this.CanAdvanceStep(selectedSourceGuid, autoStartSources)
        member this.StepWithSourcePriming(selectedSourceGuid, autoStartSources) =
            this.StepWithSourcePriming(selectedSourceGuid, autoStartSources)
        member this.ReloadConnections() = this.ReloadConnections()
        member this.ReloadDurations() = this.ReloadDurations()
        member this.SpeedMultiplier
            with get() = this.SpeedMultiplier
            and set(value) = this.SpeedMultiplier <- value
        member this.TimeIgnore
            with get() = this.TimeIgnore
            and set(value) = this.TimeIgnore <- value
        member this.InjectIOValue(apiCallGuid, value) = this.InjectIOValue(apiCallGuid, value)
        member this.NextToken() = this.NextToken()
        member this.SeedToken(sourceWorkGuid, value) = this.SeedToken(sourceWorkGuid, value)
        member this.DiscardToken(workGuid) = this.DiscardToken(workGuid)
        member this.GetWorkToken(workGuid) = this.GetWorkToken(workGuid)
        member this.GetTokenOrigin(token) = this.GetTokenOrigin(token)
        member this.StartWithHomingPhase() = this.StartWithHomingPhase()
        member this.IsHomingPhase = this.IsHomingPhase
        [<CLIEvent>] member this.WorkStateChanged = this.WorkStateChanged
        [<CLIEvent>] member this.CallStateChanged = this.CallStateChanged
        [<CLIEvent>] member this.SimulationStatusChanged = this.SimulationStatusChanged
        [<CLIEvent>] member this.TokenEvent = this.TokenEvent
        [<CLIEvent>] member this.HomingPhaseCompleted = this.HomingPhaseCompleted
        member this.Dispose() = this.Stop()
