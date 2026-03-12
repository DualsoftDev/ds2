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
type StateManager(index: SimIndex, initialTickMs: int) =
    let mutable state = SimState.create initialTickMs index.AllWorkGuids index.AllCallGuids
    let mutable pendingCallTransitions = Set.empty<Guid>
    let mutable pendingWorkTransitions = Set.empty<Guid>
    let mutable workGTriggeredResets = Set.empty<(string * string) * (string * string)>

    let getNodeName nodeType (nodeGuid: Guid) =
        match nodeType with
        | NodeTypeWork -> index.WorkName |> Map.tryFind nodeGuid |> Option.defaultValue (string nodeGuid)
        | NodeTypeCall ->
            DsQuery.getCall nodeGuid index.Store
            |> Option.map (fun c -> c.Name) |> Option.defaultValue (string nodeGuid)
        | _ -> string nodeGuid

    let getDeviceName nodeType (nodeGuid: Guid) =
        match nodeType with
        | NodeTypeWork -> index.WorkSystemName |> Map.tryFind nodeGuid |> Option.defaultValue ""
        | NodeTypeCall ->
            index.CallWorkGuid |> Map.tryFind nodeGuid
            |> Option.bind (fun wg -> index.WorkSystemName |> Map.tryFind wg) |> Option.defaultValue ""
        | _ -> ""

    member _.ApplyTransition(nodeType: string, nodeGuid: Guid, newState: Status4, shouldSkipCall: Guid -> bool) : TransitionResult =
        let oldState =
            match nodeType with
            | NodeTypeWork -> state.WorkStates |> Map.tryFind nodeGuid |> Option.defaultValue Status4.Ready
            | NodeTypeCall -> state.CallStates |> Map.tryFind nodeGuid |> Option.defaultValue Status4.Ready
            | _ -> Status4.Ready
        let nodeName = getNodeName nodeType nodeGuid
        let deviceName = getDeviceName nodeType nodeGuid

        let actualNewState, isSkipped =
            if nodeType = NodeTypeCall && oldState = Status4.Ready && newState = Status4.Going && shouldSkipCall nodeGuid then
                Status4.Finish, true
            else newState, false

        if oldState = actualNewState then
            { ActualNewState = actualNewState; OldState = oldState; IsSkipped = false; HasChanged = false; NodeName = nodeName; DeviceName = deviceName }
        else
            state <- SimState.setState nodeType nodeGuid actualNewState state
            if nodeType = NodeTypeCall then
                if isSkipped then state <- { state with SkippedCalls = state.SkippedCalls.Add(nodeGuid) }
                elif actualNewState = Status4.Ready then state <- { state with SkippedCalls = state.SkippedCalls.Remove(nodeGuid) }
            if nodeType = NodeTypeWork && oldState = Status4.Going then
                match index.WorkSystemName |> Map.tryFind nodeGuid, index.WorkName |> Map.tryFind nodeGuid with
                | Some sysName, Some wName ->
                    let predKey = (sysName, wName)
                    workGTriggeredResets <- workGTriggeredResets |> Set.filter (fun (pk, _) -> pk <> predKey)
                | _ -> ()
            { ActualNewState = actualNewState; OldState = oldState; IsSkipped = isSkipped; HasChanged = true; NodeName = nodeName; DeviceName = deviceName }

    member _.MarkPending(nodeType: string, nodeGuid: Guid) =
        if nodeType = NodeTypeCall then pendingCallTransitions <- pendingCallTransitions.Add(nodeGuid)
        else pendingWorkTransitions <- pendingWorkTransitions.Add(nodeGuid)

    member _.ClearPending(nodeType: string, nodeGuid: Guid) =
        if nodeType = NodeTypeCall then pendingCallTransitions <- pendingCallTransitions.Remove(nodeGuid)
        else pendingWorkTransitions <- pendingWorkTransitions.Remove(nodeGuid)

    member _.IsPending(nodeType: string, nodeGuid: Guid) : bool =
        if nodeType = NodeTypeCall then pendingCallTransitions.Contains(nodeGuid)
        else pendingWorkTransitions.Contains(nodeGuid)

    member _.IsResetTriggered(predKey, targetKey) = workGTriggeredResets.Contains((predKey, targetKey))
    member _.AddResetTrigger(predKey, targetKey) = workGTriggeredResets <- workGTriggeredResets.Add((predKey, targetKey))

    member _.SetIOValue(apiCallGuid: Guid, value: string) =
        state <- SimState.setIOValue apiCallGuid value state

    member _.Reset() =
        state <- SimState.reset state
        pendingCallTransitions <- Set.empty
        pendingWorkTransitions <- Set.empty
        workGTriggeredResets <- Set.empty

    member _.UpdateClock(newClock: TimeSpan) = state <- { state with Clock = newClock }
    member _.GetState() = state
    member _.GetWorkState(workGuid: Guid) = state.WorkStates |> Map.tryFind workGuid |> Option.defaultValue Status4.Ready
    member _.GetCallState(callGuid: Guid) = state.CallStates |> Map.tryFind callGuid |> Option.defaultValue Status4.Ready
    member _.ForceWorkState(workGuid: Guid, newState: Status4) =
        state <- SimState.setState NodeTypeWork workGuid newState state
    member _.ForceCallState(callGuid: Guid, newState: Status4) =
        state <- SimState.setState NodeTypeCall callGuid newState state
