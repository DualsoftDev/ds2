namespace Ds2.Runtime.Sim.Engine

open System
open Ds2.Core
open Ds2.Runtime.Sim.Engine.Core

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
                ctx.StateManager.GetWorkState(workGuid) = Status4.Finish
                && WorkConditionChecker.canResetWork ctx.Index (ctx.StateManager.GetState()) workGuid
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
                | None -> false
            | Status4.Finish ->
                match ctx.Index.CallWorkGuid |> Map.tryFind callGuid with
                | Some workGuid ->
                    ctx.StateManager.GetCallState(callGuid) = Status4.Going
                    && not (ctx.IsWorkFrozen workGuid)
                    && ctx.CanCompleteCall callGuid
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
        ctx.ApplyWorkTransition workGuid newState

    let forceAndApplyCall (ctx: Context) callGuid newState =
        ctx.StateManager.ClearCallPending(callGuid)
        ctx.ApplyCallTransition callGuid newState
