namespace Ds2.Runtime.Engine

open System
open System.Threading
open Ds2.Core
open Ds2.Runtime.Model

module internal EngineLifecycle =

    type Context = {
        EngineThreadJoinTimeoutMs: int
        GetStatus: unit -> SimulationStatus
        SetStatus: SimulationStatus -> unit
        GetEngineThread: unit -> Thread option
        SetEngineThread: Thread option -> unit
        GetCts: unit -> CancellationTokenSource option
        SetCts: CancellationTokenSource option -> unit
        TriggerStatusChanged: SimulationStatusChangedArgs -> unit
        ScheduleConditionEvaluation: unit -> unit
        StartEngineThread: CancellationTokenSource -> unit
        AllWorkGuids: Guid list
        AllCallGuids: Guid list
        GetWorkState: Guid -> Status4
        GetCallState: Guid -> Status4
        ApplyWorkTransition: Guid -> Status4 -> unit
        ForceWorkState: Guid -> Status4 -> unit
        ForceCallState: Guid -> Status4 -> unit
        SchedulerClear: unit -> unit
        ResetState: unit -> unit
    }

    let private disposeCurrentCts (ctx: Context) =
        ctx.GetCts() |> Option.iter (fun c -> c.Dispose())
        ctx.SetCts None

    let private clearDeadEngineThreadReference (ctx: Context) =
        match ctx.GetEngineThread() with
        | Some thread when not thread.IsAlive ->
            ctx.SetEngineThread None
        | _ -> ()

    let ensureEngineThreadExited (ctx: Context) operationName =
        clearDeadEngineThreadReference ctx
        match ctx.GetEngineThread() with
        | None -> ()
        | Some thread when thread.Join(ctx.EngineThreadJoinTimeoutMs) ->
            ctx.SetEngineThread None
        | Some _ ->
            raise (InvalidOperationException(
                $"EventDrivenEngine thread did not exit within {ctx.EngineThreadJoinTimeoutMs}ms during {operationName}."))

    let start (ctx: Context) =
        if ctx.GetStatus() <> Running then
            ensureEngineThreadExited ctx "Start"
            let previous = ctx.GetStatus()
            ctx.SetStatus Running
            ctx.TriggerStatusChanged({ PreviousStatus = previous; NewStatus = Running })
            ctx.ScheduleConditionEvaluation()
            disposeCurrentCts ctx
            let tokenSource = new CancellationTokenSource()
            ctx.SetCts (Some tokenSource)
            ctx.StartEngineThread tokenSource

    let pause (ctx: Context) =
        if ctx.GetStatus() = Running then
            ctx.SetStatus Paused
            ctx.TriggerStatusChanged({ PreviousStatus = Running; NewStatus = Paused })

    let resume (ctx: Context) =
        if ctx.GetStatus() = Paused then
            ensureEngineThreadExited ctx "Resume"
            disposeCurrentCts ctx
            ctx.SetStatus Running
            ctx.TriggerStatusChanged({ PreviousStatus = Paused; NewStatus = Running })
            let tokenSource = new CancellationTokenSource()
            ctx.SetCts (Some tokenSource)
            ctx.StartEngineThread tokenSource

    let stop (ctx: Context) =
        if ctx.GetStatus() <> Stopped then
            let previous = ctx.GetStatus()
            ctx.SetStatus Stopped
            ctx.GetCts() |> Option.iter (fun c -> c.Cancel())
            ensureEngineThreadExited ctx "Stop"
            disposeCurrentCts ctx
            ctx.SetEngineThread None
            ctx.TriggerStatusChanged({ PreviousStatus = previous; NewStatus = Stopped })

    let reset (ctx: Context) =
        stop ctx

        for workGuid in ctx.AllWorkGuids do
            if ctx.GetWorkState(workGuid) = Status4.Finish then
                ctx.ApplyWorkTransition workGuid Status4.Homing

        for workGuid in ctx.AllWorkGuids do
            let workState = ctx.GetWorkState(workGuid)
            if workState <> Status4.Ready && workState <> Status4.Homing then
                ctx.ForceWorkState workGuid Status4.Ready

        for callGuid in ctx.AllCallGuids do
            if ctx.GetCallState(callGuid) <> Status4.Ready then
                ctx.ForceCallState callGuid Status4.Ready

        for workGuid in ctx.AllWorkGuids do
            if ctx.GetWorkState(workGuid) = Status4.Homing then
                ctx.ApplyWorkTransition workGuid Status4.Ready

        ctx.SchedulerClear()
        ctx.ResetState()
