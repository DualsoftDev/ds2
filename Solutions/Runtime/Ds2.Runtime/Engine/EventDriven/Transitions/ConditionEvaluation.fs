namespace Ds2.Runtime.Engine

open System
open Ds2.Core
open Ds2.Runtime.Model
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Engine.Scheduler

module internal ConditionEvaluation =

    type Context = {
        Index: SimIndex
        StateManager: StateManager
        Scheduler: EventScheduler
        IsWorkFrozen: Guid -> bool
        CanStartWork: Guid -> bool
        CanStartCall: Guid -> bool
        CanCompleteCall: Guid -> bool
        ScheduleConditionEvaluation: unit -> unit
        ShiftToken: Guid -> TokenValue -> unit
        EmitTokenEvent: TokenEventKind -> TokenValue -> Guid -> Guid option -> unit
        CanReceiveToken: Guid -> bool
        ApplyWorkTransition: Guid -> Status4 -> unit
    }

    /// MarkWorkPending + ScheduleNow(WorkTransition) 공통 시퀀스
    let private scheduleWorkTransition (ctx: Context) workGuid targetStatus =
        ctx.StateManager.MarkWorkPending(workGuid)
        ctx.Scheduler.ScheduleNow(
            ScheduledEventType.WorkTransition(workGuid, targetStatus),
            ScheduledEvent.PriorityStateChange)
        |> ignore

    /// MarkCallPending + ScheduleNow(CallTransition) 공통 시퀀스
    let private scheduleCallTransition (ctx: Context) callGuid targetStatus =
        ctx.StateManager.MarkCallPending(callGuid)
        ctx.Scheduler.ScheduleNow(
            ScheduledEventType.CallTransition(callGuid, targetStatus),
            ScheduledEvent.PriorityStateChange)
        |> ignore


    let private tryQueueWorkReset (ctx: Context) (scheduledGoingGuids: Set<Guid>) workGuid =
        let tryFindResetTriggerPred resetPreds =
            // Rising edge only: pred가 이번 evaluation cycle에서 Ready→Going으로 방금 스케줄된 경우만 트리거.
            // pred가 이미 Going 상태(level)인 경우는 handleWorkGoingTransition의 triggerImmediateResets가
            // 사이클 시작 순간에 이미 처리하므로 여기서 중복 처리하지 않는다.
            resetPreds
            |> List.tryFind (fun predGuid ->
                scheduledGoingGuids.Contains(predGuid)
                && not (ctx.StateManager.IsResetTriggered(predGuid, workGuid)))

        let emitConflictIfTokenHeld () =
            match SimState.getWorkToken workGuid (ctx.StateManager.GetState()) with
            | Some token -> ctx.EmitTokenEvent Conflict token workGuid None
            | None -> ()

        let queueWorkResetTransition predGuid =
            ctx.StateManager.AddResetTrigger(predGuid, workGuid)
            scheduleWorkTransition ctx workGuid Status4.Homing

        match WorkConditionChecker.collectResetPreds ctx.Index workGuid with
        | Some (_, _, resetPreds) ->
            match tryFindResetTriggerPred resetPreds with
            | Some predGuid ->
                if SimState.getWorkToken workGuid (ctx.StateManager.GetState()) |> Option.isSome then
                    emitConflictIfTokenHeld ()
                queueWorkResetTransition predGuid
            | None -> ()
        | None -> ()

    /// Work의 부모 Flow가 Pause 상태면 새 시작 차단
    let private isFlowPausedForWork (ctx: Context) (workGuid: Guid) =
        ctx.Index.WorkFlowGuid
        |> Map.tryFind workGuid
        |> Option.map (fun flowGuid -> ctx.StateManager.GetFlowState(flowGuid) = FlowTag.Pause)
        |> Option.defaultValue false

    let evaluateWorkStarts (ctx: Context) () =
        let mutable scheduledGoingGuids = Set.empty<Guid>
        for workGuid in ctx.Index.AllWorkGuids do
            if ctx.StateManager.GetWorkState(workGuid) = Status4.Ready
               && not (isFlowPausedForWork ctx workGuid)
               && ctx.CanStartWork workGuid
               && not (ctx.StateManager.IsWorkPending(workGuid)) then
                scheduleWorkTransition ctx workGuid Status4.Going
                scheduledGoingGuids <- scheduledGoingGuids.Add(workGuid)
        scheduledGoingGuids

    let evaluateWorkResets (ctx: Context) (scheduledGoingGuids: Set<Guid>) =
        for workGuid in ctx.Index.AllWorkGuids do
            if ctx.StateManager.GetWorkState(workGuid) = Status4.Finish
               && not (isFlowPausedForWork ctx workGuid)
               && not (ctx.StateManager.IsWorkPending(workGuid)) then
                tryQueueWorkReset ctx scheduledGoingGuids workGuid

    let evaluateCallStarts (ctx: Context) () =
        // 1. Going 후보 수집
        let candidates = ResizeArray<Guid>()
        for callGuid in ctx.Index.AllCallGuids do
            match ctx.Index.CallWorkGuid |> Map.tryFind callGuid with
            | Some workGuid ->
                if ctx.StateManager.GetCallState(callGuid) = Status4.Ready
                   && not (ctx.IsWorkFrozen workGuid)
                   && not (isFlowPausedForWork ctx workGuid)
                   && ctx.StateManager.GetWorkState(workGuid) = Status4.Going
                   && ctx.CanStartCall callGuid
                   && not (ctx.StateManager.IsCallPending(callGuid)) then
                    candidates.Add(callGuid)
            | None -> ()

        // 2. Race Condition 탈락 — 이미 Going/Pending인 exclusion 상대 + 이번 사이클 먼저 온 놈 우선
        let accepted = System.Collections.Generic.HashSet<Guid>()
        let rejected = System.Collections.Generic.HashSet<Guid>()
        // 이미 점유 중이거나 Pending인 Call의 exclusion 상대를 미리 rejected에 등록.
        // 다시 준비를 기다리는 Going Call 은 점유하지 않는다(FS1-Q25, WorkConditionChecker.callOccupies).
        let occupancyState = ctx.StateManager.GetState()
        for callGuid in ctx.Index.AllCallGuids do
            if WorkConditionChecker.callOccupies ctx.Index occupancyState callGuid
               || ctx.StateManager.IsCallPending(callGuid) then
                match ctx.Index.CallRaceExclusions |> Map.tryFind callGuid with
                | Some excludedSet ->
                    for ex in excludedSet do
                        rejected.Add(ex) |> ignore
                | None -> ()
        // Call의 TxWork가 실제로 Going 가능한지 (Finish 상태면 불가)
        let canTxWorksGo callGuid =
            let txGuids = SimIndex.txWorkGuids ctx.Index callGuid
            txGuids.IsEmpty || txGuids |> List.exists (fun tx -> ctx.StateManager.GetWorkState(tx) <> Status4.Finish)
        for callGuid in candidates do
            if not (rejected.Contains(callGuid)) then
                // TxWork Going 불가능한 Call은 점유 안 함 (상대를 막지 않음)
                if canTxWorksGo callGuid then
                    accepted.Add(callGuid) |> ignore
                    match ctx.Index.CallRaceExclusions |> Map.tryFind callGuid with
                    | Some excludedSet ->
                        for ex in excludedSet do
                            rejected.Add(ex) |> ignore
                    | None -> ()
                else
                    accepted.Add(callGuid) |> ignore  // Going은 시키되 점유는 안 함

        // 3. 통과한 Call만 스케줄
        for callGuid in accepted do
            scheduleCallTransition ctx callGuid Status4.Going

    let evaluateCallCompletions (ctx: Context) () =
        for callGuid in ctx.Index.AllCallGuids do
            match ctx.Index.CallWorkGuid |> Map.tryFind callGuid with
            | Some workGuid ->
                if ctx.StateManager.GetCallState(callGuid) = Status4.Going
                   && not (ctx.IsWorkFrozen workGuid)
                   && ctx.CanCompleteCall callGuid
                   && not (ctx.StateManager.IsCallPending(callGuid)) then
                    scheduleCallTransition ctx callGuid Status4.Finish
            | None -> ()

    let evaluateWorkCompletions (ctx: Context) () =
        for workGuid in ctx.Index.AllWorkGuids do
            if ctx.StateManager.GetWorkState(workGuid) = Status4.Going
               && not (ctx.IsWorkFrozen workGuid)
               && not (ctx.StateManager.IsWorkPending(workGuid)) then
                let callGuids = SimIndex.findOrEmpty workGuid ctx.Index.WorkCallGuids
                if not callGuids.IsEmpty
                   && ctx.StateManager.IsMinDurationMet(workGuid)
                   && callGuids |> List.forall (fun callGuid -> ctx.StateManager.GetCallState(callGuid) = Status4.Finish) then
                    scheduleWorkTransition ctx workGuid Status4.Finish

    let retryBlockedTokens (ctx: Context) () =
        let canRetryBlockedToken workGuid =
            let isSink = ctx.Index.TokenSinkGuids.Contains(workGuid)
            let successors = ctx.Index.WorkTokenSuccessors |> Map.tryFind workGuid |> Option.defaultValue []
            // 뒤가 같은 토큰을 이미 받았으면 합칠 수 있다(FS1 CAT-11-R06, TokenFlow.shiftToken) — 다시 시도한다.
            let ownToken = SimState.getWorkToken workGuid (ctx.StateManager.GetState())
            let holdsSameToken g = ownToken.IsSome && SimState.getWorkToken g (ctx.StateManager.GetState()) = ownToken
            let hasEmptySuccessor = successors |> List.exists (fun g -> ctx.CanReceiveToken g || holdsSameToken g)
            // Group sync: workGuid (또는 reference group 멤버) 가 Group arrow 그룹에 속하면
            // 그룹 모든 멤버 Finish 까지 retry skip — Blocked event 무한 loop 방지.
            let groupSet =
                SimIndex.referenceGroupOf ctx.Index workGuid
                |> List.collect (fun g -> SimIndex.workGroupOf ctx.Index g |> Set.toList)
                |> Set.ofList
            let refGroup = SimIndex.referenceGroupOf ctx.Index workGuid |> Set.ofList
            let waitingForGroup =
                if Set.isEmpty groupSet then false
                else
                    groupSet
                    |> Set.exists (fun g ->
                        not (Set.contains g refGroup)
                        && ctx.StateManager.GetWorkState(g) <> Status4.Finish)
            not waitingForGroup && (hasEmptySuccessor || isSink)

        let finalizeHomingAfterTokenShift workGuid =
            if SimState.getWorkToken workGuid (ctx.StateManager.GetState()) |> Option.isNone then
                ctx.ApplyWorkTransition workGuid Status4.Ready

        let retryBlockedTokenForWork workGuid workState =
            match SimState.getWorkToken workGuid (ctx.StateManager.GetState()) with
            | None -> ()
            | Some token ->
                match ctx.Index.WorkTokenRole |> Map.tryFind workGuid with
                | Some role when role.HasFlag(TokenRole.Ignore) -> ()
                | _ when canRetryBlockedToken workGuid ->
                    ctx.ShiftToken workGuid token
                    ctx.ScheduleConditionEvaluation()
                    if workState = Status4.Homing then
                        finalizeHomingAfterTokenShift workGuid
                | _ -> ()

        for workGuid in ctx.Index.AllWorkGuids do
            let workState = ctx.StateManager.GetWorkState(workGuid)
            if workState = Status4.Finish || workState = Status4.Homing then
                retryBlockedTokenForWork workGuid workState

    /// Going 상태 Call의 TxWork가 Ready인데 스케줄되지 않은 경우 재트리거.
    /// IsFinished=true인 Device Work가 리셋 후 Ready로 돌아왔을 때,
    /// 이미 Going 상태인 Call이 TxWork 스케줄링을 재시도할 수 있게 한다.
    /// FS1-Q25 — 재트리거는 **이 Call 회차에 아직 서지 못한** Tx 에만 한다. 이미 한 번 선 Tx 가
    /// 장치의 다시 준비로 Ready 가 되었다고 같은 요청으로 또 명령하면, 요청 하나에 실행이 여럿이 된다.
    let retriggerGoingCallTxWorks (ctx: Context) () =
        let state = ctx.StateManager.GetState()
        for callGuid in ctx.Index.AllCallGuids do
            match ctx.Index.CallWorkGuid |> Map.tryFind callGuid with
            | Some workGuid ->
                if ctx.StateManager.GetCallState(callGuid) = Status4.Going
                   && not (ctx.IsWorkFrozen workGuid) then
                    let txGuids = SimIndex.txWorkGuids ctx.Index callGuid
                    let txSnapshot = state.CallTxEpochSnapshot |> Map.tryFind callGuid
                    // 배타 상대가 지금 실행 중이면(점유) 기다린다 — 다시 준비해 준 취출이 끝나기 전에 투입하지 않는다.
                    let blockedByPeer =
                        match ctx.Index.CallRaceExclusions |> Map.tryFind callGuid with
                        | Some excludedSet ->
                            excludedSet |> Set.exists (fun ex ->
                                WorkConditionChecker.callOccupies ctx.Index state ex || ctx.StateManager.IsCallPending(ex))
                        | None -> false
                    for txGuid in (if blockedByPeer then [] else txGuids) do
                        let alreadyCommanded =
                            match txSnapshot |> Option.bind (Map.tryFind txGuid) with
                            | Some saved -> SimState.getWorkEpoch (SimIndex.canonicalWorkGuid ctx.Index txGuid) state > saved
                            | None -> false
                        if not alreadyCommanded
                           && ctx.StateManager.GetWorkState(txGuid) = Status4.Ready
                           && not (ctx.StateManager.IsWorkPending(txGuid)) then
                            scheduleWorkTransition ctx txGuid Status4.Going
            | None -> ()

    let evaluateConditions (ctx: Context) () =
        let scheduledGoingGuids = evaluateWorkStarts ctx ()
        evaluateWorkResets ctx scheduledGoingGuids
        evaluateCallStarts ctx ()
        retriggerGoingCallTxWorks ctx ()
        evaluateCallCompletions ctx ()
        evaluateWorkCompletions ctx ()
        retryBlockedTokens ctx ()
