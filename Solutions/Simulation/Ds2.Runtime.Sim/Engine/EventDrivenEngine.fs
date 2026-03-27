namespace Ds2.Runtime.Sim.Engine

open System
open System.Threading
open Ds2.Core
open Ds2.Store
open Ds2.Runtime.Sim.Model
open Ds2.Runtime.Sim.Engine.Core
open Ds2.Runtime.Sim.Engine.Scheduler

/// 이벤트 기반 시뮬레이션 엔진
/// H 상태 구현: F->H (내부 Call R 정리) -> H->R (최소 1ms)
type EventDrivenEngine(index: SimIndex) =

    let mutable status = Stopped
    let scheduler = EventScheduler()

    let mutable speedMultiplier = 1.0
    let mutable timeIgnore = false

    let mutable engineThread: Thread option = None
    let mutable cts: CancellationTokenSource option = None
    let processGate = obj()
    let engineThreadJoinTimeoutMs = 1000

    let disposeCurrentCts () =
        cts |> Option.iter (fun c -> c.Dispose())
        cts <- None

    let clearDeadEngineThreadReference () =
        match engineThread with
        | Some thread ->
            if not thread.IsAlive then
                engineThread <- None
        | _ -> ()

    let ensureEngineThreadExited operationName =
        clearDeadEngineThreadReference ()
        match engineThread with
        | None -> ()
        | Some thread when thread.Join(engineThreadJoinTimeoutMs) ->
            engineThread <- None
        | Some _ ->
            raise (InvalidOperationException(
                $"EventDrivenEngine thread did not exit within {engineThreadJoinTimeoutMs}ms during {operationName}."))

    let stateManager = StateManager(index, index.TickMs)
    let durationTracker = DurationTracker(scheduler)

    // UI 이벤트
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
        OnDurationScheduled = fun wg eid ts -> durationTracker.OnDurationScheduled(wg, eid, ts)
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
        IsActiveSystemWork = isActiveSystemWork
        CanStartWork = canStartWork
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
        CancelDurationEvent = durationTracker.Cancel
        ClearPausedDuration = durationTracker.ClearPausedDuration
    }

    let handleDurationComplete workGuid =
        durationTracker.Remove(workGuid)
        if stateManager.GetWorkState(workGuid) = Status4.Going then
            stateManager.MarkMinDurationMet(workGuid)
            let callGuids = SimIndex.findOrEmpty workGuid index.WorkCallGuids
            if callGuids.IsEmpty then
                applyWorkTransition workGuid Status4.Finish
            else
                if callGuids |> List.forall (fun cg -> stateManager.GetCallState(cg) = Status4.Finish) then
                    applyWorkTransition workGuid Status4.Finish

    let runtimeContext : EventDrivenEngineRuntime.RuntimeContext = {
        ProcessGate = processGate
        Scheduler = scheduler
        GetStatus = (fun () -> Volatile.Read(&status))
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

    let stepSnapshot () =
        let state = stateManager.GetState()
        state.WorkStates, state.CallStates, state.WorkTokens, state.CompletedTokens, scheduler.CurrentTimeMs

    let hasStepProgressedSince before =
        stepSnapshot () <> before

    let advanceStepRuntime targetTimeMs =
        EventDrivenEngineRuntime.advanceAndDrain runtimeContext targetTimeMs

    let advanceUntilStepBoundary before =
        let mutable progressed = false
        let mutable guard = 0

        while not progressed && guard < 256 do
            match scheduler.NextEventTime with
            | Some nextEventTime ->
                guard <- guard + 1
                advanceStepRuntime nextEventTime
                progressed <- hasStepProgressedSince before
            | None ->
                guard <- 256

        progressed

    let runStepUntilBoundary (engine: EventDrivenEngine) =
        let before = stepSnapshot ()

        engine.SetAllFlowStates(FlowTag.Drive)
        advanceStepRuntime scheduler.CurrentTimeMs

        let progressed =
            if hasStepProgressedSince before then true
            else advanceUntilStepBoundary before

        engine.SetAllFlowStates(FlowTag.Pause)
        progressed

    let startEngineThread (tokenSource: CancellationTokenSource) =
        let t = Thread(ThreadStart(fun () -> EventDrivenEngineRuntime.simulationLoop runtimeContext tokenSource.Token))
        t.IsBackground <- true
        t.Name <- "EventDrivenEngine"
        t.Start()
        engineThread <- Some t

    // ===== Public API =====

    member _.State = stateManager.GetState()
    member _.Status = Volatile.Read(&status)
    member _.Index = index

    [<CLIEvent>] member _.WorkStateChanged = workStateChangedEvent.Publish
    [<CLIEvent>] member _.CallStateChanged = callStateChangedEvent.Publish
    [<CLIEvent>] member _.SimulationStatusChanged = simulationStatusChangedEvent.Publish
    [<CLIEvent>] member _.TokenEvent = tokenEventEvent.Publish

    member this.Start() =
        if Volatile.Read(&status) = Running then () else
        ensureEngineThreadExited "Start"
        let prev = Volatile.Read(&status)
        Volatile.Write(&status, Running)
        simulationStatusChangedEvent.Trigger({ PreviousStatus = prev; NewStatus = Running })
        scheduleConditionEvaluation ()
        disposeCurrentCts()
        let tokenSource = new CancellationTokenSource()
        cts <- Some tokenSource
        startEngineThread tokenSource

    member _.Pause() =
        if Volatile.Read(&status) = Running then
            Volatile.Write(&status, Paused)
            simulationStatusChangedEvent.Trigger({ PreviousStatus = Running; NewStatus = Paused })

    member _.Resume() =
        if Volatile.Read(&status) = Paused then
            ensureEngineThreadExited "Resume"
            disposeCurrentCts()
            Volatile.Write(&status, Running)
            simulationStatusChangedEvent.Trigger({ PreviousStatus = Paused; NewStatus = Running })
            let tokenSource = new CancellationTokenSource()
            cts <- Some tokenSource
            startEngineThread tokenSource

    member _.Stop() =
        if Volatile.Read(&status) <> Stopped then
            let prev = Volatile.Read(&status)
            Volatile.Write(&status, Stopped)
            cts |> Option.iter (fun c -> c.Cancel())
            ensureEngineThreadExited "Stop"
            disposeCurrentCts()
            engineThread <- None
            simulationStatusChangedEvent.Trigger({ PreviousStatus = prev; NewStatus = Stopped })

    member this.Reset() =
        this.Stop()
        for workGuid in index.AllWorkGuids do
            if stateManager.GetWorkState(workGuid) = Status4.Finish then
                applyWorkTransition workGuid Status4.Homing
        for workGuid in index.AllWorkGuids do
            let ws = stateManager.GetWorkState(workGuid)
            if ws <> Status4.Ready && ws <> Status4.Homing then
                stateManager.ForceWorkState(workGuid, Status4.Ready)
        for callGuid in index.AllCallGuids do
            if stateManager.GetCallState(callGuid) <> Status4.Ready then
                stateManager.ForceCallState(callGuid, Status4.Ready)
        for workGuid in index.AllWorkGuids do
            if stateManager.GetWorkState(workGuid) = Status4.Homing then
                applyWorkTransition workGuid Status4.Ready
        scheduler.Clear()
        stateManager.Reset()

    member _.ApplyInitialStates() =
        let initialRxWorkGuids = SimIndex.findInitialFlagRxWorkGuids index
        for workGuid in initialRxWorkGuids do
            stateManager.ForceWorkState(workGuid, Status4.Finish)
            let wName = index.WorkName |> Map.tryFind workGuid |> Option.defaultValue (string workGuid)
            workStateChangedEvent.Trigger({ WorkGuid = workGuid; WorkName = wName; PreviousState = Status4.Ready; NewState = Status4.Finish; Clock = TimeSpan.Zero })

    member _.ForceWorkState(workGuid, newState) =
        if index.AllWorkGuids |> List.contains workGuid then
            scheduler.ScheduleNow(ScheduledEventType.ForcedWorkTransition(workGuid, newState), ScheduledEvent.PriorityStateChange) |> ignore

    member _.ForceCallState(callGuid, newState) =
        if index.AllCallGuids |> List.contains callGuid then
            scheduler.ScheduleNow(ScheduledEventType.ForcedCallTransition(callGuid, newState), ScheduledEvent.PriorityStateChange) |> ignore

    member _.GetWorkState(workGuid) =
        if index.AllWorkGuids |> List.contains workGuid then
            Some (stateManager.GetWorkState(workGuid))
        else None
    member _.GetCallState(callGuid) = stateManager.GetState().CallStates.TryFind(callGuid)

    /// Active System의 Flow Guid 목록
    member private _.ActiveFlowGuids =
        index.AllFlowGuids
        |> List.filter (fun fg ->
            index.AllWorkGuids
            |> List.exists (fun wg ->
                index.WorkFlowGuid |> Map.tryFind wg = Some fg
                && index.WorkSystemName |> Map.tryFind wg
                   |> Option.map (fun sn -> index.ActiveSystemNames.Contains(sn))
                   |> Option.defaultValue false))

    member this.SetAllFlowStates(tag: FlowTag) =
        match tag with
        | FlowTag.Pause ->
            for fg in this.ActiveFlowGuids do
                stateManager.SetFlowState(fg, FlowTag.Pause)
            let goingWorkGuids =
                index.AllWorkGuids |> List.filter (fun wg -> stateManager.GetWorkState(wg) = Status4.Going)
            durationTracker.SavePausedDurations(
                goingWorkGuids,
                stateManager.GetCallState,
                stateManager.GetFlowState,
                index.WorkCallGuids,
                index.WorkFlowGuid)
        | FlowTag.Drive ->
            for fg in this.ActiveFlowGuids do
                stateManager.SetFlowState(fg, FlowTag.Drive)
            durationTracker.ResumePausedDurations(stateManager.GetWorkState)
        | _ ->
            stateManager.SetAllFlowStates(tag)
        scheduleConditionEvaluation ()

    member _.GetFlowState(flowGuid: Guid) = stateManager.GetFlowState(flowGuid)

    member _.SpeedMultiplier
        with get() = speedMultiplier
        and set(m) = speedMultiplier <- max 0.1 (min 1000.0 m)

    member _.TimeIgnore
        with get() = timeIgnore
        and set(i) =
            let prev = timeIgnore
            timeIgnore <- i
            if i && not prev && Volatile.Read(&status) = Running then
                for wg in index.AllWorkGuids do
                    match stateManager.GetWorkState(wg) with
                    | Status4.Going when not (stateManager.IsMinDurationMet(wg)) ->
                        scheduler.ScheduleNow(ScheduledEventType.DurationComplete wg, ScheduledEvent.PriorityDurationCheck) |> ignore
                    | Status4.Homing ->
                        scheduler.ScheduleNow(ScheduledEventType.HomingComplete wg, ScheduledEvent.PriorityStateChange) |> ignore
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
                DsQuery.getTokenSpecs index.Store
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

    member _.HasStartableWork =
        let hasStartableReadyWork =
            index.AllWorkGuids
            |> List.exists (fun wg ->
                stateManager.GetWorkState(wg) = Status4.Ready
                && canStartWork wg)
        let hasGoingWorkWithPendingCalls =
            index.AllWorkGuids
            |> List.exists (fun wg ->
                stateManager.GetWorkState(wg) = Status4.Going
                && (let callGuids = SimIndex.findOrEmpty wg index.WorkCallGuids
                    not callGuids.IsEmpty
                    && callGuids |> List.exists (fun cg -> stateManager.GetCallState(cg) <> Status4.Finish)))
        hasStartableReadyWork || hasGoingWorkWithPendingCalls

    member _.HasActiveDuration =
        index.AllWorkGuids
        |> List.exists (fun wg ->
            stateManager.GetWorkState(wg) = Status4.Going
            && (let callGuids = SimIndex.findOrEmpty wg index.WorkCallGuids
                callGuids.IsEmpty
                || callGuids |> List.forall (fun cg -> stateManager.GetCallState(cg) = Status4.Finish)))

    member this.Step() =
        lock processGate (fun () ->
            runStepUntilBoundary this)

    member this.CanAdvanceStep(selectedSourceGuid, autoStartSources) =
        let state = stateManager.GetState()
        StepSemantics.canAdvanceStep
            index
            state
            stateManager.GetWorkState
            this.HasStartableWork
            this.HasActiveDuration
            (hasGoingCall ())
            autoStartSources
            selectedSourceGuid

    member private this.PrimeStepSources(selectedSourceGuid, autoStartSources) =
        let sourceGuids =
            StepSemantics.primableSourceGuids
                index
                (stateManager.GetState())
                stateManager.GetWorkState
                autoStartSources
                selectedSourceGuid

        for sourceGuid in sourceGuids do
            if stateManager.GetWorkToken(sourceGuid) |> Option.isNone then
                let token = stateManager.NextToken()
                this.SeedToken(sourceGuid, token)
            this.ForceWorkState(sourceGuid, Status4.Going)

        not sourceGuids.IsEmpty

    member this.StepWithSourcePriming(selectedSourceGuid, autoStartSources) =
        lock processGate (fun () ->
            if hasGoingCall () then
                false
            else
                let hasEngineProgress = this.HasStartableWork || this.HasActiveDuration
                if not hasEngineProgress then
                    this.PrimeStepSources(selectedSourceGuid, autoStartSources) |> ignore

                if this.HasStartableWork || this.HasActiveDuration then
                    runStepUntilBoundary this
                else
                    false)

    member _.ReloadConnections() =
        lock processGate (fun () ->
            ConnectionReload.removeScheduledConditionEvents connectionReloadContext
            stateManager.ClearConnectionTransientState ()
            SimIndex.reloadConnections index
            ConnectionReload.invalidateOrphanedGoingWorks connectionReloadContext
            ConnectionReload.invalidateOrphanedTokenHolders connectionReloadContext
            scheduleConditionEvaluation ())

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
        member this.ForceWorkState(wg, ns) = this.ForceWorkState(wg, ns)
        member this.ForceCallState(cg, ns) = this.ForceCallState(cg, ns)
        member this.GetWorkState(wg) = this.GetWorkState(wg)
        member this.GetCallState(cg) = this.GetCallState(cg)
        member this.SetAllFlowStates(tag) = this.SetAllFlowStates(tag)
        member this.GetFlowState(fg) = this.GetFlowState(fg)
        member this.HasStartableWork = this.HasStartableWork
        member this.HasActiveDuration = this.HasActiveDuration
        member this.Step() = this.Step()
        member this.CanAdvanceStep(selectedSourceGuid, autoStartSources) =
            this.CanAdvanceStep(selectedSourceGuid, autoStartSources)
        member this.StepWithSourcePriming(selectedSourceGuid, autoStartSources) =
            this.StepWithSourcePriming(selectedSourceGuid, autoStartSources)
        member this.ReloadConnections() = this.ReloadConnections()
        member this.SpeedMultiplier
            with get() = this.SpeedMultiplier
            and set(v) = this.SpeedMultiplier <- v
        member this.TimeIgnore
            with get() = this.TimeIgnore
            and set(v) = this.TimeIgnore <- v
        member this.InjectIOValue(a, v) = this.InjectIOValue(a, v)
        member this.NextToken() = this.NextToken()
        member this.SeedToken(wg, v) = this.SeedToken(wg, v)
        member this.DiscardToken(wg) = this.DiscardToken(wg)
        member this.GetWorkToken(wg) = this.GetWorkToken(wg)
        member this.GetTokenOrigin(t) = this.GetTokenOrigin(t)
        [<CLIEvent>] member this.WorkStateChanged = this.WorkStateChanged
        [<CLIEvent>] member this.CallStateChanged = this.CallStateChanged
        [<CLIEvent>] member this.SimulationStatusChanged = this.SimulationStatusChanged
        [<CLIEvent>] member this.TokenEvent = this.TokenEvent
        member this.Dispose() = this.Stop()
