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
                    match index.WorkSystemName |> Map.tryFind predGuid, index.WorkName |> Map.tryFind predGuid with
                    | Some sysName, Some wName -> Some (sysName, wName)
                    | _ -> None)
                |> List.distinct
            predecessorKeys |> combiner (fun (sysName, wName) ->
                index.AllWorkGuids |> List.exists (fun wg ->
                    index.WorkSystemName |> Map.tryFind wg = Some sysName &&
                    index.WorkName |> Map.tryFind wg = Some wName &&
                    state.WorkStates |> Map.tryFind wg = Some targetState))

    /// Work 시작 가능 여부 (PredecessorStart 모두 F)
    let canStartWork (index: SimIndex) (state: SimState) (workGuid: Guid) : bool =
        let preds = index.WorkStartPreds |> Map.tryFind workGuid |> Option.defaultValue []
        checkPredecessorCondition index state preds Status4.Finish List.forall

    /// Work 리셋 가능 여부 (PredecessorReset 중 하나라도 G)
    let canResetWork (index: SimIndex) (state: SimState) (workGuid: Guid) : bool =
        match index.WorkSystemName |> Map.tryFind workGuid, index.WorkName |> Map.tryFind workGuid with
        | Some sysName, Some wName ->
            let allSameKeyPreds =
                index.AllWorkGuids
                |> List.filter (fun wg ->
                    index.WorkSystemName |> Map.tryFind wg = Some sysName &&
                    index.WorkName |> Map.tryFind wg = Some wName)
                |> List.collect (fun wg -> index.WorkResetPreds |> Map.tryFind wg |> Option.defaultValue [])
                |> List.distinct
            if allSameKeyPreds.IsEmpty then false
            else checkPredecessorCondition index state allSameKeyPreds Status4.Going List.exists
        | _ -> false

    /// 조건 스펙 평가 (RxWork 상태 + ValueSpec 비교)
    let checkConditionSpec (state: SimState) (spec: ConditionEntry) : bool =
        if ValueSpecEvaluator.isFalseSpec spec.InputSpec then
            state.WorkStates |> Map.tryFind spec.RxWorkGuid = Some Status4.Ready
        else
            match state.WorkStates |> Map.tryFind spec.RxWorkGuid with
            | Some s when s = Status4.Finish ->
                match spec.ApiCallGuid with
                | Some apiCallGuid ->
                    match state.IOValues |> Map.tryFind apiCallGuid with
                    | Some currentValue -> ValueSpecEvaluator.evaluate spec.InputSpec currentValue
                    | None -> true
                | None -> true
            | _ -> false

    /// ActiveTriggers Skip 여부
    let shouldSkipCall (index: SimIndex) (state: SimState) (callGuid: Guid) : bool =
        let activeSpecs = index.CallActiveConditions |> Map.tryFind callGuid |> Option.defaultValue []
        if activeSpecs.IsEmpty then false
        else not (activeSpecs |> List.forall (checkConditionSpec state))

    /// Call 시작 가능 여부 (Work G + 선행 Call F + Auto/Common 조건)
    let canStartCall (index: SimIndex) (state: SimState) (callGuid: Guid) : bool =
        let callWork = index.CallWorkGuid |> Map.tryFind callGuid
        let callPreds = index.CallStartPreds |> Map.tryFind callGuid |> Option.defaultValue []
        let basicOk =
            callWork |> Option.map (fun wg -> state.WorkStates |> Map.tryFind wg = Some Status4.Going) |> Option.defaultValue false &&
            callPreds |> List.forall (fun pred -> state.CallStates |> Map.tryFind pred = Some Status4.Finish)
        if not basicOk then false
        elif shouldSkipCall index state callGuid then true
        else
            let autoSpecs = index.CallAutoConditions |> Map.tryFind callGuid |> Option.defaultValue []
            let commonSpecs = index.CallCommonConditions |> Map.tryFind callGuid |> Option.defaultValue []
            (autoSpecs |> List.forall (checkConditionSpec state)) &&
            (commonSpecs |> List.forall (checkConditionSpec state))

    /// Call 완료 가능 여부 (RxWork 모두 F)
    let canCompleteCall (index: SimIndex) (state: SimState) (callGuid: Guid) : bool =
        let apiCallIds = index.CallApiCallGuids |> Map.tryFind callGuid |> Option.defaultValue []
        if apiCallIds.IsEmpty then true
        else
            apiCallIds |> List.forall (fun apiCallId ->
                match Ds2.UI.Core.DsQuery.getApiCall apiCallId index.Store with
                | Some apiCall ->
                    match apiCall.ApiDefId with
                    | Some apiDefId ->
                        match Ds2.UI.Core.DsQuery.getApiDef apiDefId index.Store with
                        | Some apiDef ->
                            match apiDef.Properties.RxGuid with
                            | Some rxGuid -> state.WorkStates |> Map.tryFind rxGuid = Some Status4.Finish
                            | None -> true
                        | None -> true
                    | None -> true
                | None -> true)
