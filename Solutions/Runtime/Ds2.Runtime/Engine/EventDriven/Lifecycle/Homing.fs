namespace Ds2.Runtime.Engine

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Model

/// 자동 원위치 페이즈 사전 계산 (Pure).
/// Engine.StartWithHomingPhase에서 context 빌드 전에 필요한 Active Call/Work + going target 계산.
module internal HomingPhaseSetup =

    type Plan = {
        ActiveCallGuids: Guid list
        AllGoingTargets: Set<Guid>
        DisplayHomingCallGuids: Guid list
        ExecutionCallGuids: Guid list
        ActiveWorkGuids: Guid list
    }

    let private filterActiveCallGuids (index: SimIndex) =
        index.AllCallGuids
        |> List.filter (fun cg ->
            index.CallWorkGuid |> Map.tryFind cg
            |> Option.bind (fun wg -> index.WorkSystemName |> Map.tryFind wg)
            |> Option.map index.ActiveSystemNames.Contains
            |> Option.defaultValue false)

    let computePlan (index: SimIndex) (isFinishedGuids: Set<Guid>) : Plan =
        let activeCallGuids = filterActiveCallGuids index

        let finishTargets, _ = SimIndex.computeAutoHomingPlan index
        let displayHomingCalls, _ = SimIndex.computeAutoHomingCallPlan index
        let allGoingTargets = finishTargets |> Set.filter (fun g -> not (isFinishedGuids.Contains g))

        let callTriggersHoming callGuid =
            SimIndex.txWorkGuids index callGuid
            |> List.exists allGoingTargets.Contains

        let executionCallGuids =
            activeCallGuids
            |> List.filter callTriggersHoming

        let displayHomingCallGuids =
            activeCallGuids
            |> List.filter (fun callGuid ->
                displayHomingCalls.Contains(callGuid)
                && (SimIndex.rxWorkGuids index callGuid
                    |> List.exists allGoingTargets.Contains))

        let activeWorkGuids =
            displayHomingCallGuids
            |> List.choose (fun cg -> index.CallWorkGuid |> Map.tryFind cg)
            |> List.distinct

        {
            ActiveCallGuids = activeCallGuids
            AllGoingTargets = allGoingTargets
            DisplayHomingCallGuids = displayHomingCallGuids
            ExecutionCallGuids = executionCallGuids
            ActiveWorkGuids = activeWorkGuids
        }

type HomingPhaseContext = {
    AllGoingTargets: Set<Guid>
    DisplayHomingCallGuids: Guid list
    ExecutionCallGuids: Guid list
    ActiveWorkGuids: Guid list
    IsHomingPhase: unit -> bool
    SetIsHomingPhase: bool -> unit
    SetWorkStateDirect: Guid -> Status4 -> unit
    SetCallStateDirect: Guid -> Status4 -> unit
    TriggerHomingPhaseCompleted: unit -> unit
    SubscribeWorkStateChanged: (WorkStateChangedArgs -> unit) -> IDisposable
    StartEngine: unit -> unit
    ExecuteCallHoming: Guid -> Set<Guid> -> unit
}

module EventDrivenHoming =
    let private finishHomingPhase (ctx: HomingPhaseContext) =
        for callGuid in ctx.DisplayHomingCallGuids do
            ctx.SetCallStateDirect callGuid Status4.Ready

        for workGuid in ctx.ActiveWorkGuids do
            ctx.SetWorkStateDirect workGuid Status4.Ready

        ctx.SetIsHomingPhase false
        ctx.TriggerHomingPhaseCompleted ()

    let startWithHomingPhase (ctx: HomingPhaseContext) =
        if ctx.AllGoingTargets.IsEmpty then
            ctx.StartEngine()
            false
        else
            ctx.SetIsHomingPhase true

            for workGuid in ctx.ActiveWorkGuids do
                ctx.SetWorkStateDirect workGuid Status4.Homing

            for callGuid in ctx.DisplayHomingCallGuids do
                ctx.SetCallStateDirect callGuid Status4.Homing

            let homingTargets = HashSet<Guid>(ctx.AllGoingTargets)
            let mutable handler: IDisposable option = None

            let subscription =
                ctx.SubscribeWorkStateChanged(fun args ->
                    if ctx.IsHomingPhase() && args.NewState = Status4.Finish && homingTargets.Contains(args.WorkGuid) then
                        homingTargets.Remove(args.WorkGuid) |> ignore

                        if homingTargets.Count = 0 then
                            handler |> Option.iter (fun subscription -> subscription.Dispose())
                            finishHomingPhase ctx)

            handler <- Some subscription

            ctx.StartEngine()

            for callGuid in ctx.ExecutionCallGuids do
                ctx.ExecuteCallHoming callGuid ctx.AllGoingTargets

            true
