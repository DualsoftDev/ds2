namespace Ds2.Runtime.Sim.Engine

open System
open Ds2.Core
open Ds2.Runtime.Sim.Model
open Ds2.Runtime.Sim.Engine.Core
open Ds2.Runtime.Sim.Engine.Scheduler

/// 화살표 변경 후 고아 Work/토큰 정리 로직
module internal ConnectionReload =

    type Context = {
        Index: SimIndex
        StateManager: StateManager
        Scheduler: EventScheduler
        IsActiveSystemWork: Guid -> bool
        EmitTokenEvent: TokenEventKind -> TokenValue -> Guid -> Guid option -> unit
        TriggerWorkStateChanged: WorkStateChangedArgs -> unit
        PauseDuration: Guid -> unit
        ResumePausedDuration: Guid -> unit
        /// paused duration 제거 콜백
        ClearPausedDuration: Guid -> unit
    }

    let removeScheduledConditionEvents (ctx: Context) =
        ctx.Scheduler.RemoveWhere(fun event ->
            match event.EventType with
            | ScheduledEventType.EvaluateConditions -> true
            | _ -> false)
        |> ignore

    let private removeScheduledEventsForWorkGroup (ctx: Context) (groupWorkGuids: Guid list) =
        let workGuidSet = groupWorkGuids |> Set.ofList
        let callGuidSet =
            groupWorkGuids
            |> List.collect (fun workGuid -> SimIndex.findOrEmpty workGuid ctx.Index.WorkCallGuids)
            |> Set.ofList

        ctx.Scheduler.RemoveWhere(fun event ->
            match event.EventType with
            | ScheduledEventType.WorkTransition(workGuid, _)
            | ScheduledEventType.ForcedWorkTransition(workGuid, _)
            | ScheduledEventType.DurationComplete workGuid
            | ScheduledEventType.HomingComplete workGuid ->
                workGuidSet.Contains(workGuid)
            | ScheduledEventType.CallTransition(callGuid, _)
            | ScheduledEventType.ForcedCallTransition(callGuid, _) ->
                callGuidSet.Contains(callGuid)
            | ScheduledEventType.EvaluateConditions -> false)
        |> ignore

        for workGuid in groupWorkGuids do
            ctx.StateManager.ClearWorkPending(workGuid)
            ctx.PauseDuration(workGuid)

        for callGuid in callGuidSet do
            ctx.StateManager.ClearCallPending(callGuid)

    let private unfreezeWorkGroup (ctx: Context) (groupWorkGuids: Guid list) =
        for workGuid in groupWorkGuids do
            ctx.StateManager.UnfreezeWork(workGuid)
            ctx.ResumePausedDuration(workGuid)

    let private freezeWorkGroup (ctx: Context) (groupWorkGuids: Guid list) =
        removeScheduledEventsForWorkGroup ctx groupWorkGuids
        for workGuid in groupWorkGuids do
            ctx.StateManager.FreezeWork(workGuid)

    let private anyPureStartPredBefore (snapshot: SimIndex.ConnectionSnapshot) (groupWorkGuids: Guid list) =
        groupWorkGuids
        |> List.exists (fun workGuid ->
            snapshot.WorkPureStartPreds
            |> Map.tryFind workGuid
            |> Option.defaultValue []
            |> List.isEmpty
            |> not)

    /// 순수 Start 의존으로 Going에 들어간 Work는 연결 상실 시 Going/Token 유지 + 진행 정지.
    /// StartReset으로 시작된 Work는 연결 변경 후에도 현재 진행을 유지한다.
    let invalidateOrphanedGoingWorks
        (ctx: Context)
        (previousSnapshot: SimIndex.ConnectionSnapshot)
        (_currentSnapshot: SimIndex.ConnectionSnapshot) =
        let activeCanonicalWorks =
            ctx.Index.AllWorkGuids
            |> List.filter ctx.IsActiveSystemWork
            |> List.map (SimIndex.canonicalWorkGuid ctx.Index)
            |> List.distinct

        let simState = ctx.StateManager.GetState()

        for canonicalWorkGuid in activeCanonicalWorks do
            let groupWorkGuids = SimIndex.referenceGroupOf ctx.Index canonicalWorkGuid
            let isGoing =
                groupWorkGuids
                |> List.exists (fun workGuid -> ctx.StateManager.GetWorkState(workGuid) = Status4.Going)

            if isGoing then
                let hadPureStartBefore = anyPureStartPredBefore previousSnapshot groupWorkGuids
                let stillStartSatisfied =
                    groupWorkGuids
                    |> List.exists (fun workGuid -> WorkConditionChecker.predecessorSatisfied ctx.Index simState workGuid)

                if hadPureStartBefore && not stillStartSatisfied then
                    freezeWorkGroup ctx groupWorkGuids
                else
                    unfreezeWorkGroup ctx groupWorkGuids

    let invalidateOrphanedTokenHolders (ctx: Context) =
        for workGuid in ctx.Index.AllWorkGuids do
            if ctx.IsActiveSystemWork workGuid
               && not (ctx.Index.TokenPathGuids.Contains workGuid)
               && not (ctx.Index.TokenSinkGuids.Contains workGuid) then
                match ctx.StateManager.GetWorkToken(workGuid) with
                | Some token ->
                    match ctx.StateManager.GetWorkState(workGuid) with
                    | Status4.Going ->
                        ()
                    | _ ->
                        ctx.StateManager.SetWorkToken(workGuid, None)
                        ctx.EmitTokenEvent Discard token workGuid None
                | None -> ()
