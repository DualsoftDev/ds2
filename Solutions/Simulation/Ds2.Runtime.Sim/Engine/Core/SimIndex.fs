namespace Ds2.Runtime.Sim.Engine.Core

open System
open Ds2.Core
open Ds2.Store

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
    AllFlowGuids: Guid list
    /// Work Guid -> canonical/original Work Guid
    WorkCanonicalGuids: Map<Guid, Guid>
    WorkCallGuids: Map<Guid, Guid list>
    mutable WorkStartPreds: Map<Guid, Guid list>
    mutable WorkResetPreds: Map<Guid, Guid list>
    WorkDuration: Map<Guid, float>
    WorkSystemName: Map<Guid, string>
    WorkName: Map<Guid, string>
    /// Work → 소속 Flow Guid
    WorkFlowGuid: Map<Guid, Guid>
    mutable CallStartPreds: Map<Guid, Guid list>
    CallWorkGuid: Map<Guid, Guid>
    CallApiCallGuids: Map<Guid, Guid list>
    CallAutoAuxConditions: Map<Guid, ConditionEntry list>
    CallComAuxConditions: Map<Guid, ConditionEntry list>
    CallSkipUnmatchConditions: Map<Guid, ConditionEntry list>
    /// ReferenceOf 기반 OR 그룹: 원본 WorkId → [원본 + 참조 Work Guid 목록]
    WorkReferenceGroups: Map<Guid, Guid list>
    ActiveSystemNames: Set<string>
    TickMs: int
    // ── Token ──
    WorkTokenRole: Map<Guid, TokenRole>
    mutable WorkTokenSuccessors: Map<Guid, Guid list>
    TokenSourceGuids: Guid list
    /// Sink Work: TokenRole.Sink로 수동 지정된 Work
    TokenSinkGuids: Set<Guid>
    /// 토큰 경로에 포함된 Work (Source → successor 체인의 모든 Work)
    mutable TokenPathGuids: Set<Guid>
}

