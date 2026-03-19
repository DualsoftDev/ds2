namespace Ds2.Runtime.Sim.Engine.Core

open System
open Ds2.UI.Core
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
    let mutable state = SimState.create initialTickMs index.AllWorkGuids index.AllCallGuids
    let mutable pendingCallTransitions = Set.empty<Guid>
    let mutable pendingWorkTransitions = Set.empty<Guid>
    let mutable workGTriggeredResets = Set.empty<(string * string) * (string * string)>

    member _.ApplyWorkTransition(guid: Guid, newState: Status4) : TransitionResult =
        lock syncRoot (fun () ->
            let oldState = state.WorkStates |> Map.tryFind guid |> Option.defaultValue Status4.Ready
            let nodeName = index.WorkName |> Map.tryFind guid |> Option.defaultValue (string guid)
            let deviceName = index.WorkSystemName |> Map.tryFind guid |> Option.defaultValue ""
            if oldState = newState then
                { ActualNewState = newState; OldState = oldState; IsSkipped = false; HasChanged = false; NodeName = nodeName; DeviceName = deviceName }
            else
                state <- SimState.setWorkState guid newState state
                if oldState = Status4.Going then
                    match Map.tryFind guid index.WorkSystemName, Map.tryFind guid index.WorkName with
                    | Some sysName, Some wName ->
                        workGTriggeredResets <- workGTriggeredResets |> Set.filter (fun (pk, _) -> pk <> (sysName, wName))
                    | _ -> ()
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

    member _.MarkWorkPending(guid: Guid)  = lock syncRoot (fun () -> pendingWorkTransitions <- pendingWorkTransitions.Add(guid))
    member _.MarkCallPending(guid: Guid)  = lock syncRoot (fun () -> pendingCallTransitions <- pendingCallTransitions.Add(guid))
    member _.ClearWorkPending(guid: Guid) = lock syncRoot (fun () -> pendingWorkTransitions <- pendingWorkTransitions.Remove(guid))
    member _.ClearCallPending(guid: Guid) = lock syncRoot (fun () -> pendingCallTransitions <- pendingCallTransitions.Remove(guid))
    member _.IsWorkPending(guid: Guid)    = lock syncRoot (fun () -> pendingWorkTransitions.Contains(guid))
    member _.IsCallPending(guid: Guid)    = lock syncRoot (fun () -> pendingCallTransitions.Contains(guid))

    member _.IsResetTriggered(predKey, targetKey) = lock syncRoot (fun () -> workGTriggeredResets.Contains((predKey, targetKey)))
    member _.AddResetTrigger(predKey, targetKey)  = lock syncRoot (fun () -> workGTriggeredResets <- workGTriggeredResets.Add((predKey, targetKey)))

    member _.SetIOValue(apiCallGuid: Guid, value: string) =
        lock syncRoot (fun () -> state <- SimState.setIOValue apiCallGuid value state)

    member _.Reset() =
        lock syncRoot (fun () ->
            state                  <- SimState.reset state
            pendingCallTransitions <- Set.empty
            pendingWorkTransitions <- Set.empty
            workGTriggeredResets   <- Set.empty)

    // ── Token ──
    member _.SetWorkToken(workGuid: Guid, token: TokenValue option) =
        lock syncRoot (fun () -> state <- SimState.setWorkToken workGuid token state)
    member _.GetWorkToken(workGuid: Guid) =
        lock syncRoot (fun () -> SimState.getWorkToken workGuid state)
    member _.AddCompletedToken(token: TokenValue) =
        lock syncRoot (fun () -> state <- SimState.addCompletedToken token state)
    member _.NextToken() =
        lock syncRoot (fun () ->
            let token, newState = SimState.nextToken state
            state <- newState
            token)

    member _.UpdateClock(newClock: TimeSpan) = lock syncRoot (fun () -> state <- { state with Clock = newClock })
    member _.GetState() = lock syncRoot (fun () -> state)
    member _.GetWorkState(workGuid: Guid) = lock syncRoot (fun () -> state.WorkStates |> Map.tryFind workGuid |> Option.defaultValue Status4.Ready)
    member _.GetCallState(callGuid: Guid) = lock syncRoot (fun () -> state.CallStates |> Map.tryFind callGuid |> Option.defaultValue Status4.Ready)
    member _.ForceWorkState(workGuid: Guid, newState: Status4) =
        lock syncRoot (fun () -> state <- SimState.setWorkState workGuid newState state)
    member _.ForceCallState(callGuid: Guid, newState: Status4) =
        lock syncRoot (fun () -> state <- SimState.setCallState callGuid newState state)
