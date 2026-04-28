namespace Ds2.Runtime.Engine

open System
open System.Diagnostics
open System.Threading
open Ds2.Core
open Ds2.Runtime.Model
open Ds2.Runtime.Engine.Scheduler

module internal EventDrivenEngineRuntime =

    type RuntimeClock() =
        let syncLock = obj()
        let mutable lastTimestamp = Stopwatch.GetTimestamp()
        let mutable fractionalSimMs = 0.0

        member _.Reset() =
            lock syncLock (fun () ->
                lastTimestamp <- Stopwatch.GetTimestamp()
                fractionalSimMs <- 0.0)

        member _.CaptureTargetMs(currentTimeMs: int64, speedMultiplier: float) =
            lock syncLock (fun () ->
                let now = Stopwatch.GetTimestamp()
                let elapsedTicks = now - lastTimestamp
                lastTimestamp <- now

                let elapsedMs =
                    (float elapsedTicks * 1000.0) / float Stopwatch.Frequency
                let simDelta = elapsedMs * max 0.000001 speedMultiplier + fractionalSimMs
                let wholeMs = int64 (Math.Floor simDelta)
                fractionalSimMs <- simDelta - float wholeMs
                currentTimeMs + wholeMs)

    type RuntimeContext = {
        ProcessGate: obj
        Scheduler: EventScheduler
        RuntimeClock: RuntimeClock
        WakeSignal: WaitHandle
        /// Hub OnTagChanged 가 lock 없이 enqueue 한 IO 신호를 simulationLoop 이 advance 안에서 batch 처리.
        /// 한 번 호출에 한 항목 dequeue + SetIOValue + ScheduleConditionEvaluation 까지. 처리했으면 true.
        /// 페어 broadcast(IN=true → IN=false) 의 의미를 보존하려면 dequeue 들 사이에 ConditionEval 이 끼어들어야 해서
        /// while 루프 안에서 hub queue 를 우선 한 항목씩 처리한다.
        TryDrainHubInjectOnce: unit -> bool
        GetStatus: unit -> SimulationStatus
        SpeedMultiplier: unit -> float
        UpdateClock: TimeSpan -> unit
        GetWorkState: Guid -> Status4
        GetWorkToken: Guid -> TokenValue option
        ClearAndApplyWorkTransition: Guid -> Status4 -> unit
        ClearAndApplyCallTransition: Guid -> Status4 -> unit
        ForceAndApplyWorkTransition: Guid -> Status4 -> unit
        ForceAndApplyCallTransition: Guid -> Status4 -> unit
        ApplyWorkTransition: Guid -> Status4 -> unit
        HandleDurationComplete: Guid -> unit
        ShiftToken: Guid -> TokenValue -> unit
        EmitTokenEvent: TokenEventKind -> TokenValue -> Guid -> Guid option -> unit
        ScheduleConditionEvaluation: unit -> unit
        EvaluateConditions: unit -> unit
        TriggerCallTimeout: CallTimeoutArgs -> unit
        GetCallState: Guid -> Status4
        GetCallName: Guid -> string
        GetCallTimeoutMs: Guid -> int option
        CurrentTimeMs: unit -> int64
    }

    let processEvent (ctx: RuntimeContext) (event: ScheduledEvent) =
        match event.EventType with
        | ScheduledEventType.WorkTransition(workGuid, targetState) ->
            ctx.ClearAndApplyWorkTransition workGuid targetState
        | ScheduledEventType.CallTransition(callGuid, targetState) ->
            ctx.ClearAndApplyCallTransition callGuid targetState
        | ScheduledEventType.ForcedWorkTransition(workGuid, targetState) ->
            ctx.ForceAndApplyWorkTransition workGuid targetState
        | ScheduledEventType.ForcedCallTransition(callGuid, targetState) ->
            ctx.ForceAndApplyCallTransition callGuid targetState
        | ScheduledEventType.DurationComplete workGuid ->
            ctx.HandleDurationComplete workGuid
        | ScheduledEventType.CallTimeout callGuid ->
            if ctx.GetCallState callGuid = Status4.Going then
                let clock = TimeSpan.FromMilliseconds(float (ctx.CurrentTimeMs()))
                let timeoutMs = ctx.GetCallTimeoutMs callGuid |> Option.defaultValue 0
                ctx.TriggerCallTimeout({
                    CallGuid = callGuid
                    CallName = ctx.GetCallName callGuid
                    TimeoutMs = timeoutMs
                    Clock = clock })
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

    let private setCurrentTime (ctx: RuntimeContext) (timeMs: int64) =
        ctx.Scheduler.SetCurrentTime(timeMs)
        ctx.UpdateClock(TimeSpan.FromMilliseconds(float timeMs))

    let private advanceTo (ctx: RuntimeContext) (targetMs: int64) (shouldContinue: unit -> bool) =
        let targetMs = max ctx.Scheduler.CurrentTimeMs targetMs
        // 시계는 advance 시작 시 한 번만 targetMs 로 fast-forward.
        // 같은 batch 의 ScheduleAfter(0) 새 이벤트들이 모두 동일 timestamp 에 추가돼야
        // dequeue 순서가 priority 만으로 결정 — race 없이 결정적.
        setCurrentTime ctx targetMs
        let mutable processed = false
        let mutable draining = true
        while draining && shouldContinue() do
            // 1) Hub 큐 우선 — SignalR thread 가 enqueue 한 IO 신호를 한 항목씩 적용.
            //    페어 broadcast(IN=true → IN=false) 사이에 ConditionEval 이 끼어들도록
            //    한 번에 하나만 dequeue 하고 continue.
            if ctx.TryDrainHubInjectOnce() then
                processed <- true
            else
                // 2) Hub 큐 비면 scheduler 의 due event 처리.
                match ctx.Scheduler.TryDequeueDue(targetMs) with
                | Some event ->
                    processed <- true
                    if shouldContinue() then
                        processEvent ctx event
                | None ->
                    draining <- false

        processed

    let advanceAndDrainWhileRunning (ctx: RuntimeContext) (targetMs: int64) =
        advanceTo ctx targetMs (fun () -> ctx.GetStatus () = Running) |> ignore

    let advanceAndDrain (ctx: RuntimeContext) (targetMs: int64) =
        advanceTo ctx targetMs (fun () -> true) |> ignore

    let syncClockToRealTimeWhileRunning (ctx: RuntimeContext) =
        if ctx.GetStatus () = Running then
            let targetMs =
                ctx.RuntimeClock.CaptureTargetMs(ctx.Scheduler.CurrentTimeMs, ctx.SpeedMultiplier())
            advanceAndDrainWhileRunning ctx targetMs

    let private nextWaitTimeoutMs (ctx: RuntimeContext) =
        match ctx.Scheduler.NextEventTime with
        | Some nextEventTime when nextEventTime <= ctx.Scheduler.CurrentTimeMs -> 0
        | Some nextEventTime ->
            let simDelayMs = nextEventTime - ctx.Scheduler.CurrentTimeMs
            let realDelayMs = Math.Ceiling(float simDelayMs / max 0.000001 (ctx.SpeedMultiplier()))
            if realDelayMs >= float Int32.MaxValue then Int32.MaxValue
            else max 0 (int realDelayMs)
        | None ->
            Timeout.Infinite

    let private waitForWork (ctx: RuntimeContext) (ct: CancellationToken) (timeoutMs: int) =
        if timeoutMs <> 0 then
            WaitHandle.WaitAny([| ctx.WakeSignal; ct.WaitHandle |], timeoutMs) |> ignore

    let simulationLoop (ctx: RuntimeContext) (ct: CancellationToken) =
        ctx.RuntimeClock.Reset()

        while not ct.IsCancellationRequested && ctx.GetStatus () = Running do
            lock ctx.ProcessGate (fun () ->
                syncClockToRealTimeWhileRunning ctx)

            if not ct.IsCancellationRequested && ctx.GetStatus () = Running then
                let timeoutMs =
                    lock ctx.ProcessGate (fun () ->
                        nextWaitTimeoutMs ctx)
                waitForWork ctx ct timeoutMs
