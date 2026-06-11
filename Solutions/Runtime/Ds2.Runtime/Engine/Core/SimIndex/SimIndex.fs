namespace Ds2.Runtime.Engine.Core

open System
open Ds2.Core
open Ds2.Core.Store

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module SimIndex =

    let findOrEmpty key map =
        SimIndexAlgorithms.findOrEmpty key map

    let canonicalWorkGuid (index: SimIndex) (workGuid: Guid) =
        index.WorkCanonicalGuids
        |> Map.tryFind workGuid
        |> Option.defaultValue workGuid

    let referenceGroupOf (index: SimIndex) (workGuid: Guid) =
        let canonical = canonicalWorkGuid index workGuid
        index.WorkReferenceGroups
        |> Map.tryFind canonical
        |> Option.defaultValue [ canonical ]

    let workGroupOf (index: SimIndex) (workGuid: Guid) =
        index.WorkGroupSets |> Map.tryFind workGuid |> Option.defaultValue Set.empty

    let canonicalCallGuid (index: SimIndex) (callGuid: Guid) =
        index.CallCanonicalGuids
        |> Map.tryFind callGuid
        |> Option.defaultValue callGuid

    let callReferenceGroupOf (index: SimIndex) (callGuid: Guid) =
        let canonical = canonicalCallGuid index callGuid
        index.CallReferenceGroups
        |> Map.tryFind canonical
        |> Option.defaultValue [ canonical ]

    let isTokenSource (index: SimIndex) (workGuid: Guid) =
        let canonical = canonicalWorkGuid index workGuid
        index.TokenSourceGuids |> List.contains canonical

    let txWorkGuids (index: SimIndex) (callGuid: Guid) =
        SimIndexAlgorithms.resolveApiDefGuids
            index.Store
            (findOrEmpty callGuid index.CallApiCallGuids)
            (fun d -> d.TxGuid)

    let rxWorkGuids (index: SimIndex) (callGuid: Guid) =
        SimIndexAlgorithms.resolveApiDefGuids
            index.Store
            (findOrEmpty callGuid index.CallApiCallGuids)
            (fun d -> d.RxGuid)

    let completionWorkGuids (index: SimIndex) (callGuid: Guid) =
        findOrEmpty callGuid index.CallApiCallGuids
        |> List.choose (fun apiCallGuid ->
            match index.Store.ApiCalls.TryGetValue(apiCallGuid) with
            | true, apiCall ->
                apiCall.ApiDefId
                |> Option.bind (fun apiDefGuid ->
                    match index.Store.ApiDefs.TryGetValue(apiDefGuid) with
                    | true, apiDef ->
                        match apiDef.SensingType with
                        | SensingType.Virtual _ ->
                            apiDef.RxGuid |> Option.orElse apiDef.TxGuid
                        | _ ->
                            apiDef.RxGuid
                    | _ -> None)
            | _ -> None)
        |> List.distinct

    /// v10 §11.2 — ApiCall 의 SensingType 이 Real(_, Some Append n) 이면 n, 아니면 0.
    /// SetIOValue 호출 직후 *n ms 후 ConditionEval 재 schedule* 위해 사용.
    let apiCallSensingAppendMs (index: SimIndex) (apiCallGuid: Guid) : int =
        match index.Store.ApiCalls.TryGetValue(apiCallGuid) with
        | true, apiCall ->
            apiCall.ApiDefId
            |> Option.bind (fun id ->
                match index.Store.ApiDefs.TryGetValue(id) with
                | true, def -> Some def
                | _ -> None)
            |> Option.bind (fun def ->
                match def.SensingType with
                | SensingType.Normal (Some n) -> Some n
                | SensingType.Latch n -> Some n
                | _ -> None)
            |> Option.defaultValue 0
        | _ -> 0

    /// ApiCall 의 SensingType 이 Virtual T 면 T, 아니면 0 — Call Going 진입 시
    /// "출력 발생 + T 후 완료" 재평가(ConditionEval) 스케줄에 사용.
    let apiCallVirtualSensingMs (index: SimIndex) (apiCallGuid: Guid) : int =
        match index.Store.ApiCalls.TryGetValue(apiCallGuid) with
        | true, apiCall ->
            apiCall.ApiDefId
            |> Option.bind (fun id ->
                match index.Store.ApiDefs.TryGetValue(id) with
                | true, def -> Some def
                | _ -> None)
            |> Option.bind (fun def ->
                match def.SensingType with
                | SensingType.Virtual n -> Some n
                | _ -> None)
            |> Option.defaultValue 0
        | _ -> 0

    let build (store: DsStore) (tickMs: int) : SimIndex =
        SimIndexBuild.build store tickMs

    type ConnectionSnapshot = SimIndexConnectionSnapshot

    let snapshotConnections (index: SimIndex) : ConnectionSnapshot =
        SimIndexReload.snapshotConnections index

    let reloadConnections (index: SimIndex) : ConnectionSnapshot * ConnectionSnapshot =
        SimIndexReload.reloadConnections index

    let reloadDurations (index: SimIndex) (skipGuids: Set<Guid>) =
        SimIndexReload.reloadDurations index skipGuids

    let computeAutoHomingTargets (index: SimIndex) : Set<Guid> =
        SimIndexHomingQueries.computeAutoHomingTargets index

    let computeAutoHomingPlan (index: SimIndex) : Set<Guid> * Set<Guid> =
        SimIndexHomingQueries.computeAutoHomingPlan index

    let computeAutoHomingCallPlan (index: SimIndex) : Set<Guid> * Set<Guid> =
        SimIndexHomingQueries.computeAutoHomingCallPlan index

    let findHomingEntryPoints (index: SimIndex) (readyTargets: Set<Guid>) : Guid list * string list =
        SimIndexHomingQueries.findHomingEntryPoints index readyTargets

    let findInitialFlagRxWorkGuids (index: SimIndex) : Set<Guid> =
        SimIndexHomingQueries.findInitialFlagRxWorkGuids index

    let hasAnyTokenRole (index: SimIndex) : bool =
        SimIndexHomingQueries.hasAnyTokenRole index
