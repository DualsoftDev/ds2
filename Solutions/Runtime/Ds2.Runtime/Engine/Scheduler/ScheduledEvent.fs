namespace Ds2.Runtime.Engine.Scheduler

open System
open Ds2.Core

/// 스케줄링 가능한 이벤트 타입
[<RequireQualifiedAccess>]
type ScheduledEventType =
    | WorkTransition of workGuid: Guid * targetState: Status4
    | CallTransition of callGuid: Guid * targetState: Status4
    | ForcedWorkTransition of workGuid: Guid * targetState: Status4
    | ForcedCallTransition of callGuid: Guid * targetState: Status4
    | DurationComplete of workGuid: Guid
    | HomingComplete of workGuid: Guid
    | CallTimeout of callGuid: Guid
    | EvaluateConditions

/// 스케줄된 이벤트 (PriorityQueue용)
[<Struct>]
type ScheduledEvent = {
    EventId: Guid
    ScheduledTimeMs: int64
    EventType: ScheduledEventType
    Priority: int
    CreatedAt: DateTime
}

module ScheduledEvent =
    let PriorityStateChange = 0
    let PriorityDurationCheck = 10
    let PriorityConditionEval = 20

    let create eventType scheduledTimeMs priority = {
        EventId = Guid.NewGuid()
        ScheduledTimeMs = scheduledTimeMs
        EventType = eventType
        Priority = priority
        CreatedAt = DateTime.UtcNow
    }

    let createImmediate eventType currentTimeMs priority =
        create eventType currentTimeMs priority

    let createDelayed eventType currentTimeMs delayMs priority =
        create eventType (currentTimeMs + delayMs) priority
