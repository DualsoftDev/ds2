namespace Ds2.Runtime.Engine

open System
open Ds2.Core
open Ds2.Runtime.Model
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Engine.Scheduler

module internal WorkTransitions =
    type Context = {
        Index: SimIndex
        StateManager: StateManager
        Scheduler: EventScheduler
        RuntimeMode: Ds2.Core.RuntimeMode
        IsHomingPhase: unit -> bool
        TimeIgnore: unit -> bool
        ScheduleConditionEvaluation: unit -> unit
        OnWorkFinish: Guid -> unit
        TriggerWorkStateChanged: WorkStateChangedArgs -> unit
        /// Duration 이벤트 추적 콜백: workGuid -> eventId -> scheduledTimeMs
        OnDurationScheduled: Guid -> Guid -> int64 -> unit
        /// v10 §10 — Work cycle 시작/종료 시 passive sensing append flag 제거.
        ClearSensingAppendDelay: Guid -> unit
        /// Work SkipAction 평가: Ready→Going 시 unmatch 면 Going 거치지 않고 Finish 로 skip
        ShouldSkipWork: Guid -> bool
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

    let private isDeviceWork (ctx: Context) (workGuid: Guid) =
        match ctx.Index.WorkSystemName |> Map.tryFind workGuid with
        | Some sysName -> not (ctx.Index.ActiveSystemNames.Contains sysName)
        | None -> false

    let scheduleDuration (ctx: Context) (workGuid: Guid) =
        // device work 포함 모든 work 가 plan(duration) 으로 정상 Finish 진행한다(In 무관).
        // (device 의 In/actual 은 abnormal adapter 가 plan 과 대조할 뿐 Finish 스케줄엔 개입 안 함.)
        let scheduleDurationComplete delayMs =
            let eventId =
                ctx.Scheduler.ScheduleAfter(
                    ScheduledEventType.DurationComplete workGuid,
                    delayMs,
                    ScheduledEvent.PriorityDurationCheck)
            ctx.OnDurationScheduled workGuid eventId (ctx.Scheduler.CurrentTimeMs + delayMs)

        let scheduleDeviceOverdueCheck () =
            let isExternalPlanMode =
                ctx.RuntimeMode = RuntimeMode.Control || ctx.RuntimeMode = RuntimeMode.Monitoring
            if isExternalPlanMode && isDeviceWork ctx workGuid then
                match ctx.Index.WorkDurationRange |> Map.tryFind workGuid with
                | Some range when range.MaxMs > 0 ->
                    let workEpoch = ctx.StateManager.GetWorkEpoch(workGuid)
                    ctx.Scheduler.ScheduleAfter(
                        ScheduledEventType.DeviceOverdueCheck(workGuid, workEpoch),
                        max 1L (int64 range.MaxMs + 1L),
                        ScheduledEvent.PriorityDurationCheck)
                    |> ignore
                | _ -> ()

        scheduleDeviceOverdueCheck ()

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
        // HomingComplete must run after same-tick CallTransition(..., Homing) events.
        // If it fires first under TimeIgnore, the work can return to Ready before its
        // child calls leave Finish, and those pending call resets are then dropped.
        if ctx.TimeIgnore() then
            ctx.Scheduler.ScheduleNow(
                ScheduledEventType.HomingComplete workGuid,
                ScheduledEvent.PriorityDurationCheck)
            |> ignore
        else
            ctx.Scheduler.ScheduleAfter(
                ScheduledEventType.HomingComplete workGuid,
                1L,
                ScheduledEvent.PriorityDurationCheck)
            |> ignore

    let handleWorkGoingTransition (ctx: Context) workGuid =
        ctx.ClearSensingAppendDelay workGuid
        ctx.StateManager.IncrementWorkEpoch(workGuid)
        scheduleDuration ctx workGuid
        triggerImmediateResets ctx workGuid
        ctx.ScheduleConditionEvaluation()

    // device 동시-Finish 데드락 tie-break. 상호 대칭 resetPred 짝(ADV↔RET)이 둘 다 Finish 면
    //   going 시작점이 없어 영구 교착(triggerImmediateResets/evaluateWorkResets 둘 다 going 전이가 유일 트리거).
    //   min Guid 한쪽만 Homing 으로 깨워 cycle 재기동(Homing→Ready→going 정상 경로). pending 가드로 핑퐁 방지.
    let private tryBreakMutualFinishDeadlock (ctx: Context) (workGuid: Guid) =
        let peers =
            match WorkConditionChecker.collectResetPreds ctx.Index workGuid with
            | Some (_, _, resetPreds) -> resetPreds
            | None -> []
        for peer in peers do
            let symmetric =
                match WorkConditionChecker.collectResetPreds ctx.Index peer with
                | Some (_, _, peerPreds) -> peerPreds |> List.contains workGuid
                | None -> false
            if symmetric
               && ctx.StateManager.GetWorkState(peer) = Status4.Finish
               && (SimState.getWorkToken peer (ctx.StateManager.GetState()) |> Option.isNone)
               && not (ctx.StateManager.IsWorkPending(peer)) then
                let loser = if workGuid < peer then workGuid else peer
                if not (ctx.StateManager.IsWorkPending(loser)) then
                    ctx.StateManager.MarkWorkPending(loser)
                    ctx.Scheduler.ScheduleNow(
                        ScheduledEventType.WorkTransition(loser, Status4.Homing),
                        ScheduledEvent.PriorityStateChange) |> ignore

    let handleWorkFinishTransition (ctx: Context) workGuid =
        ctx.OnWorkFinish workGuid
        // device 한정: ADV/RET 동시-Finish 교착이면 한쪽을 Homing 으로 깨워 cycle 재기동.
        if isDeviceWork ctx workGuid then
            tryBreakMutualFinishDeadlock ctx workGuid
        ctx.ScheduleConditionEvaluation()

    let handleWorkHomingTransition (ctx: Context) workGuid =
        scheduleCallTransitions ctx workGuid Status4.Homing Status4.Homing
        scheduleHomingCompletion ctx workGuid

    let handleWorkReadyTransition (ctx: Context) workGuid =
        ctx.ClearSensingAppendDelay workGuid
        scheduleCallTransitions ctx workGuid Status4.Ready Status4.Ready
        ctx.ScheduleConditionEvaluation()

    let runPostWorkTransitionEffects (ctx: Context) workGuid newState =
        // Passive 모드(VP/Monitoring): active work 는 passive inference 가 상태 owner라 후속 자동전이 차단.
        // 단 device work 는 engine 이 cycle owner(=plan duration) — Control 과 동치로 통과시킨다.
        let isPassiveBlocked =
            match ctx.RuntimeMode with
            | Ds2.Core.RuntimeMode.VirtualPlant
            | Ds2.Core.RuntimeMode.Monitoring -> not (isDeviceWork ctx workGuid)
            | _ -> false
        if isPassiveBlocked then () else
        match newState with
        | Status4.Going -> handleWorkGoingTransition ctx workGuid
        | Status4.Finish -> handleWorkFinishTransition ctx workGuid
        | Status4.Homing -> handleWorkHomingTransition ctx workGuid
        | Status4.Ready -> handleWorkReadyTransition ctx workGuid
        | _ -> ()

    let applyWorkTransition (ctx: Context) (workGuid: Guid) newState =
        let result = ctx.StateManager.ApplyWorkTransition(workGuid, newState, ctx.ShouldSkipWork)
        if result.HasChanged then
            emitWorkStateChanged ctx workGuid result
            runPostWorkTransitionEffects ctx workGuid result.ActualNewState
