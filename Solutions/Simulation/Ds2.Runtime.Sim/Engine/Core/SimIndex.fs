namespace Ds2.Runtime.Sim.Engine.Core

open System
open Ds2.Core
open Ds2.UI.Core

/// 조건 평가 엔트리 (ValueSpec 원본 보존)
type ConditionEntry = {
    RxWorkGuid: Guid
    ApiCallGuid: Guid option
    InputSpec: ValueSpec
}

/// DsStore 기반 시뮬레이션 인덱스 (초기화 시 한 번 빌드)
type SimIndex = {
    Store: DsStore
    AllWorkGuids: Guid list
    AllCallGuids: Guid list
    WorkCallGuids: Map<Guid, Guid list>
    WorkStartPreds: Map<Guid, Guid list>
    WorkResetPreds: Map<Guid, Guid list>
    WorkDuration: Map<Guid, float>
    WorkSystemName: Map<Guid, string>
    WorkName: Map<Guid, string>
    CallStartPreds: Map<Guid, Guid list>
    CallWorkGuid: Map<Guid, Guid>
    CallApiCallGuids: Map<Guid, Guid list>
    CallAutoAuxConditions: Map<Guid, ConditionEntry list>
    CallComAuxConditions: Map<Guid, ConditionEntry list>
    CallSkipUnmatchConditions: Map<Guid, ConditionEntry list>
    /// (SystemName, WorkName) → 같은 키를 공유하는 Work Guid 목록 (O(1) 조회용)
    WorkGuidsByKey: Map<string * string, Guid list>
    ActiveSystemNames: Set<string>
    TickMs: int
}

