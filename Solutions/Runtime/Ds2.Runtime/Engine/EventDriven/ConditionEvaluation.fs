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
            let isGoingOrScheduled predGuid =
                ctx.StateManager.GetWorkState(predGuid) = Status4.Going
                || scheduledGoingGuids.Contains(predGuid)

            resetPreds
            |> List.tryFind (fun predGuid ->
                isGoingOrScheduled predGuid
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
        for callGuid in ctx.Index.AllCallGuids do
            match ctx.Index.CallWorkGuid |> Map.tryFind callGuid with
            | Some workGuid ->
                if ctx.StateManager.GetCallState(callGuid) = Status4.Ready
                   && not (ctx.IsWorkFrozen workGuid)
                   && not (isFlowPausedForWork ctx workGuid)
                   && ctx.StateManager.GetWorkState(workGuid) = Status4.Going
                   && ctx.CanStartCall callGuid
                   && not (ctx.StateManager.IsCallPending(callGuid)) then
                    scheduleCallTransition ctx callGuid Status4.Going
            | None -> ()

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
            let hasEmptySuccessor = successors |> List.exists ctx.CanReceiveToken
            hasEmptySuccessor || isSink

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
    let retriggerGoingCallTxWorks (ctx: Context) () =
        for callGuid in ctx.Index.AllCallGuids do
            match ctx.Index.CallWorkGuid |> Map.tryFind callGuid with
            | Some workGuid ->
                if ctx.StateManager.GetCallState(callGuid) = Status4.Going
                   && not (ctx.IsWorkFrozen workGuid) then
                    let txGuids = SimIndex.txWorkGuids ctx.Index callGuid
                    for txGuid in txGuids do
                        if ctx.StateManager.GetWorkState(txGuid) = Status4.Ready
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
