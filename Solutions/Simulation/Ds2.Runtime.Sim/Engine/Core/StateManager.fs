namespace Ds2.Runtime.Sim.Engine.Core

open System
open Ds2.Store
open Ds2.Core
open Ds2.Runtime.Sim.Model

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

    let canonicalWorkGuid (guid: Guid) =
        SimIndex.canonicalWorkGuid index guid

    let referenceGroupOf (guid: Guid) =
        SimIndex.referenceGroupOf index guid

    let setWorkStateForGroup (guid: Guid) (newState: Status4) =
        let groupGuids = referenceGroupOf guid
        state <-
            groupGuids
            |> List.fold (fun acc groupGuid -> SimState.setWorkState groupGuid newState acc) state

    let setWorkTokenForGroup (guid: Guid) (token: TokenValue option) =
        let groupGuids = referenceGroupOf guid
        state <-
            groupGuids
            |> List.fold (fun acc groupGuid -> SimState.setWorkToken groupGuid token acc) state

    let groupState guid =
        let canonical = canonicalWorkGuid guid
        state.WorkStates |> Map.tryFind canonical |> Option.defaultValue Status4.Ready

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
                    let canonical = canonicalWorkGuid guid
                    workMinDurationMet <- workMinDurationMet.Remove(canonical)
                    workGTriggeredResets <- workGTriggeredResets |> Set.filter (fun (predGuid, _) -> predGuid <> canonical)
                { ActualNewState = newState; OldState = oldState; IsSkipped = false; HasChanged = true; NodeName = nodeName; DeviceName = deviceName })

    member _.ApplyCallTransition(guid: Guid, newState: Status4, shouldSkipCall: Guid -> bool) : TransitionResult =
        lock syncRoot (fun () ->
            let oldState = state.CallStates |> Map.tryFind guid |> Option.defaultValue Status4.Ready
            let nodeName =
                DsQuery.getCall guid index.Store
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
                state <- SimState.setCallState guid actualNewState state
                if isSkipped then state <- { state with SkippedCalls = state.SkippedCalls.Add(guid) }
                elif actualNewState = Status4.Ready then state <- { state with SkippedCalls = state.SkippedCalls.Remove(guid) }
                { ActualNewState = actualNewState; OldState = oldState; IsSkipped = isSkipped; HasChanged = true; NodeName = nodeName; DeviceName = deviceName })

    member _.MarkWorkPending(guid: Guid)  = lock syncRoot (fun () -> pendingWorkTransitions <- pendingWorkTransitions.Add(canonicalWorkGuid guid))
    member _.MarkCallPending(guid: Guid)  = lock syncRoot (fun () -> pendingCallTransitions <- pendingCallTransitions.Add(guid))
    member _.ClearWorkPending(guid: Guid) = lock syncRoot (fun () -> pendingWorkTransitions <- pendingWorkTransitions.Remove(canonicalWorkGuid guid))
    member _.ClearCallPending(guid: Guid) = lock syncRoot (fun () -> pendingCallTransitions <- pendingCallTransitions.Remove(guid))
    member _.IsWorkPending(guid: Guid)    = lock syncRoot (fun () -> pendingWorkTransitions.Contains(canonicalWorkGuid guid))
    member _.IsCallPending(guid: Guid)    = lock syncRoot (fun () -> pendingCallTransitions.Contains(guid))

    member _.IsResetTriggered(predKey, targetKey) =
        lock syncRoot (fun () ->
            workGTriggeredResets.Contains((canonicalWorkGuid predKey, canonicalWorkGuid targetKey)))
    member _.AddResetTrigger(predKey, targetKey)  =
        lock syncRoot (fun () ->
            workGTriggeredResets <- workGTriggeredResets.Add((canonicalWorkGuid predKey, canonicalWorkGuid targetKey)))

    member _.MarkMinDurationMet(guid: Guid) = lock syncRoot (fun () -> workMinDurationMet <- workMinDurationMet.Add(canonicalWorkGuid guid))
    member _.IsMinDurationMet(guid: Guid)   = lock syncRoot (fun () -> workMinDurationMet.Contains(canonicalWorkGuid guid))
    member _.ClearMinDuration(guid: Guid)   = lock syncRoot (fun () -> workMinDurationMet <- workMinDurationMet.Remove(canonicalWorkGuid guid))

    member _.ClearConnectionTransientState() =
        lock syncRoot (fun () ->
            workGTriggeredResets <- Set.empty)

    member _.FreezeWork(guid: Guid) =
        lock syncRoot (fun () ->
            frozenWorks <- frozenWorks.Add(canonicalWorkGuid guid))

    member _.UnfreezeWork(guid: Guid) =
        lock syncRoot (fun () ->
            frozenWorks <- frozenWorks.Remove(canonicalWorkGuid guid))

    member _.IsWorkFrozen(guid: Guid) =
        lock syncRoot (fun () ->
            frozenWorks.Contains(canonicalWorkGuid guid))

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
        lock syncRoot (fun () -> SimState.getWorkToken (canonicalWorkGuid workGuid) state)
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
