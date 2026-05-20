namespace Ds2.Runtime.Model

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

/// Call Timeout 발동 이벤트 인자
type CallTimeoutArgs = {
    CallGuid: Guid
    CallName: string
    TimeoutMs: int
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
    IOValueEpoch: Map<Guid, int>
    /// v10 §11.2 — 현재 IOValue 가 마지막으로 *변경된* sim clock 시점.
    /// WaitInputStable / WaitInputEdgeStable 의 n ms 안정 측정에 사용.
    IOValueChangedAt: Map<Guid, TimeSpan>
    CallInputEpochSnapshot: Map<Guid, Map<Guid, int>>
    OutputValues: Map<Guid, string>
    SkippedCalls: Set<Guid>
    // ── Token ──
    WorkTokens: Map<Guid, TokenValue option>
    TokenCounter: int
    CompletedTokens: TokenValue list
    WorkMinDurationMet: Set<Guid>
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
        IOValueEpoch = Map.empty
        IOValueChangedAt = Map.empty
        CallInputEpochSnapshot = Map.empty
        OutputValues = Map.empty
        SkippedCalls = Set.empty
        WorkTokens = Map.empty
        TokenCounter = 0
        CompletedTokens = []
        WorkMinDurationMet = Set.empty
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
        let previous = simState.IOValues |> Map.tryFind apiCallGuid
        let isChanged = previous <> Some value
        let nextEpoch =
            if isChanged then
                let current = simState.IOValueEpoch |> Map.tryFind apiCallGuid |> Option.defaultValue 0
                simState.IOValueEpoch.Add(apiCallGuid, current + 1)
            else simState.IOValueEpoch
        // v10 §11.2 — 값이 *바뀌면* ChangedAt = 현재 sim clock. 같은 값으로 재set 은 유지 (안정 유지).
        let nextChangedAt =
            if isChanged then simState.IOValueChangedAt.Add(apiCallGuid, simState.Clock)
            else simState.IOValueChangedAt
        { simState with
            IOValues = simState.IOValues.Add(apiCallGuid, value)
            IOValueEpoch = nextEpoch
            IOValueChangedAt = nextChangedAt }

    /// 지정된 ApiCall 들의 IOValue 만 제거. Simulation/Control Reset 시 다음 사이클을 위해 사용.
    let clearIOValues (apiCallGuids: Guid seq) simState =
        let nextValues = apiCallGuids |> Seq.fold (fun (m: Map<Guid, string>) g -> m.Remove g) simState.IOValues
        let nextChangedAt = apiCallGuids |> Seq.fold (fun (m: Map<Guid, TimeSpan>) g -> m.Remove g) simState.IOValueChangedAt
        { simState with IOValues = nextValues; IOValueChangedAt = nextChangedAt }

    /// v10 §11.2 — 현재 IOValue 가 ON 으로 유지된 ms. 변경 기록이 없으면 0.
    let getIOStableMs (apiCallGuid: Guid) simState : int =
        match simState.IOValueChangedAt |> Map.tryFind apiCallGuid with
        | Some changedAt ->
            let elapsed = simState.Clock - changedAt
            if elapsed < TimeSpan.Zero then 0 else int elapsed.TotalMilliseconds
        | None -> 0

    let snapshotCallInputEpochs (callGuid: Guid) (apiCallGuids: Guid seq) simState =
        let epochMap =
            apiCallGuids
            |> Seq.map (fun apiCallGuid ->
                apiCallGuid,
                simState.IOValueEpoch
                |> Map.tryFind apiCallGuid
                |> Option.defaultValue 0)
            |> Map.ofSeq
        { simState with CallInputEpochSnapshot = simState.CallInputEpochSnapshot.Add(callGuid, epochMap) }

    let clearCallInputEpochSnapshot (callGuid: Guid) simState =
        { simState with CallInputEpochSnapshot = simState.CallInputEpochSnapshot.Remove(callGuid) }

    let setOutputValue (apiCallGuid: Guid) (value: string) simState =
        { simState with OutputValues = simState.OutputValues.Add(apiCallGuid, value) }

    let clearOutputValues (apiCallGuids: Guid seq) simState =
        let next = apiCallGuids |> Seq.fold (fun (m: Map<Guid, string>) g -> m.Remove g) simState.OutputValues
        { simState with OutputValues = next }

    // ── Token helpers ──

    let getWorkToken (guid: Guid) simState =
        simState.WorkTokens |> Map.tryFind guid |> Option.flatten

    let setWorkToken (guid: Guid) (token: TokenValue option) simState =
        { simState with WorkTokens = simState.WorkTokens.Add(guid, token) }

    let addCompletedToken (token: TokenValue) simState =
        { simState with CompletedTokens = token :: simState.CompletedTokens }

    let markMinDurationMet (guid: Guid) simState =
        { simState with WorkMinDurationMet = simState.WorkMinDurationMet.Add(guid) }

    let clearMinDuration (guid: Guid) simState =
        { simState with WorkMinDurationMet = simState.WorkMinDurationMet.Remove(guid) }

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
            IOValueEpoch = Map.empty
            IOValueChangedAt = Map.empty
            CallInputEpochSnapshot = Map.empty
            OutputValues = Map.empty
            SkippedCalls = Set.empty
            WorkTokens = Map.empty
            TokenCounter = 0
            CompletedTokens = []
            WorkMinDurationMet = Set.empty
            TokenOrigins = Map.empty
            TokenOriginCounters = Map.empty
            WorkCycleEpoch = Map.empty
            CallRxEpochSnapshot = Map.empty
    }
