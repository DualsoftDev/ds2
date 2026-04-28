namespace Ds2.Runtime.Engine

open System
open Ds2.Core
open Ds2.Runtime.Engine.Core

type ApiCallExecutionContext = {
    RuntimeMode: RuntimeMode
    GetDeviceState: Guid -> Status4
    GetDeviceName: Guid -> string
    GetTxOutAddresses: Guid -> string list
    WriteTag: string -> string -> unit
    ForceWorkState: Guid -> Status4 -> unit
}

type CallTransitionApplyContext = {
    RuntimeMode: RuntimeMode
    GetCallOutAddresses: Guid -> string list
    WriteTag: string -> string -> unit
    GetCallState: Guid -> Status4
    ApplyCallTransitionCore: Guid -> Status4 -> unit
}

type DurationCompleteContext = {
    IsPassiveMode: bool
    RemoveDuration: Guid -> unit
    GetWorkState: Guid -> Status4
    MarkMinDurationMet: Guid -> unit
    GetWorkCallGuids: Guid -> Guid list
    GetCallState: Guid -> Status4
    ApplyWorkTransition: Guid -> Status4 -> unit
}

module EventDrivenExecution =
    let executeApiCall (ctx: ApiCallExecutionContext) deviceWorkGuid =
        let curSt = ctx.GetDeviceState deviceWorkGuid
        let wname = ctx.GetDeviceName deviceWorkGuid

        let diagPath =
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                sprintf "ds2_execapi_%A.txt" ctx.RuntimeMode)

        try
            System.IO.File.AppendAllText(
                diagPath,
                sprintf "[%O] mode=%A device=%s state=%A\n"
                    DateTime.Now ctx.RuntimeMode wname curSt)
        with _ ->
            ()

        if curSt <> Status4.Finish then
            match ctx.RuntimeMode with
            | RuntimeMode.Simulation ->
                ctx.ForceWorkState deviceWorkGuid Status4.Going
            | RuntimeMode.Control ->
                for addr in ctx.GetTxOutAddresses deviceWorkGuid do
                    ctx.WriteTag addr "true"

                ctx.ForceWorkState deviceWorkGuid Status4.Going
            | RuntimeMode.Monitoring
            | RuntimeMode.VirtualPlant ->
                ()
            | _ ->
                ()

    let executeCallGoing index (ctx: ApiCallExecutionContext) callGuid =
        SimIndex.txWorkGuids index callGuid |> List.iter (executeApiCall ctx)

    let executeCallHoming index (ctx: ApiCallExecutionContext) (callGuid: Guid) (goingTargets: Set<Guid>) =
        for txGuid in SimIndex.txWorkGuids index callGuid do
            if goingTargets.Contains txGuid then
                executeApiCall ctx txGuid

    let private resetOutTagsForCall (ctx: CallTransitionApplyContext) callGuid =
        if ctx.RuntimeMode = RuntimeMode.Control then
            for outAddress in ctx.GetCallOutAddresses callGuid do
                ctx.WriteTag outAddress "false"

    let applyCallTransition (ctx: CallTransitionApplyContext) callGuid newState =
        let prevState = ctx.GetCallState callGuid
        ctx.ApplyCallTransitionCore callGuid newState
        let curState = ctx.GetCallState callGuid

        if prevState <> Status4.Finish && curState = Status4.Finish then
            resetOutTagsForCall ctx callGuid

    let handleDurationComplete (ctx: DurationCompleteContext) workGuid =
        ctx.RemoveDuration workGuid

        if not ctx.IsPassiveMode && ctx.GetWorkState workGuid = Status4.Going then
            ctx.MarkMinDurationMet workGuid

            let callGuids = ctx.GetWorkCallGuids workGuid

            if callGuids.IsEmpty then
                ctx.ApplyWorkTransition workGuid Status4.Finish
            elif callGuids |> List.forall (fun callGuid -> ctx.GetCallState callGuid = Status4.Finish) then
                ctx.ApplyWorkTransition workGuid Status4.Finish
