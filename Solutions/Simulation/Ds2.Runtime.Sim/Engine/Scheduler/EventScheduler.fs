namespace Ds2.Runtime.Sim.Engine.Scheduler

open System
open System.Collections.Generic

/// 스레드 안전한 이벤트 스케줄러 (PriorityQueue 기반)
type EventScheduler() =
    let queue = PriorityQueue<ScheduledEvent, struct(int64 * int)>()
    let syncLock = obj()
    let mutable currentSimTimeMs = 0L
    let pendingEvents = HashSet<Guid>()

    member _.Schedule(event: ScheduledEvent) =
        lock syncLock (fun () ->
            if pendingEvents.Add(event.EventId) then
                queue.Enqueue(event, struct(event.ScheduledTimeMs, event.Priority)))

    member this.ScheduleAfter(eventType: ScheduledEventType, delayMs: int64, priority: int) =
        let event = ScheduledEvent.create eventType (currentSimTimeMs + delayMs) priority
        this.Schedule(event)
        event.EventId

    member this.ScheduleNow(eventType: ScheduledEventType, priority: int) =
        this.ScheduleAfter(eventType, 0L, priority)

    member this.ScheduleAt(eventType: ScheduledEventType, timeMs: int64, priority: int) =
        let event = ScheduledEvent.create eventType timeMs priority
        this.Schedule(event)
        event.EventId

    member _.Cancel(eventId: Guid) =
        lock syncLock (fun () -> pendingEvents.Remove(eventId) |> ignore)

    member _.RemoveWhere(predicate: ScheduledEvent -> bool) =
        lock syncLock (fun () ->
            let kept = ResizeArray<ScheduledEvent>()
            let mutable removed = 0

            while queue.Count > 0 do
                let event = queue.Dequeue()
                if pendingEvents.Remove(event.EventId) then
                    if predicate event then
                        removed <- removed + 1
                    else
                        kept.Add(event)

            for event in kept do
                pendingEvents.Add(event.EventId) |> ignore
                queue.Enqueue(event, struct(event.ScheduledTimeMs, event.Priority))

            removed)

    member _.TryDequeue() : ScheduledEvent option =
        lock syncLock (fun () ->
            let mutable result = None
            while queue.Count > 0 && result.IsNone do
                let event = queue.Dequeue()
                if pendingEvents.Remove(event.EventId) then
                    result <- Some event
            result)

    member _.AdvanceTo(targetTimeMs: int64) : ScheduledEvent list =
        let mutable events = []
        lock syncLock (fun () ->
            currentSimTimeMs <- targetTimeMs
            while queue.Count > 0 && queue.Peek().ScheduledTimeMs <= targetTimeMs do
                let event = queue.Dequeue()
                if pendingEvents.Remove(event.EventId) then
                    events <- event :: events)
        events |> List.rev

    member _.CurrentTimeMs = currentSimTimeMs

    member _.SetCurrentTime(timeMs: int64) =
        lock syncLock (fun () -> currentSimTimeMs <- timeMs)

    member _.Clear() =
        lock syncLock (fun () ->
            queue.Clear()
            pendingEvents.Clear()
            currentSimTimeMs <- 0L)

    member _.Count = lock syncLock (fun () -> pendingEvents.Count)

    member _.NextEventTime =
        lock syncLock (fun () ->
            if queue.Count > 0 then Some (queue.Peek().ScheduledTimeMs)
            else None)