module SimIndex =

    let private log = log4net.LogManager.GetLogger("SimIndex")

    type private BuildState = {
        mutable AllWorkGuids: Guid list
        mutable AllCallGuids: Guid list
        mutable WorkCallGuids: Map<Guid, Guid list>
        mutable WorkStartPreds: Map<Guid, Guid list>
        mutable WorkResetPreds: Map<Guid, Guid list>
        mutable WorkDuration: Map<Guid, float>
        mutable WorkSystemName: Map<Guid, string>
        mutable WorkName: Map<Guid, string>
        mutable CallStartPreds: Map<Guid, Guid list>
        mutable CallWorkGuid: Map<Guid, Guid>
        mutable CallApiCallGuids: Map<Guid, Guid list>
        mutable CallAutoAuxConditions: Map<Guid, ConditionEntry list>
        mutable CallComAuxConditions: Map<Guid, ConditionEntry list>
        mutable CallSkipUnmatchConditions: Map<Guid, ConditionEntry list>
    }

    let findOrEmpty key map =
        map |> Map.tryFind key |> Option.defaultValue []

    // ── Group Arrow 분해 (Union-Find) ───────────────────────────────

    let private addPredecessor (target: Guid) (source: Guid) (m: Map<Guid, Guid list>) =
        match m.TryFind target with
        | Some preds -> if List.contains source preds then m else m.Add(target, source :: preds)
        | None -> m.Add(target, [source])

    let rec private findRoot (parent: Map<Guid, Guid>) (x: Guid) =
        match parent.TryFind x with
        | Some p when p <> x -> findRoot parent p
        | _ -> x

    let private union (parent: Map<Guid, Guid>) (x: Guid) (y: Guid) =
        let rootX = findRoot parent x
        let rootY = findRoot parent y
        if rootX = rootY then parent else parent.Add(rootY, rootX)

    let private buildGroupSets (sourceIds: Guid list) (targetIds: Guid list) =
        if sourceIds.IsEmpty then []
        else
            let allNodes = (sourceIds @ targetIds) |> List.distinct
            let initialParent = allNodes |> List.map (fun n -> n, n) |> Map.ofList
            let finalParent =
                List.zip sourceIds targetIds
                |> List.fold (fun p (s, t) -> union p s t) initialParent
            allNodes
            |> List.groupBy (findRoot finalParent)
            |> List.map (fun (_, nodes) -> Set.ofList nodes)

    let private expandSingleGroup (groupSet: Set<Guid>) (startMap: Map<Guid, Guid list>) (resetMap: Map<Guid, Guid list>) =
        let members = groupSet |> Set.toList
        // 외부 Start predecessor 수집 → 그룹 멤버 전체에 복사
        let externalStartPreds =
            members |> List.collect (fun m -> startMap.TryFind m |> Option.defaultValue [])
            |> List.filter (fun p -> not (Set.contains p groupSet)) |> List.distinct
        let startWithExternal =
            List.allPairs members externalStartPreds
            |> List.fold (fun acc (mem, pred) -> addPredecessor mem pred acc) startMap
        // 그룹 멤버를 predecessor로 가진 successor에게 모든 멤버 추가
        let startSuccessors =
            startWithExternal |> Map.toSeq
            |> Seq.filter (fun (sg, preds) -> not (Set.contains sg groupSet) && preds |> List.exists (fun p -> Set.contains p groupSet))
            |> Seq.map fst |> Seq.toList
        let finalStart =
            List.allPairs startSuccessors members
            |> List.fold (fun acc (succ, mem) -> addPredecessor succ mem acc) startWithExternal
        // Reset도 동일하게
        let externalResetPreds =
            members |> List.collect (fun m -> resetMap.TryFind m |> Option.defaultValue [])
            |> List.filter (fun p -> not (Set.contains p groupSet)) |> List.distinct
        let resetWithExternal =
            List.allPairs members externalResetPreds
            |> List.fold (fun acc (mem, pred) -> addPredecessor mem pred acc) resetMap
        let resetSuccessors =
            resetWithExternal |> Map.toSeq
            |> Seq.filter (fun (sg, preds) -> not (Set.contains sg groupSet) && preds |> List.exists (fun p -> Set.contains p groupSet))
            |> Seq.map fst |> Seq.toList
        let finalReset =
            List.allPairs resetSuccessors members
            |> List.fold (fun acc (succ, mem) -> addPredecessor succ mem acc) resetWithExternal
        (finalStart, finalReset)

    let private expandWorkGroupArrows (groupArrows: ArrowBetweenWorks list) (startMap: Map<Guid, Guid list>) (resetMap: Map<Guid, Guid list>) =
        let sources = groupArrows |> List.map (fun a -> a.SourceId)
        let targets = groupArrows |> List.map (fun a -> a.TargetId)
        let groupSets = buildGroupSets sources targets
        groupSets |> List.fold (fun (s, r) gs -> expandSingleGroup gs s r) (startMap, resetMap)

    let private expandCallSingleGroup (groupSet: Set<Guid>) (predsMap: Map<Guid, Guid list>) =
        let members = groupSet |> Set.toList
        let externalPreds =
            members |> List.collect (fun m -> predsMap.TryFind m |> Option.defaultValue [])
            |> List.filter (fun p -> not (Set.contains p groupSet)) |> List.distinct
        let predsWithExternal =
            List.allPairs members externalPreds
            |> List.fold (fun acc (mem, pred) -> addPredecessor mem pred acc) predsMap
        let successors =
            predsWithExternal |> Map.toSeq
            |> Seq.filter (fun (sg, preds) -> not (Set.contains sg groupSet) && preds |> List.exists (fun p -> Set.contains p groupSet))
            |> Seq.map fst |> Seq.toList
        List.allPairs successors members
        |> List.fold (fun acc (succ, mem) -> addPredecessor succ mem acc) predsWithExternal

    let private expandCallGroupArrows (groupArrows: ArrowBetweenCalls list) (predsMap: Map<Guid, Guid list>) =
        let sources = groupArrows |> List.map (fun a -> a.SourceId)
        let targets = groupArrows |> List.map (fun a -> a.TargetId)
        let groupSets = buildGroupSets sources targets
        groupSets |> List.fold (fun pm gs -> expandCallSingleGroup gs pm) predsMap

    // ── 타입별 조회 헬퍼 ─────────────────────────────────────────────

    /// ApiCall → ApiDef → property Guid 체인 (TxGuid/RxGuid 공용)
    let private resolveApiDefGuids (store: DsStore) (apiCallGuids: Guid list) (propGetter: ApiDefProperties -> Guid option) =
        apiCallGuids |> List.choose (fun apiCallId ->
            DsQuery.getApiCall apiCallId store
            |> Option.bind (fun ac -> ac.ApiDefId)
            |> Option.bind (fun defId -> DsQuery.getApiDef defId store)
            |> Option.bind (fun def -> propGetter def.Properties))

    /// Call의 ApiCall들에서 TxWork Guid 목록 추출
    let txWorkGuids (index: SimIndex) (callGuid: Guid) =
        resolveApiDefGuids index.Store (findOrEmpty callGuid index.CallApiCallGuids) (fun p -> p.TxGuid)

    /// Call의 ApiCall들에서 RxWork Guid 목록 추출
    let rxWorkGuids (index: SimIndex) (callGuid: Guid) =
        resolveApiDefGuids index.Store (findOrEmpty callGuid index.CallApiCallGuids) (fun p -> p.RxGuid)


    let private convertConditions (store: DsStore) (conditions: CallCondition seq) : ConditionEntry list =
        conditions
        |> Seq.collect (fun cond ->
            cond.Conditions |> Seq.choose (fun apiCall ->
                match apiCall.ApiDefId with
                | Some apiDefId ->
                    match DsQuery.getApiDef apiDefId store with
                    | Some apiDef ->
                        match apiDef.Properties.RxGuid with
                        | Some rxWorkGuid ->
                            Some { RxWorkGuid = rxWorkGuid; ApiCallGuid = Some apiCall.Id; InputSpec = apiCall.InputSpec }
                        | None -> None
                    | None -> None
                | None -> None))
        |> Seq.toList

    let private conditionSpecs store conditionType (call: Call) =
        call.CallConditions
        |> Seq.filter (fun cc -> cc.Type = Some conditionType)
        |> convertConditions store

    /// DsStore에서 SimIndex 빌드
    let build (store: DsStore) (tickMs: int) : SimIndex =
        let project = DsQuery.allProjects store |> List.tryHead

        let activeSystemNames =
            match project with
            | Some p -> DsQuery.activeSystemsOf p.Id store |> List.map (fun s -> s.Name) |> Set.ofList
            | None -> Set.empty

        let allSystems =
            match project with
            | Some p -> DsQuery.projectSystemsOf p.Id store
            | None -> []

        let state = {
            AllWorkGuids = []; AllCallGuids = []
            WorkCallGuids = Map.empty; WorkStartPreds = Map.empty; WorkResetPreds = Map.empty
            WorkDuration = Map.empty; WorkSystemName = Map.empty; WorkName = Map.empty
            CallStartPreds = Map.empty; CallWorkGuid = Map.empty; CallApiCallGuids = Map.empty
            CallAutoAuxConditions = Map.empty; CallComAuxConditions = Map.empty; CallSkipUnmatchConditions = Map.empty
        }

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

        let addCallData (work: Work) (callStartPreds: Map<Guid, Guid list>) (call: Call) =
            let apiCallIds = call.ApiCalls |> Seq.map (fun apiCall -> apiCall.Id) |> Seq.toList
            state.CallApiCallGuids <- state.CallApiCallGuids.Add(call.Id, apiCallIds)
            state.CallStartPreds <- state.CallStartPreds.Add(call.Id, findOrEmpty call.Id callStartPreds)
            state.CallWorkGuid <- state.CallWorkGuid.Add(call.Id, work.Id)
            state.CallAutoAuxConditions <- state.CallAutoAuxConditions.Add(call.Id, conditionSpecs store CallConditionType.AutoAux call)
            state.CallComAuxConditions <- state.CallComAuxConditions.Add(call.Id, conditionSpecs store CallConditionType.ComAux call)
            state.CallSkipUnmatchConditions <- state.CallSkipUnmatchConditions.Add(call.Id, conditionSpecs store CallConditionType.SkipUnmatch call)
            state.AllCallGuids <- call.Id :: state.AllCallGuids

        let addWorkData (system: DsSystem) (work: Work) (callGuids: Guid list) (workStartPreds: Map<Guid, Guid list>) (workResetPreds: Map<Guid, Guid list>) =
            let duration =
                work.Properties.Period
                |> Option.map (fun ts -> ts.TotalMilliseconds)
                |> Option.defaultValue 0.0
            state.WorkCallGuids <- state.WorkCallGuids.Add(work.Id, callGuids)
            state.WorkStartPreds <- state.WorkStartPreds.Add(work.Id, findOrEmpty work.Id workStartPreds)
            state.WorkResetPreds <- state.WorkResetPreds.Add(work.Id, findOrEmpty work.Id workResetPreds)
            state.WorkDuration <- state.WorkDuration.Add(work.Id, duration)
            state.WorkSystemName <- state.WorkSystemName.Add(work.Id, system.Name)
            state.WorkName <- state.WorkName.Add(work.Id, work.Name)
            state.AllWorkGuids <- work.Id :: state.AllWorkGuids

        for system in allSystems do
            let workArrows = DsQuery.arrowWorksOf system.Id store
            let wType = fun (a: ArrowBetweenWorks) -> a.ArrowType
            let wSrc  = fun (a: ArrowBetweenWorks) -> a.SourceId
            let wTgt  = fun (a: ArrowBetweenWorks) -> a.TargetId
            let wStartPreds =
                groupArrows [ ArrowType.Start; ArrowType.StartReset ] wType wTgt wSrc workArrows
            let wResetPreds =
                mergeGroupedMaps [
                    groupArrows [ ArrowType.Reset; ArrowType.ResetReset ] wType wTgt wSrc workArrows
                    groupArrows [ ArrowType.StartReset; ArrowType.ResetReset ] wType wSrc wTgt workArrows
                ]
            // Work Group Arrow 분해
            let workGroupArrows = workArrows |> List.filter (fun a -> a.ArrowType = ArrowType.Group)
            let wStartPreds, wResetPreds = expandWorkGroupArrows workGroupArrows wStartPreds wResetPreds

            let flows = DsQuery.flowsOf system.Id store
            // Call Arrow: Work별 수집 후 합산 → Group 분해
            let allCallArrows =
                flows
                |> List.collect (fun f -> DsQuery.worksOf f.Id store)
                |> List.collect (fun w -> DsQuery.arrowCallsOf w.Id store)
            let cType = fun (a: ArrowBetweenCalls) -> a.ArrowType
            let cSrc  = fun (a: ArrowBetweenCalls) -> a.SourceId
            let cTgt  = fun (a: ArrowBetweenCalls) -> a.TargetId
            let cStartPreds =
                groupArrows [ ArrowType.Start; ArrowType.StartReset ] cType cTgt cSrc allCallArrows
            // Call Group Arrow 분해
            let callGroupArrows = allCallArrows |> List.filter (fun a -> a.ArrowType = ArrowType.Group)
            let cStartPreds = expandCallGroupArrows callGroupArrows cStartPreds

            for flow in flows do
                let works = DsQuery.worksOf flow.Id store
                for work in works do
                    let calls = DsQuery.callsOf work.Id store
                    let callGuids = calls |> List.map (fun c -> c.Id)
                    for call in calls do
                        addCallData work cStartPreds call

                    addWorkData system work callGuids wStartPreds wResetPreds

        log.Debug($"SimIndex built: {state.AllWorkGuids.Length} works, {state.AllCallGuids.Length} calls")

        let workGuidsByKey =
            state.AllWorkGuids
            |> List.choose (fun wg ->
                match Map.tryFind wg state.WorkSystemName, Map.tryFind wg state.WorkName with
                | Some sn, Some wn -> Some ((sn, wn), wg)
                | _ -> None)
            |> List.groupBy fst
            |> List.map (fun (key, grouped) -> key, grouped |> List.map snd)
            |> Map.ofList

        { Store = store
          AllWorkGuids = state.AllWorkGuids |> List.rev
          AllCallGuids = state.AllCallGuids |> List.rev
          WorkCallGuids = state.WorkCallGuids
          WorkStartPreds = state.WorkStartPreds
          WorkResetPreds = state.WorkResetPreds
          WorkDuration = state.WorkDuration
          WorkSystemName = state.WorkSystemName
          WorkName = state.WorkName
          CallStartPreds = state.CallStartPreds
          CallWorkGuid = state.CallWorkGuid
          CallApiCallGuids = state.CallApiCallGuids
          CallAutoAuxConditions = state.CallAutoAuxConditions
          CallComAuxConditions = state.CallComAuxConditions
          CallSkipUnmatchConditions = state.CallSkipUnmatchConditions
          WorkGuidsByKey = workGuidsByKey
          ActiveSystemNames = activeSystemNames
          TickMs = tickMs }

    // ── InitialFlag 헬퍼 ─────────────────────────────────────────────

    /// RET 방향 Call인지 확인
    let isCallRetDirection (index: SimIndex) (callGuid: Guid) : bool =
        rxWorkGuids index callGuid
        |> List.exists (fun rxGuid ->
            DsQuery.getWork rxGuid index.Store
            |> Option.map (fun work -> work.Name.ToUpperInvariant().Contains("RET"))
            |> Option.defaultValue false)

    let findInitialFlagCallGuids (index: SimIndex) : Set<Guid> =
        index.AllCallGuids |> List.filter (isCallRetDirection index) |> Set.ofList

    let findInitialFlagRxWorkGuids (index: SimIndex) : Set<Guid> =
        findInitialFlagCallGuids index
        |> Set.toSeq
        |> Seq.collect (fun callGuid -> rxWorkGuids index callGuid)
        |> Set.ofSeq
