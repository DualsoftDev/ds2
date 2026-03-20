namespace Ds2.Runtime.Sim.Engine

open System
open Ds2.Core
open Ds2.Runtime.Sim.Model
open Ds2.Runtime.Sim.Engine.Core
open Ds2.Runtime.Sim.Engine.Scheduler

module internal WorkTransitions =
    type Context = {
        Index: SimIndex
        StateManager: StateManager
        Scheduler: EventScheduler
        TimeIgnore: unit -> bool
        ScheduleConditionEvaluation: unit -> unit
        OnWorkFinish: Guid -> unit
        TriggerWorkStateChanged: WorkStateChangedArgs -> unit
    }

    let scheduleCallTransitions (ctx: Context) workGuid targetState excludeState =
        let callGuids = SimIndex.findOrEmpty workGuid ctx.Index.WorkCallGuids
        for callGuid in callGuids do
            let callState = ctx.StateManager.GetCallState(callGuid)
            if callState <> targetState
               && callState <> excludeState
               && not (ctx.StateManager.IsCallPending(callGuid)) then
                ctx.StateManager.MarkCallPending(callGuid)
                ctx.Scheduler.ScheduleNow(
                    ScheduledEventType.CallTransition(callGuid, targetState),
                    ScheduledEvent.PriorityStateChange)
                |> ignore

    let scheduleWorkIfReady (ctx: Context) workGuid targetState =
        if ctx.StateManager.GetWorkState(workGuid) = Status4.Ready
           && not (ctx.StateManager.IsWorkPending(workGuid)) then
            ctx.StateManager.MarkWorkPending(workGuid)
            ctx.Scheduler.ScheduleNow(
                ScheduledEventType.WorkTransition(workGuid, targetState),
                ScheduledEvent.PriorityStateChange)
            |> ignore

    let scheduleLeafDuration (ctx: Context) (workGuid: Guid) =
        let scheduleDurationComplete delayMs =
            ctx.Scheduler.ScheduleAfter(
                ScheduledEventType.DurationComplete workGuid,
                delayMs,
                ScheduledEvent.PriorityDurationCheck)
            |> ignore

        let callGuids = SimIndex.findOrEmpty workGuid ctx.Index.WorkCallGuids
        if callGuids.IsEmpty then
            let duration = ctx.Index.WorkDuration |> Map.tryFind workGuid |> Option.defaultValue 0.0
            if ctx.TimeIgnore() then
                ctx.Scheduler.ScheduleNow(
                    ScheduledEventType.DurationComplete workGuid,
                    ScheduledEvent.PriorityDurationCheck)
                |> ignore
            else
                scheduleDurationComplete (max 1L (int64 duration))

    let triggerImmediateResets (ctx: Context) (workGuid: Guid) =
        let tryFindWorkKey () =
            match Map.tryFind workGuid ctx.Index.WorkSystemName, Map.tryFind workGuid ctx.Index.WorkName with
            | Some systemName, Some workName -> Some (systemName, workName)
            | _ -> None

        let shouldTriggerImmediateReset targetGuid =
            let targetHasToken =
                SimState.getWorkToken targetGuid (ctx.StateManager.GetState()) |> Option.isSome
            ctx.StateManager.GetWorkState(targetGuid) = Status4.Finish
            && not targetHasToken
            && not (ctx.StateManager.IsWorkPending(targetGuid))
            && match WorkConditionChecker.collectResetPreds ctx.Index targetGuid with
               | Some (_, _, resetPreds) -> resetPreds |> List.contains workGuid
               | None -> false

        let queueImmediateReset resetPredKey targetGuid =
            match WorkConditionChecker.collectResetPreds ctx.Index targetGuid, Map.tryFind targetGuid ctx.Index.WorkSystemName with
            | Some (_, targetName, _), Some targetSystem ->
                let targetKey = (targetSystem, targetName)
                if not (ctx.StateManager.IsResetTriggered(resetPredKey, targetKey)) then
                    ctx.StateManager.AddResetTrigger(resetPredKey, targetKey)
                    ctx.StateManager.MarkWorkPending(targetGuid)
                    ctx.Scheduler.ScheduleNow(
                        ScheduledEventType.WorkTransition(targetGuid, Status4.Homing),
                        ScheduledEvent.PriorityStateChange)
                    |> ignore
            | _ -> ()

        match tryFindWorkKey () with
        | Some resetPredKey ->
            for targetGuid in ctx.Index.AllWorkGuids do
                if shouldTriggerImmediateReset targetGuid then
                    queueImmediateReset resetPredKey targetGuid
        | None -> ()

    let emitWorkStateChanged (ctx: Context) workGuid (result: TransitionResult) =
        let clock = TimeSpan.FromMilliseconds(float ctx.Scheduler.CurrentTimeMs)
        ctx.TriggerWorkStateChanged({
            WorkGuid = workGuid
            WorkName = result.NodeName
            PreviousState = result.OldState
            NewState = result.ActualNewState
            Clock = clock })

    let scheduleHomingCompletion (ctx: Context) workGuid =
        if ctx.TimeIgnore() then
            ctx.Scheduler.ScheduleNow(
                ScheduledEventType.HomingComplete workGuid,
                ScheduledEvent.PriorityStateChange)
            |> ignore
        else
            ctx.Scheduler.ScheduleAfter(
                ScheduledEventType.HomingComplete workGuid,
                1L,
                ScheduledEvent.PriorityStateChange)
            |> ignore

    let handleWorkGoingTransition (ctx: Context) workGuid =
        scheduleLeafDuration ctx workGuid
        triggerImmediateResets ctx workGuid
        ctx.ScheduleConditionEvaluation()

    let handleWorkFinishTransition (ctx: Context) workGuid =
        ctx.OnWorkFinish workGuid
        ctx.ScheduleConditionEvaluation()

    let handleWorkHomingTransition (ctx: Context) workGuid =
        scheduleCallTransitions ctx workGuid Status4.Homing Status4.Homing
        scheduleHomingCompletion ctx workGuid

    let handleWorkReadyTransition (ctx: Context) workGuid =
        scheduleCallTransitions ctx workGuid Status4.Ready Status4.Ready
        ctx.ScheduleConditionEvaluation()

    let runPostWorkTransitionEffects (ctx: Context) workGuid newState =
        match newState with
        | Status4.Going -> handleWorkGoingTransition ctx workGuid
        | Status4.Finish -> handleWorkFinishTransition ctx workGuid
        | Status4.Homing -> handleWorkHomingTransition ctx workGuid
        | Status4.Ready -> handleWorkReadyTransition ctx workGuid
        | _ -> ()

    let applyWorkTransition (ctx: Context) (workGuid: Guid) newState =
        let result = ctx.StateManager.ApplyWorkTransition(workGuid, newState)
        if result.HasChanged then
            emitWorkStateChanged ctx workGuid result
            runPostWorkTransitionEffects ctx workGuid result.ActualNewState
