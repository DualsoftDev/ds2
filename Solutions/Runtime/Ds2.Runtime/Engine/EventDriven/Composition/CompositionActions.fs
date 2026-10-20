namespace Ds2.Runtime.Engine

open System
open Ds2.Core
open Ds2.Runtime.IO
open Ds2.Runtime.Model
open Ds2.Runtime.Engine.Core

module internal EventDrivenCompositionActions =

    let applyInitialStates (index: SimIndex) initialization (stateManager: StateManager) resolveWorkName triggerWorkStateChanged =
        let targets =
            if initialization = SimulationInitializationPolicy.ModelOnly then
                SimIndex.findExplicitInitialFinishWorkGuids index
            else
                SimIndex.findInitialFlagRxWorkGuids index
        // 명시 `finished`(IsFinished) 만 적재 완료로 표시한다 — 두 정책 모두(FS1 CAT-03-R04, 결정 Q16).
        // Legacy 의 자동 원점 추론 대상(명시 플래그 없음)은 표시하지 않아 종전 동작을 유지한다.
        let loaded = SimIndex.findExplicitInitialFinishWorkGuids index
        EngineLifecycle.applyInitialFinishStates targets loaded stateManager resolveWorkName triggerWorkStateChanged

    let applyToken (stateManager: StateManager) emitTokenEvent scheduleConditionEvaluation (workGuid, newValue, kind, token) =
        stateManager.SetWorkToken(workGuid, newValue)
        emitTokenEvent kind token workGuid None
        scheduleConditionEvaluation ()

    let seedToken (stateManager: StateManager) (tokenFlowContext: TokenFlow.Context) applyToken sourceWorkGuid value =
        match stateManager.GetWorkToken(sourceWorkGuid) with
        | Some _ -> ()
        | None ->
            stateManager.SetTokenOrigin(value, TokenFlow.resolveSeedOriginLabel tokenFlowContext sourceWorkGuid)
            TokenFlow.resolveSeedSpecId tokenFlowContext sourceWorkGuid |> Option.iter (fun specId -> stateManager.SetTokenSpec(value, specId))
            applyToken (sourceWorkGuid, Some value, Seed, value)

    let startSourceWork (index: SimIndex) (stateManager: StateManager) seedToken forceWorkState sourceWorkGuid =
        if index.AllWorkGuids |> List.contains sourceWorkGuid then
            if SimIndex.isTokenSource index sourceWorkGuid
               && stateManager.GetWorkToken(sourceWorkGuid) |> Option.isNone then
                seedToken sourceWorkGuid (stateManager.NextToken())
            forceWorkState sourceWorkGuid Status4.Going

    let discardToken (stateManager: StateManager) applyToken workGuid =
        match stateManager.GetWorkToken(workGuid) with
        | Some token -> applyToken (workGuid, None, Discard, token)
        | None -> ()

    let getTokenOrigin (stateManager: StateManager) token =
        match token with
        | IntToken id -> stateManager.GetState().TokenOrigins |> Map.tryFind id

    let hasStartableWork (index: SimIndex) (stateManager: StateManager) canStartWork =
        EngineFlowStep.hasStartableWork index stateManager canStartWork stateManager.IsWorkFrozen

    let hasActiveDuration (index: SimIndex) (stateManager: StateManager) =
        EngineFlowStep.hasActiveDuration index stateManager stateManager.IsWorkFrozen

    let getWorkStateOpt (index: SimIndex) (stateManager: StateManager) workGuid =
        if index.AllWorkGuids |> List.contains workGuid then
            Some (stateManager.GetWorkState(workGuid))
        else
            None

    let startWithHomingPhase
        (index: SimIndex)
        initialization
        applyInitialStates
        getIsHomingPhase
        setIsHomingPhase
        setWorkStateDirect
        setCallStateDirect
        triggerHomingPhaseCompleted
        subscribeWorkStateChanged
        startEngine
        executeCallHoming =
        if initialization = SimulationInitializationPolicy.ModelOnly then
            applyInitialStates ()
            startEngine ()
            false
        else
            let isFinishedGuids = SimIndex.findInitialFlagRxWorkGuids index
            applyInitialStates ()
            let plan = HomingPhaseSetup.computePlan index isFinishedGuids
            let homingContext : HomingPhaseContext = {
                AllGoingTargets = plan.AllGoingTargets
                DisplayHomingCallGuids = plan.DisplayHomingCallGuids
                ExecutionCallGuids = plan.ExecutionCallGuids
                ActiveWorkGuids = plan.ActiveWorkGuids
                IsHomingPhase = getIsHomingPhase
                SetIsHomingPhase = setIsHomingPhase
                SetWorkStateDirect = setWorkStateDirect
                SetCallStateDirect = setCallStateDirect
                TriggerHomingPhaseCompleted = triggerHomingPhaseCompleted
                SubscribeWorkStateChanged = subscribeWorkStateChanged
                StartEngine = startEngine
                ExecuteCallHoming = executeCallHoming
            }
            EventDrivenHoming.startWithHomingPhase homingContext

    let enqueueHubIOValueByAddress
        (ioMap: SignalIOMap)
        signalWork
        (hubInjectQueue: System.Collections.Concurrent.ConcurrentQueue<struct(string * string * int64)>)
        address
        value =
        if ioMap.InAddressToMappings.ContainsKey(address) then
            hubInjectQueue.Enqueue(struct(address, value, System.Diagnostics.Stopwatch.GetTimestamp()))
            signalWork ()
            true
        else
            false

    let reloadDurations (index: SimIndex) (stateManager: StateManager) processGate =
        lock processGate (fun () ->
            SimIndex.reloadDurations index
                (index.AllWorkGuids
                 |> List.filter (fun workGuid -> stateManager.GetWorkState(workGuid) = Status4.Going)
                 |> Set.ofList))
