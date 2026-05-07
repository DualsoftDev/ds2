namespace Ds2.Runtime.Engine

open System
open System.Threading
open Ds2.Core
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
    }

    let createApiCallExecutionContext
        runtimeMode
        (stateManager: StateManager)
        resolveWorkName
        (ioMap: SignalIOMap)
        writeTag
        forceWorkState
        : ApiCallExecutionContext = {
        RuntimeMode = runtimeMode
        GetDeviceState = stateManager.GetWorkState
        GetDeviceName = resolveWorkName
        GetTxOutAddresses = fun deviceWorkGuid ->
            ioMap.TxWorkToOutAddresses |> Map.tryFind deviceWorkGuid |> Option.defaultValue []
        WriteTag = writeTag
        ForceWorkState = forceWorkState
    }

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
    }

    let createCallTransitionApplyContext
        runtimeMode
        (ioMap: SignalIOMap)
        writeTag
        (stateManager: StateManager)
        applyCallTransitionCore
        : CallTransitionApplyContext = {
        RuntimeMode = runtimeMode
        GetCallOutAddresses = fun callGuid ->
            ioMap.CallToMappings
            |> Map.tryFind callGuid
            |> Option.defaultValue []
            |> List.choose (fun mapping ->
                if String.IsNullOrEmpty mapping.OutAddress then None
                else Some mapping.OutAddress)
        WriteTag = writeTag
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
        applyWorkTransition
        : DurationCompleteContext = {
        IsPassiveMode = isPassiveMode
        RemoveDuration = durationTracker.Remove
        GetWorkState = stateManager.GetWorkState
        MarkMinDurationMet = stateManager.MarkMinDurationMet
        GetWorkCallGuids = fun workGuid -> SimIndex.findOrEmpty workGuid index.WorkCallGuids
        GetCallState = stateManager.GetCallState
        ApplyWorkTransition = applyWorkTransition
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
