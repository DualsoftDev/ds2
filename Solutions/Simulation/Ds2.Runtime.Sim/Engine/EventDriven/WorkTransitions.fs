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
        /// Duration 이벤트 추적 콜백: workGuid -> eventId -> scheduledTimeMs
        OnDurationScheduled: Guid -> Guid -> int64 -> unit
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

    let scheduleDuration (ctx: Context) (workGuid: Guid) =
        let scheduleDurationComplete delayMs =
            let eventId =
                ctx.Scheduler.ScheduleAfter(
                    ScheduledEventType.DurationComplete workGuid,
                    delayMs,
                    ScheduledEvent.PriorityDurationCheck)
            ctx.OnDurationScheduled workGuid eventId (ctx.Scheduler.CurrentTimeMs + delayMs)

        let duration = ctx.Index.WorkDuration |> Map.tryFind workGuid |> Option.defaultValue 0.0
        let callGuids = SimIndex.findOrEmpty workGuid ctx.Index.WorkCallGuids
        // Leaf Works: 항상 스케줄, Works with Calls: duration > 0일 때만 (min duration 보장)
        if callGuids.IsEmpty || duration > 0.0 then
            if callGuids.IsEmpty then
                ctx.StateManager.MarkMinDurationMet(workGuid) // leaf는 즉시 met 표시 (조건 없음)
            if ctx.TimeIgnore() then
                let eventId =
                    ctx.Scheduler.ScheduleNow(
                        ScheduledEventType.DurationComplete workGuid,
                        ScheduledEvent.PriorityDurationCheck)
                ctx.OnDurationScheduled workGuid eventId ctx.Scheduler.CurrentTimeMs
            else
                scheduleDurationComplete (max 1L (int64 duration))
        else
            ctx.StateManager.MarkMinDurationMet(workGuid) // duration 없으면 즉시 met

    let triggerImmediateResets (ctx: Context) (workGuid: Guid) =
        let shouldTriggerImmediateReset targetGuid =
            let targetHasToken =
                SimState.getWorkToken targetGuid (ctx.StateManager.GetState()) |> Option.isSome
            ctx.StateManager.GetWorkState(targetGuid) = Status4.Finish
            && not targetHasToken
            && not (ctx.StateManager.IsWorkPending(targetGuid))
            && match WorkConditionChecker.collectResetPreds ctx.Index targetGuid with
               | Some (_, _, resetPreds) -> resetPreds |> List.contains workGuid
               | None -> false

        let queueImmediateReset targetGuid =
            if not (ctx.StateManager.IsResetTriggered(workGuid, targetGuid)) then
                ctx.StateManager.AddResetTrigger(workGuid, targetGuid)
                ctx.StateManager.MarkWorkPending(targetGuid)
                ctx.Scheduler.ScheduleNow(
                    ScheduledEventType.WorkTransition(targetGuid, Status4.Homing),
                    ScheduledEvent.PriorityStateChange)
                |> ignore

        for targetGuid in ctx.Index.AllWorkGuids do
            if shouldTriggerImmediateReset targetGuid then
                queueImmediateReset targetGuid

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
        ctx.StateManager.IncrementWorkEpoch(workGuid)
        scheduleDuration ctx workGuid
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
