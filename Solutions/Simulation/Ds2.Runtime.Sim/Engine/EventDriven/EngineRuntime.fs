namespace Ds2.Runtime.Sim.Engine

open System
open System.Threading
open Ds2.Core
open Ds2.Runtime.Sim.Model
open Ds2.Runtime.Sim.Engine.Scheduler

module internal EventDrivenEngineRuntime =

    type RuntimeContext = {
        ProcessGate: obj
        Scheduler: EventScheduler
        GetStatus: unit -> SimulationStatus
        SpeedMultiplier: unit -> float
        UpdateClock: TimeSpan -> unit
        GetWorkState: Guid -> Status4
        GetWorkToken: Guid -> TokenValue option
        ClearAndApplyWorkTransition: Guid -> Status4 -> unit
        ClearAndApplyCallTransition: Guid -> Status4 -> unit
        ApplyWorkTransition: Guid -> Status4 -> unit
        HandleDurationComplete: Guid -> unit
        ShiftToken: Guid -> TokenValue -> unit
        EmitTokenEvent: TokenEventKind -> TokenValue -> Guid -> Guid option -> unit
        ScheduleConditionEvaluation: unit -> unit
        EvaluateConditions: unit -> unit
    }

    let processEvent (ctx: RuntimeContext) (event: ScheduledEvent) =
        match event.EventType with
        | ScheduledEventType.WorkTransition(workGuid, targetState) ->
            ctx.ClearAndApplyWorkTransition workGuid targetState
        | ScheduledEventType.CallTransition(callGuid, targetState) ->
            ctx.ClearAndApplyCallTransition callGuid targetState
        | ScheduledEventType.DurationComplete workGuid ->
            ctx.HandleDurationComplete workGuid
        | ScheduledEventType.HomingComplete workGuid ->
            if ctx.GetWorkState workGuid = Status4.Homing then
                match ctx.GetWorkToken workGuid with
                | Some token ->
                    ctx.ShiftToken workGuid token
                    if ctx.GetWorkToken workGuid |> Option.isNone then
                        ctx.ApplyWorkTransition workGuid Status4.Ready
                        ctx.ScheduleConditionEvaluation ()
                    else
                        ctx.EmitTokenEvent BlockedOnHoming token workGuid None
                | None ->
                    ctx.ApplyWorkTransition workGuid Status4.Ready
        | ScheduledEventType.EvaluateConditions ->
            ctx.EvaluateConditions ()

    let private advanceTo (ctx: RuntimeContext) (targetMs: int64) =
        let events = ctx.Scheduler.AdvanceTo(targetMs)
        ctx.UpdateClock(TimeSpan.FromMilliseconds(float ctx.Scheduler.CurrentTimeMs))
        events

    let advanceAndDrainWhileRunning (ctx: RuntimeContext) (targetMs: int64) =
        for event in advanceTo ctx targetMs do
            if ctx.GetStatus () = Running then
                processEvent ctx event

        let mutable draining = true
        while draining && ctx.GetStatus () = Running do
            let pending = advanceTo ctx targetMs
            if pending.IsEmpty then
                draining <- false
            else
                for event in pending do
                    if ctx.GetStatus () = Running then
                        processEvent ctx event

    let advanceAndDrain (ctx: RuntimeContext) (targetMs: int64) =
        for event in advanceTo ctx targetMs do
            processEvent ctx event

        let mutable draining = true
        while draining do
            let pending = advanceTo ctx targetMs
            if pending.IsEmpty then
                draining <- false
            else
                for event in pending do
                    processEvent ctx event

    let simulationLoop (ctx: RuntimeContext) (ct: CancellationToken) =
        let mutable lastRealTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

        while not ct.IsCancellationRequested && ctx.GetStatus () = Running do
            let nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            let realDelta = nowMs - lastRealTimeMs
            lastRealTimeMs <- nowMs

            let simDelta = int64 (float realDelta * ctx.SpeedMultiplier ())
            let targetMs = ctx.Scheduler.CurrentTimeMs + simDelta

            lock ctx.ProcessGate (fun () ->
                advanceAndDrainWhileRunning ctx targetMs)

            Thread.Sleep(1)
