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
    let runtimeClock = EventDrivenEngineRuntime.RuntimeClock()
    let wakeSignal = new AutoResetEvent(false)
    let signalWork () = wakeSignal.Set() |> ignore
    /// Hub OnTagChanged 가 lock 없이 enqueue 하는 IO 신호 큐.
    /// simulationLoop 이 advance 안에서 매 iter 마다 한 항목씩 dequeue → SetIOValue + ConditionEval
    /// 페어 broadcast(IN=true → IN=false) 의 의미가 advance 도중 분리돼 처리되도록 함.
    /// 큐 항목: address, value, enqueue 시점 ticks. dequeue 시점에 elapsed 계산해 latency 진단.
    let hubInjectQueue = System.Collections.Concurrent.ConcurrentQueue<struct(string * string * int64)>()
    /// Control 모드 ms 단위 PLC 연동 가정 — broadcast → dequeue latency 임계치 5ms 초과 시 경고 로그.
    /// 간헐적 OS scheduling 지연/lock contention 진단용. 정상 시는 조용.
    let hubInjectLatencyWarnThresholdMs = 5L
    let hubInjectLatencyLog =
        log4net.LogManager.GetLogger("EventDrivenEngine.HubInject")
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
        signalWork ()
    let scheduleNowStateChange eventType = scheduleNow eventType ScheduledEvent.PriorityStateChange
    let scheduleNowDurationCheck eventType = scheduleNow eventType ScheduledEvent.PriorityDurationCheck
    let scheduleConditionEvaluation () =
        scheduleNow ScheduledEventType.EvaluateConditions ScheduledEvent.PriorityConditionEval
    /// simulationLoop 의 advance 안에서 호출. 큐에서 한 항목 dequeue 후 SetIOValue + scheduleConditionEval.
    /// dequeue 마다 ConditionEval 이벤트를 사이에 끼워 IN=true 가 적용된 시점의 평가를 보장한다.
    let tryDrainHubInjectOnce () : bool =
        let mutable item = Unchecked.defaultof<struct(string * string * int64)>
        if hubInjectQueue.TryDequeue(&item) then
            let struct(address, value, enqueueTicks) = item
            // enqueue → dequeue latency 측정. 임계치 초과 시 경고 (Control 모드 ms 단위 응답성 진단)
            let elapsedMs =
                let now = System.Diagnostics.Stopwatch.GetTimestamp()
                ((now - enqueueTicks) * 1000L) / System.Diagnostics.Stopwatch.Frequency
            if elapsedMs >= hubInjectLatencyWarnThresholdMs then
                hubInjectLatencyLog.Warn(
                    sprintf "Hub inject latency %dms (addr=%s value=%s) — wake/lock contention 의심" elapsedMs address value)
            match ioMap.InAddressToMappings |> Map.tryFind address with
            | Some mappings ->
                for m in mappings do
                    stateManager.SetIOValue(m.ApiCallGuid, value)
                scheduleConditionEvaluation ()
            | None -> ()
            true
        else
            false
    let canStartWork workGuid = WorkConditionChecker.canStartWork index (stateManager.GetState()) workGuid
    let canStartCall callGuid = WorkConditionChecker.canStartCall index (stateManager.GetState()) callGuid
    let canCompleteCall callGuid = WorkConditionChecker.canCompleteCall index (stateManager.GetState()) callGuid
    let shouldSkipCall callGuid = WorkConditionChecker.shouldSkipCall index (stateManager.GetState()) callGuid
    let isActiveSystemWork workGuid =
        index.WorkSystemName
        |> Map.tryFind workGuid
        |> Option.map index.ActiveSystemNames.Contains
        |> Option.defaultValue false
    let tokenFlowContext =
        EventDrivenCompositionContext.createTokenFlowContext index stateManager scheduler tokenEventEvent.Trigger
    let workNameOf guid = TokenFlow.workNameOf tokenFlowContext guid
    let canReceiveToken workGuid = TokenFlow.canReceiveToken tokenFlowContext workGuid
    let emitTokenEvent kind token workGuid (targetGuid: Guid option) =
        TokenFlow.emitTokenEvent tokenFlowContext kind token workGuid targetGuid
    let shiftToken workGuid token = TokenFlow.shiftToken tokenFlowContext workGuid token
    let onWorkFinish workGuid = TokenFlow.onWorkFinish tokenFlowContext workGuid
    let workTransitionContext =
        EventDrivenCompositionContext.createWorkTransitionContext
            index
            stateManager
            scheduler
            runtimeMode
            (fun () -> isHomingPhase)
            (fun () -> timeIgnore)
            scheduleConditionEvaluation
            onWorkFinish
            workStateChangedEvent.Trigger
            (fun workGuid eventId scheduledTimeMs ->
                durationTracker.OnDurationScheduled(workGuid, eventId, scheduledTimeMs))
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
    let apiCallExecutionContext =
        EventDrivenCompositionContext.createApiCallExecutionContext
            runtimeMode
            stateManager
            resolveWorkName
            ioMap
            writeTagFn
            forceWorkState
    let executeApiCall deviceWorkGuid =
        EventDrivenExecution.executeApiCall apiCallExecutionContext deviceWorkGuid
    let executeCallGoing callGuid =
        EventDrivenExecution.executeCallGoing index apiCallExecutionContext callGuid
    /// Call Homing 처리: allGoingTargets에 해당하는 TxWork를 직접 Going시켜 원위치 유도.
    /// 개별 Call의 Ready 전이는 하지 않음 — StartWithHomingPhase completion handler가 일괄 처리.
    let executeCallHoming (callGuid: Guid) (goingTargets: Set<Guid>) =
        EventDrivenExecution.executeCallHoming index apiCallExecutionContext callGuid goingTargets
    let callTransitionContext =
        EventDrivenCompositionContext.createCallTransitionContext
            index
            stateManager
            scheduler
            runtimeMode
            shouldSkipCall
            callStateChangedEvent.Trigger
            scheduleWorkIfReady
            scheduleConditionEvaluation
            durationTracker
            (fun () -> timeIgnore)
            executeCallGoing
    let callTransitionApplyContext =
        EventDrivenCompositionContext.createCallTransitionApplyContext
            runtimeMode
            ioMap
            writeTagFn
            stateManager
            (fun callGuid newState ->
                CallTransitions.applyCallTransition callTransitionContext callGuid newState)
    let applyCallTransition callGuid newState =
        EventDrivenExecution.applyCallTransition callTransitionApplyContext callGuid newState
    let conditionEvaluationContext =
        EventDrivenCompositionContext.createConditionEvaluationContext
            index
            stateManager
            scheduler
            canStartWork
            canStartCall
            canCompleteCall
            scheduleConditionEvaluation
            shiftToken
            emitTokenEvent
            canReceiveToken
            applyWorkTransition
    /// Passive 모드 (VirtualPlant / Monitoring)는 상태 전이를 IO 이벤트 기반 외부 유추로만 처리.
    /// 엔진의 자체 조건 평가 루프를 끔 — 수동 관찰자 역할.
    let isPassiveMode =
        match runtimeMode with
        | RuntimeMode.VirtualPlant | RuntimeMode.Monitoring -> true
        | _ -> false

    let evaluateConditions () =
        if not isPassiveMode then
            ConditionEvaluation.evaluateConditions conditionEvaluationContext ()
    let transitionGuardsContext =
        EventDrivenCompositionContext.createTransitionGuardsContext
            index
            stateManager
            canStartCall
            canCompleteCall
            applyWorkTransition
            applyCallTransition
    let hasGoingCall () =
        index.AllCallGuids
        |> List.exists (fun callGuid -> stateManager.GetCallState(callGuid) = Status4.Going)
    let connectionReloadContext =
        EventDrivenCompositionContext.createConnectionReloadContext
            index
            stateManager
            scheduler
            isActiveSystemWork
            emitTokenEvent
            workStateChangedEvent.Trigger
            durationTracker
    let durationCompleteContext =
        EventDrivenCompositionContext.createDurationCompleteContext
            isPassiveMode
            durationTracker
            stateManager
            index
            applyWorkTransition
    let handleDurationComplete workGuid =
        EventDrivenExecution.handleDurationComplete durationCompleteContext workGuid
    let runtimeContext =
        EventDrivenCompositionContext.createRuntimeContext
            processGate
            scheduler
            runtimeClock
            wakeSignal
            tryDrainHubInjectOnce
            getStatus
            (fun () -> speedMultiplier)
            stateManager.UpdateClock
            stateManager.GetWorkState
            stateManager.GetWorkToken
            (TransitionGuards.clearAndApplyWork transitionGuardsContext)
            (TransitionGuards.clearAndApplyCall transitionGuardsContext)
            (TransitionGuards.forceAndApplyWork transitionGuardsContext)
            (TransitionGuards.forceAndApplyCall transitionGuardsContext)
            applyWorkTransition
            handleDurationComplete
            shiftToken
            emitTokenEvent
            scheduleConditionEvaluation
            evaluateConditions
            callTimeoutEvent.Trigger
            stateManager.GetCallState
            (fun callGuid ->
                Queries.getCall callGuid index.Store
                |> Option.map (fun c -> c.Name)
                |> Option.defaultValue (string callGuid))
            (fun callGuid ->
                index.CallTimeoutMap
                |> Map.tryFind callGuid
                |> Option.map (fun ts -> int ts.TotalMilliseconds))
    let advanceStepRuntime targetTimeMs =
        EventDrivenEngineRuntime.advanceAndDrain runtimeContext targetTimeMs
    let startEngineThread (tokenSource: CancellationTokenSource) =
        runtimeClock.Reset()
        let thread = Thread(ThreadStart(fun () -> EventDrivenEngineRuntime.simulationLoop runtimeContext tokenSource.Token))
        thread.IsBackground <- true
        thread.Name <- "EventDrivenEngine"
        // Control 모드에서 외부 PLC 와 ms 단위 응답성 유지 위해 우선순위 상향.
        // wakeSignal 도착 후 simulationLoop 이 lock 잡고 advance 시작하기까지 OS scheduler 의존
        // — AboveNormal 로 두면 일반 thread 들 사이에서 우선 깨어나게.
        thread.Priority <- ThreadPriority.AboveNormal
        thread.Start()
        engineThread <- Some thread
    let runExternalMutation action =
        // 외부 mutation 은 lock 안에서 상태/스케줄러만 변경하고 wake 신호만 보낸다.
        // advance 는 simulationLoop thread 가 단독으로 처리해서 두 thread 가 번갈아
        // advance 하며 Reset 흐름 도중 stale IN signal 이 condition eval 로 reapply 되어
        // device Work 가 Homing→Finish 로 잘못 전이되는 race 차단.
        lock processGate (fun () ->
            let result = action ()
            signalWork ()
            result)
    let lifecycleContext =
        EventDrivenCompositionContext.createLifecycleContext
            engineThreadJoinTimeoutMs
            getStatus
            setStatus
            (fun () -> engineThread)
            (fun thread -> engineThread <- thread)
            (fun () -> cts)
            (fun tokenSource -> cts <- tokenSource)
            simulationStatusChangedEvent.Trigger
            scheduleConditionEvaluation
            startEngineThread
            index
            stateManager
            applyWorkTransition
            scheduler.Clear
            stateManager.Reset
    let flowContext =
        EventDrivenCompositionContext.createFlowContext
            index
            stateManager
            durationTracker
            (fun () -> EventDrivenEngineRuntime.syncClockToCurrentTimeWhileRunning runtimeContext)
            scheduleConditionEvaluation
    let stepBoundaryContext =
        EventDrivenCompositionContext.createStepBoundaryContext
            index
            stateManager.GetState
            (fun () -> scheduler.CurrentTimeMs)
            (EngineFlowStep.setAllFlowStates flowContext)
            advanceStepRuntime
            (fun () -> scheduler.NextEventTime)
    let reloadContext =
        EventDrivenCompositionContext.createReloadContext
            (fun () -> ConnectionReload.removeScheduledConditionEvents connectionReloadContext)
            stateManager.ClearConnectionTransientState
            (fun () -> SimIndex.reloadConnections index)
            (fun previousSnapshot currentSnapshot ->
                ConnectionReload.invalidateOrphanedGoingWorks connectionReloadContext previousSnapshot currentSnapshot)
            (fun () -> ConnectionReload.invalidateOrphanedTokenHolders connectionReloadContext)
            scheduleConditionEvaluation

    let applyInitialStates () =
        EventDrivenCompositionActions.applyInitialStates index stateManager resolveWorkName workStateChangedEvent.Trigger

    let applyToken (workGuid, newValue, kind, token) =
        EventDrivenCompositionActions.applyToken stateManager emitTokenEvent scheduleConditionEvaluation (workGuid, newValue, kind, token)

    let seedToken (sourceWorkGuid: Guid) (value: TokenValue) =
        EventDrivenCompositionActions.seedToken stateManager tokenFlowContext applyToken sourceWorkGuid value

    let startSourceWork (sourceWorkGuid: Guid) =
        EventDrivenCompositionActions.startSourceWork index stateManager seedToken forceWorkState sourceWorkGuid

    let discardToken (workGuid: Guid) =
        EventDrivenCompositionActions.discardToken stateManager applyToken workGuid

    let getTokenOrigin (token: TokenValue) =
        EventDrivenCompositionActions.getTokenOrigin stateManager token

    let hasStartableWork () =
        EventDrivenCompositionActions.hasStartableWork index stateManager canStartWork

    let hasActiveDuration () =
        EventDrivenCompositionActions.hasActiveDuration index stateManager

    let getWorkStateOpt (workGuid: Guid) =
        EventDrivenCompositionActions.getWorkStateOpt index stateManager workGuid

    let stepContext =
        EventDrivenCompositionContext.createStepContext
            index
            stateManager
            hasStartableWork
            hasActiveDuration
            hasGoingCall
            stateManager.NextToken
            seedToken
            forceWorkState
            (fun () -> EngineFlowStep.runStepUntilBoundary stepBoundaryContext)

    let startWithHomingPhase () : bool =
        EventDrivenCompositionActions.startWithHomingPhase
            index
            applyInitialStates
            (fun () -> isHomingPhase)
            (fun value -> isHomingPhase <- value)
            setWorkStateDirect
            setCallStateDirect
            (fun () -> homingPhaseCompletedEvent.Trigger(EventArgs.Empty))
            (fun handler -> workStateChangedEvent.Publish.Subscribe(handler))
            (fun () -> EngineLifecycle.start lifecycleContext)
            executeCallHoming

    let enqueueHubIOValueByAddress (address: string) (value: string) : bool =
        EventDrivenCompositionActions.enqueueHubIOValueByAddress ioMap signalWork hubInjectQueue address value

    let reloadDurations () =
        EventDrivenCompositionActions.reloadDurations index stateManager processGate

    interface ISimulationEngine with
        member _.State = stateManager.GetState()
        member _.Status = getStatus ()
        member _.Index = index
        member _.Start() = runExternalMutation (fun () -> EngineLifecycle.start lifecycleContext)
        member _.Pause() = runExternalMutation (fun () -> EngineLifecycle.pause lifecycleContext)
        member _.Resume() = runExternalMutation (fun () -> EngineLifecycle.resume lifecycleContext)
        member _.Stop() =
            EngineLifecycle.stop lifecycleContext
            signalWork ()
        member _.Reset() =
            EngineLifecycle.reset lifecycleContext
            signalWork ()
        member _.ApplyInitialStates() = applyInitialStates ()
        member _.ForceWorkState(workGuid, newState) =
            runExternalMutation (fun () -> forceWorkState workGuid newState)
        member _.TryForceWorkStateIfGoing(workGuid, newState) =
            runExternalMutation (fun () ->
                if index.AllWorkGuids |> List.contains workGuid
                   && stateManager.GetWorkState(workGuid) = Status4.Going then
                    forceWorkState workGuid newState)
        member _.ForceCallState(callGuid, newState) =
            runExternalMutation (fun () -> forceCallState callGuid newState)
        member _.GetWorkState(workGuid) = getWorkStateOpt workGuid
        member _.GetCallState(callGuid) = stateManager.GetState().CallStates.TryFind(callGuid)
        member _.SetAllFlowStates(tag) =
            runExternalMutation (fun () -> EngineFlowStep.setAllFlowStates flowContext tag)
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
        member _.BeginStepBatch(selectedSourceGuid, autoStartSources) =
            lock processGate (fun () ->
                EngineFlowStep.beginStepBatch stepBoundaryContext stepContext selectedSourceGuid autoStartSources)
        member _.IsStepBatchActive(batch: Guid[]) =
            EngineFlowStep.isAnyBatchStillGoing stepBoundaryContext (Set.ofArray batch)
        member _.AdvanceSimulationTo(targetTimeMs) =
            lock processGate (fun () -> advanceStepRuntime targetTimeMs)
        member _.EndStep() =
            lock processGate (fun () ->
                EngineFlowStep.endStep stepBoundaryContext)
        member _.ReloadConnections() =
            runExternalMutation (fun () -> EngineFlowStep.reloadConnections reloadContext)
        member _.ReloadDurations() =
            runExternalMutation (fun () -> reloadDurations ())
        member _.CurrentTimeMs = scheduler.CurrentTimeMs
        member _.NextEventTimeMs = scheduler.NextEventTime
        member _.SpeedMultiplier
            with get() = speedMultiplier
            and set(value) =
                runExternalMutation (fun () ->
                    speedMultiplier <- max 0.1 (min 1000.0 value))
        member _.TimeIgnore
            with get() = timeIgnore
            and set(isIgnored) =
                runExternalMutation (fun () ->
                    let previous = timeIgnore
                    timeIgnore <- isIgnored
                    if isIgnored && not previous && getStatus() = Running then
                        EngineFlowStep.rescheduleOnTimeIgnoreEnabled index stateManager scheduleNowDurationCheck scheduleNowDurationCheck)
        member _.InjectIOValue(apiCallGuid, value) =
            runExternalMutation (fun () ->
                stateManager.SetIOValue(apiCallGuid, value)
                scheduleConditionEvaluation ())
        member _.InjectIOValueByAddress(address, value) =
            // Hub 콜백(SignalR thread) 에서 호출. lock 없이 큐에 enqueue 만 하고 wake.
            // simulationLoop 이 advance 안에서 한 항목씩 dequeue → SetIOValue + ConditionEval
            // 페어 broadcast(IN=true → IN=false) 사이에 평가가 끼어들도록 보장.
            enqueueHubIOValueByAddress address value
        member _.IOMap = ioMap
        member _.NextToken() = stateManager.NextToken()
        member _.SeedToken(sourceWorkGuid, value) =
            runExternalMutation (fun () -> seedToken sourceWorkGuid value)
        member _.StartSourceWork(sourceWorkGuid) =
            runExternalMutation (fun () -> startSourceWork sourceWorkGuid)
        member _.DiscardToken(workGuid) =
            runExternalMutation (fun () -> discardToken workGuid)
        member _.GetWorkToken(workGuid) = stateManager.GetWorkToken(workGuid)
        member _.GetTokenOrigin(token) = getTokenOrigin token
        member _.StartWithHomingPhase() =
            runExternalMutation (fun () -> startWithHomingPhase ())
        member _.IsHomingPhase = isHomingPhase
        [<CLIEvent>] member _.WorkStateChanged = workStateChangedEvent.Publish
        [<CLIEvent>] member _.CallStateChanged = callStateChangedEvent.Publish
        [<CLIEvent>] member _.SimulationStatusChanged = simulationStatusChangedEvent.Publish
        [<CLIEvent>] member _.TokenEvent = tokenEventEvent.Publish
        [<CLIEvent>] member _.CallTimeout = callTimeoutEvent.Publish
        [<CLIEvent>] member _.HomingPhaseCompleted = homingPhaseCompletedEvent.Publish
        member _.Dispose() =
            EngineLifecycle.stop lifecycleContext
            wakeSignal.Dispose()

    new(index: SimIndex, runtimeMode: RuntimeMode) = new EventDrivenEngine(index, runtimeMode, None)
