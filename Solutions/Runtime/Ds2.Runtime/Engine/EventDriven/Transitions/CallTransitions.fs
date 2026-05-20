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
        /// v10 §11 — 이 Call 의 SensingType.Append ms 최대값 (Call 의 ApiCall 들 중).
        /// 0 = 추가 대기 없음. > 0 = Going→Finish 전이를 ms 후로 연기.
        GetCallSensingAppendMs: Guid -> int
        /// v10 §11 — Call 단위 sensing append 적용 여부 flag. false → 1차 호출, true → 2차 호출(확정).
        IsCallSensingAppendApplied: Guid -> bool
        /// v10 §11 — 적용 표시 + ms 후 Call Finish 재 schedule.
        ApplyCallSensingAppendDelay: Guid * int -> unit
        /// v10 §11 — Call cycle 종료/리셋 시 sensing append flag 제거.
        ClearCallSensingAppendDelay: Guid -> unit
    }

    /// v10 §11.2 — SetIOValue 직후 *해당 ApiCall 의 SensingType debounce ms* 만큼 후 ConditionEval 재 schedule.
    /// WaitInputStable / WaitInputEdgeStable 의 n ms 안정 시간 충족 시점에 자동 trigger.
    let private scheduleDebounceReeval (ctx: Context) (apiCallId: Guid) =
        let appendMs = SimIndex.apiCallSensingAppendMs ctx.Index apiCallId
        if appendMs > 0 && not (ctx.TimeIgnore()) then
            ctx.Scheduler.ScheduleAfter(
                ScheduledEventType.EvaluateConditions,
                int64 appendMs,
                ScheduledEvent.PriorityConditionEval) |> ignore

    /// Call F 시 ApiCall의 InputSpec을 IO값으로 설정
    let setCallIOValues (ctx: Context) (callGuid: Guid) =
        SimIndex.findOrEmpty callGuid ctx.Index.CallApiCallGuids
        |> List.iter (fun apiCallId ->
            Queries.getApiCall apiCallId ctx.Index.Store
            |> Option.iter (fun apiCall ->
                ctx.StateManager.SetIOValue(apiCallId, ValueSpec.toDefaultString apiCall.InputSpec)
                scheduleDebounceReeval ctx apiCallId))

    /// Call R(Reset) 시 그 Call 이 직접 owning 한 ApiCall 들의 IOValue 를 비움.
    /// Simulation/Control 모드에서만 호출 — 두 모드 모두 시뮬 엔진이 reset 의 주체.
    /// Monitoring/VirtualPlant 는 외부 PLC 가 진실원이라 자체 clear 금지.
    let clearCallIOValues (ctx: Context) (callGuid: Guid) =
        let apiCallIds = SimIndex.findOrEmpty callGuid ctx.Index.CallApiCallGuids
        if not apiCallIds.IsEmpty then
            ctx.StateManager.ClearIOValues(apiCallIds)

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
                    ctx.StateManager.SetIOValue(apiCallId, ValueSpec.toDefaultString apiCall.InputSpec)
                    scheduleDebounceReeval ctx apiCallId))

    let private applyCallTransitionCore (ctx: Context) (callGuid: Guid) newState =
        let result = ctx.StateManager.ApplyCallTransition(callGuid, newState, ctx.ShouldSkipCall)
        if not result.HasChanged then () else
        let clock = TimeSpan.FromMilliseconds(float ctx.Scheduler.CurrentTimeMs)
        if result.ActualNewState = Status4.Finish then setCallIOValues ctx callGuid
        ctx.TriggerCallStateChanged({
            CallGuid = callGuid; CallName = result.NodeName
            PreviousState = result.OldState; NewState = result.ActualNewState; IsSkipped = result.IsSkipped; Clock = clock })
        match result.ActualNewState with
        | Status4.Going ->
            let apiCallGuids = SimIndex.findOrEmpty callGuid ctx.Index.CallApiCallGuids
            if not apiCallGuids.IsEmpty then
                ctx.StateManager.SnapshotCallInputEpochs(callGuid, apiCallGuids)
            let completionWorkGuids = SimIndex.completionWorkGuids ctx.Index callGuid
            if not completionWorkGuids.IsEmpty then
                ctx.StateManager.SnapshotCallRxEpochs(callGuid, completionWorkGuids)
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
            ctx.ClearCallSensingAppendDelay callGuid
            ctx.StateManager.ClearCallInputEpochSnapshot(callGuid)
            ctx.StateManager.ClearCallRxEpochSnapshot(callGuid)
            // 시뮬 엔진이 reset 주체인 모드에서만 IO 값 비움 — 외부 PLC 가 진실원인 모드는 유지.
            match ctx.RuntimeMode with
            | RuntimeMode.Simulation
            | RuntimeMode.Control -> clearCallIOValues ctx callGuid
            | _ -> ()
        | _ -> ()

    let applyCallTransition (ctx: Context) (callGuid: Guid) newState =
        // v10 §11.2 — SensingType.Real(_, Some Append n) 의 안정 시간은 WorkConditionChecker 의
        // completionTriggerSatisfied 가 *condition eval 시점에 stable ms* 측정으로 정확히 구현.
        // (이전 Call 단위 sensing append delay 분기는 §11.2 측정과 중복되어 제거.)
        applyCallTransitionCore ctx callGuid newState
