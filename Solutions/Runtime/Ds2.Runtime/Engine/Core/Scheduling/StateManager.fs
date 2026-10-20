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

    let canonicalWorkGuid (guid: Guid) =
        SimIndex.canonicalWorkGuid index guid

    let referenceGroupOf (guid: Guid) =
        SimIndex.referenceGroupOf index guid

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

    member _.ApplyWorkTransition(guid: Guid, newState: Status4, shouldSkipWork: Guid -> bool) : TransitionResult =
        lock syncRoot (fun () ->
            let oldState = groupState guid
            let nodeName = index.WorkName |> Map.tryFind guid |> Option.defaultValue (string guid)
            let deviceName = index.WorkSystemName |> Map.tryFind guid |> Option.defaultValue ""
            let actualNewState, isSkipped =
                if oldState = Status4.Ready && newState = Status4.Going && shouldSkipWork guid then
                    Status4.Finish, true
                else newState, false
            if oldState = actualNewState then
                { ActualNewState = actualNewState; OldState = oldState; IsSkipped = false; HasChanged = false; NodeName = nodeName; DeviceName = deviceName }
            else
                setWorkStateForGroup guid actualNewState
                let canonical = canonicalWorkGuid guid
                if oldState = Status4.Going then
                    workMinDurationMet <- workMinDurationMet.Remove(canonical)
                    state <- SimState.clearMinDuration canonical state
                    workGTriggeredResets <- workGTriggeredResets |> Set.filter (fun (predGuid, _) -> predGuid <> canonical)
                if actualNewState = Status4.Ready then
                    // Ready 복귀 시 이 Work를 target으로 갖는 reset trigger 클리어
                    // → 다음 사이클에서 동일 predecessor가 다시 reset 트리거 가능
                    workGTriggeredResets <- workGTriggeredResets |> Set.filter (fun (_, targetGuid) -> targetGuid <> canonical)
                { ActualNewState = actualNewState; OldState = oldState; IsSkipped = isSkipped; HasChanged = true; NodeName = nodeName; DeviceName = deviceName })

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

    member _.MarkWorkPending(guid: Guid)  = lock syncRoot (fun () -> pendingWorkTransitions <- pendingWorkTransitions.Add(canonicalWorkGuid guid))
    member _.MarkCallPending(guid: Guid)  = lock syncRoot (fun () -> pendingCallTransitions <- pendingCallTransitions.Add(canonicalCallGuid guid))
    member _.ClearWorkPending(guid: Guid) = lock syncRoot (fun () -> pendingWorkTransitions <- pendingWorkTransitions.Remove(canonicalWorkGuid guid))
    member _.ClearCallPending(guid: Guid) = lock syncRoot (fun () -> pendingCallTransitions <- pendingCallTransitions.Remove(canonicalCallGuid guid))
    member _.IsWorkPending(guid: Guid)    = lock syncRoot (fun () -> pendingWorkTransitions.Contains(canonicalWorkGuid guid))
    member _.IsCallPending(guid: Guid)    = lock syncRoot (fun () -> pendingCallTransitions.Contains(canonicalCallGuid guid))

    member _.IsResetTriggered(predKey, targetKey) =
        lock syncRoot (fun () ->
            workGTriggeredResets.Contains((canonicalWorkGuid predKey, canonicalWorkGuid targetKey)))
    member _.AddResetTrigger(predKey, targetKey)  =
        lock syncRoot (fun () ->
            workGTriggeredResets <- workGTriggeredResets.Add((canonicalWorkGuid predKey, canonicalWorkGuid targetKey)))

    member _.MarkMinDurationMet(guid: Guid) =
        lock syncRoot (fun () ->
            let canonical = canonicalWorkGuid guid
            workMinDurationMet <- workMinDurationMet.Add(canonical)
            state <- SimState.markMinDurationMet canonical state)
    member _.IsMinDurationMet(guid: Guid)   = lock syncRoot (fun () -> workMinDurationMet.Contains(canonicalWorkGuid guid))
    member _.ClearMinDuration(guid: Guid)   =
        lock syncRoot (fun () ->
            let canonical = canonicalWorkGuid guid
            workMinDurationMet <- workMinDurationMet.Remove(canonical)
            state <- SimState.clearMinDuration canonical state)

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

    /// Diagnostic-only source marking; the same value/epoch/stability rules apply.
    member _.SetSynthesizedIOValue(apiCallGuid: Guid, value: string) =
        lock syncRoot (fun () -> state <- SimState.setSynthesizedIOValue apiCallGuid value state)

    member _.ClearIOValues(apiCallGuids: Guid seq) =
        lock syncRoot (fun () -> state <- SimState.clearIOValues apiCallGuids state)

    member _.SnapshotCallInputEpochs(callGuid: Guid, apiCallGuids: Guid seq) =
        lock syncRoot (fun () -> state <- SimState.snapshotCallInputEpochs callGuid apiCallGuids state)

    member _.ClearCallInputEpochSnapshot(callGuid: Guid) =
        lock syncRoot (fun () -> state <- SimState.clearCallInputEpochSnapshot callGuid state)

    member _.SetOutputValue(apiCallGuid: Guid, value: string) =
        lock syncRoot (fun () -> state <- SimState.setOutputValue apiCallGuid value state)

    member _.ClearOutputValues(apiCallGuids: Guid seq) =
        lock syncRoot (fun () -> state <- SimState.clearOutputValues apiCallGuids state)

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
        lock syncRoot (fun () ->
            setWorkTokenForGroup workGuid token
            // 새로 놓거나 비운 토큰의 출처는 모른다 — 넘겨받은 경우만 SetWorkTokenFrom 이 다시 적는다.
            state <- SimState.setWorkTokenFrom (canonicalWorkGuid workGuid) None state)
    /// FS1 CAT-05-R07 — 토큰을 넘겨준 Work 를 적는다(SetWorkToken 바로 뒤에 부른다).
    member _.SetWorkTokenFrom(workGuid: Guid, fromWork: Guid) =
        lock syncRoot (fun () -> state <- SimState.setWorkTokenFrom (canonicalWorkGuid workGuid) (Some fromWork) state)
    member _.GetWorkToken(workGuid: Guid) =
        lock syncRoot (fun () -> SimState.getWorkToken (canonicalWorkGuid workGuid) state)
    member _.AddCompletedToken(token: TokenValue) =
        lock syncRoot (fun () -> state <- SimState.addCompletedToken token state)
    member _.SetTokenOrigin(token: TokenValue, workName: string) =
        lock syncRoot (fun () ->
            match token with IntToken id -> state <- SimState.setTokenOrigin id workName state)
    /// #8938 — 토큰이 실은 제품 사양(TokenSpec.Id).
    member _.SetTokenSpec(token: TokenValue, specId: int) =
        lock syncRoot (fun () ->
            match token with IntToken id -> state <- SimState.setTokenSpecId id specId state)
    member _.NextToken() =
        lock syncRoot (fun () ->
            let token, newState = SimState.nextToken state
            state <- newState
            token)

    // ── Epoch (WaitForCompletion) ──
    member _.IncrementWorkEpoch(workGuid: Guid) =
        lock syncRoot (fun () -> state <- SimState.incrementWorkEpoch (canonicalWorkGuid workGuid) state)
    member _.GetWorkEpoch(workGuid: Guid) =
        lock syncRoot (fun () -> SimState.getWorkEpoch (canonicalWorkGuid workGuid) state)
    member _.SnapshotCallRxEpochs(callGuid: Guid, rxGuids: Guid list) =
        lock syncRoot (fun () ->
            let epochMap = rxGuids |> List.map (fun rx -> rx, SimState.getWorkEpoch (canonicalWorkGuid rx) state) |> Map.ofList
            let finishMap = rxGuids |> List.map (fun rx -> rx, SimState.getWorkFinishEpoch (canonicalWorkGuid rx) state) |> Map.ofList
            state <- state |> SimState.snapshotCallRxEpochs callGuid epochMap |> SimState.snapshotCallRxFinishEpochs callGuid finishMap)
    member _.ClearCallRxEpochSnapshot(callGuid: Guid) =
        lock syncRoot (fun () -> state <- SimState.clearCallRxEpochSnapshot callGuid state)
    /// FS1-Q25 — Finish 전이 사건을 센다(canonical).
    member _.IncrementWorkFinishEpoch(workGuid: Guid) =
        lock syncRoot (fun () -> state <- SimState.incrementWorkFinishEpoch (canonicalWorkGuid workGuid) state)
    /// FS1-Q25 — Call Going 때 Tx 의 Going 횟수를 적어 둔다. 늘었으면 그 회차의 명령은 이미 나갔다.
    member _.SnapshotCallTxEpochs(callGuid: Guid, txGuids: Guid list) =
        lock syncRoot (fun () ->
            let epochMap = txGuids |> List.map (fun tx -> tx, SimState.getWorkEpoch (canonicalWorkGuid tx) state) |> Map.ofList
            state <- SimState.snapshotCallTxEpochs callGuid epochMap state)
    member _.ClearCallTxEpochSnapshot(callGuid: Guid) =
        lock syncRoot (fun () -> state <- SimState.clearCallTxEpochSnapshot callGuid state)

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
    /// FS1 CAT-03-R04 — `finished` 로 적재한 완료를 표시한다(같은 참조 묶음 전체).
    member _.MarkLoadedFinish(workGuid: Guid) =
        lock syncRoot (fun () -> state <- SimState.markLoadedFinish (referenceGroupOf workGuid) state)
    member _.ForceCallState(callGuid: Guid, newState: Status4) =
        lock syncRoot (fun () -> state <- SimState.setCallState callGuid newState state)
