namespace Ds2.Runtime.Sim.Engine.Core

open System
open Ds2.Core
open Ds2.Runtime.Sim.Model

/// Work/Call 상태 전이 조건 검사 모듈 (순수 함수)
module WorkConditionChecker =

    /// Predecessor 조건 검사 공통 함수
    let private checkPredecessorCondition
        (index: SimIndex) (state: SimState) (predecessorGuids: Guid list)
        (targetState: Status4) (combiner: ((string * string) -> bool) -> (string * string) list -> bool) : bool =
        if predecessorGuids.IsEmpty then false
        else
            let predecessorKeys =
                predecessorGuids
                |> List.choose (fun predGuid ->
                    match Map.tryFind predGuid index.WorkSystemName, Map.tryFind predGuid index.WorkName with
                    | Some sysName, Some wName -> Some (sysName, wName)
                    | _ -> None)
                |> List.distinct
            predecessorKeys |> combiner (fun (sysName, wName) ->
                index.AllWorkGuids |> List.exists (fun wg ->
                    Map.tryFind wg index.WorkSystemName = Some sysName &&
                    Map.tryFind wg index.WorkName = Some wName &&
                    Map.tryFind wg state.WorkStates = Some targetState))

    /// Work 시작 가능 여부 (PredecessorStart 모두 F)
    let canStartWork (index: SimIndex) (state: SimState) (workGuid: Guid) : bool =
        let preds = SimIndex.findOrEmpty workGuid index.WorkStartPreds
        checkPredecessorCondition index state preds Status4.Finish List.forall

    /// Work 리셋 가능 여부 (PredecessorReset 중 하나라도 G)
    let canResetWork (index: SimIndex) (state: SimState) (workGuid: Guid) : bool =
        match Map.tryFind workGuid index.WorkSystemName, Map.tryFind workGuid index.WorkName with
        | Some sysName, Some wName ->
            let allSameKeyPreds =
                index.AllWorkGuids
                |> List.filter (fun wg ->
                    Map.tryFind wg index.WorkSystemName = Some sysName &&
                    Map.tryFind wg index.WorkName = Some wName)
                |> List.collect (fun wg -> SimIndex.findOrEmpty wg index.WorkResetPreds)
                |> List.distinct
            if allSameKeyPreds.IsEmpty then false
            else checkPredecessorCondition index state allSameKeyPreds Status4.Going List.exists
        | _ -> false

    /// 조건 스펙 평가 (RxWork 상태 + ValueSpec 비교)
    let checkConditionSpec (state: SimState) (spec: ConditionEntry) : bool =
        if ValueSpec.isFalse spec.InputSpec then
            state.WorkStates |> Map.tryFind spec.RxWorkGuid = Some Status4.Ready
        else
            match state.WorkStates |> Map.tryFind spec.RxWorkGuid with
            | Some s when s = Status4.Finish ->
                match spec.ApiCallGuid with
                | Some apiCallGuid ->
                    match state.IOValues |> Map.tryFind apiCallGuid with
                    | Some currentValue -> ValueSpec.evaluate spec.InputSpec currentValue
                    | None -> true
                | None -> true
            | _ -> false

    /// ActiveTriggers Skip 여부
    let shouldSkipCall (index: SimIndex) (state: SimState) (callGuid: Guid) : bool =
        let activeSpecs = SimIndex.findOrEmpty callGuid index.CallActiveConditions
        if activeSpecs.IsEmpty then false
        else not (activeSpecs |> List.forall (checkConditionSpec state))

    /// Call 시작 가능 여부 (Work G + 선행 Call F + Auto/Common 조건)
    let canStartCall (index: SimIndex) (state: SimState) (callGuid: Guid) : bool =
        let callWork = Map.tryFind callGuid index.CallWorkGuid
        let callPreds = SimIndex.findOrEmpty callGuid index.CallStartPreds
        let basicOk =
            callWork |> Option.map (fun wg -> Map.tryFind wg state.WorkStates = Some Status4.Going) |> Option.defaultValue false &&
            callPreds |> List.forall (fun pred -> Map.tryFind pred state.CallStates = Some Status4.Finish)
        if not basicOk then false
        elif shouldSkipCall index state callGuid then true
        else
            let autoSpecs = SimIndex.findOrEmpty callGuid index.CallAutoConditions
            let commonSpecs = SimIndex.findOrEmpty callGuid index.CallCommonConditions
            (autoSpecs |> List.forall (checkConditionSpec state)) &&
            (commonSpecs |> List.forall (checkConditionSpec state))

    /// Call 완료 가능 여부 (RxWork 모두 F)
    let canCompleteCall (index: SimIndex) (state: SimState) (callGuid: Guid) : bool =
        let rxGuids = SimIndex.rxWorkGuids index callGuid
        if rxGuids.IsEmpty then true
        else rxGuids |> List.forall (fun rxGuid -> Map.tryFind rxGuid state.WorkStates = Some Status4.Finish)
