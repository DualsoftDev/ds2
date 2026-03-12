namespace Ds2.Runtime.Sim.Model

open System
open Ds2.Core

/// 노드 타입 리터럴 상수
[<AutoOpen>]
module NodeTypes =
    [<Literal>]
    let NodeTypeWork = "work"
    [<Literal>]
    let NodeTypeCall = "call"

/// 시뮬레이션 상태 (Running/Paused/Stopped)
type SimulationStatus =
    | Running
    | Paused
    | Stopped

/// Work 상태 변경 이벤트 인자
type WorkStateChangedArgs = {
    WorkGuid: Guid
    WorkName: string
    PreviousState: Status4
    NewState: Status4
    Clock: TimeSpan
}

/// Call 상태 변경 이벤트 인자
type CallStateChangedArgs = {
    CallGuid: Guid
    CallName: string
    PreviousState: Status4
    NewState: Status4
    IsSkipped: bool
    Clock: TimeSpan
}

/// 시뮬레이션 상태 변경 이벤트 인자
type SimulationStatusChangedArgs = {
    PreviousStatus: SimulationStatus
    NewStatus: SimulationStatus
}

/// 시뮬레이션 상태 (immutable snapshot)
type SimState = {
    WorkStates: Map<Guid, Status4>
    CallStates: Map<Guid, Status4>
    WorkProgress: Map<Guid, float>
    Clock: TimeSpan
    TickMs: int
    IOValues: Map<Guid, string>
    SkippedCalls: Set<Guid>
}

module SimState =
    let create tickMs (workGuids: Guid seq) (callGuids: Guid seq) = {
        WorkStates = workGuids |> Seq.map (fun guid -> guid, Status4.Ready) |> Map.ofSeq
        CallStates = callGuids |> Seq.map (fun guid -> guid, Status4.Ready) |> Map.ofSeq
        WorkProgress = workGuids |> Seq.map (fun guid -> guid, 0.0) |> Map.ofSeq
        Clock = TimeSpan.Zero
        TickMs = tickMs
        IOValues = Map.empty
        SkippedCalls = Set.empty
    }

    let setState nodeType (nodeGuid: Guid) state simState =
        match nodeType with
        | NodeTypeWork ->
            let progress =
                match state with
                | Status4.Ready -> 0.0
                | Status4.Finish -> 1.0
                | _ -> simState.WorkProgress |> Map.tryFind nodeGuid |> Option.defaultValue 0.0
            { simState with
                WorkStates = simState.WorkStates.Add(nodeGuid, state)
                WorkProgress = simState.WorkProgress.Add(nodeGuid, progress) }
        | NodeTypeCall ->
            { simState with CallStates = simState.CallStates.Add(nodeGuid, state) }
        | _ -> simState

    let setIOValue (apiCallGuid: Guid) (value: string) simState =
        { simState with IOValues = simState.IOValues.Add(apiCallGuid, value) }

    let reset simState = {
        simState with
            WorkStates = simState.WorkStates |> Map.map (fun _ _ -> Status4.Ready)
            CallStates = simState.CallStates |> Map.map (fun _ _ -> Status4.Ready)
            WorkProgress = simState.WorkProgress |> Map.map (fun _ _ -> 0.0)
            Clock = TimeSpan.Zero
            IOValues = Map.empty
            SkippedCalls = Set.empty
    }
