namespace Ds2.Runtime.Engine

open System
open System.Threading
open Ds2.Core
open Ds2.Core.Store
open Ds2.Runtime.IO
open Ds2.Runtime.Model
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Engine.Scheduler

module internal EventDrivenCompositionContext =

    let createTokenFlowContext
        (index: SimIndex)
        (stateManager: StateManager)
        (scheduler: EventScheduler)
        triggerTokenEvent
        : TokenFlow.Context = {
        Index = index
        StateManager = stateManager
        CurrentTimeMs = (fun () -> scheduler.CurrentTimeMs)
        TriggerTokenEvent = triggerTokenEvent
    }

    /// v10 §10 — sensing append 처리 추적. Work 별 *append 적용 여부* flag.
    /// 첫 DurationComplete 시 *Append ms 만큼 재 schedule*, 두 번째 호출 시 *Finish 인정*.
    let private sensingAppendApplied = System.Collections.Concurrent.ConcurrentDictionary<Guid, bool>()

    let createWorkTransitionContext
        (index: SimIndex)
        (stateManager: StateManager)
        (scheduler: EventScheduler)
        runtimeMode
        getIsHomingPhase
        getTimeIgnore
        scheduleConditionEvaluation
        onWorkFinish
        triggerWorkStateChanged
        onDurationScheduled
        shouldSkipWork
        : WorkTransitions.Context = {
        Index = index
        StateManager = stateManager
        Scheduler = scheduler
        RuntimeMode = runtimeMode
        IsHomingPhase = getIsHomingPhase
        TimeIgnore = getTimeIgnore
        ScheduleConditionEvaluation = scheduleConditionEvaluation
        OnWorkFinish = onWorkFinish
        TriggerWorkStateChanged = triggerWorkStateChanged
        OnDurationScheduled = onDurationScheduled
        ClearSensingAppendDelay = fun workGuid ->
            sensingAppendApplied.TryRemove(workGuid) |> ignore
        ShouldSkipWork = shouldSkipWork
    }

    /// v10 §7 — Device 단위 Latch 추적. ApiDef.ParentId(=DeviceId) → currently active Latched ApiCall 들.
    /// 새 ApiCall 진입 시 기존 등록 ApiCall 들의 OutTag 를 "false" 로 송출 + 제거.
    type private LatchRegistry() =
        let lock = obj()
        // (deviceId, apiCallId) → IOTag (Latched 출력 주소)
        let active = System.Collections.Generic.Dictionary<Guid, System.Collections.Generic.Dictionary<Guid, IOTag>>()

        member _.ResetPriorOnDevice (deviceId: Guid, currentApiCallId: Guid, writeTag: string -> string -> unit) =
            lock |> System.Threading.Monitor.Enter
            try
                match active.TryGetValue(deviceId) with
                | true, devActive ->
                    let toReset =
                        devActive
                        |> Seq.filter (fun kv -> kv.Key <> currentApiCallId)
                        |> Seq.map (fun kv -> kv.Key, kv.Value)
                        |> List.ofSeq
                    for (priorId, tag) in toReset do
                        writeTag tag.Address "false"
                        devActive.Remove(priorId) |> ignore
                | _ -> ()
            finally
                lock |> System.Threading.Monitor.Exit

        member _.Register (deviceId: Guid, apiCallId: Guid, tag: IOTag) =
            lock |> System.Threading.Monitor.Enter
            try
                let devActive =
                    match active.TryGetValue(deviceId) with
                    | true, d -> d
                    | _ ->
                        let d = System.Collections.Generic.Dictionary<Guid, IOTag>()
                        active.[deviceId] <- d
                        d
                devActive.[apiCallId] <- tag
            finally
                lock |> System.Threading.Monitor.Exit

    let private latchRegistry = LatchRegistry()

    let createApiCallExecutionContext
        runtimeMode
        (index: SimIndex)
        (stateManager: StateManager)
        (scheduler: EventScheduler)
        resolveWorkName
        (ioMap: SignalIOMap)
        writeTag
        forceWorkState
        : ApiCallExecutionContext =
        let writeTagForRuntime address value =
            match runtimeMode with
            | RuntimeMode.Simulation ->
                ioMap.OutAddressToMappings
                |> Map.tryFind address
                |> Option.iter (fun mappings ->
                    mappings
                    |> List.iter (fun mapping ->
                        stateManager.SetOutputValue(mapping.ApiCallGuid, value)))
            | _ ->
                writeTag address value

        {
        RuntimeMode = runtimeMode
        GetDeviceState = stateManager.GetWorkState
        GetDeviceName = resolveWorkName
        GetTxOutAddresses = fun deviceWorkGuid ->
            ioMap.TxWorkToOutAddresses |> Map.tryFind deviceWorkGuid |> Option.defaultValue []
        GetApiCallsForWork = fun workGuid ->
            // v10 §11: Work GUID → 그 Work 를 TxGuid 로 가리키는 ApiDef + 그 ApiDef 를 참조하는 ApiCall 들.
            // SimIndex 에 직접 매핑이 없으므로 store 순회.
            let store = index.Store
            [ for kv in store.ApiDefs do
                let apiDef = kv.Value
                if apiDef.TxGuid = Some workGuid then
                    for callKv in store.Calls do
                        for apiCall in callKv.Value.ApiCalls do
                            if apiCall.ApiDefId = Some apiDef.Id then
                                yield (apiDef, apiCall) ]
        WriteTag = writeTagForRuntime
        ScheduleAfter = fun (delayMs, action) ->
            // v10 §11: TimePolicy.Append / EdgePulse 의 시간 지연 적용.
            // Control 모드는 wall-clock 기반 — Task.Delay 로 충분. Simulation 모드는 시뮬 step 이 별도 시간 모델.
            System.Threading.Tasks.Task.Run(fun () ->
                System.Threading.Tasks.Task.Delay(delayMs).Wait()
                action ()) |> ignore
        ScheduleOutputWrite = fun (delayMs, address, value) ->
            match runtimeMode with
            | RuntimeMode.Simulation ->
                scheduler.ScheduleAfter(
                    ScheduledEventType.OutputWrite(address, value),
                    int64 delayMs,
                    ScheduledEvent.PriorityStateChange) |> ignore
            | _ ->
                System.Threading.Tasks.Task.Run(fun () ->
                    System.Threading.Tasks.Task.Delay(delayMs).Wait()
                    writeTag address value) |> ignore
        ResetPriorLatchesOnDevice = fun (deviceId, apiCallId) ->
            latchRegistry.ResetPriorOnDevice (deviceId, apiCallId, writeTagForRuntime)
        RegisterLatch = fun (deviceId, apiCallId, tag) ->
            latchRegistry.Register (deviceId, apiCallId, tag)
        ForceWorkState = forceWorkState
    }

    /// v10 §11 — Call 단위 sensing append 적용 추적. callGuid → 적용 여부.
    /// Call 사이클 종료 시 (Status4.Ready 진입) flag clear.
    let private callSensingAppendApplied = System.Collections.Concurrent.ConcurrentDictionary<Guid, bool>()

    let createCallTransitionContext
        (index: SimIndex)
        (stateManager: StateManager)
        (scheduler: EventScheduler)
        runtimeMode
        shouldSkipCall
        triggerCallStateChanged
        scheduleWorkIfReady
        scheduleConditionEvaluation
        (durationTracker: DurationTracker)
        getTimeIgnore
        executeCallGoing
        : CallTransitions.Context = {
        Index = index
        StateManager = stateManager
        Scheduler = scheduler
        RuntimeMode = runtimeMode
        ShouldSkipCall = shouldSkipCall
        TriggerCallStateChanged = triggerCallStateChanged
        ScheduleWorkIfReady = scheduleWorkIfReady
        ScheduleConditionEvaluation = scheduleConditionEvaluation
        DurationTracker = durationTracker
        TimeIgnore = getTimeIgnore
        ExecuteCallGoing = executeCallGoing
        GetCallSensingAppendMs = fun callGuid ->
            // v10 §11: 이 Call 의 ApiCall 들이 참조하는 ApiDef.SensingType 의 Append ms 최대값.
            // Real(_, Some Append n) 만 Call 완료 안정화 시간이다.
            // Virtual(Some Append n) 은 Work DurationComplete 쪽에서 Duration+n 으로 처리한다.
            let store = index.Store
            SimIndex.findOrEmpty callGuid index.CallApiCallGuids
            |> List.choose (fun apiCallId ->
                Queries.getApiCall apiCallId store
                |> Option.bind (fun apiCall -> apiCall.ApiDefId)
                |> Option.bind (fun apiDefId -> Queries.getApiDef apiDefId store)
                |> Option.bind (fun apiDef ->
                    match apiDef.SensingType with
                    | SensingType.Real (_, Some (Append n)) -> Some n
                    | _ -> None))
            |> function
               | [] -> 0
               | xs -> List.max xs
        IsCallSensingAppendApplied = fun callGuid ->
            match callSensingAppendApplied.TryGetValue(callGuid) with
            | true, v -> v
            | _ -> false
        ApplyCallSensingAppendDelay = fun (callGuid, appendMs) ->
            callSensingAppendApplied.[callGuid] <- true
            // Call 사이클 종료 (Ready 진입) 시 flag clear 위해 별도 hook 필요 —
            // ApplyCallTransition (Ready) path 에서 callSensingAppendApplied.TryRemove 호출하도록
            // 후속 적용. 현재는 *재 schedule* 한 번만 의미.
            scheduler.ScheduleAfter(
                ScheduledEventType.CallTransition (callGuid, Status4.Finish),
                int64 appendMs,
                ScheduledEvent.PriorityStateChange) |> ignore
        ClearCallSensingAppendDelay = fun callGuid ->
            callSensingAppendApplied.TryRemove(callGuid) |> ignore
    }

    let createCallTransitionApplyContext
        runtimeMode
        (index: SimIndex)
        (ioMap: SignalIOMap)
        (scheduler: EventScheduler)
        writeTag
        (stateManager: StateManager)
        applyCallTransitionCore
        : CallTransitionApplyContext = {
        RuntimeMode = runtimeMode
        GetCallOutputResets = fun callGuid ->
            ioMap.CallToMappings
            |> Map.tryFind callGuid
            |> Option.defaultValue []
            |> List.choose (fun mapping ->
                if String.IsNullOrEmpty mapping.OutAddress then
                    None
                else
                    Queries.getApiCall mapping.ApiCallGuid index.Store
                    |> Option.bind (fun apiCall -> apiCall.ApiDefId)
                    |> Option.bind (fun apiDefId -> Queries.getApiDef apiDefId index.Store)
                    |> Option.bind (fun apiDef ->
                        match apiDef.ActionType with
                        | ActionType.Real (Level, None) ->
                            Some (ResetNow mapping.OutAddress)
                        | ActionType.Real (Level, Some (Append n)) ->
                            Some (ResetAfter (mapping.OutAddress, n))
                        | _ ->
                            None))
        WriteTag = fun address value ->
            match runtimeMode with
            | RuntimeMode.Simulation ->
                ioMap.OutAddressToMappings
                |> Map.tryFind address
                |> Option.iter (fun mappings ->
                    mappings
                    |> List.iter (fun mapping ->
                        stateManager.SetOutputValue(mapping.ApiCallGuid, value)))
            | _ ->
                writeTag address value
        ScheduleOutputWrite = fun (delayMs, address, value) ->
            match runtimeMode with
            | RuntimeMode.Simulation ->
                scheduler.ScheduleAfter(
                    ScheduledEventType.OutputWrite(address, value),
                    int64 delayMs,
                    ScheduledEvent.PriorityStateChange) |> ignore
            | _ ->
                System.Threading.Tasks.Task.Run(fun () ->
                    System.Threading.Tasks.Task.Delay(delayMs).Wait()
                    writeTag address value) |> ignore
        GetCallState = stateManager.GetCallState
        ApplyCallTransitionCore = applyCallTransitionCore
    }

    let createConditionEvaluationContext
        (index: SimIndex)
        (stateManager: StateManager)
        (scheduler: EventScheduler)
        canStartWork
        canStartCall
        canCompleteCall
        scheduleConditionEvaluation
        shiftToken
        emitTokenEvent
        canReceiveToken
        applyWorkTransition
        : ConditionEvaluation.Context = {
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

    let createTransitionGuardsContext
        (index: SimIndex)
        (stateManager: StateManager)
        canStartCall
        canCompleteCall
        onCallFinishRejected
        applyWorkTransition
        applyCallTransition
        : TransitionGuards.Context = {
        Index = index
        StateManager = stateManager
        IsWorkFrozen = stateManager.IsWorkFrozen
        CanStartCall = canStartCall
        CanCompleteCall = canCompleteCall
        ApplyWorkTransition = applyWorkTransition
        ApplyCallTransition = applyCallTransition
        OnCallFinishRejected = onCallFinishRejected
    }

    let createConnectionReloadContext
        (index: SimIndex)
        (stateManager: StateManager)
        (scheduler: EventScheduler)
        isActiveSystemWork
        emitTokenEvent
        triggerWorkStateChanged
        (durationTracker: DurationTracker)
        : ConnectionReload.Context = {
        Index = index
        StateManager = stateManager
        Scheduler = scheduler
        IsActiveSystemWork = isActiveSystemWork
        EmitTokenEvent = emitTokenEvent
        TriggerWorkStateChanged = triggerWorkStateChanged
        PauseDuration = durationTracker.PauseDuration
        ResumePausedDuration = durationTracker.TryResumePausedDuration
        ClearPausedDuration = durationTracker.ClearPausedDuration
    }

    let createDurationCompleteContext
        isPassiveMode
        (durationTracker: DurationTracker)
        (stateManager: StateManager)
        (index: SimIndex)
        (scheduler: EventScheduler)
        scheduleConditionEvaluation
        applyWorkTransition
        : DurationCompleteContext = {
        IsPassiveMode = isPassiveMode
        RemoveDuration = fun workGuid ->
            durationTracker.Remove workGuid
        GetWorkState = stateManager.GetWorkState
        MarkMinDurationMet = stateManager.MarkMinDurationMet
        GetWorkCallGuids = fun workGuid -> SimIndex.findOrEmpty workGuid index.WorkCallGuids
        GetCallState = stateManager.GetCallState
        ApplyWorkTransition = applyWorkTransition
        GetMaxSensingAppendMs = fun workGuid ->
            let virtualAppendMs (apiDef: ApiDef) =
                [
                    match apiDef.ActionType with
                    | ActionType.Virtual (Some (Append n)) -> yield n
                    | _ -> ()
                    match apiDef.SensingType with
                    | SensingType.Virtual (Some (Append n)) -> yield n
                    | _ -> ()
                ]

            let maxVirtualAppendMs apiDef =
                virtualAppendMs apiDef
                |> List.sortDescending
                |> List.tryHead

            // v10: Virtual(Some Append ms) 는 해당 completion Work 의 Duration+n 이다.
            // Action/Sensing 양쪽 모두 같은 시간 축에 표시되므로 중복 합산하지 않고 max 로 처리한다.
            // 일반 Device ApiDef 는 Tx/Rx Work 에 적용하고, Tx/Rx 가 없는 pure virtual wait 은 parent Work 에 적용한다.
            let boundAppend =
                index.Store.ApiDefs.Values
                |> Seq.choose (fun apiDef ->
                    let isCompletionWork =
                        apiDef.RxGuid = Some workGuid
                        || (apiDef.RxGuid.IsNone && apiDef.TxGuid = Some workGuid)
                    if isCompletionWork then
                        maxVirtualAppendMs apiDef
                    else
                        None)
                |> Seq.toList

            let callGuids = SimIndex.findOrEmpty workGuid index.WorkCallGuids
            let store = index.Store
            let unboundAppend =
                callGuids
                |> List.collect (fun callGuid ->
                    SimIndex.findOrEmpty callGuid index.CallApiCallGuids
                    |> List.choose (fun apiCallId ->
                        Queries.getApiCall apiCallId store
                        |> Option.bind (fun apiCall -> apiCall.ApiDefId)
                        |> Option.bind (fun apiDefId -> Queries.getApiDef apiDefId store)
                        |> Option.bind (fun apiDef ->
                            if apiDef.TxGuid.IsNone && apiDef.RxGuid.IsNone then
                                maxVirtualAppendMs apiDef
                            else
                                None)))
            boundAppend @ unboundAppend
            |> function
               | [] -> 0
               | xs -> List.max xs
        IsSensingAppendApplied = fun workGuid ->
            match sensingAppendApplied.TryGetValue(workGuid) with
            | true, v -> v
            | _ -> false
        ApplySensingAppendDelay = fun (workGuid, extraMs) ->
            sensingAppendApplied.[workGuid] <- true
            scheduler.ScheduleAfter(
                ScheduledEventType.DurationComplete workGuid,
                int64 extraMs,
                ScheduledEvent.PriorityDurationCheck) |> ignore
        ScheduleConditionEvaluation = scheduleConditionEvaluation
    }

    let createRuntimeContext
        processGate
        (scheduler: EventScheduler)
        runtimeClock
        (wakeSignal: WaitHandle)
        tryDrainHubInjectOnce
        getStatus
        getSpeedMultiplier
        updateClock
        getWorkState
        getWorkToken
        clearAndApplyWorkTransition
        clearAndApplyCallTransition
        forceAndApplyWorkTransition
        forceAndApplyCallTransition
        applyWorkTransition
        handleDurationComplete
        shiftToken
        emitTokenEvent
        scheduleConditionEvaluation
        evaluateConditions
        triggerCallTimeout
        applyOutputWrite
        getCallState
        getCallName
        getCallTimeoutMs
        : EventDrivenEngineRuntime.RuntimeContext = {
        ProcessGate = processGate
        Scheduler = scheduler
        RuntimeClock = runtimeClock
        WakeSignal = wakeSignal
        TryDrainHubInjectOnce = tryDrainHubInjectOnce
        GetStatus = getStatus
        SpeedMultiplier = getSpeedMultiplier
        UpdateClock = updateClock
        GetWorkState = getWorkState
        GetWorkToken = getWorkToken
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
        TriggerCallTimeout = triggerCallTimeout
        ApplyOutputWrite = applyOutputWrite
        GetCallState = getCallState
        GetCallName = getCallName
        GetCallTimeoutMs = getCallTimeoutMs
        CurrentTimeMs = (fun () -> scheduler.CurrentTimeMs)
    }

    let createLifecycleContext
        engineThreadJoinTimeoutMs
        getStatus
        setStatus
        getEngineThread
        setEngineThread
        getCts
        setCts
        triggerStatusChanged
        scheduleConditionEvaluation
        startEngineThread
        (index: SimIndex)
        (stateManager: StateManager)
        applyWorkTransition
        schedulerClear
        resetState
        : EngineLifecycle.Context = {
        EngineThreadJoinTimeoutMs = engineThreadJoinTimeoutMs
        GetStatus = getStatus
        SetStatus = setStatus
        GetEngineThread = getEngineThread
        SetEngineThread = setEngineThread
        GetCts = getCts
        SetCts = setCts
        TriggerStatusChanged = triggerStatusChanged
        ScheduleConditionEvaluation = scheduleConditionEvaluation
        StartEngineThread = startEngineThread
        AllWorkGuids = index.AllWorkGuids
        AllCallGuids = index.AllCallGuids
        GetWorkState = stateManager.GetWorkState
        GetCallState = stateManager.GetCallState
        ApplyWorkTransition = applyWorkTransition
        ForceWorkState = (fun workGuid newState -> stateManager.ForceWorkState(workGuid, newState))
        ForceCallState = (fun callGuid newState -> stateManager.ForceCallState(callGuid, newState))
        SchedulerClear = schedulerClear
        ResetState = resetState
    }

    let createFlowContext
        (index: SimIndex)
        (stateManager: StateManager)
        (durationTracker: DurationTracker)
        syncCurrentTime
        scheduleConditionEvaluation
        : EngineFlowStep.FlowContext = {
        Index = index
        StateManager = stateManager
        DurationTracker = durationTracker
        SyncCurrentTime = syncCurrentTime
        ScheduleConditionEvaluation = scheduleConditionEvaluation
    }

    let createStepBoundaryContext
        (index: SimIndex)
        getState
        currentTimeMs
        setAllFlowStates
        advanceStepRuntime
        nextEventTime
        : EngineFlowStep.StepBoundaryContext = {
        Index = index
        GetState = getState
        CurrentTimeMs = currentTimeMs
        SetAllFlowStates = setAllFlowStates
        AdvanceStepRuntime = advanceStepRuntime
        NextEventTime = nextEventTime
    }

    let createReloadContext
        removeScheduledConditionEvents
        clearConnectionTransientState
        reloadConnections
        invalidateOrphanedGoingWorks
        invalidateOrphanedTokenHolders
        scheduleConditionEvaluation
        : EngineFlowStep.ReloadContext = {
        RemoveScheduledConditionEvents = removeScheduledConditionEvents
        ClearConnectionTransientState = clearConnectionTransientState
        ReloadConnections = reloadConnections
        InvalidateOrphanedGoingWorks = invalidateOrphanedGoingWorks
        InvalidateOrphanedTokenHolders = invalidateOrphanedTokenHolders
        ScheduleConditionEvaluation = scheduleConditionEvaluation
    }

    let createStepContext
        (index: SimIndex)
        (stateManager: StateManager)
        hasStartableWork
        hasActiveDuration
        hasGoingCall
        nextToken
        seedToken
        forceWorkState
        runStepUntilBoundary
        : EngineFlowStep.StepContext = {
        Index = index
        StateManager = stateManager
        HasStartableWork = hasStartableWork
        HasActiveDuration = hasActiveDuration
        HasGoingCall = hasGoingCall
        NextToken = nextToken
        SeedToken = seedToken
        ForceWorkState = forceWorkState
        RunStepUntilBoundary = runStepUntilBoundary
    }
