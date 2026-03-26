namespace Ds2.Runtime.Sim.Engine

open System
open Ds2.Core
open Ds2.Runtime.Sim.Model
open Ds2.Runtime.Sim.Engine.Core

module internal TokenFlow =
    type Context = {
        Index: SimIndex
        StateManager: StateManager
        CurrentTimeMs: unit -> int64
        TriggerTokenEvent: TokenEventArgs -> unit
    }

    let workNameOf (ctx: Context) guid =
        ctx.Index.WorkName |> Map.tryFind guid |> Option.defaultValue (string guid)

    let canReceiveToken (ctx: Context) workGuid =
        ctx.StateManager.GetWorkState(workGuid) = Status4.Ready
        && (SimState.getWorkToken workGuid (ctx.StateManager.GetState()) |> Option.isNone)

    let emitTokenEvent (ctx: Context) kind token workGuid (targetGuid: Guid option) =
        let clock = TimeSpan.FromMilliseconds(float (ctx.CurrentTimeMs()))
        ctx.TriggerTokenEvent({
            Kind = kind
            Token = token
            WorkGuid = workGuid
            WorkName = workNameOf ctx workGuid
            TargetWorkGuid = targetGuid
            TargetWorkName = targetGuid |> Option.map (workNameOf ctx)
            Clock = clock })

    let shiftToken (ctx: Context) (workGuid: Guid) (token: TokenValue) =
        let completeTokenAtWork () =
            ctx.StateManager.SetWorkToken(workGuid, None)
            ctx.StateManager.AddCompletedToken(token)
            emitTokenEvent ctx Complete token workGuid None

        let receivableSuccessors () =
            ctx.Index.WorkTokenSuccessors
            |> Map.tryFind workGuid
            |> Option.defaultValue []
            |> List.filter (canReceiveToken ctx)

        let shiftTokenToTargets targetGuids =
            for targetGuid in targetGuids do
                ctx.StateManager.SetWorkToken(targetGuid, Some token)
                emitTokenEvent ctx Shift token workGuid (Some targetGuid)
            ctx.StateManager.SetWorkToken(workGuid, None)

        if ctx.Index.TokenSinkGuids.Contains(workGuid) then
            completeTokenAtWork ()
        else
            match receivableSuccessors () with
            | [] -> emitTokenEvent ctx Blocked token workGuid None
            | targetGuids -> shiftTokenToTargets targetGuids

    let onWorkFinish (ctx: Context) (workGuid: Guid) =
        let tryGetActiveToken () =
            SimState.getWorkToken workGuid (ctx.StateManager.GetState())

        let isIgnoreRole () =
            match ctx.Index.WorkTokenRole |> Map.tryFind workGuid with
            | Some role -> role.HasFlag(TokenRole.Ignore)
            | None -> false

        let completeTokenAtWork token =
            ctx.StateManager.SetWorkToken(workGuid, None)
            ctx.StateManager.AddCompletedToken(token)
            emitTokenEvent ctx Complete token workGuid None

        match tryGetActiveToken () with
        | None -> ()
        | Some token when isIgnoreRole () ->
            completeTokenAtWork token
        | Some token ->
            shiftToken ctx workGuid token
