namespace Ds2.Core.Store

open System
open Ds2.Core
open Ds2.Core.V10Validation

/// <summary>v10 §12 — store 전체 batch validation.
/// 모든 ApiCall (V1/V2/V5), ApiDef (V3/V4), Device 그룹 (V6) 순회.
/// 시뮬레이션 시작 직전 / AASX 임포트 후에 호출.</summary>
module V10ValidationBatch =

    /// Work.Duration TimeSpan option → ms 로 환산.
    let private workDurationMs (store: DsStore) (workIdOpt: Guid option) : int option =
        workIdOpt
        |> Option.bind (fun id ->
            match store.Works.TryGetValue(id) with
            | true, w -> w.Duration |> Option.map (fun ts -> int ts.TotalMilliseconds)
            | _ -> None)

    let validateStore (store: DsStore) : ValidationIssue list =
        let issues = ResizeArray<ValidationIssue>()

        let apiDefById id =
            match store.ApiDefs.TryGetValue(id) with
            | true, d -> Some d
            | _ -> None

        // V1/V2/V5 — ApiCall 단위.
        for kv in store.ApiCalls do
            let apiCall = kv.Value
            match apiCall.ApiDefId |> Option.bind apiDefById with
            | Some apiDef ->
                validateApiCallV1 apiDef apiCall |> Option.iter issues.Add
                validateApiCallV2 apiDef apiCall |> Option.iter issues.Add
            | None -> ()
            for issue in validateApiCallV5 apiCall do
                issues.Add issue

        // V3/V4 — ApiDef 단위.
        for kv in store.ApiDefs do
            let apiDef = kv.Value
            for issue in validateApiDefV3 apiDef do
                issues.Add issue
            for issue in validateApiDefV7 apiDef do
                issues.Add issue
            let txMs = workDurationMs store apiDef.TxGuid
            let rxMs = workDurationMs store apiDef.RxGuid
            for issue in validateApiDefV4 apiDef txMs rxMs do
                issues.Add issue

        // V6 — Device(ApiDef.ParentId) 단위 Latched collision.
        store.ApiDefs.Values
        |> Seq.groupBy (fun ad -> ad.ParentId)
        |> Seq.iter (fun (deviceId, defs) ->
            for issue in validateDeviceV6 deviceId (List.ofSeq defs) do
                issues.Add issue)

        // v12 abnormal timing range — Work 단위.
        for kv in store.Works do
            for issue in validateWorkAbnormalDurationRange kv.Value do
                issues.Add issue

        List.ofSeq issues

    /// 단일 ApiCall 단위 (ApiDef pair 룩업) — V1/V2/V5 만. ApiCall 편집 dialog 저장 시점용.
    let validateSingleApiCall (store: DsStore) (apiCall: ApiCall) : ValidationIssue list =
        let issues = ResizeArray<ValidationIssue>()
        match apiCall.ApiDefId |> Option.bind (fun id ->
                match store.ApiDefs.TryGetValue(id) with
                | true, d -> Some d
                | _ -> None) with
        | Some apiDef ->
            validateApiCallV1 apiDef apiCall |> Option.iter issues.Add
            validateApiCallV2 apiDef apiCall |> Option.iter issues.Add
        | None -> ()
        for issue in validateApiCallV5 apiCall do
            issues.Add issue
        List.ofSeq issues
