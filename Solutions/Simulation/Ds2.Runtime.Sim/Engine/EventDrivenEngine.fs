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

    let disposeCurrentCts () =
        cts |> Option.iter (fun c -> c.Dispose())
        cts <- None

    let stateManager = StateManager(index, index.TickMs)

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

    // ── IO값 관리 (EventPublisher 인라인) ────────────────────────────

    /// Call F 시 ApiCall의 InputSpec을 IO값으로 설정
    let setCallIOValues (callGuid: Guid) =
        SimIndex.findOrEmpty callGuid index.CallApiCallGuids
        |> List.iter (fun apiCallId ->
            DsQuery.getApiCall apiCallId index.Store
            |> Option.iter (fun apiCall ->
                stateManager.SetIOValue(apiCallId, ValueSpec.toDefaultString apiCall.InputSpec)))

    /// Call F → RxWork에 IO값 설정 (RxGuid가 있는 ApiCall만)
    let setRxWorkIOValues (callGuid: Guid) =
        SimIndex.findOrEmpty callGuid index.CallApiCallGuids
        |> List.iter (fun apiCallId ->
            DsQuery.getApiCall apiCallId index.Store
            |> Option.iter (fun apiCall ->
                let hasRx =
                    apiCall.ApiDefId
                    |> Option.bind (fun defId -> DsQuery.getApiDef defId index.Store)
                    |> Option.bind (fun def -> def.Properties.RxGuid)
                    |> Option.isSome
                if hasRx then
                    stateManager.SetIOValue(apiCallId, ValueSpec.toDefaultString apiCall.InputSpec)))

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
    let hasNoSuccessors workGuid = TokenFlow.hasNoSuccessors tokenFlowContext workGuid
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
    }

    let scheduleWorkIfReady workGuid targetState =
        WorkTransitions.scheduleWorkIfReady workTransitionContext workGuid targetState

    let applyWorkTransition workGuid newState =
        WorkTransitions.applyWorkTransition workTransitionContext workGuid newState

    // ── Call 상태 전이 ──

    let applyCallTransition (callGuid: Guid) newState =
        let result = stateManager.ApplyCallTransition(callGuid, newState, shouldSkipCall)
        if not result.HasChanged then () else
        let clock = TimeSpan.FromMilliseconds(float scheduler.CurrentTimeMs)
        if result.ActualNewState = Status4.Finish then setCallIOValues callGuid
        callStateChangedEvent.Trigger({
            CallGuid = callGuid; CallName = result.NodeName
            PreviousState = result.OldState; NewState = result.ActualNewState; IsSkipped = result.IsSkipped; Clock = clock })
        match result.ActualNewState with
        | Status4.Going ->
            SimIndex.txWorkGuids index callGuid |> List.iter (fun txGuid -> scheduleWorkIfReady txGuid Status4.Going)
            scheduleConditionEvaluation ()
        | Status4.Finish ->
            if not result.IsSkipped then
                setRxWorkIOValues callGuid
                SimIndex.rxWorkGuids index callGuid |> List.iter (fun rxGuid -> scheduleWorkIfReady rxGuid Status4.Finish)
            scheduleConditionEvaluation ()
        | _ -> ()

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
        HasNoSuccessors = hasNoSuccessors
        CanReceiveToken = canReceiveToken
        ApplyWorkTransition = applyWorkTransition
    }

    let evaluateConditions () =
        ConditionEvaluation.evaluateConditions conditionEvaluationContext ()

    let clearAndApplyWorkTransition workGuid newState =
        stateManager.ClearWorkPending(workGuid)
        applyWorkTransition workGuid newState

    let clearAndApplyCallTransition callGuid newState =
        stateManager.ClearCallPending(callGuid)
        applyCallTransition callGuid newState

    let handleDurationComplete workGuid =
        if stateManager.GetWorkState(workGuid) = Status4.Going then
            stateManager.MarkMinDurationMet(workGuid)
            let callGuids = SimIndex.findOrEmpty workGuid index.WorkCallGuids
            if callGuids.IsEmpty then
                applyWorkTransition workGuid Status4.Finish
            else
                // Works with Calls: duration met, check if all calls also finished
                if callGuids |> List.forall (fun cg -> stateManager.GetCallState(cg) = Status4.Finish) then
                    applyWorkTransition workGuid Status4.Finish

    let runtimeContext : EventDrivenEngineRuntime.RuntimeContext = {
        Scheduler = scheduler
        GetStatus = (fun () -> Volatile.Read(&status))
        SpeedMultiplier = (fun () -> speedMultiplier)
        GetWorkState = stateManager.GetWorkState
        GetWorkToken = stateManager.GetWorkToken
        ClearAndApplyWorkTransition = clearAndApplyWorkTransition
        ClearAndApplyCallTransition = clearAndApplyCallTransition
        ApplyWorkTransition = applyWorkTransition
        HandleDurationComplete = handleDurationComplete
        ShiftToken = shiftToken
        EmitTokenEvent = emitTokenEvent
        ScheduleConditionEvaluation = scheduleConditionEvaluation
        EvaluateConditions = evaluateConditions
    }

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
            // 이전 스레드가 while 탈출 후 종료될 때까지 대기
            engineThread |> Option.iter (fun t -> t.Join(1000) |> ignore)
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
            engineThread |> Option.iter (fun t -> t.Join(1000) |> ignore)
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
            scheduler.ScheduleNow(ScheduledEventType.WorkTransition(workGuid, newState), ScheduledEvent.PriorityStateChange) |> ignore

    member _.ForceCallState(callGuid, newState) =
        if index.AllCallGuids |> List.contains callGuid then
            scheduler.ScheduleNow(ScheduledEventType.CallTransition(callGuid, newState), ScheduledEvent.PriorityStateChange) |> ignore

    member _.GetWorkState(workGuid) = stateManager.GetState().WorkStates.TryFind(workGuid)
    member _.GetCallState(callGuid) = stateManager.GetState().CallStates.TryFind(callGuid)

    member _.SpeedMultiplier
        with get() = speedMultiplier
        and set(m) = speedMultiplier <- max 0.1 (min 1000.0 m)

    member _.TimeIgnore
        with get() = timeIgnore
        and set(i) =
            let prev = timeIgnore
            timeIgnore <- i
            // 시뮬레이션 도중 TimeIgnore 켜면, 이미 스케줄된 Duration/Homing을 즉시 완료
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
        | Some _ -> ()  // 이미 차있으면 무시
        | None ->
            // TokenSpec Label 우선, 없으면 Work 이름 fallback
            let originLabel =
                DsQuery.getTokenSpecs index.Store
                |> List.tryFind (fun spec -> spec.WorkId = Some sourceWorkGuid)
                |> Option.map (fun spec -> spec.Label)
                |> Option.defaultWith (fun () -> workNameOf sourceWorkGuid)
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
