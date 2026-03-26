namespace Ds2.Runtime.Sim.Engine

open System
open Ds2.Core
open Ds2.Runtime.Sim.Model
open Ds2.Runtime.Sim.Engine.Core
open Ds2.Runtime.Sim.Engine.Scheduler

module internal ConditionEvaluation =
    type Context = {
        Index: SimIndex
        StateManager: StateManager
        Scheduler: EventScheduler
        CanStartWork: Guid -> bool
        CanStartCall: Guid -> bool
        CanCompleteCall: Guid -> bool
        ScheduleConditionEvaluation: unit -> unit
        ShiftToken: Guid -> TokenValue -> unit
        EmitTokenEvent: TokenEventKind -> TokenValue -> Guid -> Guid option -> unit
        CanReceiveToken: Guid -> bool
        ApplyWorkTransition: Guid -> Status4 -> unit
    }

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
            ctx.StateManager.MarkWorkPending(workGuid)
            ctx.Scheduler.ScheduleNow(
                ScheduledEventType.WorkTransition(workGuid, Status4.Homing),
                ScheduledEvent.PriorityStateChange)
            |> ignore

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
                ctx.StateManager.MarkWorkPending(workGuid)
                ctx.Scheduler.ScheduleNow(
                    ScheduledEventType.WorkTransition(workGuid, Status4.Going),
                    ScheduledEvent.PriorityStateChange)
                |> ignore
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
                   && not (isFlowPausedForWork ctx workGuid)
                   && ctx.StateManager.GetWorkState(workGuid) = Status4.Going
                   && ctx.CanStartCall callGuid
                   && not (ctx.StateManager.IsCallPending(callGuid)) then
                    ctx.StateManager.MarkCallPending(callGuid)
                    ctx.Scheduler.ScheduleNow(
                        ScheduledEventType.CallTransition(callGuid, Status4.Going),
                        ScheduledEvent.PriorityStateChange)
                    |> ignore
            | None -> ()

    let evaluateCallCompletions (ctx: Context) () =
        for callGuid in ctx.Index.AllCallGuids do
            if ctx.StateManager.GetCallState(callGuid) = Status4.Going
               && ctx.CanCompleteCall callGuid
               && not (ctx.StateManager.IsCallPending(callGuid)) then
                ctx.StateManager.MarkCallPending(callGuid)
                ctx.Scheduler.ScheduleNow(
                    ScheduledEventType.CallTransition(callGuid, Status4.Finish),
                    ScheduledEvent.PriorityStateChange)
                |> ignore

    let evaluateWorkCompletions (ctx: Context) () =
        for workGuid in ctx.Index.AllWorkGuids do
            if ctx.StateManager.GetWorkState(workGuid) = Status4.Going
               && not (ctx.StateManager.IsWorkPending(workGuid)) then
                let callGuids = SimIndex.findOrEmpty workGuid ctx.Index.WorkCallGuids
                if not callGuids.IsEmpty
                   && ctx.StateManager.IsMinDurationMet(workGuid)
                   && callGuids |> List.forall (fun callGuid -> ctx.StateManager.GetCallState(callGuid) = Status4.Finish) then
                    ctx.StateManager.MarkWorkPending(workGuid)
                    ctx.Scheduler.ScheduleNow(
                        ScheduledEventType.WorkTransition(workGuid, Status4.Finish),
                        ScheduledEvent.PriorityStateChange)
                    |> ignore

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

    let evaluateConditions (ctx: Context) () =
        let scheduledGoingGuids = evaluateWorkStarts ctx ()
        evaluateWorkResets ctx scheduledGoingGuids
        evaluateCallStarts ctx ()
        evaluateCallCompletions ctx ()
        evaluateWorkCompletions ctx ()
        retryBlockedTokens ctx ()
