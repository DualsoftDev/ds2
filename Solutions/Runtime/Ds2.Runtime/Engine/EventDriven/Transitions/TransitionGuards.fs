namespace Ds2.Runtime.Engine

open System
open Ds2.Core
open Ds2.Runtime.Engine.Core

/// 스케줄된 전이 이벤트 실행 전 상태 검증 (shouldApply) 로직
module internal TransitionGuards =

    type Context = {
        Index: SimIndex
        StateManager: StateManager
        IsWorkFrozen: Guid -> bool
        CanStartCall: Guid -> bool
        CanCompleteCall: Guid -> bool
        ApplyWorkTransition: Guid -> Status4 -> unit
        ApplyCallTransition: Guid -> Status4 -> unit
        OnCallFinishRejected: Guid -> unit
    }

    let clearAndApplyWork (ctx: Context) workGuid newState =
        ctx.StateManager.ClearWorkPending(workGuid)
        let shouldApply =
            match newState with
            | Status4.Going ->
                // Ready 상태만 확인 — canStartWork는 스케줄 시점(evaluateWorkStarts)에서 이미 검증됨.
                // 여기서 재검증하면 선행 Work가 이미 Homing/Ready로 리셋된 경우 레이스 컨디션 발생.
                ctx.StateManager.GetWorkState(workGuid) = Status4.Ready
            | Status4.Finish ->
                if ctx.StateManager.GetWorkState(workGuid) <> Status4.Going then false
                elif ctx.IsWorkFrozen workGuid then false
                else
                    let callGuids = SimIndex.findOrEmpty workGuid ctx.Index.WorkCallGuids
                    ctx.StateManager.IsMinDurationMet(workGuid)
                    && (callGuids |> List.forall (fun callGuid -> ctx.StateManager.GetCallState(callGuid) = Status4.Finish))
            | Status4.Homing ->
                // 리셋 조건은 evaluateWorkResets에서 이미 검증됨 (scheduledGoingGuids 포함)
                // 여기서 canResetWork를 재검증하면 pred가 아직 Going이 아닌 경우 거부됨
                ctx.StateManager.GetWorkState(workGuid) = Status4.Finish
            | Status4.Ready ->
                ctx.StateManager.GetWorkState(workGuid) = Status4.Homing
            | _ -> true

        if shouldApply then
            ctx.ApplyWorkTransition workGuid newState

    let clearAndApplyCall (ctx: Context) callGuid newState =
        ctx.StateManager.ClearCallPending(callGuid)
        let shouldApply =
            match newState with
            | Status4.Going ->
                match ctx.Index.CallWorkGuid |> Map.tryFind callGuid with
                | Some workGuid ->
                    ctx.StateManager.GetCallState(callGuid) = Status4.Ready
                    && not (ctx.IsWorkFrozen workGuid)
                    && ctx.CanStartCall callGuid
                    && not (
                        match ctx.Index.CallRaceExclusions |> Map.tryFind callGuid with
                        | Some excludedSet ->
                            excludedSet |> Set.exists (fun ex ->
                                ctx.StateManager.GetCallState(ex) = Status4.Going
                                || ctx.StateManager.IsCallPending(ex))
                        | None -> false)
                | None -> false
            | Status4.Finish ->
                match ctx.Index.CallWorkGuid |> Map.tryFind callGuid with
                | Some workGuid ->
                    let canApply =
                        ctx.StateManager.GetCallState(callGuid) = Status4.Going
                        && not (ctx.IsWorkFrozen workGuid)
                        && ctx.CanCompleteCall callGuid
                    if not canApply then
                        ctx.OnCallFinishRejected callGuid
                    canApply
                | None -> false
            | Status4.Homing ->
                match ctx.Index.CallWorkGuid |> Map.tryFind callGuid with
                | Some workGuid -> ctx.StateManager.GetWorkState(workGuid) = Status4.Homing
                | None -> false
            | Status4.Ready ->
                match ctx.Index.CallWorkGuid |> Map.tryFind callGuid with
                | Some workGuid -> ctx.StateManager.GetWorkState(workGuid) = Status4.Ready
                | None -> false
            | _ -> true

        if shouldApply then
            ctx.ApplyCallTransition callGuid newState

    let forceAndApplyWork (ctx: Context) workGuid newState =
        ctx.StateManager.ClearWorkPending(workGuid)
        // device work 의 Forced Going 은 apply 시점에 Ready 재확인 — schedule-time IfReady 의 race window 차단.
        //   forced 전이는 무가드라, 큐 enqueue(Ready 확인) 후 적용 전 device 가 engine cycle 로 Finish/Homing 가면
        //   Forced Going 이 무가드로 그 위에 박혀 Finish->Going / Homing->Going 비정상 전이가 났다.
        //   (active work force 는 isDevice=false 라 무영향. device going trigger 만 apply-time 가드.)
        let isDevice =
            match ctx.Index.WorkSystemName |> Map.tryFind workGuid with
            | Some sysName -> not (ctx.Index.ActiveSystemNames.Contains sysName)
            | None -> false
        let blockedForcedGoing =
            newState = Status4.Going && isDevice
            && ctx.StateManager.GetWorkState(workGuid) <> Status4.Ready
        if not blockedForcedGoing then
            ctx.ApplyWorkTransition workGuid newState

    let forceAndApplyCall (ctx: Context) callGuid newState =
        ctx.StateManager.ClearCallPending(callGuid)
        ctx.ApplyCallTransition callGuid newState
