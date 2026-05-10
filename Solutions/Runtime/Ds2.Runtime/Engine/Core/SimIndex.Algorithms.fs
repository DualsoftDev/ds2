namespace Ds2.Runtime.Engine.Core

open System
open Ds2.Core
open Ds2.Core.Store

module internal SimIndexAlgorithms =

    type ConditionEntryData = {
        RxWorkGuid: Guid
        ApiCallGuid: Guid option
        InputSpec: ValueSpec
    }

    let resolveApiDefGuids (store: DsStore) (apiCallGuids: Guid list) (propGetter: ApiDef -> Guid option) =
        apiCallGuids
        |> List.choose (fun apiCallId ->
            Queries.getApiCall apiCallId store
            |> Option.bind (fun apiCall -> apiCall.ApiDefId)
            |> Option.bind (fun defId -> Queries.getApiDef defId store)
            |> Option.bind propGetter)

    let groupArrows arrowTypes getArrowType keySelector valueSelector arrows =
        arrows
        |> List.filter (fun arrow -> List.contains (getArrowType arrow) arrowTypes)
        |> List.groupBy keySelector
        |> List.map (fun (key, grouped) -> key, grouped |> List.map valueSelector)
        |> Map.ofList

    let mergeGroupedMaps maps =
        maps
        |> List.collect Map.toList
        |> List.groupBy fst
        |> List.map (fun (key, groupedValues) -> key, groupedValues |> List.collect snd)
        |> Map.ofList

    let buildReferenceGroups canonicalGuids =
        canonicalGuids
        |> Map.toList
        |> List.groupBy snd
        |> List.filter (fun (_, members) -> members.Length > 1)
        |> List.map (fun (origId, members) -> origId, (members |> List.map fst |> List.sort))
        |> Map.ofList

    let expandByCanonical workCanonicalGuids (edgeMap: Map<Guid, Guid list>) =
        workCanonicalGuids
        |> Map.fold (fun acc workGuid canonical ->
            if workGuid <> canonical then
                match acc |> Map.tryFind workGuid with
                | Some (existing: Guid list) when not existing.IsEmpty -> acc
                | _ ->
                    match acc |> Map.tryFind canonical with
                    | Some edges -> acc.Add(workGuid, edges)
                    | None -> acc
            else
                acc) edgeMap

    let buildExpandedTokenRoleMap workCanonicalGuids tokenRoleMap =
        let mergedRoles =
            tokenRoleMap
            |> Map.fold (fun acc workGuid role ->
                let canonical = workCanonicalGuids |> Map.tryFind workGuid |> Option.defaultValue workGuid
                let existing = acc |> Map.tryFind canonical |> Option.defaultValue TokenRole.None
                acc.Add(canonical, existing ||| role)) Map.empty

        workCanonicalGuids
        |> Map.toSeq
        |> Seq.choose (fun (workGuid, canonical) ->
            let role = mergedRoles |> Map.tryFind canonical |> Option.defaultValue TokenRole.None
            if role = TokenRole.None then None else Some (workGuid, role))
        |> Map.ofSeq

    let buildRaceExclusions
        (allCallGuids: Guid list)
        (callApiCallGuids: Map<Guid, Guid list>)
        (callWorkGuid: Map<Guid, Guid>)
        (callStartPreds: Map<Guid, Guid list>)
        (workStartPreds: Map<Guid, Guid list>)
        (workResetPreds: Map<Guid, Guid list>)
        (resolveTxGuids: Guid list -> Guid list) =
        let deviceWorkToCalls =
            allCallGuids
            |> List.collect (fun callGuid ->
                let apiCallGuids = callApiCallGuids |> Map.tryFind callGuid |> Option.defaultValue []
                resolveTxGuids apiCallGuids |> List.map (fun txGuid -> txGuid, callGuid))
            |> List.groupBy fst
            |> List.map (fun (deviceWorkGuid, pairs) -> deviceWorkGuid, pairs |> List.map snd)
            |> Map.ofList

        let isReachable (predsMap: Map<Guid, Guid list>) fromId toId =
            let rec bfs visited = function
                | [] -> false
                | currentGuid :: rest ->
                    if currentGuid = fromId then true
                    elif Set.contains currentGuid visited then bfs visited rest
                    else
                        let preds = predsMap |> Map.tryFind currentGuid |> Option.defaultValue []
                        bfs (Set.add currentGuid visited) (rest @ preds)
            bfs Set.empty [ toId ]

        let areOrdered callA callB =
            match callWorkGuid |> Map.tryFind callA, callWorkGuid |> Map.tryFind callB with
            | Some workA, Some workB when workA = workB ->
                isReachable callStartPreds callA callB
                || isReachable callStartPreds callB callA
            | Some workA, Some workB ->
                isReachable workStartPreds workA workB
                || isReachable workStartPreds workB workA
            | _ -> false

        allCallGuids
        |> List.map (fun callGuid ->
            let apiCallGuids = callApiCallGuids |> Map.tryFind callGuid |> Option.defaultValue []
            let peerDeviceWorks =
                resolveTxGuids apiCallGuids
                |> List.collect (fun txGuid -> workResetPreds |> Map.tryFind txGuid |> Option.defaultValue [])
            let excludedCalls =
                peerDeviceWorks
                |> List.collect (fun peerDeviceWorkGuid -> deviceWorkToCalls |> Map.tryFind peerDeviceWorkGuid |> Option.defaultValue [])
                |> List.filter (fun otherCallGuid -> otherCallGuid <> callGuid && not (areOrdered callGuid otherCallGuid))
                |> Set.ofList
            callGuid, excludedCalls)
            |> List.filter (fun (_, excludedCalls) -> not excludedCalls.IsEmpty)
        |> Map.ofList

    /// 한 CallCondition 의 직접 ApiCall list 를 ConditionEntryData list 로.
    /// children 은 호출자가 별도 재귀 처리 (트리 구조 보존).
    let convertApiCallsToEntries (store: DsStore) (apiCalls: ApiCall seq) : ConditionEntryData list =
        apiCalls
        |> Seq.choose (fun apiCall ->
            match apiCall.ApiDefId with
            | Some apiDefId ->
                match Queries.getApiDef apiDefId store with
                | Some apiDef ->
                    match apiDef.RxGuid with
                    | Some rxWorkGuid ->
                        Some {
                            RxWorkGuid = rxWorkGuid
                            ApiCallGuid = Some apiCall.Id
                            InputSpec = apiCall.InputSpec
                        }
                    | None -> None
                | None -> None
            | None -> None)
        |> Seq.toList

    let private findOrEmpty key map =
        map |> Map.tryFind key |> Option.defaultValue []

    let private computeWorkDuration (store: DsStore) (workCallGuids: Map<Guid, Guid list>) (workGuid: Guid) : float =
        match Queries.getWork workGuid store with
        | None -> 0.0
        | Some work ->
            let periodSource =
                match work.ReferenceOf with
                | Some origId -> Queries.getWork origId store |> Option.bind (fun w -> w.Duration)
                | None -> work.Duration
            let userDurationMs =
                periodSource
                |> Option.map (fun ts -> ts.TotalMilliseconds)
                |> Option.defaultValue 0.0
            let resolvedId = work.ReferenceOf |> Option.defaultValue work.Id
            let callGuids = findOrEmpty resolvedId workCallGuids
            if callGuids.IsEmpty then userDurationMs
            else
                let deviceMs =
                    Queries.tryGetDeviceDurationMs resolvedId store
                    |> Option.defaultValue 0
                    |> float
                max userDurationMs deviceMs

    let reloadDurations
        (store: DsStore)
        (workCallGuids: Map<Guid, Guid list>)
        (allWorkGuids: Guid list)
        (currentDurations: Map<Guid, float>)
        (skipGuids: Set<Guid>) =
        allWorkGuids
        |> List.fold (fun (acc: Map<Guid, float>) workGuid ->
            if skipGuids.Contains workGuid then acc
            else acc.Add(workGuid, computeWorkDuration store workCallGuids workGuid)) currentDurations
