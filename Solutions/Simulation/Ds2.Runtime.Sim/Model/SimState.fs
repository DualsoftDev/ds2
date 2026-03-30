namespace Ds2.Runtime.Sim.Model

open System
open Ds2.Core

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

/// 토큰 이벤트 종류
type TokenEventKind =
    | Seed
    | Shift
    | Complete
    | Blocked
    | Discard
    | BlockedOnHoming
    | Conflict

/// 토큰 이벤트 인자
type TokenEventArgs = {
    Kind: TokenEventKind
    Token: TokenValue
    WorkGuid: Guid
    WorkName: string
    TargetWorkGuid: Guid option
    TargetWorkName: string option
    Clock: TimeSpan
}

/// 시뮬레이션 상태 (immutable snapshot)
type SimState = {
    WorkStates: Map<Guid, Status4>
    CallStates: Map<Guid, Status4>
    WorkProgress: Map<Guid, float>
    FlowStates: Map<Guid, FlowTag>
    Clock: TimeSpan
    TickMs: int
    IOValues: Map<Guid, string>
    SkippedCalls: Set<Guid>
    // ── Token ──
    WorkTokens: Map<Guid, TokenValue option>
    TokenCounter: int
    CompletedTokens: TokenValue list
    /// 토큰 번호 → (이름, 이름별 순번)
    TokenOrigins: Map<int, string * int>
    /// 이름별 발행 카운터
    TokenOriginCounters: Map<string, int>
    // ── Epoch (WaitForCompletion 지원) ──
    /// Work의 Going 진입 횟수 (canonical 기준)
    WorkCycleEpoch: Map<Guid, int>
    /// Call Going 시점의 RxWork epoch 스냅샷
    CallRxEpochSnapshot: Map<Guid, Map<Guid, int>>
}

module SimState =
    let create tickMs (workGuids: Guid seq) (callGuids: Guid seq) (flowGuids: Guid seq) = {
        WorkStates = workGuids |> Seq.map (fun guid -> guid, Status4.Ready) |> Map.ofSeq
        CallStates = callGuids |> Seq.map (fun guid -> guid, Status4.Ready) |> Map.ofSeq
        WorkProgress = workGuids |> Seq.map (fun guid -> guid, 0.0) |> Map.ofSeq
        FlowStates = flowGuids |> Seq.map (fun guid -> guid, FlowTag.Ready) |> Map.ofSeq
        Clock = TimeSpan.Zero
        TickMs = tickMs
        IOValues = Map.empty
        SkippedCalls = Set.empty
        WorkTokens = Map.empty
        TokenCounter = 0
        CompletedTokens = []
        TokenOrigins = Map.empty
        TokenOriginCounters = Map.empty
        WorkCycleEpoch = Map.empty
        CallRxEpochSnapshot = Map.empty
    }

    let setWorkState (guid: Guid) state simState =
        let progress =
            match state with
            | Status4.Ready -> 0.0
            | Status4.Finish -> 1.0
            | _ -> simState.WorkProgress |> Map.tryFind guid |> Option.defaultValue 0.0
        { simState with
            WorkStates = simState.WorkStates.Add(guid, state)
            WorkProgress = simState.WorkProgress.Add(guid, progress) }

    let setCallState (guid: Guid) state simState =
        { simState with CallStates = simState.CallStates.Add(guid, state) }

    let setIOValue (apiCallGuid: Guid) (value: string) simState =
        { simState with IOValues = simState.IOValues.Add(apiCallGuid, value) }

    // ── Token helpers ──

    let getWorkToken (guid: Guid) simState =
        simState.WorkTokens |> Map.tryFind guid |> Option.flatten

    let setWorkToken (guid: Guid) (token: TokenValue option) simState =
        { simState with WorkTokens = simState.WorkTokens.Add(guid, token) }

    let addCompletedToken (token: TokenValue) simState =
        { simState with CompletedTokens = token :: simState.CompletedTokens }

    let setTokenOrigin (tokenId: int) (originName: string) simState =
        let count = simState.TokenOriginCounters |> Map.tryFind originName |> Option.defaultValue 0
        let next = count + 1
        { simState with
            TokenOrigins = simState.TokenOrigins.Add(tokenId, (originName, next))
            TokenOriginCounters = simState.TokenOriginCounters.Add(originName, next) }

    let nextToken simState =
        let counter = simState.TokenCounter + 1
        IntToken counter, { simState with TokenCounter = counter }

    let setFlowState (guid: Guid) (tag: FlowTag) simState =
        { simState with FlowStates = simState.FlowStates.Add(guid, tag) }

    // ── Epoch helpers (WaitForCompletion) ──

    let incrementWorkEpoch (guid: Guid) simState =
        let current = simState.WorkCycleEpoch |> Map.tryFind guid |> Option.defaultValue 0
        { simState with WorkCycleEpoch = simState.WorkCycleEpoch.Add(guid, current + 1) }

    let getWorkEpoch (guid: Guid) simState =
        simState.WorkCycleEpoch |> Map.tryFind guid |> Option.defaultValue 0

    let snapshotCallRxEpochs (callGuid: Guid) (rxEpochs: Map<Guid, int>) simState =
        { simState with CallRxEpochSnapshot = simState.CallRxEpochSnapshot.Add(callGuid, rxEpochs) }

    let getCallRxEpochSnapshot (callGuid: Guid) simState =
        simState.CallRxEpochSnapshot |> Map.tryFind callGuid

    let clearCallRxEpochSnapshot (callGuid: Guid) simState =
        { simState with CallRxEpochSnapshot = simState.CallRxEpochSnapshot.Remove(callGuid) }

    let reset simState = {
        simState with
            WorkStates = simState.WorkStates |> Map.map (fun _ _ -> Status4.Ready)
            CallStates = simState.CallStates |> Map.map (fun _ _ -> Status4.Ready)
            WorkProgress = simState.WorkProgress |> Map.map (fun _ _ -> 0.0)
            FlowStates = simState.FlowStates |> Map.map (fun _ _ -> FlowTag.Ready)
            Clock = TimeSpan.Zero
            IOValues = Map.empty
            SkippedCalls = Set.empty
            WorkTokens = Map.empty
            TokenCounter = 0
            CompletedTokens = []
            TokenOrigins = Map.empty
            TokenOriginCounters = Map.empty
            WorkCycleEpoch = Map.empty
            CallRxEpochSnapshot = Map.empty
    }
