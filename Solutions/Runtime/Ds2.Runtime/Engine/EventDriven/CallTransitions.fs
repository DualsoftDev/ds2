namespace Ds2.Runtime.Engine

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Runtime.Model
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Engine.Scheduler

/// Call 상태 전이 + IO값 관리
module internal CallTransitions =

    type Context = {
        Index: SimIndex
        StateManager: StateManager
        Scheduler: EventScheduler
        RuntimeMode: RuntimeMode
        ShouldSkipCall: Guid -> bool
        TriggerCallStateChanged: CallStateChangedArgs -> unit
        ScheduleWorkIfReady: Guid -> Status4 -> unit
        ScheduleConditionEvaluation: unit -> unit
        DurationTracker: DurationTracker
        TimeIgnore: unit -> bool
        ExecuteCallGoing: Guid -> unit
    }

    /// Call F 시 ApiCall의 InputSpec을 IO값으로 설정
    let setCallIOValues (ctx: Context) (callGuid: Guid) =
        SimIndex.findOrEmpty callGuid ctx.Index.CallApiCallGuids
        |> List.iter (fun apiCallId ->
            Queries.getApiCall apiCallId ctx.Index.Store
            |> Option.iter (fun apiCall ->
                ctx.StateManager.SetIOValue(apiCallId, ValueSpec.toDefaultString apiCall.InputSpec)))

    /// Call F -> RxWork에 IO값 설정 (RxGuid가 있는 ApiCall만)
    let setRxWorkIOValues (ctx: Context) (callGuid: Guid) =
        SimIndex.findOrEmpty callGuid ctx.Index.CallApiCallGuids
        |> List.iter (fun apiCallId ->
            Queries.getApiCall apiCallId ctx.Index.Store
            |> Option.iter (fun apiCall ->
                let hasRx =
                    apiCall.ApiDefId
                    |> Option.bind (fun defId -> Queries.getApiDef defId ctx.Index.Store)
                    |> Option.bind (fun def -> def.RxGuid)
                    |> Option.isSome
                if hasRx then
                    ctx.StateManager.SetIOValue(apiCallId, ValueSpec.toDefaultString apiCall.InputSpec)))

    let applyCallTransition (ctx: Context) (callGuid: Guid) newState =
        let result = ctx.StateManager.ApplyCallTransition(callGuid, newState, ctx.ShouldSkipCall)
        if not result.HasChanged then () else
        let clock = TimeSpan.FromMilliseconds(float ctx.Scheduler.CurrentTimeMs)
        if result.ActualNewState = Status4.Finish then setCallIOValues ctx callGuid
        ctx.TriggerCallStateChanged({
            CallGuid = callGuid; CallName = result.NodeName
            PreviousState = result.OldState; NewState = result.ActualNewState; IsSkipped = result.IsSkipped; Clock = clock })
        match result.ActualNewState with
        | Status4.Going ->
            let rxGuids = SimIndex.rxWorkGuids ctx.Index callGuid
            if not rxGuids.IsEmpty then
                ctx.StateManager.SnapshotCallRxEpochs(callGuid, rxGuids)
            ctx.ExecuteCallGoing callGuid
            // Call Timeout 스케줄
            match ctx.Index.CallTimeoutMap |> Map.tryFind callGuid with
            | Some timeout when not (ctx.TimeIgnore()) ->
                ctx.Scheduler.ScheduleAfter(
                    ScheduledEventType.CallTimeout callGuid,
                    int64 timeout.TotalMilliseconds,
                    ScheduledEvent.PriorityDurationCheck) |> ignore
            | _ -> ()
            ctx.ScheduleConditionEvaluation ()
        | Status4.Finish ->
            if not result.IsSkipped then
                setRxWorkIOValues ctx callGuid
                SimIndex.rxWorkGuids ctx.Index callGuid |> List.iter (fun rxGuid -> ctx.ScheduleWorkIfReady rxGuid Status4.Finish)
            // Pause 중 마지막 Call Finish -> 해당 Work의 중단된 duration 재스케줄
            match ctx.Index.CallWorkGuid |> Map.tryFind callGuid with
            | Some workGuid ->
                let callGuids = SimIndex.findOrEmpty workGuid ctx.Index.WorkCallGuids
                if callGuids |> List.forall (fun cg -> ctx.StateManager.GetCallState(cg) = Status4.Finish) then
                    ctx.DurationTracker.TryResumePausedDuration(workGuid)
            | None -> ()
            ctx.ScheduleConditionEvaluation ()
        | Status4.Ready ->
            ctx.StateManager.ClearCallRxEpochSnapshot(callGuid)
        | _ -> ()
