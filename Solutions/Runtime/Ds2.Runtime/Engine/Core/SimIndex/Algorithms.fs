namespace Ds2.Runtime.Engine.Core

open System
open Ds2.Core
open Ds2.Core.Store

module internal SimIndexAlgorithms =

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

    let private normalizeRawName (name: string) =
        if isNull name then "" else name.Trim().ToUpperInvariant()

    let private rawConstantExpression (apiCall: ApiCall) =
        match normalizeRawName apiCall.Name with
        | "_ON" -> Some true
        | "_OFF" -> Some false
        | "__INVERTER__" when apiCall.ContactKind = ContactKind.Inverter -> None
        | "" when apiCall.ContactKind = ContactKind.Inverter -> None
        | "" -> None
        | _ -> Some false
        |> Option.map (fun value ->
            match apiCall.ContactKind with
            | ContactKind.NcContact
            | ContactKind.Inverter -> Const (not value)
            | ContactKind.RisingPulse
            | ContactKind.FallingPulse -> Const false
            | _ -> Const value)

    /// SkipAction leaf 의 접점을 NoContact / NcContact 둘로 접는다.
    ///
    /// 펄스는 "이번 clock 에 값이 바뀌었는가" 를 함께 보는데, SkipAction 판정은
    /// Ready→Going 전이 그 순간 1회뿐이다. IO 가 바뀐 clock 과 전이 clock 이 정확히
    /// 같아야만 서므로 사실상 늘 거짓이고, 그 결과 펄스를 건 SkipAction 은 의도와
    /// 무관하게 거의 항상 스킵된다. 엣지를 떼어 내면 남는 뜻 그대로 접는다.
    ///   RisingPulse  = matched     && edge  →  matched      = NoContact
    ///   FallingPulse = not matched && edge  →  not matched  = NcContact
    /// (AutoAux / ComAux 는 조건 루프에서 반복 평가되므로 엣지가 뜻을 가진다 — 그대로 둔다.)
    let private foldSkipActionContact (conditionType: ConditionType) (kind: ContactKind) : ContactKind =
        if conditionType <> ConditionType.SkipAction then kind
        else
            match kind with
            | ContactKind.RisingPulse  -> ContactKind.NoContact
            | ContactKind.FallingPulse -> ContactKind.NcContact
            | k -> k

    /// 한 Condition 의 직접 ApiCall list 를 ConditionExpression list 로.
    /// children 은 호출자가 별도 재귀 처리 (트리 구조 보존).
    let convertApiCallsToExpressions (store: DsStore) (conditionType: ConditionType) (apiCalls: ApiCall seq) : ConditionExpression list =
        apiCalls
        |> Seq.choose (fun apiCall ->
            match apiCall.ApiDefId with
            | Some apiDefId ->
                match Queries.getApiDef apiDefId store with
                | Some apiDef ->
                    match apiDef.RxGuid with
                    | Some rxWorkGuid ->
                        Some (Leaf {
                            RxWorkGuid = rxWorkGuid
                            ApiCallGuid = Some apiCall.Id
                            InputSpec = apiCall.InputSpec
                            ContactKind = foldSkipActionContact conditionType apiCall.ContactKind
                        })
                    | None -> Some (Const false)
                | None -> Some (Const false)
            | None -> rawConstantExpression apiCall)
        |> Seq.toList

    let findOrEmpty key map =
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

    let reloadDurationRanges
        (store: DsStore)
        (allWorkGuids: Guid list)
        (currentRanges: Map<Guid, RxTimingRange>)
        (skipGuids: Set<Guid>) =
        allWorkGuids
        |> List.fold (fun (acc: Map<Guid, RxTimingRange>) workGuid ->
            if skipGuids.Contains workGuid then acc
            else
                let resolvedGuid =
                    Queries.getWork workGuid store
                    |> Option.bind (fun work -> work.ReferenceOf)
                    |> Option.defaultValue workGuid
                match Queries.tryGetDeviceDurationRangeMs resolvedGuid store with
                | Some range -> acc.Add(workGuid, range)
                | None -> acc.Remove(workGuid)) currentRanges
