namespace Ds2.Runtime.Engine.Passive

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.IO

type PassiveInferenceTarget =
    | Work = 0
    | Call = 1

type PassiveInferenceLogKind =
    | System = 0
    | Warn = 1

[<CLIMutable>]
type PassiveInferenceAction = {
    TargetKind: PassiveInferenceTarget
    TargetGuid: Guid
    State: Status4
}

[<CLIMutable>]
type PassiveInferenceLog = {
    Kind: PassiveInferenceLogKind
    Message: string
}

type internal WorkLearning() =
    member val Sequence = ResizeArray<string>() with get
    member val GroupKeys = ResizeArray<string * string>() with get
    member val GroupStartTicks = ResizeArray<int64>() with get
    member val GroupEndTicks = ResizeArray<int64>() with get
    member val LearningCurrentKey: (string * string) option = None with get, set
    member val DetectedPeriod: int option = None with get, set
    member val WorkFinishGroupIdx: int option = None with get, set
    member val WorkGoingStartGroupIdx: int option = None with get, set
    member val ProvisionalHeadGroupIdx: int option = None with get, set
    member val ProvisionalTailGroupIdx: int option = None with get, set
    member val LargestGapTicks = 0L with get, set
    member val Synced = false with get, set
    member val NextExpectedGroupIdx = 0 with get, set
    member val CycleSequence = ResizeArray<string>() with get
    member val CycleGroupKeys = ResizeArray<string * string>() with get
    member val LiveCurrentKey: (string * string) option = None with get, set
    member val LiveCurrentTokens = HashSet<string>(StringComparer.Ordinal) with get
    member val HasObservedSyncedGoing = false with get, set

type internal StateOverlay(getWorkState: Func<Guid, Status4>, getCallState: Func<Guid, Status4>) =
    let workStates = Dictionary<Guid, Status4>()
    let callStates = Dictionary<Guid, Status4>()

    member _.GetWorkState(workGuid: Guid) =
        match workStates.TryGetValue(workGuid) with
        | true, state -> state
        | _ -> getWorkState.Invoke(workGuid)

    member _.SetWorkState(workGuid: Guid, state: Status4) =
        workStates[workGuid] <- state

    member _.GetCallState(callGuid: Guid) =
        match callStates.TryGetValue(callGuid) with
        | true, state -> state
        | _ -> getCallState.Invoke(callGuid)

    member _.SetCallState(callGuid: Guid, state: Status4) =
        callStates[callGuid] <- state

type internal PassiveWorkContext = {
    Index: SimIndex
    IoMap: SignalIOMap
    RuntimeMode: RuntimeMode
    AddLog: PassiveInferenceLogKind -> string -> unit
    WorkLearning: Dictionary<Guid, WorkLearning>
    WorkUniqueAddresses: Dictionary<Guid, HashSet<string>>
    WorkPositiveFamilyTokens: Dictionary<Guid, Dictionary<string, string>>
    WorkResetTargetsByPred: Dictionary<Guid, ResizeArray<Guid>>
    CallOutHighAddresses: Dictionary<Guid, HashSet<string>>
    CallInHighAddresses: Dictionary<Guid, HashSet<string>>
}
