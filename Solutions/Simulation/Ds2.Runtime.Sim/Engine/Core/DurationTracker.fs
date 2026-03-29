namespace Ds2.Runtime.Sim.Engine.Core

open System
open Ds2.Core
open Ds2.Runtime.Sim.Engine.Scheduler

/// Duration 이벤트 추적 (Pause 시 취소 / Resume 시 재스케줄)
type DurationTracker(scheduler: EventScheduler) =

    /// workGuid -> (eventId, scheduledTimeMs)
    let mutable durationEvents = Map.empty<Guid, struct(Guid * int64)>

    /// Pause 시 저장된 남은 시간: workGuid -> remainingMs
    let mutable pausedDurationRemaining = Map.empty<Guid, int64>

    /// Duration 스케줄 완료 콜백 (WorkTransitions.OnDurationScheduled용)
    member _.OnDurationScheduled(workGuid: Guid, eventId: Guid, scheduledTimeMs: int64) =
        durationEvents <- durationEvents.Add(workGuid, struct(eventId, scheduledTimeMs))

    /// Duration 이벤트 취소 (스케줄러 Cancel + map 제거)
    member _.Cancel(workGuid: Guid) =
        match durationEvents |> Map.tryFind workGuid with
        | Some struct(eventId, _) ->
            scheduler.Cancel(eventId)
            durationEvents <- durationEvents.Remove(workGuid)
        | None -> ()

    /// Duration 이벤트 map에서만 제거 (handleDurationComplete 시 사용)
    member _.Remove(workGuid: Guid) =
        durationEvents <- durationEvents.Remove(workGuid)

    /// 진행 중 Work를 현재 시점에서 정지시키고 남은 duration을 보존
    member _.PauseDuration(workGuid: Guid) =
        match durationEvents |> Map.tryFind workGuid with
        | Some struct(eventId, scheduledTimeMs) ->
            scheduler.Cancel(eventId)
            let remaining = max 0L (scheduledTimeMs - scheduler.CurrentTimeMs)
            pausedDurationRemaining <- pausedDurationRemaining.Add(workGuid, remaining)
            durationEvents <- durationEvents.Remove(workGuid)
        | None -> ()

    /// Paused duration 제거
    member _.ClearPausedDuration(workGuid: Guid) =
        pausedDurationRemaining <- pausedDurationRemaining.Remove(workGuid)

    /// Pause 로직: Going Work들의 duration 이벤트를 취소하고 남은 시간 저장
    /// Going Call 없고 미시작 Call 있는 Work만 대상 (진행 중이거나 leaf/all-finish는 보존)
    member _.SavePausedDurations
        (goingWorkGuids: Guid list,
         getCallState: Guid -> Status4,
         getFlowState: Guid -> FlowTag,
         workCallGuids: Map<Guid, Guid list>,
         workFlowGuid: Map<Guid, Guid>) =
        for wg in goingWorkGuids do
            let callGuids = SimIndex.findOrEmpty wg workCallGuids
            let hasGoingCall =
                callGuids |> List.exists (fun cg -> getCallState cg = Status4.Going)
            let hasUnfinishedCall =
                callGuids |> List.exists (fun cg -> getCallState cg <> Status4.Finish)
            if not hasGoingCall && hasUnfinishedCall then
                match workFlowGuid |> Map.tryFind wg with
                | Some fg when getFlowState fg = FlowTag.Pause ->
                    match durationEvents |> Map.tryFind wg with
                    | Some struct(eventId, scheduledTimeMs) ->
                        scheduler.Cancel(eventId)
                        let remaining = max 0L (scheduledTimeMs - scheduler.CurrentTimeMs)
                        pausedDurationRemaining <- pausedDurationRemaining.Add(wg, remaining)
                        durationEvents <- durationEvents.Remove(wg)
                    | None -> ()
                | _ -> ()

    /// Drive 로직: 저장된 남은 시간으로 duration 재스케줄
    member _.ResumePausedDurations(getWorkState: Guid -> Status4) =
        for kv in pausedDurationRemaining |> Map.toList do
            let wg, remaining = kv
            if getWorkState wg = Status4.Going then
                let eventId =
                    scheduler.ScheduleAfter(
                        ScheduledEventType.DurationComplete wg,
                        (max 1L remaining),
                        ScheduledEvent.PriorityDurationCheck)
                durationEvents <- durationEvents.Add(wg, struct(eventId, scheduler.CurrentTimeMs + remaining))
        pausedDurationRemaining <- Map.empty

    /// Call Finish 시 모든 Call이 Finish면 중단된 duration 재스케줄
    member _.TryResumePausedDuration(workGuid: Guid) =
        match pausedDurationRemaining |> Map.tryFind workGuid with
        | Some remaining ->
            let eventId =
                scheduler.ScheduleAfter(
                    ScheduledEventType.DurationComplete workGuid,
                    (max 1L remaining),
                    ScheduledEvent.PriorityDurationCheck)
            durationEvents <- durationEvents.Add(workGuid, struct(eventId, scheduler.CurrentTimeMs + remaining))
            pausedDurationRemaining <- pausedDurationRemaining.Remove(workGuid)
        | None -> ()
