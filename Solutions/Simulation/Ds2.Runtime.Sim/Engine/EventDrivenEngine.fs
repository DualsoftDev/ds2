namespace Ds2.Runtime.Sim.Engine

open System
open System.Threading
open Ds2.Core
open Ds2.UI.Core
open Ds2.Runtime.Sim.Model
open Ds2.Runtime.Sim.Engine.Core
open Ds2.Runtime.Sim.Engine.Scheduler

/// 이벤트 기반 시뮬레이션 엔진
/// H 상태 구현: F->H (내부 Call R 정리) -> H->R (최소 1ms)
type EventDrivenEngine(index: SimIndex) =

    static let _log = log4net.LogManager.GetLogger(typeof<EventDrivenEngine>)

    let mutable status = Stopped
    let scheduler = EventScheduler()

    let mutable speedMultiplier = 1.0
    let mutable timeIgnore = false

    let mutable engineThread: Thread option = None
    let mutable cts: CancellationTokenSource option = None

    let stateManager = StateManager(index, index.TickMs)

    // UI 이벤트
    let workStateChangedEvent = Event<WorkStateChangedArgs>()
    let callStateChangedEvent = Event<CallStateChangedArgs>()
    let simulationStatusChangedEvent = Event<SimulationStatusChangedArgs>()

    // 헬퍼
    let canStartWork wg = WorkConditionChecker.canStartWork index (stateManager.GetState()) wg
    let canResetWork wg = WorkConditionChecker.canResetWork index (stateManager.GetState()) wg
    let canStartCall cg = WorkConditionChecker.canStartCall index (stateManager.GetState()) cg
    let canCompleteCall cg = WorkConditionChecker.canCompleteCall index (stateManager.GetState()) cg
    let shouldSkipCall cg = WorkConditionChecker.shouldSkipCall index (stateManager.GetState()) cg

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

    // ── 상태 전이 ────────────────────────────────────────────────────

    // ── 공용 헬퍼 ──

    let scheduleCallTransitions workGuid targetState excludeState =
        let callGuids = SimIndex.findOrEmpty workGuid index.WorkCallGuids
        for cg in callGuids do
            let cs = stateManager.GetCallState(cg)
            if cs <> targetState && cs <> excludeState && not (stateManager.IsCallPending(cg)) then
                stateManager.MarkCallPending(cg)
                scheduler.ScheduleNow(ScheduledEventType.CallTransition(cg, targetState), ScheduledEvent.PriorityStateChange) |> ignore

    let scheduleWorkIfReady workGuid targetState =
        if stateManager.GetWorkState(workGuid) = Status4.Ready && not (stateManager.IsWorkPending(workGuid)) then
            stateManager.MarkWorkPending(workGuid)
            scheduler.ScheduleNow(ScheduledEventType.WorkTransition(workGuid, targetState), ScheduledEvent.PriorityStateChange) |> ignore

    // ── Work 상태 전이 ──

    let applyWorkTransition (workGuid: Guid) newState =
        let result = stateManager.ApplyWorkTransition(workGuid, newState)
        if not result.HasChanged then () else
        let clock = TimeSpan.FromMilliseconds(float scheduler.CurrentTimeMs)
        workStateChangedEvent.Trigger({
            WorkGuid = workGuid; WorkName = result.NodeName
            PreviousState = result.OldState; NewState = result.ActualNewState; Clock = clock })
        match result.ActualNewState with
        | Status4.Going ->
            let callGuids = SimIndex.findOrEmpty workGuid index.WorkCallGuids
            if callGuids.IsEmpty then
                let duration = index.WorkDuration |> Map.tryFind workGuid |> Option.defaultValue 0.0
                if timeIgnore then
                    scheduler.ScheduleNow(ScheduledEventType.DurationComplete workGuid, ScheduledEvent.PriorityDurationCheck) |> ignore
                else
                    let delayMs = max 1L (int64 duration)
                    scheduler.ScheduleAfter(ScheduledEventType.DurationComplete workGuid, delayMs, ScheduledEvent.PriorityDurationCheck) |> ignore
            scheduler.ScheduleNow(ScheduledEventType.EvaluateConditions, ScheduledEvent.PriorityConditionEval) |> ignore
        | Status4.Finish ->
            scheduler.ScheduleNow(ScheduledEventType.EvaluateConditions, ScheduledEvent.PriorityConditionEval) |> ignore
        | Status4.Homing ->
            scheduleCallTransitions workGuid Status4.Homing Status4.Homing
            scheduler.ScheduleAfter(ScheduledEventType.HomingComplete workGuid, 1L, ScheduledEvent.PriorityStateChange) |> ignore
        | Status4.Ready ->
            scheduleCallTransitions workGuid Status4.Ready Status4.Ready
        | _ -> ()

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
            scheduler.ScheduleNow(ScheduledEventType.EvaluateConditions, ScheduledEvent.PriorityConditionEval) |> ignore
        | Status4.Finish ->
            if not result.IsSkipped then
                setRxWorkIOValues callGuid
                SimIndex.rxWorkGuids index callGuid |> List.iter (fun rxGuid -> scheduleWorkIfReady rxGuid Status4.Finish)
            scheduler.ScheduleNow(ScheduledEventType.EvaluateConditions, ScheduledEvent.PriorityConditionEval) |> ignore
        | _ -> ()

    // ── 조건 평가 ────────────────────────────────────────────────────

    let evaluateConditions () =
        // 시작 가능한 Work
        for workGuid in index.AllWorkGuids do
            if stateManager.GetWorkState(workGuid) = Status4.Ready && canStartWork workGuid && not (stateManager.IsWorkPending(workGuid)) then
                stateManager.MarkWorkPending(workGuid)
                scheduler.ScheduleNow(ScheduledEventType.WorkTransition(workGuid, Status4.Going), ScheduledEvent.PriorityStateChange) |> ignore

        // 리셋 가능한 Work (F->H, 라이징 에지)
        for workGuid in index.AllWorkGuids do
            if stateManager.GetWorkState(workGuid) = Status4.Finish && not (stateManager.IsWorkPending(workGuid)) then
                match Map.tryFind workGuid index.WorkSystemName, Map.tryFind workGuid index.WorkName with
                | Some sysName, Some wName ->
                    let allSameKeyPreds =
                        index.AllWorkGuids
                        |> List.filter (fun wg ->
                            Map.tryFind wg index.WorkSystemName = Some sysName &&
                            Map.tryFind wg index.WorkName = Some wName)
                        |> List.collect (fun wg -> SimIndex.findOrEmpty wg index.WorkResetPreds)
                        |> List.distinct
                    if not allSameKeyPreds.IsEmpty then
                        let targetKey = (sysName, wName)
                        let untriggered =
                            allSameKeyPreds |> List.tryFind (fun pg ->
                                match Map.tryFind pg index.WorkSystemName, Map.tryFind pg index.WorkName with
                                | Some pSys, Some pName ->
                                    stateManager.GetWorkState(pg) = Status4.Going &&
                                    not (stateManager.IsResetTriggered((pSys, pName), targetKey))
                                | _ -> false)
                        match untriggered with
                        | Some pg ->
                            match Map.tryFind pg index.WorkSystemName, Map.tryFind pg index.WorkName with
                            | Some pSys, Some pName ->
                                stateManager.AddResetTrigger((pSys, pName), targetKey)
                                stateManager.MarkWorkPending(workGuid)
                                scheduler.ScheduleNow(ScheduledEventType.WorkTransition(workGuid, Status4.Homing), ScheduledEvent.PriorityStateChange) |> ignore
                            | _ -> ()
                        | None -> ()
                | _ -> ()

        // 시작 가능한 Call
        for callGuid in index.AllCallGuids do
            let callWork = index.CallWorkGuid |> Map.tryFind callGuid
            match callWork with
            | Some workGuid ->
                if stateManager.GetCallState(callGuid) = Status4.Ready
                   && stateManager.GetWorkState(workGuid) = Status4.Going
                   && canStartCall callGuid
                   && not (stateManager.IsCallPending(callGuid)) then
                    stateManager.MarkCallPending(callGuid)
                    scheduler.ScheduleNow(ScheduledEventType.CallTransition(callGuid, Status4.Going), ScheduledEvent.PriorityStateChange) |> ignore
            | None -> ()

        // 완료 가능한 Call
        for callGuid in index.AllCallGuids do
            if stateManager.GetCallState(callGuid) = Status4.Going && canCompleteCall callGuid && not (stateManager.IsCallPending(callGuid)) then
                stateManager.MarkCallPending(callGuid)
                scheduler.ScheduleNow(ScheduledEventType.CallTransition(callGuid, Status4.Finish), ScheduledEvent.PriorityStateChange) |> ignore

        // 모든 Call 완료된 Work -> F
        for workGuid in index.AllWorkGuids do
            if stateManager.GetWorkState(workGuid) = Status4.Going && not (stateManager.IsWorkPending(workGuid)) then
                let callGuids = SimIndex.findOrEmpty workGuid index.WorkCallGuids
                if not callGuids.IsEmpty then
                    if callGuids |> List.forall (fun cg -> stateManager.GetCallState(cg) = Status4.Finish) then
                        stateManager.MarkWorkPending(workGuid)
                        scheduler.ScheduleNow(ScheduledEventType.WorkTransition(workGuid, Status4.Finish), ScheduledEvent.PriorityStateChange) |> ignore

    // ── 이벤트 처리 ──────────────────────────────────────────────────

    let processEvent (event: ScheduledEvent) =
        match event.EventType with
        | ScheduledEventType.WorkTransition(wg, ts) ->
            stateManager.ClearWorkPending(wg)
            applyWorkTransition wg ts
        | ScheduledEventType.CallTransition(cg, ts) ->
            stateManager.ClearCallPending(cg)
            applyCallTransition cg ts
        | ScheduledEventType.DurationComplete wg ->
            if stateManager.GetWorkState(wg) = Status4.Going then
                if (SimIndex.findOrEmpty wg index.WorkCallGuids).IsEmpty then
                    applyWorkTransition wg Status4.Finish
        | ScheduledEventType.HomingComplete wg ->
            if stateManager.GetWorkState(wg) = Status4.Homing then
                applyWorkTransition wg Status4.Ready
        | ScheduledEventType.EvaluateConditions ->
            evaluateConditions()

    // ── 시뮬레이션 루프 ──────────────────────────────────────────────

    let simulationLoop (ct: CancellationToken) =
        let mutable lastRealTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

        while not ct.IsCancellationRequested && status = Running do
            let nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            let realDelta = nowMs - lastRealTimeMs
            lastRealTimeMs <- nowMs

            let simDelta = int64 (float realDelta * speedMultiplier)
            let targetMs = scheduler.CurrentTimeMs + simDelta

            for event in scheduler.AdvanceTo(targetMs) do
                if status = Running then processEvent event

            Thread.Sleep(1)

    // ===== Public API =====

    member _.State = stateManager.GetState()
    member _.Status = status
    member _.Index = index

    [<CLIEvent>] member _.WorkStateChanged = workStateChangedEvent.Publish
    [<CLIEvent>] member _.CallStateChanged = callStateChangedEvent.Publish
    [<CLIEvent>] member _.SimulationStatusChanged = simulationStatusChangedEvent.Publish

    member this.Start() =
        if status = Running then () else
        status <- Running
        simulationStatusChangedEvent.Trigger({ PreviousStatus = Stopped; NewStatus = Running })
        scheduler.ScheduleNow(ScheduledEventType.EvaluateConditions, ScheduledEvent.PriorityConditionEval) |> ignore
        let tokenSource = new CancellationTokenSource()
        cts <- Some tokenSource
        let t = Thread(ThreadStart(fun () -> simulationLoop tokenSource.Token))
        t.IsBackground <- true; t.Name <- "EventDrivenEngine"; t.Start()
        engineThread <- Some t

    member _.Pause() =
        if status = Running then
            status <- Paused
            simulationStatusChangedEvent.Trigger({ PreviousStatus = Running; NewStatus = Paused })

    member _.Resume() =
        if status = Paused then
            status <- Running
            simulationStatusChangedEvent.Trigger({ PreviousStatus = Paused; NewStatus = Running })
            // Pause 시 while 루프가 종료되어 스레드가 죽었으므로 새 스레드 시작
            let tokenSource = new CancellationTokenSource()
            cts <- Some tokenSource
            let t = Thread(ThreadStart(fun () -> simulationLoop tokenSource.Token))
            t.IsBackground <- true; t.Name <- "EventDrivenEngine"; t.Start()
            engineThread <- Some t

    member _.Stop() =
        if status <> Stopped then
            status <- Stopped
            cts |> Option.iter (fun c -> c.Cancel())
            engineThread |> Option.iter (fun t -> t.Join(1000) |> ignore)
            cts <- None; engineThread <- None
            simulationStatusChangedEvent.Trigger({ PreviousStatus = Running; NewStatus = Stopped })

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

    member _.SetSpeedMultiplier(m) = speedMultiplier <- max 0.1 (min 1000.0 m)
    member _.SetTimeIgnore(i) = timeIgnore <- i
    member _.GetSpeedMultiplier() = speedMultiplier
    member _.GetTimeIgnore() = timeIgnore

    member _.InjectIOValue(apiCallGuid, value) =
        stateManager.SetIOValue(apiCallGuid, value)
        scheduler.ScheduleNow(ScheduledEventType.EvaluateConditions, ScheduledEvent.PriorityConditionEval) |> ignore

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
        member this.SetSpeedMultiplier(m) = this.SetSpeedMultiplier(m)
        member this.GetSpeedMultiplier() = this.GetSpeedMultiplier()
        member this.SetTimeIgnore(i) = this.SetTimeIgnore(i)
        member this.GetTimeIgnore() = this.GetTimeIgnore()
        member this.InjectIOValue(a, v) = this.InjectIOValue(a, v)
        [<CLIEvent>] member this.WorkStateChanged = this.WorkStateChanged
        [<CLIEvent>] member this.CallStateChanged = this.CallStateChanged
        [<CLIEvent>] member this.SimulationStatusChanged = this.SimulationStatusChanged
        member this.Dispose() = this.Stop()
