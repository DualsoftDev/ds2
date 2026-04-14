namespace Ds2.Runtime.Engine.Core

open System
open Ds2.Core.Store
open Ds2.Core
open Ds2.Runtime.Model

/// 상태 전이 결과
type TransitionResult = {
    ActualNewState: Status4
    OldState: Status4
    IsSkipped: bool
    HasChanged: bool
    NodeName: string
    DeviceName: string
}

/// 상태 관리자 (TransitionLogic 인라인)
/// sim 스레드에서 쓰기, UI 스레드에서 읽기 — lock으로 동기화
type StateManager(index: SimIndex, initialTickMs: int) =
    let syncRoot = obj()
    let mutable state = SimState.create initialTickMs index.AllWorkGuids index.AllCallGuids index.AllFlowGuids
    let mutable pendingCallTransitions = Set.empty<Guid>
    let mutable pendingWorkTransitions = Set.empty<Guid>
    let mutable workGTriggeredResets = Set.empty<Guid * Guid>
    let mutable workMinDurationMet = Set.empty<Guid>
    let mutable frozenWorks = Set.empty<Guid>

    let canonicalCallGuid (guid: Guid) =
        SimIndex.canonicalCallGuid index guid

    let callReferenceGroupOf (guid: Guid) =
        SimIndex.callReferenceGroupOf index guid

    let setCallStateForGroup (guid: Guid) (newState: Status4) =
        let groupGuids = callReferenceGroupOf guid
        state <-
            groupGuids
            |> List.fold (fun acc groupGuid -> SimState.setCallState groupGuid newState acc) state

    let setWorkStateForGroup (guid: Guid) (newState: Status4) =
        state <- SimState.setWorkState guid newState state

    let setWorkTokenForGroup (guid: Guid) (token: TokenValue option) =
        state <- SimState.setWorkToken guid token state

    let groupState guid =
        state.WorkStates |> Map.tryFind guid |> Option.defaultValue Status4.Ready

    member _.ApplyWorkTransition(guid: Guid, newState: Status4) : TransitionResult =
        lock syncRoot (fun () ->
            let oldState = groupState guid
            let nodeName = index.WorkName |> Map.tryFind guid |> Option.defaultValue (string guid)
            let deviceName = index.WorkSystemName |> Map.tryFind guid |> Option.defaultValue ""
            if oldState = newState then
                { ActualNewState = newState; OldState = oldState; IsSkipped = false; HasChanged = false; NodeName = nodeName; DeviceName = deviceName }
            else
                setWorkStateForGroup guid newState
                if oldState = Status4.Going then
                    workMinDurationMet <- workMinDurationMet.Remove(guid)
                    workGTriggeredResets <- workGTriggeredResets |> Set.filter (fun (predGuid, _) -> predGuid <> guid)
                if newState = Status4.Ready then
                    workGTriggeredResets <- workGTriggeredResets |> Set.filter (fun (_, targetGuid) -> targetGuid <> guid)
                { ActualNewState = newState; OldState = oldState; IsSkipped = false; HasChanged = true; NodeName = nodeName; DeviceName = deviceName })

    member _.ApplyCallTransition(guid: Guid, newState: Status4, shouldSkipCall: Guid -> bool) : TransitionResult =
        lock syncRoot (fun () ->
            let oldState = state.CallStates |> Map.tryFind guid |> Option.defaultValue Status4.Ready
            let nodeName =
                Queries.getCall guid index.Store
                |> Option.map (fun c -> c.Name) |> Option.defaultValue (string guid)
            let deviceName =
                index.CallWorkGuid |> Map.tryFind guid
                |> Option.bind (fun wg -> index.WorkSystemName |> Map.tryFind wg) |> Option.defaultValue ""
            let actualNewState, isSkipped =
                if oldState = Status4.Ready && newState = Status4.Going && shouldSkipCall guid then
                    Status4.Finish, true
                else newState, false
            if oldState = actualNewState then
                { ActualNewState = actualNewState; OldState = oldState; IsSkipped = false; HasChanged = false; NodeName = nodeName; DeviceName = deviceName }
            else
                setCallStateForGroup guid actualNewState
                if isSkipped then
                    for g in callReferenceGroupOf guid do
                        state <- { state with SkippedCalls = state.SkippedCalls.Add(g) }
                elif actualNewState = Status4.Ready then
                    for g in callReferenceGroupOf guid do
                        state <- { state with SkippedCalls = state.SkippedCalls.Remove(g) }
                { ActualNewState = actualNewState; OldState = oldState; IsSkipped = isSkipped; HasChanged = true; NodeName = nodeName; DeviceName = deviceName })

    member _.MarkWorkPending(guid: Guid)  = lock syncRoot (fun () -> pendingWorkTransitions <- pendingWorkTransitions.Add(guid))
    member _.MarkCallPending(guid: Guid)  = lock syncRoot (fun () -> pendingCallTransitions <- pendingCallTransitions.Add(canonicalCallGuid guid))
    member _.ClearWorkPending(guid: Guid) = lock syncRoot (fun () -> pendingWorkTransitions <- pendingWorkTransitions.Remove(guid))
    member _.ClearCallPending(guid: Guid) = lock syncRoot (fun () -> pendingCallTransitions <- pendingCallTransitions.Remove(canonicalCallGuid guid))
    member _.IsWorkPending(guid: Guid)    = lock syncRoot (fun () -> pendingWorkTransitions.Contains(guid))
    member _.IsCallPending(guid: Guid)    = lock syncRoot (fun () -> pendingCallTransitions.Contains(canonicalCallGuid guid))

    member _.IsResetTriggered(predKey, targetKey) =
        lock syncRoot (fun () ->
            workGTriggeredResets.Contains((predKey, targetKey)))
    member _.AddResetTrigger(predKey, targetKey)  =
        lock syncRoot (fun () ->
            workGTriggeredResets <- workGTriggeredResets.Add((predKey, targetKey)))

    member _.MarkMinDurationMet(guid: Guid) = lock syncRoot (fun () -> workMinDurationMet <- workMinDurationMet.Add(guid))
    member _.IsMinDurationMet(guid: Guid)   = lock syncRoot (fun () -> workMinDurationMet.Contains(guid))
    member _.ClearMinDuration(guid: Guid)   = lock syncRoot (fun () -> workMinDurationMet <- workMinDurationMet.Remove(guid))

    member _.ClearConnectionTransientState() =
        lock syncRoot (fun () ->
            workGTriggeredResets <- Set.empty)

    member _.FreezeWork(guid: Guid) =
        lock syncRoot (fun () ->
            frozenWorks <- frozenWorks.Add(guid))

    member _.UnfreezeWork(guid: Guid) =
        lock syncRoot (fun () ->
            frozenWorks <- frozenWorks.Remove(guid))

    member _.IsWorkFrozen(guid: Guid) =
        lock syncRoot (fun () ->
            frozenWorks.Contains(guid))

    member _.SetIOValue(apiCallGuid: Guid, value: string) =
        lock syncRoot (fun () -> state <- SimState.setIOValue apiCallGuid value state)

    member _.Reset() =
        lock syncRoot (fun () ->
            state                  <- SimState.reset state
            pendingCallTransitions <- Set.empty
            pendingWorkTransitions <- Set.empty
            workGTriggeredResets   <- Set.empty
            workMinDurationMet     <- Set.empty
            frozenWorks            <- Set.empty)

    // ── Token ──
    member _.SetWorkToken(workGuid: Guid, token: TokenValue option) =
        lock syncRoot (fun () -> setWorkTokenForGroup workGuid token)
    member _.GetWorkToken(workGuid: Guid) =
        lock syncRoot (fun () -> SimState.getWorkToken workGuid state)
    member _.AddCompletedToken(token: TokenValue) =
        lock syncRoot (fun () -> state <- SimState.addCompletedToken token state)
    member _.SetTokenOrigin(token: TokenValue, workName: string) =
        lock syncRoot (fun () ->
            match token with IntToken id -> state <- SimState.setTokenOrigin id workName state)
    member _.NextToken() =
        lock syncRoot (fun () ->
            let token, newState = SimState.nextToken state
            state <- newState
            token)

    // ── Epoch (WaitForCompletion) ──
    member _.IncrementWorkEpoch(workGuid: Guid) =
        lock syncRoot (fun () -> state <- SimState.incrementWorkEpoch workGuid state)
    member _.GetWorkEpoch(workGuid: Guid) =
        lock syncRoot (fun () -> SimState.getWorkEpoch workGuid state)
    member _.SnapshotCallRxEpochs(callGuid: Guid, rxGuids: Guid list) =
        lock syncRoot (fun () ->
            let epochMap = rxGuids |> List.map (fun rx -> rx, SimState.getWorkEpoch rx state) |> Map.ofList
            state <- SimState.snapshotCallRxEpochs callGuid epochMap state)
    member _.ClearCallRxEpochSnapshot(callGuid: Guid) =
        lock syncRoot (fun () -> state <- SimState.clearCallRxEpochSnapshot callGuid state)

    member _.UpdateClock(newClock: TimeSpan) = lock syncRoot (fun () -> state <- { state with Clock = newClock })
    member _.GetState() = lock syncRoot (fun () -> state)
    member _.GetWorkState(workGuid: Guid) =
        lock syncRoot (fun () -> groupState workGuid)
    member _.GetCallState(callGuid: Guid) = lock syncRoot (fun () -> state.CallStates |> Map.tryFind callGuid |> Option.defaultValue Status4.Ready)
    member _.GetFlowState(flowGuid: Guid) = lock syncRoot (fun () -> state.FlowStates |> Map.tryFind flowGuid |> Option.defaultValue FlowTag.Ready)
    member _.SetFlowState(flowGuid: Guid, tag: FlowTag) =
        lock syncRoot (fun () -> state <- SimState.setFlowState flowGuid tag state)
    member _.SetAllFlowStates(tag: FlowTag) =
        lock syncRoot (fun () -> state <- { state with FlowStates = state.FlowStates |> Map.map (fun _ _ -> tag) })
    member _.ForceWorkState(workGuid: Guid, newState: Status4) =
        lock syncRoot (fun () -> setWorkStateForGroup workGuid newState)
    member _.ForceCallState(callGuid: Guid, newState: Status4) =
        lock syncRoot (fun () -> state <- SimState.setCallState callGuid newState state)
