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
    let shiftToken workGuid token = TokenFlow.shiftToken tokenFlowContext workGuid token
    let onWorkFinish workGuid = TokenFlow.onWorkFinish tokenFlowContext workGuid

    // ── Duration 이벤트 추적 (Pause 시 취소/Resume 시 재스케줄) ──
    /// workGuid → (eventId, scheduledTimeMs)
    let mutable durationEvents = Map.empty<Guid, struct(Guid * int64)>
    /// Pause 시 저장된 남은 시간: workGuid → remainingMs
    let mutable pausedDurationRemaining = Map.empty<Guid, int64>
    let onDurationScheduled workGuid eventId scheduledTimeMs =
        durationEvents <- durationEvents.Add(workGuid, struct(eventId, scheduledTimeMs))

    let workTransitionContext : WorkTransitions.Context = {
        Index = index
        StateManager = stateManager
        Scheduler = scheduler
        TimeIgnore = (fun () -> timeIgnore)
        ScheduleConditionEvaluation = scheduleConditionEvaluation
        OnWorkFinish = onWorkFinish
        TriggerWorkStateChanged = workStateChangedEvent.Trigger
        OnDurationScheduled = onDurationScheduled
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
            // Pause 중 마지막 Call Finish → 해당 Work의 중단된 duration 재스케줄
            match index.CallWorkGuid |> Map.tryFind callGuid with
            | Some workGuid ->
                let callGuids = SimIndex.findOrEmpty workGuid index.WorkCallGuids
                if callGuids |> List.forall (fun cg -> stateManager.GetCallState(cg) = Status4.Finish) then
                    match pausedDurationRemaining |> Map.tryFind workGuid with
                    | Some remaining ->
                        let eventId =
                            scheduler.ScheduleAfter(
                                ScheduledEventType.DurationComplete workGuid,
                                (max 1L remaining),
                                ScheduledEvent.PriorityDurationCheck)
                        durationEvents <- durationEvents.Add(workGuid, struct(eventId, scheduler.CurrentTimeMs + remaining))
                        pausedDurationRemaining <- pausedDurationRemaining.Remove(workGuid)
                    | None -> ()
            | None -> ()
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
        CanReceiveToken = canReceiveToken
        ApplyWorkTransition = applyWorkTransition
    }

    let evaluateConditions () =
        ConditionEvaluation.evaluateConditions conditionEvaluationContext ()

    let clearAndApplyWorkTransition workGuid newState =
        stateManager.ClearWorkPending(workGuid)
        let shouldApply =
            match newState with
            | Status4.Going ->
                stateManager.GetWorkState(workGuid) = Status4.Ready
                && (
                    not (isActiveSystemWork workGuid)
                    || canStartWork workGuid
                )
            | Status4.Finish ->
                if stateManager.GetWorkState(workGuid) <> Status4.Going then false
                else
                    let callGuids = SimIndex.findOrEmpty workGuid index.WorkCallGuids
                    stateManager.IsMinDurationMet(workGuid)
                    && (callGuids |> List.forall (fun callGuid -> stateManager.GetCallState(callGuid) = Status4.Finish))
            | Status4.Homing ->
                stateManager.GetWorkState(workGuid) = Status4.Finish
                && WorkConditionChecker.canResetWork index (stateManager.GetState()) workGuid
            | Status4.Ready ->
                stateManager.GetWorkState(workGuid) = Status4.Homing
            | _ -> true

        if shouldApply then
            applyWorkTransition workGuid newState

    let clearAndApplyCallTransition callGuid newState =
        stateManager.ClearCallPending(callGuid)
        let shouldApply =
            match newState with
            | Status4.Going ->
                stateManager.GetCallState(callGuid) = Status4.Ready
                && canStartCall callGuid
            | Status4.Finish ->
                stateManager.GetCallState(callGuid) = Status4.Going
                && canCompleteCall callGuid
            | Status4.Homing ->
                match index.CallWorkGuid |> Map.tryFind callGuid with
                | Some workGuid -> stateManager.GetWorkState(workGuid) = Status4.Homing
                | None -> false
            | Status4.Ready ->
                match index.CallWorkGuid |> Map.tryFind callGuid with
                | Some workGuid -> stateManager.GetWorkState(workGuid) = Status4.Ready
                | None -> false
            | _ -> true

        if shouldApply then
            applyCallTransition callGuid newState

    let forceAndApplyWorkTransition workGuid newState =
        stateManager.ClearWorkPending(workGuid)
        applyWorkTransition workGuid newState

    let forceAndApplyCallTransition callGuid newState =
        stateManager.ClearCallPending(callGuid)
        applyCallTransition callGuid newState

    let hasGoingCall () =
        index.AllCallGuids
        |> List.exists (fun callGuid -> stateManager.GetCallState(callGuid) = Status4.Going)

    let removeConnectionSensitiveScheduledEvents () =
        scheduler.RemoveWhere(fun event ->
            match event.EventType with
            | ScheduledEventType.EvaluateConditions -> true
            | _ -> false)
        |> ignore

    /// 화살표 삭제 후 선행 조건이 사라진 Going Work를 Ready로 되돌림
    let revertOrphanedGoingWork workGuid =
        stateManager.ClearWorkPending(workGuid)
        // Duration 이벤트 취소
        match durationEvents |> Map.tryFind workGuid with
        | Some struct(eventId, _) ->
            scheduler.Cancel(eventId)
            durationEvents <- durationEvents.Remove(workGuid)
        | None -> ()
        pausedDurationRemaining <- pausedDurationRemaining.Remove(workGuid)
        // 토큰 회수
        match stateManager.GetWorkToken(workGuid) with
        | Some token ->
            stateManager.SetWorkToken(workGuid, None)
            emitTokenEvent Discard token workGuid None
        | None -> ()
        // Call 상태 초기화
        for callGuid in SimIndex.findOrEmpty workGuid index.WorkCallGuids do
            stateManager.ClearCallPending(callGuid)
            if stateManager.GetCallState(callGuid) <> Status4.Ready then
                stateManager.ForceCallState(callGuid, Status4.Ready)
        // Work → Ready
        stateManager.ClearMinDuration(workGuid)
        stateManager.ForceWorkState(workGuid, Status4.Ready)
        let wName = index.WorkName |> Map.tryFind workGuid |> Option.defaultValue (string workGuid)
        let clock = TimeSpan.FromMilliseconds(float scheduler.CurrentTimeMs)
        workStateChangedEvent.Trigger({
            WorkGuid = workGuid; WorkName = wName
            PreviousState = Status4.Going; NewState = Status4.Ready; Clock = clock })

    let memberHasValidStart (memberGuid: Guid) =
        let isSource =
            index.WorkTokenRole |> Map.tryFind memberGuid
            |> Option.map (fun r -> r.HasFlag(TokenRole.Source))
            |> Option.defaultValue false
        let preds = SimIndex.findOrEmpty memberGuid index.WorkStartPreds
        not preds.IsEmpty || isSource

    /// 새 topology에서 선행 노드가 사라진 Going Work를 되돌림 (ReferenceOf 그룹 인식)
    let invalidateOrphanedGoingWorks () =
        let processedCanonicals = System.Collections.Generic.HashSet<Guid>()
        for workGuid in index.AllWorkGuids do
            let canonical = SimIndex.canonicalWorkGuid index workGuid
            if not (processedCanonicals.Contains(canonical))
               && isActiveSystemWork workGuid
               && stateManager.GetWorkState(workGuid) = Status4.Going then
                processedCanonicals.Add(canonical) |> ignore
                let groupMembers = SimIndex.referenceGroupOf index workGuid
                // 그룹 내 어느 멤버든 유효한 선행 노드가 있으면 전체 그룹 유지
                let anyMemberJustified = groupMembers |> List.exists memberHasValidStart
                if not anyMemberJustified then
                    revertOrphanedGoingWork canonical

    let invalidateOrphanedTokenHolders () =
        for workGuid in index.AllWorkGuids do
            if isActiveSystemWork workGuid
               && not (index.TokenPathGuids.Contains workGuid)
               && not (index.TokenSinkGuids.Contains workGuid) then
                match stateManager.GetWorkToken(workGuid) with
                | Some token ->
                    stateManager.SetWorkToken(workGuid, None)
                    emitTokenEvent Discard token workGuid None
                | None -> ()

    let handleDurationComplete workGuid =
        durationEvents <- durationEvents.Remove(workGuid)
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
        ProcessGate = processGate
        Scheduler = scheduler
        GetStatus = (fun () -> Volatile.Read(&status))
        SpeedMultiplier = (fun () -> speedMultiplier)
        UpdateClock = stateManager.UpdateClock
        GetWorkState = stateManager.GetWorkState
        GetWorkToken = stateManager.GetWorkToken
        ClearAndApplyWorkTransition = clearAndApplyWorkTransition
        ClearAndApplyCallTransition = clearAndApplyCallTransition
        ForceAndApplyWorkTransition = forceAndApplyWorkTransition
        ForceAndApplyCallTransition = forceAndApplyCallTransition
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
            // Flow → System: Flow의 Work들 중 하나의 SystemName이 Active이면 Active Flow
            index.AllWorkGuids
            |> List.exists (fun wg ->
                index.WorkFlowGuid |> Map.tryFind wg = Some fg
                && index.WorkSystemName |> Map.tryFind wg
                   |> Option.map (fun sn -> index.ActiveSystemNames.Contains(sn))
                   |> Option.defaultValue false))

    member this.SetAllFlowStates(tag: FlowTag) =
        match tag with
        | FlowTag.Pause ->
            // Active Flow만 Pause + duration 취소/남은 시간 저장
            for fg in this.ActiveFlowGuids do
                stateManager.SetFlowState(fg, FlowTag.Pause)
            // Active Flow에 속한 Going Work의 duration 이벤트 취소
            // Going Call이 있으면 "진행 중인 STEP" → duration도 같이 흘러감 (취소 안 함)
            // Going Call 없고 미시작 Call 있으면 → 다음 STEP 대기 → duration 취소
            // 모든 Call Finish 또는 leaf → duration 계속 진행
            for wg in index.AllWorkGuids do
                if stateManager.GetWorkState(wg) = Status4.Going then
                    let callGuids = SimIndex.findOrEmpty wg index.WorkCallGuids
                    let hasGoingCall =
                        callGuids |> List.exists (fun cg -> stateManager.GetCallState(cg) = Status4.Going)
                    let hasUnfinishedCall =
                        callGuids |> List.exists (fun cg -> stateManager.GetCallState(cg) <> Status4.Finish)
                    if not hasGoingCall && hasUnfinishedCall then
                        match index.WorkFlowGuid |> Map.tryFind wg with
                        | Some fg when stateManager.GetFlowState(fg) = FlowTag.Pause ->
                            match durationEvents |> Map.tryFind wg with
                            | Some struct(eventId, scheduledTimeMs) ->
                                scheduler.Cancel(eventId)
                                let remaining = max 0L (scheduledTimeMs - scheduler.CurrentTimeMs)
                                pausedDurationRemaining <- pausedDurationRemaining.Add(wg, remaining)
                                durationEvents <- durationEvents.Remove(wg)
                            | None -> ()
                        | _ -> ()
        | FlowTag.Drive ->
            // Active Flow만 Drive + 저장된 남은 시간으로 duration 재스케줄
            for fg in this.ActiveFlowGuids do
                stateManager.SetFlowState(fg, FlowTag.Drive)
            for kv in pausedDurationRemaining |> Map.toList do
                let wg, remaining = kv
                if stateManager.GetWorkState(wg) = Status4.Going then
                    let eventId =
                        scheduler.ScheduleAfter(
                            ScheduledEventType.DurationComplete wg,
                            (max 1L remaining),
                            ScheduledEvent.PriorityDurationCheck)
                    durationEvents <- durationEvents.Add(wg, struct(eventId, scheduler.CurrentTimeMs + remaining))
            pausedDurationRemaining <- Map.empty
        | _ ->
            // Ready 등: 전체 Flow 대상
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
            let canonicalSourceWorkGuid = SimIndex.canonicalWorkGuid index sourceWorkGuid
            // TokenSpec Label 우선, 없으면 Work 이름 fallback
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
        // 1) Ready Work 중 실제 시작 조건(predecessor+token) 충족하는 게 있으면 → STEP 가능
        let hasStartableReadyWork =
            index.AllWorkGuids
            |> List.exists (fun wg ->
                stateManager.GetWorkState(wg) = Status4.Ready
                && canStartWork wg)
        // 2) Going Work에 아직 끝나지 않은 Call이 있으면 → STEP으로 Call 진행 필요
        //    (Going leaf Work / 모든 Call Finish인 경우는 HasGoingCall로 처리됨)
        let hasGoingWorkWithPendingCalls =
            index.AllWorkGuids
            |> List.exists (fun wg ->
                stateManager.GetWorkState(wg) = Status4.Going
                && (let callGuids = SimIndex.findOrEmpty wg index.WorkCallGuids
                    not callGuids.IsEmpty
                    && callGuids |> List.exists (fun cg -> stateManager.GetCallState(cg) <> Status4.Finish)))
        hasStartableReadyWork || hasGoingWorkWithPendingCalls

    member _.HasActiveDuration =
        // Pause에서도 duration timer가 취소되지 않는 Going Work:
        // leaf Work (Call 없음) 또는 모든 Call이 Finish인 Work
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
            removeConnectionSensitiveScheduledEvents ()
            stateManager.ClearConnectionTransientState ()
            SimIndex.reloadConnections index
            invalidateOrphanedGoingWorks ()
            invalidateOrphanedTokenHolders ()
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
