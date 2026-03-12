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
        index.CallApiCallGuids |> Map.tryFind callGuid |> Option.defaultValue []
        |> List.iter (fun apiCallId ->
            DsQuery.getApiCall apiCallId index.Store
            |> Option.iter (fun apiCall ->
                let value = ValueSpecEvaluator.toDefaultString apiCall.InputSpec
                stateManager.SetIOValue(apiCallId, value)))

    /// Call F → RxWork에 IO값 설정
    let setRxWorkIOValues (callGuid: Guid) =
        index.CallApiCallGuids |> Map.tryFind callGuid |> Option.defaultValue []
        |> List.iter (fun apiCallId ->
            DsQuery.getApiCall apiCallId index.Store
            |> Option.iter (fun apiCall ->
                match apiCall.ApiDefId with
                | Some apiDefId ->
                    DsQuery.getApiDef apiDefId index.Store
                    |> Option.iter (fun apiDef ->
                        apiDef.Properties.RxGuid |> Option.iter (fun _rxGuid ->
                            let value = ValueSpecEvaluator.toDefaultString apiCall.InputSpec
                            stateManager.SetIOValue(apiCallId, value)))
                | None -> ()))

    // ── 상태 전이 ────────────────────────────────────────────────────

    let applyTransition nodeType (nodeGuid: Guid) newState =
        let result = stateManager.ApplyTransition(nodeType, nodeGuid, newState, shouldSkipCall)
        if not result.HasChanged then () else

        let oldState = result.OldState
        let actualNewState = result.ActualNewState
        let isSkipped = result.IsSkipped
        let clock = TimeSpan.FromMilliseconds(float scheduler.CurrentTimeMs)

        // Call F -> IOValues 업데이트
        if nodeType = NodeTypeCall && actualNewState = Status4.Finish then
            setCallIOValues nodeGuid

        // UI 이벤트 (Clock 포함)
        match nodeType with
        | NodeTypeWork ->
            workStateChangedEvent.Trigger({
                WorkGuid = nodeGuid; WorkName = result.NodeName
                PreviousState = oldState; NewState = actualNewState; Clock = clock })
        | NodeTypeCall ->
            callStateChangedEvent.Trigger({
                CallGuid = nodeGuid; CallName = result.NodeName
                PreviousState = oldState; NewState = actualNewState; IsSkipped = isSkipped; Clock = clock })
        | _ -> ()

        // 후속 이벤트 스케줄
        match nodeType, actualNewState with
        | NodeTypeWork, s when s = Status4.Going ->
            let callGuids = index.WorkCallGuids |> Map.tryFind nodeGuid |> Option.defaultValue []
            if callGuids.IsEmpty then
                let duration = index.WorkDuration |> Map.tryFind nodeGuid |> Option.defaultValue 0.0
                if timeIgnore then
                    scheduler.ScheduleNow(ScheduledEventType.DurationComplete nodeGuid, ScheduledEvent.PriorityDurationCheck) |> ignore
                else
                    let delayMs = max 1L (int64 duration)
                    scheduler.ScheduleAfter(ScheduledEventType.DurationComplete nodeGuid, delayMs, ScheduledEvent.PriorityDurationCheck) |> ignore
            scheduler.ScheduleNow(ScheduledEventType.EvaluateConditions, ScheduledEvent.PriorityConditionEval) |> ignore

        | NodeTypeWork, s when s = Status4.Finish ->
            scheduler.ScheduleNow(ScheduledEventType.EvaluateConditions, ScheduledEvent.PriorityConditionEval) |> ignore

        | NodeTypeWork, s when s = Status4.Homing ->
            // H 진입: 내부 Call들을 R로 정리
            let callGuids = index.WorkCallGuids |> Map.tryFind nodeGuid |> Option.defaultValue []
            for callGuid in callGuids do
                if stateManager.GetCallState(callGuid) <> Status4.Ready && not (stateManager.IsPending(NodeTypeCall, callGuid)) then
                    stateManager.MarkPending(NodeTypeCall, callGuid)
                    scheduler.ScheduleNow(ScheduledEventType.CallTransition(callGuid, Status4.Ready), ScheduledEvent.PriorityStateChange) |> ignore
            // H->R 스케줄 (최소 1ms)
            scheduler.ScheduleAfter(ScheduledEventType.HomingComplete nodeGuid, 1L, ScheduledEvent.PriorityStateChange) |> ignore

        | NodeTypeWork, s when s = Status4.Ready ->
            // 내부 Call 리셋
            let callGuids = index.WorkCallGuids |> Map.tryFind nodeGuid |> Option.defaultValue []
            for callGuid in callGuids do
                if stateManager.GetCallState(callGuid) <> Status4.Ready && not (stateManager.IsPending(NodeTypeCall, callGuid)) then
                    stateManager.MarkPending(NodeTypeCall, callGuid)
                    scheduler.ScheduleNow(ScheduledEventType.CallTransition(callGuid, Status4.Ready), ScheduledEvent.PriorityStateChange) |> ignore

        | NodeTypeCall, s when s = Status4.Going ->
            // TxWork G 스케줄
            index.CallApiCallGuids |> Map.tryFind nodeGuid |> Option.defaultValue []
            |> List.iter (fun apiCallId ->
                DsQuery.getApiCall apiCallId index.Store
                |> Option.iter (fun apiCall ->
                    match apiCall.ApiDefId with
                    | Some apiDefId ->
                        DsQuery.getApiDef apiDefId index.Store
                        |> Option.iter (fun apiDef ->
                            apiDef.Properties.TxGuid |> Option.iter (fun txGuid ->
                                if stateManager.GetWorkState(txGuid) = Status4.Ready && not (stateManager.IsPending(NodeTypeWork, txGuid)) then
                                    stateManager.MarkPending(NodeTypeWork, txGuid)
                                    scheduler.ScheduleNow(ScheduledEventType.WorkTransition(txGuid, Status4.Going), ScheduledEvent.PriorityStateChange) |> ignore))
                    | None -> ()))
            scheduler.ScheduleNow(ScheduledEventType.EvaluateConditions, ScheduledEvent.PriorityConditionEval) |> ignore

        | NodeTypeCall, s when s = Status4.Finish ->
            // RxWork F 스케줄
            if not isSkipped then
                setRxWorkIOValues nodeGuid
                index.CallApiCallGuids |> Map.tryFind nodeGuid |> Option.defaultValue []
                |> List.iter (fun apiCallId ->
                    DsQuery.getApiCall apiCallId index.Store
                    |> Option.iter (fun apiCall ->
                        match apiCall.ApiDefId with
                        | Some apiDefId ->
                            DsQuery.getApiDef apiDefId index.Store
                            |> Option.iter (fun apiDef ->
                                apiDef.Properties.RxGuid |> Option.iter (fun rxGuid ->
                                    if stateManager.GetWorkState(rxGuid) = Status4.Ready && not (stateManager.IsPending(NodeTypeWork, rxGuid)) then
                                        stateManager.MarkPending(NodeTypeWork, rxGuid)
                                        scheduler.ScheduleNow(ScheduledEventType.WorkTransition(rxGuid, Status4.Finish), ScheduledEvent.PriorityStateChange) |> ignore))
                        | None -> ()))
            scheduler.ScheduleNow(ScheduledEventType.EvaluateConditions, ScheduledEvent.PriorityConditionEval) |> ignore
        | _ -> ()

    // ── 조건 평가 ────────────────────────────────────────────────────

    let evaluateConditions () =
        // 시작 가능한 Work
        for workGuid in index.AllWorkGuids do
            if stateManager.GetWorkState(workGuid) = Status4.Ready && canStartWork workGuid && not (stateManager.IsPending(NodeTypeWork, workGuid)) then
                stateManager.MarkPending(NodeTypeWork, workGuid)
                scheduler.ScheduleNow(ScheduledEventType.WorkTransition(workGuid, Status4.Going), ScheduledEvent.PriorityStateChange) |> ignore

        // 리셋 가능한 Work (F->H, 라이징 에지)
        for workGuid in index.AllWorkGuids do
            if stateManager.GetWorkState(workGuid) = Status4.Finish && not (stateManager.IsPending(NodeTypeWork, workGuid)) then
                match index.WorkSystemName |> Map.tryFind workGuid, index.WorkName |> Map.tryFind workGuid with
                | Some sysName, Some wName ->
                    let allSameKeyPreds =
                        index.AllWorkGuids
                        |> List.filter (fun wg ->
                            index.WorkSystemName |> Map.tryFind wg = Some sysName &&
                            index.WorkName |> Map.tryFind wg = Some wName)
                        |> List.collect (fun wg -> index.WorkResetPreds |> Map.tryFind wg |> Option.defaultValue [])
                        |> List.distinct
                    if not allSameKeyPreds.IsEmpty then
                        let targetKey = (sysName, wName)
                        let untriggered =
                            allSameKeyPreds |> List.tryFind (fun pg ->
                                match index.WorkSystemName |> Map.tryFind pg, index.WorkName |> Map.tryFind pg with
                                | Some pSys, Some pName ->
                                    stateManager.GetWorkState(pg) = Status4.Going &&
                                    not (stateManager.IsResetTriggered((pSys, pName), targetKey))
                                | _ -> false)
                        match untriggered with
                        | Some pg ->
                            match index.WorkSystemName |> Map.tryFind pg, index.WorkName |> Map.tryFind pg with
                            | Some pSys, Some pName ->
                                stateManager.AddResetTrigger((pSys, pName), targetKey)
                                stateManager.MarkPending(NodeTypeWork, workGuid)
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
                   && not (stateManager.IsPending(NodeTypeCall, callGuid)) then
                    stateManager.MarkPending(NodeTypeCall, callGuid)
                    scheduler.ScheduleNow(ScheduledEventType.CallTransition(callGuid, Status4.Going), ScheduledEvent.PriorityStateChange) |> ignore
            | None -> ()

        // 완료 가능한 Call
        for callGuid in index.AllCallGuids do
            if stateManager.GetCallState(callGuid) = Status4.Going && canCompleteCall callGuid && not (stateManager.IsPending(NodeTypeCall, callGuid)) then
                stateManager.MarkPending(NodeTypeCall, callGuid)
                scheduler.ScheduleNow(ScheduledEventType.CallTransition(callGuid, Status4.Finish), ScheduledEvent.PriorityStateChange) |> ignore

        // 모든 Call 완료된 Work -> F
        for workGuid in index.AllWorkGuids do
            if stateManager.GetWorkState(workGuid) = Status4.Going && not (stateManager.IsPending(NodeTypeWork, workGuid)) then
                let callGuids = index.WorkCallGuids |> Map.tryFind workGuid |> Option.defaultValue []
                if not callGuids.IsEmpty then
                    if callGuids |> List.forall (fun cg -> stateManager.GetCallState(cg) = Status4.Finish) then
                        stateManager.MarkPending(NodeTypeWork, workGuid)
                        scheduler.ScheduleNow(ScheduledEventType.WorkTransition(workGuid, Status4.Finish), ScheduledEvent.PriorityStateChange) |> ignore

    // ── 이벤트 처리 ──────────────────────────────────────────────────

    let processEvent (event: ScheduledEvent) =
        match event.EventType with
        | ScheduledEventType.WorkTransition(wg, ts) ->
            stateManager.ClearPending(NodeTypeWork, wg)
            applyTransition NodeTypeWork wg ts
        | ScheduledEventType.CallTransition(cg, ts) ->
            stateManager.ClearPending(NodeTypeCall, cg)
            applyTransition NodeTypeCall cg ts
        | ScheduledEventType.DurationComplete wg ->
            if stateManager.GetWorkState(wg) = Status4.Going then
                let callGuids = index.WorkCallGuids |> Map.tryFind wg |> Option.defaultValue []
                if callGuids.IsEmpty then applyTransition NodeTypeWork wg Status4.Finish
        | ScheduledEventType.HomingComplete wg ->
            if stateManager.GetWorkState(wg) = Status4.Homing then
                applyTransition NodeTypeWork wg Status4.Ready
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
                applyTransition NodeTypeWork workGuid Status4.Homing
        for workGuid in index.AllWorkGuids do
            let ws = stateManager.GetWorkState(workGuid)
            if ws <> Status4.Ready && ws <> Status4.Homing then
                stateManager.ForceWorkState(workGuid, Status4.Ready)
        for callGuid in index.AllCallGuids do
            if stateManager.GetCallState(callGuid) <> Status4.Ready then
                stateManager.ForceCallState(callGuid, Status4.Ready)
        for workGuid in index.AllWorkGuids do
            if stateManager.GetWorkState(workGuid) = Status4.Homing then
                applyTransition NodeTypeWork workGuid Status4.Ready
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