module SimIndex =

    let private log = log4net.LogManager.GetLogger("SimIndex")

    type private BuildState = {
        mutable AllWorkGuids: Guid list
        mutable AllCallGuids: Guid list
        mutable AllFlowGuids: Guid list
        mutable WorkCallGuids: Map<Guid, Guid list>
        mutable WorkStartPreds: Map<Guid, Guid list>
        mutable WorkResetPreds: Map<Guid, Guid list>
        mutable WorkDuration: Map<Guid, float>
        mutable WorkSystemName: Map<Guid, string>
        mutable WorkName: Map<Guid, string>
        mutable WorkFlowGuid: Map<Guid, Guid>
        mutable CallStartPreds: Map<Guid, Guid list>
        mutable CallWorkGuid: Map<Guid, Guid>
        mutable CallApiCallGuids: Map<Guid, Guid list>
        mutable CallAutoAuxConditions: Map<Guid, ConditionEntry list>
        mutable CallComAuxConditions: Map<Guid, ConditionEntry list>
        mutable CallSkipUnmatchConditions: Map<Guid, ConditionEntry list>
    }

    let findOrEmpty key map =
        map |> Map.tryFind key |> Option.defaultValue []

    let canonicalWorkGuid (index: SimIndex) (workGuid: Guid) =
        index.WorkCanonicalGuids
        |> Map.tryFind workGuid
        |> Option.defaultValue workGuid

    let referenceGroupOf (index: SimIndex) (workGuid: Guid) =
        let canonical = canonicalWorkGuid index workGuid
        index.WorkReferenceGroups
        |> Map.tryFind canonical
        |> Option.defaultValue [ canonical ]

    let isTokenSource (index: SimIndex) (workGuid: Guid) =
        let canonical = canonicalWorkGuid index workGuid
        index.TokenSourceGuids |> List.contains canonical

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

        let mutable tokenRoleMap = Map.empty<Guid, TokenRole>
        let mutable tokenSuccMap = Map.empty<Guid, Guid list>
        let mutable tokenSources = []

        let state = {
            AllWorkGuids = []; AllCallGuids = []; AllFlowGuids = []
            WorkCallGuids = Map.empty; WorkStartPreds = Map.empty; WorkResetPreds = Map.empty
            WorkDuration = Map.empty; WorkSystemName = Map.empty; WorkName = Map.empty; WorkFlowGuid = Map.empty
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

        let addWorkData (system: DsSystem) (flowId: Guid) (work: Work) (callGuids: Guid list) (workStartPreds: Map<Guid, Guid list>) (workResetPreds: Map<Guid, Guid list>) =
            let periodSource =
                match work.ReferenceOf with
                | Some origId -> DsQuery.getWork origId store |> Option.bind (fun w -> w.Properties.Period)
                | None -> work.Properties.Period
            let userDurationMs =
                periodSource
                |> Option.map (fun ts -> ts.TotalMilliseconds)
                |> Option.defaultValue 0.0
            // Works with Calls: effective = max(userDuration, deviceCriticalPath)
            let resolvedId = work.ReferenceOf |> Option.defaultValue work.Id
            let duration =
                if callGuids.IsEmpty then userDurationMs
                else
                    let deviceMs =
                        DsQuery.tryGetDeviceDurationMs resolvedId store
                        |> Option.defaultValue 0
                        |> float
                    max userDurationMs deviceMs
            state.WorkCallGuids <- state.WorkCallGuids.Add(work.Id, callGuids)
            state.WorkStartPreds <- state.WorkStartPreds.Add(work.Id, findOrEmpty work.Id workStartPreds)
            state.WorkResetPreds <- state.WorkResetPreds.Add(work.Id, findOrEmpty work.Id workResetPreds)
            state.WorkDuration <- state.WorkDuration.Add(work.Id, duration)
            state.WorkSystemName <- state.WorkSystemName.Add(work.Id, system.Name)
            state.WorkName <- state.WorkName.Add(work.Id, work.Name)
            state.WorkFlowGuid <- state.WorkFlowGuid.Add(work.Id, flowId)
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
            let wStartPreds, wResetPreds = SimIndexGroupExpansion.expandWorkGroupArrows workGroupArrows wStartPreds wResetPreds

            // ── Token successor 맵 (wStartPreds 역전: predecessor→successor) ──
            // Group 확장 후의 wStartPreds에서 빌드하므로 Group 멤버 관계도 포함
            tokenSuccMap <- SimIndexTokenGraph.appendSuccessorsFromStartPreds tokenSuccMap wStartPreds

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
            let cStartPreds = SimIndexGroupExpansion.expandCallGroupArrows callGroupArrows cStartPreds

            for flow in flows do
                state.AllFlowGuids <- flow.Id :: state.AllFlowGuids
                let works = DsQuery.worksOf flow.Id store
                for work in works do
                    // 레퍼런스 Work → 원본의 Call을 공유
                    let resolvedWorkId = work.ReferenceOf |> Option.defaultValue work.Id
                    let calls = DsQuery.callsOf resolvedWorkId store
                    let callGuids = calls |> List.map (fun c -> c.Id)
                    if work.ReferenceOf.IsNone then
                        for call in calls do
                            addCallData work cStartPreds call

                    addWorkData system flow.Id work callGuids wStartPreds wResetPreds

                    // ── Token role/successor 수집 ──
                    if work.TokenRole <> TokenRole.None then
                        tokenRoleMap <- tokenRoleMap.Add(work.Id, work.TokenRole)
                        if work.TokenRole.HasFlag(TokenRole.Source) then
                            tokenSources <- work.Id :: tokenSources

        log.Debug($"SimIndex built: {state.AllWorkGuids.Length} works, {state.AllCallGuids.Length} calls")

        let workCanonicalGuids =
            state.AllWorkGuids
            |> List.choose (fun wg ->
                DsQuery.getWork wg store
                |> Option.map (fun w -> wg, (w.ReferenceOf |> Option.defaultValue wg)))
            |> Map.ofList

        // ReferenceOf 기반 OR 그룹 빌드
        let workReferenceGroups =
            workCanonicalGuids
            |> Map.toList
            |> List.groupBy snd
            |> List.filter (fun (_, members) -> members.Length > 1)
            |> List.map (fun (origId, members) -> origId, (members |> List.map fst |> List.sort))
            |> Map.ofList

        let expandReferenceMembers canonicalWorkGuid =
            workReferenceGroups
            |> Map.tryFind canonicalWorkGuid
            |> Option.defaultValue [ canonicalWorkGuid ]

        let mergeTokenRoleByCanonical =
            tokenRoleMap
            |> Map.fold (fun acc workGuid role ->
                let canonical = workCanonicalGuids |> Map.tryFind workGuid |> Option.defaultValue workGuid
                let existing = acc |> Map.tryFind canonical |> Option.defaultValue TokenRole.None
                acc.Add(canonical, existing ||| role)) Map.empty

        let expandedTokenRoleMap =
            workCanonicalGuids
            |> Map.toSeq
            |> Seq.choose (fun (workGuid, canonical) ->
                let role = mergeTokenRoleByCanonical |> Map.tryFind canonical |> Option.defaultValue TokenRole.None
                if role = TokenRole.None then None else Some (workGuid, role))
            |> Map.ofSeq

        let canonicalTokenSuccMap =
            tokenSuccMap
            |> Map.toSeq
            |> Seq.collect (fun (sourceGuid, targetGuids) ->
                let canonicalSource = workCanonicalGuids |> Map.tryFind sourceGuid |> Option.defaultValue sourceGuid
                targetGuids
                |> Seq.map (fun targetGuid ->
                    canonicalSource,
                    (workCanonicalGuids |> Map.tryFind targetGuid |> Option.defaultValue targetGuid)))
            |> Seq.filter (fun (sourceGuid, targetGuid) -> sourceGuid <> targetGuid)
            |> Seq.groupBy fst
            |> Seq.map (fun (sourceGuid, pairs) ->
                sourceGuid,
                (pairs |> Seq.map snd |> Seq.distinct |> Seq.toList))
            |> Map.ofSeq

        let expandedTokenSuccMap =
            workCanonicalGuids
            |> Map.toSeq
            |> Seq.choose (fun (workGuid, canonical) ->
                canonicalTokenSuccMap
                |> Map.tryFind canonical
                |> Option.map (fun targetGuids -> workGuid, targetGuids))
            |> Map.ofSeq

        let tokenSourceCanonicals =
            mergeTokenRoleByCanonical
            |> Map.toSeq
            |> Seq.choose (fun (workGuid, role) ->
                if role.HasFlag(TokenRole.Source) then Some workGuid else None)
            |> Seq.toList

        // ── Sink: TokenRole.Sink로 수동 지정된 Work ──
        let tokenSinkGuids =
            mergeTokenRoleByCanonical
            |> Map.toSeq
            |> Seq.choose (fun (workGuid, role) ->
                if role.HasFlag(TokenRole.Sink) then Some workGuid else None)
            |> Seq.collect expandReferenceMembers
            |> Set.ofSeq

        // 토큰 경로: Source에서 successor를 BFS로 따라간 canonical Work
        let tokenPathGuids =
            SimIndexTokenGraph.buildTokenPathGuids tokenSourceCanonicals canonicalTokenSuccMap
            |> Seq.collect expandReferenceMembers
            |> Set.ofSeq

        let allWorkGuidsRev = state.AllWorkGuids |> List.rev

        { Store = store
          AllWorkGuids = allWorkGuidsRev
          AllCallGuids = state.AllCallGuids |> List.rev
          AllFlowGuids = state.AllFlowGuids |> List.rev
          WorkCanonicalGuids = workCanonicalGuids
          WorkCallGuids = state.WorkCallGuids
          WorkStartPreds = state.WorkStartPreds
          WorkResetPreds = state.WorkResetPreds
          WorkDuration = state.WorkDuration
          WorkSystemName = state.WorkSystemName
          WorkName = state.WorkName
          WorkFlowGuid = state.WorkFlowGuid
          CallStartPreds = state.CallStartPreds
          CallWorkGuid = state.CallWorkGuid
          CallApiCallGuids = state.CallApiCallGuids
          CallAutoAuxConditions = state.CallAutoAuxConditions
          CallComAuxConditions = state.CallComAuxConditions
          CallSkipUnmatchConditions = state.CallSkipUnmatchConditions
          WorkReferenceGroups = workReferenceGroups
          ActiveSystemNames = activeSystemNames
          TickMs = tickMs
          WorkTokenRole = expandedTokenRoleMap
          WorkTokenSuccessors = expandedTokenSuccMap
          TokenSourceGuids = tokenSourceCanonicals |> List.distinct |> List.sort
          TokenSinkGuids = tokenSinkGuids
          TokenPathGuids = tokenPathGuids }

    let reloadConnections (index: SimIndex) =
        let rebuilt = build index.Store index.TickMs
        index.WorkStartPreds <- rebuilt.WorkStartPreds
        index.WorkResetPreds <- rebuilt.WorkResetPreds
        index.CallStartPreds <- rebuilt.CallStartPreds
        index.WorkTokenSuccessors <- rebuilt.WorkTokenSuccessors
        index.TokenPathGuids <- rebuilt.TokenPathGuids

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
