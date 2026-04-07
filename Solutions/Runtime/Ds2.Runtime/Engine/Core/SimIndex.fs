namespace Ds2.Runtime.Engine.Core

open System
open Ds2.Core
open Ds2.Core.Store

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
    mutable WorkPureStartPreds: Map<Guid, Guid list>
    mutable WorkResetPreds: Map<Guid, Guid list>
    mutable WorkDuration: Map<Guid, float>
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
    /// Call Guid → canonical/original Call Guid
    CallCanonicalGuids: Map<Guid, Guid>
    /// ReferenceOf 기반 OR 그룹: 원본 CallId → [원본 + 참조 Call Guid 목록]
    CallReferenceGroups: Map<Guid, Guid list>
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
    /// Call Guid → CallType (WaitForCompletion / SkipIfCompleted)
    CallTypeMap: Map<Guid, CallType>
    /// Call Guid → Timeout (TimeSpan)
    CallTimeoutMap: Map<Guid, TimeSpan>
}

module SimIndex =

    let private log = log4net.LogManager.GetLogger("SimIndex")

    type private BuildState = {
        mutable AllWorkGuids: Guid list
        mutable AllCallGuids: Guid list
        mutable AllFlowGuids: Guid list
        mutable WorkCallGuids: Map<Guid, Guid list>
        mutable WorkStartPreds: Map<Guid, Guid list>
        mutable WorkPureStartPreds: Map<Guid, Guid list>
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
        mutable CallTypeMap: Map<Guid, CallType>
        mutable CallTimeoutMap: Map<Guid, TimeSpan>
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

    // ── 타입별 조회 헬퍼 ─────────────────────────────────────────────

    /// ApiCall → ApiDef → property Guid 체인 (TxGuid/RxGuid 공용)
    let private resolveApiDefGuids (store: DsStore) (apiCallGuids: Guid list) (propGetter: ApiDef -> Guid option) =
        apiCallGuids |> List.choose (fun apiCallId ->
            Queries.getApiCall apiCallId store
            |> Option.bind (fun ac -> ac.ApiDefId)
            |> Option.bind (fun defId -> Queries.getApiDef defId store)
            |> Option.bind propGetter)

    /// Call의 ApiCall들에서 TxWork Guid 목록 추출
    let txWorkGuids (index: SimIndex) (callGuid: Guid) =
        resolveApiDefGuids index.Store (findOrEmpty callGuid index.CallApiCallGuids) (fun d -> d.TxGuid)

    /// Call의 ApiCall들에서 RxWork Guid 목록 추출
    let rxWorkGuids (index: SimIndex) (callGuid: Guid) =
        resolveApiDefGuids index.Store (findOrEmpty callGuid index.CallApiCallGuids) (fun p -> p.RxGuid)


    let private convertConditions (store: DsStore) (conditions: CallCondition seq) : ConditionEntry list =
        conditions
        |> Seq.collect (fun cond ->
            cond.Conditions |> Seq.choose (fun apiCall ->
                match apiCall.ApiDefId with
                | Some apiDefId ->
                    match Queries.getApiDef apiDefId store with
                    | Some apiDef ->
                        match apiDef.RxGuid with
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
        let project = Queries.allProjects store |> List.tryHead

        let activeSystemNames =
            match project with
            | Some p -> Queries.activeSystemsOf p.Id store |> List.map (fun s -> s.Name) |> Set.ofList
            | None -> Set.empty

        let allSystems =
            match project with
            | Some p -> Queries.projectSystemsOf p.Id store
            | None -> []

        let mutable tokenRoleMap = Map.empty<Guid, TokenRole>
        let mutable tokenSuccMap = Map.empty<Guid, Guid list>
        let mutable tokenSources = []

        let state = {
            AllWorkGuids = []; AllCallGuids = []; AllFlowGuids = []
            WorkCallGuids = Map.empty; WorkStartPreds = Map.empty; WorkPureStartPreds = Map.empty; WorkResetPreds = Map.empty
            WorkDuration = Map.empty; WorkSystemName = Map.empty; WorkName = Map.empty; WorkFlowGuid = Map.empty
            CallStartPreds = Map.empty; CallWorkGuid = Map.empty; CallApiCallGuids = Map.empty
            CallAutoAuxConditions = Map.empty; CallComAuxConditions = Map.empty; CallSkipUnmatchConditions = Map.empty
            CallTypeMap = Map.empty
            CallTimeoutMap = Map.empty
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
            // 레퍼런스 Call → 원본 Call의 데이터(ApiCalls, 조건, Preds) 사용
            let dataSource =
                match call.ReferenceOf with
                | Some origId -> Queries.getCall origId store |> Option.defaultValue call
                | None -> call
            let apiCallIds = dataSource.ApiCalls |> Seq.map (fun apiCall -> apiCall.Id) |> Seq.toList
            state.CallApiCallGuids <- state.CallApiCallGuids.Add(call.Id, apiCallIds)
            state.CallStartPreds <- state.CallStartPreds.Add(call.Id, findOrEmpty dataSource.Id callStartPreds)
            state.CallWorkGuid <- state.CallWorkGuid.Add(call.Id, work.Id)
            state.CallAutoAuxConditions <- state.CallAutoAuxConditions.Add(call.Id, conditionSpecs store CallConditionType.AutoAux dataSource)
            state.CallComAuxConditions <- state.CallComAuxConditions.Add(call.Id, conditionSpecs store CallConditionType.ComAux dataSource)
            state.CallSkipUnmatchConditions <- state.CallSkipUnmatchConditions.Add(call.Id, conditionSpecs store CallConditionType.SkipUnmatch dataSource)
            let simProps = dataSource.GetSimulationProperties()
            let callType = simProps |> Option.map (fun p -> p.CallType) |> Option.defaultValue CallType.WaitForCompletion
            state.CallTypeMap <- state.CallTypeMap.Add(call.Id, callType)
            match simProps |> Option.bind (fun p -> p.Timeout) with
            | Some timeout when timeout > TimeSpan.Zero -> state.CallTimeoutMap <- state.CallTimeoutMap.Add(call.Id, timeout)
            | _ -> ()
            state.AllCallGuids <- call.Id :: state.AllCallGuids

        let addWorkData
            (system: DsSystem)
            (flowId: Guid)
            (work: Work)
            (callGuids: Guid list)
            (workStartPreds: Map<Guid, Guid list>)
            (workPureStartPreds: Map<Guid, Guid list>)
            (workResetPreds: Map<Guid, Guid list>) =
            let periodSource =
                match work.ReferenceOf with
                | Some origId -> Queries.getWork origId store |> Option.bind (fun w -> w.Duration)
                | None -> work.Duration
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
                        Queries.tryGetDeviceDurationMs resolvedId store
                        |> Option.defaultValue 0
                        |> float
                    max userDurationMs deviceMs
            state.WorkCallGuids <- state.WorkCallGuids.Add(work.Id, callGuids)
            state.WorkStartPreds <- state.WorkStartPreds.Add(work.Id, findOrEmpty work.Id workStartPreds)
            state.WorkPureStartPreds <- state.WorkPureStartPreds.Add(work.Id, findOrEmpty work.Id workPureStartPreds)
            state.WorkResetPreds <- state.WorkResetPreds.Add(work.Id, findOrEmpty work.Id workResetPreds)
            state.WorkDuration <- state.WorkDuration.Add(work.Id, duration)
            state.WorkSystemName <- state.WorkSystemName.Add(work.Id, system.Name)
            state.WorkName <- state.WorkName.Add(work.Id, work.Name)
            state.WorkFlowGuid <- state.WorkFlowGuid.Add(work.Id, flowId)
            state.AllWorkGuids <- work.Id :: state.AllWorkGuids

        for system in allSystems do
            let workArrows = Queries.arrowWorksOf system.Id store
            let wType = fun (a: ArrowBetweenWorks) -> a.ArrowType
            let wSrc  = fun (a: ArrowBetweenWorks) -> a.SourceId
            let wTgt  = fun (a: ArrowBetweenWorks) -> a.TargetId
            let wStartPreds =
                groupArrows [ ArrowType.Start; ArrowType.StartReset ] wType wTgt wSrc workArrows
            let wPureStartPreds =
                groupArrows [ ArrowType.Start ] wType wTgt wSrc workArrows
            let wResetPreds =
                mergeGroupedMaps [
                    groupArrows [ ArrowType.Reset; ArrowType.ResetReset ] wType wTgt wSrc workArrows
                    groupArrows [ ArrowType.StartReset; ArrowType.ResetReset ] wType wSrc wTgt workArrows
                ]
            // Work Group Arrow 분해
            let workGroupArrows = workArrows |> List.filter (fun a -> a.ArrowType = ArrowType.Group)
            let wStartPreds, wResetPreds = SimIndexGroupExpansion.expandWorkGroupArrows workGroupArrows wStartPreds wResetPreds
            let wPureStartPreds, _ = SimIndexGroupExpansion.expandWorkGroupArrows workGroupArrows wPureStartPreds Map.empty

            // ── Token successor 맵 (wStartPreds 역전: predecessor→successor) ──
            // Group 확장 후의 wStartPreds에서 빌드하므로 Group 멤버 관계도 포함
            tokenSuccMap <- SimIndexTokenGraph.appendSuccessorsFromStartPreds tokenSuccMap wStartPreds

            let flows = Queries.flowsOf system.Id store
            // Call Arrow: Work별 수집 후 합산 → Group 분해
            let allCallArrows =
                flows
                |> List.collect (fun f -> Queries.worksOf f.Id store)
                |> List.collect (fun w -> Queries.arrowCallsOf w.Id store)
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
                let works = Queries.worksOf flow.Id store
                for work in works do
                    // 레퍼런스 Work → 원본의 Call을 공유
                    let resolvedWorkId = work.ReferenceOf |> Option.defaultValue work.Id
                    let calls = Queries.callsOf resolvedWorkId store
                    let callGuids = calls |> List.map (fun c -> c.Id)
                    if work.ReferenceOf.IsNone then
                        for call in calls do
                            addCallData work cStartPreds call

                    addWorkData system flow.Id work callGuids wStartPreds wPureStartPreds wResetPreds

                    // ── Token role/successor 수집 ──
                    if work.TokenRole <> TokenRole.None then
                        tokenRoleMap <- tokenRoleMap.Add(work.Id, work.TokenRole)
                        if work.TokenRole.HasFlag(TokenRole.Source) then
                            tokenSources <- work.Id :: tokenSources

        log.Debug($"SimIndex built: {state.AllWorkGuids.Length} works, {state.AllCallGuids.Length} calls")

        let workCanonicalGuids =
            state.AllWorkGuids
            |> List.choose (fun wg ->
                Queries.getWork wg store
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

        // Call ReferenceOf 기반 OR 그룹 빌드
        let callCanonicalGuids =
            state.AllCallGuids
            |> List.choose (fun cg ->
                Queries.getCall cg store
                |> Option.map (fun c -> cg, (c.ReferenceOf |> Option.defaultValue cg)))
            |> Map.ofList

        let callReferenceGroups =
            callCanonicalGuids
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

        // ── 디버그: Group expansion + 토큰 경로 확인 ──
        let nameOf guid = state.WorkName |> Map.tryFind guid |> Option.defaultValue (string guid)
        for wg in allWorkGuidsRev do
            let preds = state.WorkStartPreds |> Map.tryFind wg |> Option.defaultValue []
            let succs = expandedTokenSuccMap |> Map.tryFind wg |> Option.defaultValue []
            let inPath = tokenPathGuids.Contains wg
            let canonical = workCanonicalGuids |> Map.tryFind wg |> Option.defaultValue wg
            let isRef = canonical <> wg
            let role = expandedTokenRoleMap |> Map.tryFind wg |> Option.defaultValue TokenRole.None
            if preds.Length > 0 || succs.Length > 0 || inPath || isRef then
                let predsStr = preds |> List.map nameOf |> String.concat ","
                let succsStr = succs |> List.map nameOf |> String.concat ","
                let canonStr = nameOf canonical
                log.Debug($"[SimIndex] {nameOf wg}: preds=[{predsStr}] tokenSucc=[{succsStr}] inTokenPath={inPath} role={role} isRef={isRef} canonical={canonStr}")

        { Store = store
          AllWorkGuids = allWorkGuidsRev
          AllCallGuids = state.AllCallGuids |> List.rev
          AllFlowGuids = state.AllFlowGuids |> List.rev
          WorkCanonicalGuids = workCanonicalGuids
          WorkCallGuids = state.WorkCallGuids
          WorkStartPreds = state.WorkStartPreds
          WorkPureStartPreds = state.WorkPureStartPreds
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
          CallCanonicalGuids = callCanonicalGuids
          CallReferenceGroups = callReferenceGroups
          ActiveSystemNames = activeSystemNames
          TickMs = tickMs
          WorkTokenRole = expandedTokenRoleMap
          WorkTokenSuccessors = expandedTokenSuccMap
          TokenSourceGuids = tokenSourceCanonicals |> List.distinct |> List.sort
          TokenSinkGuids = tokenSinkGuids
          TokenPathGuids = tokenPathGuids
          CallTypeMap = state.CallTypeMap
          CallTimeoutMap = state.CallTimeoutMap }

    type ConnectionSnapshot = {
        WorkStartPreds: Map<Guid, Guid list>
        WorkPureStartPreds: Map<Guid, Guid list>
        WorkResetPreds: Map<Guid, Guid list>
        CallStartPreds: Map<Guid, Guid list>
        WorkTokenSuccessors: Map<Guid, Guid list>
        TokenPathGuids: Set<Guid>
    }

    let snapshotConnections (index: SimIndex) = {
        WorkStartPreds = index.WorkStartPreds
        WorkPureStartPreds = index.WorkPureStartPreds
        WorkResetPreds = index.WorkResetPreds
        CallStartPreds = index.CallStartPreds
        WorkTokenSuccessors = index.WorkTokenSuccessors
        TokenPathGuids = index.TokenPathGuids
    }

    let reloadConnections (index: SimIndex) =
        let previous = snapshotConnections index
        let rebuilt = build index.Store index.TickMs
        index.WorkStartPreds <- rebuilt.WorkStartPreds
        index.WorkPureStartPreds <- rebuilt.WorkPureStartPreds
        index.WorkResetPreds <- rebuilt.WorkResetPreds
        index.CallStartPreds <- rebuilt.CallStartPreds
        index.WorkTokenSuccessors <- rebuilt.WorkTokenSuccessors
        index.TokenPathGuids <- rebuilt.TokenPathGuids
        previous, snapshotConnections index

    // ── Duration 재빌드 ────────────────────────────────────────────

    /// 단일 Work의 Duration을 Store에서 재계산
    let private computeWorkDuration (store: DsStore) (index: SimIndex) (workGuid: Guid) : float =
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
            let callGuids = findOrEmpty resolvedId index.WorkCallGuids
            if callGuids.IsEmpty then userDurationMs
            else
                let deviceMs =
                    Queries.tryGetDeviceDurationMs resolvedId store
                    |> Option.defaultValue 0
                    |> float
                max userDurationMs deviceMs

    /// Store에서 모든 Work의 Duration을 재계산하여 SimIndex.WorkDuration을 갱신.
    /// skipGuids에 포함된 Work는 기존 값 유지 (Going 중인 Work 보호용).
    let reloadDurations (index: SimIndex) (skipGuids: Set<Guid>) =
        let store = index.Store
        let newDurations =
            index.AllWorkGuids
            |> List.fold (fun (acc: Map<Guid, float>) workGuid ->
                if skipGuids.Contains workGuid then acc
                else acc.Add(workGuid, computeWorkDuration store index workGuid)
            ) index.WorkDuration
        index.WorkDuration <- newDurations

    // ── 자동 원위치 (Auto-Homing Origin) ─────────────────────────────

    /// DAG에서 두 노드의 ancestor-descendant 관계를 O(1)로 판별하는 함수 반환.
    /// Start 화살표 기반. Some true = v1이 v2의 조상, Some false = v2가 v1의 조상, None = 관계 없음.
    let private buildAncestorDescendant (workGuids: Guid list) (startSuccessors: Map<Guid, Guid list>) : (Guid -> Guid -> bool option) =
        let idxMap = workGuids |> List.mapi (fun i g -> g, i) |> Map.ofList
        let n = workGuids.Length
        if n = 0 then (fun _ _ -> None)
        else
            let table = Array2D.create<bool> n n false
            let visited = System.Collections.Generic.HashSet<Guid>()

            let rec traverse (v: Guid) (ancestors: Guid list) =
                let vi = idxMap.[v]
                for a in ancestors do
                    table.[idxMap.[a], vi] <- true
                if visited.Contains(v) then
                    for a in ancestors do
                        let ai = idxMap.[a]
                        for d = 0 to n - 1 do
                            if table.[vi, d] then
                                table.[ai, d] <- true
                else
                    visited.Add(v) |> ignore
                    let succs = startSuccessors |> Map.tryFind v |> Option.defaultValue []
                    for s in succs do
                        traverse s (v :: ancestors)

            let hasIncoming =
                startSuccessors |> Map.toList |> List.collect snd |> Set.ofList
            let inits = workGuids |> List.filter (fun g -> not (hasIncoming.Contains g))
            for init in inits do
                traverse init []

            fun v1 v2 ->
                match Map.tryFind v1 idxMap, Map.tryFind v2 idxMap with
                | Some i1, Some i2 ->
                    if table.[i1, i2] then Some true
                    elif table.[i2, i1] then Some false
                    else None
                | _ -> None

    /// Call 간 ancestor-descendant 관계에서 InitialType 결정 (ds의 getInitialType 포팅).
    /// Some true = On (RxWork가 Finish로 시작), Some false = Off (Ready), None = 판별 불가
    let private computeCallInitialType (ancestorOf: Guid -> Guid -> bool option) (callGuid: Guid) (mutualPartnerCallGuids: Guid list) : bool option =
        if mutualPartnerCallGuids.IsEmpty then None
        else
            let relations = mutualPartnerCallGuids |> List.choose (fun partner -> ancestorOf callGuid partner)
            if relations.IsEmpty then None
            elif relations |> List.forall id then Some false       // ancestor (앞) → Off
            elif relations |> List.forall (fun r -> not r) then Some true  // descendant (뒤) → On
            else None  // 방향 혼재 → NotCare

    /// Active System의 Call 그래프 기반으로 Device Work 자동 원위치 초기상태를 계산.
    /// ds의 getOriginInfo 알고리즘 포팅:
    ///   1. 각 Active Work의 Call 간 Start 화살표로 순서 그래프 구성
    ///   2. 같은 DevicesAlias의 Call끼리 mutual partner로 묶음
    ///   3. ancestor/descendant 판별 → descendant Call의 RxWork = Finish 대상
    ///   4. 여러 Work에서 같은 Device Work에 대해 결론 충돌 시 → 제외 (수동 IsFinished 필요)
    let computeAutoHomingTargets (index: SimIndex) : Set<Guid> =
        let store = index.Store

        // Device Work Guid → 투표 결과 수집 (true=On/Finish, false=Off/Ready)
        let votes = System.Collections.Generic.Dictionary<Guid, ResizeArray<bool>>()
        let addVote (deviceWorkGuid: Guid) (isOn: bool) =
            match votes.TryGetValue(deviceWorkGuid) with
            | true, list -> list.Add(isOn)
            | false, _ -> votes.[deviceWorkGuid] <- ResizeArray([isOn])

        // Active System의 모든 Work 순회
        let activeWorks =
            Queries.allProjects store
            |> List.collect (fun p -> Queries.activeSystemsOf p.Id store)
            |> List.collect (fun sys -> Queries.flowsOf sys.Id store)
            |> List.collect (fun flow -> Queries.worksOf flow.Id store)

        for activeWork in activeWorks do
            let calls = Queries.callsOf activeWork.Id store
            if calls.Length < 2 then () // Call 1개 이하면 순서 판별 불필요
            else
                let callGuids = calls |> List.map (fun c -> c.Id)

                // Call 간 Start 화살표로 successor 맵 구성
                let callStartSuccessors =
                    callGuids
                    |> List.fold (fun (acc: Map<Guid, Guid list>) cg ->
                        let preds = index.CallStartPreds |> Map.tryFind cg |> Option.defaultValue []
                        let localPreds = preds |> List.filter (fun p -> callGuids |> List.contains p)
                        localPreds |> List.fold (fun a p ->
                            let existing = a |> Map.tryFind p |> Option.defaultValue []
                            a |> Map.add p (cg :: existing)
                        ) acc
                    ) Map.empty

                let ancestorOf = buildAncestorDescendant callGuids callStartSuccessors

                // 같은 Device System(ApiDef.ParentId)끼리 mutual partner 그룹
                let resolveDeviceSystemId (call: Call) =
                    call.ApiCalls
                    |> Seq.tryHead
                    |> Option.bind (fun ac -> ac.ApiDefId)
                    |> Option.bind (fun defId -> Queries.getApiDef defId store)
                    |> Option.map (fun def -> def.ParentId)
                let callsByDevice =
                    calls
                    |> List.choose (fun c -> resolveDeviceSystemId c |> Option.map (fun sysId -> sysId, c))
                    |> List.groupBy fst
                    |> List.map (fun (_, pairs) -> pairs |> List.map snd)
                    |> List.filter (fun group -> group.Length > 1)

                for deviceCalls in callsByDevice do
                    // SkipIfCompleted는 실행 시 스킵일 뿐, 원위치 초기상태 계산에는 참여해야 함
                    if deviceCalls.Length < 2 then () else
                    for call in deviceCalls do
                        let partnerGuids =
                            deviceCalls
                            |> List.filter (fun c -> c.Id <> call.Id)
                            |> List.map (fun c -> c.Id)

                        match computeCallInitialType ancestorOf call.Id partnerGuids with
                        | Some isOn ->
                            // Call의 RxWork 찾기: ApiCall → ApiDef → RxGuid → Device Work
                            for apiCall in call.ApiCalls do
                                match apiCall.ApiDefId |> Option.bind (fun defId -> Queries.getApiDef defId store) with
                                | Some apiDef ->
                                    apiDef.RxGuid |> Option.iter (fun rxWorkGuid ->
                                        if index.AllWorkGuids |> List.contains rxWorkGuid then
                                            addVote rxWorkGuid isOn)
                                | None -> ()
                        | None -> () // 병렬/판별불가 → 투표 안 함

        // 투표 합의: 모든 투표가 On(true)인 Device Work만 Finish 대상
        votes
        |> Seq.choose (fun kv ->
            let allVotes = kv.Value
            if allVotes.Count > 0 && allVotes |> Seq.forall id then Some kv.Key
            else None)
        |> Set.ofSeq

    /// 자동 원위치 계획: Finish 대상 + Ready 대상 Device Work를 함께 반환.
    /// fst = Finish 대상 (On), snd = Ready 대상 (Off)
    let computeAutoHomingPlan (index: SimIndex) : Set<Guid> * Set<Guid> =
        let store = index.Store
        let onVotes = System.Collections.Generic.Dictionary<Guid, ResizeArray<bool>>()
        let offVotes = System.Collections.Generic.Dictionary<Guid, ResizeArray<bool>>()
        let addVote (deviceWorkGuid: Guid) (isOn: bool) =
            let target = if isOn then onVotes else offVotes
            match target.TryGetValue(deviceWorkGuid) with
            | true, list -> list.Add(true)
            | false, _ -> target.[deviceWorkGuid] <- ResizeArray([true])

        let activeWorks =
            Queries.allProjects store
            |> List.collect (fun p -> Queries.activeSystemsOf p.Id store)
            |> List.collect (fun sys -> Queries.flowsOf sys.Id store)
            |> List.collect (fun flow -> Queries.worksOf flow.Id store)

        for activeWork in activeWorks do
            let calls = Queries.callsOf activeWork.Id store
            if calls.Length >= 2 then
                let callGuids = calls |> List.map (fun c -> c.Id)
                let callStartSuccessors =
                    callGuids
                    |> List.fold (fun (acc: Map<Guid, Guid list>) cg ->
                        let preds = index.CallStartPreds |> Map.tryFind cg |> Option.defaultValue []
                        let localPreds = preds |> List.filter (fun p -> callGuids |> List.contains p)
                        localPreds |> List.fold (fun a p ->
                            let existing = a |> Map.tryFind p |> Option.defaultValue []
                            a |> Map.add p (cg :: existing)) acc) Map.empty
                let ancestorOf = buildAncestorDescendant callGuids callStartSuccessors
                let resolveDeviceSystemId (call: Call) =
                    call.ApiCalls |> Seq.tryHead
                    |> Option.bind (fun ac -> ac.ApiDefId)
                    |> Option.bind (fun defId -> Queries.getApiDef defId store)
                    |> Option.map (fun def -> def.ParentId)
                let callsByDevice =
                    calls
                    |> List.choose (fun c -> resolveDeviceSystemId c |> Option.map (fun sysId -> sysId, c))
                    |> List.groupBy fst |> List.map (fun (_, pairs) -> pairs |> List.map snd)
                    |> List.filter (fun group -> group.Length > 1)
                for deviceCalls in callsByDevice do
                    if deviceCalls.Length >= 2 then
                        for call in deviceCalls do
                            let partnerGuids = deviceCalls |> List.filter (fun c -> c.Id <> call.Id) |> List.map (fun c -> c.Id)
                            match computeCallInitialType ancestorOf call.Id partnerGuids with
                            | Some isOn ->
                                for apiCall in call.ApiCalls do
                                    match apiCall.ApiDefId |> Option.bind (fun defId -> Queries.getApiDef defId store) with
                                    | Some apiDef ->
                                        apiDef.RxGuid |> Option.iter (fun rxWorkGuid ->
                                            if index.AllWorkGuids |> List.contains rxWorkGuid then
                                                addVote rxWorkGuid isOn)
                                    | None -> ()
                            | None -> ()

        let finishTargets =
            onVotes |> Seq.choose (fun kv ->
                if kv.Value.Count > 0 then Some kv.Key else None) |> Set.ofSeq
        let readyTargets =
            offVotes |> Seq.choose (fun kv ->
                if kv.Value.Count > 0 then Some kv.Key else None) |> Set.ofSeq
        finishTargets, readyTargets

    /// Ready 대상 Device Work를 Reset시키는 진입점 Work를 찾고,
    /// 그 진입점에서 역방향 탐색하여 ApiDef에 연결된 Device Work를 반환.
    /// ApiDef 연결 Work를 Going시키면 Reset 연쇄로 Ready 대상이 원위치됨.
    let findHomingEntryPoints (index: SimIndex) (readyTargets: Set<Guid>) : Guid list * string list =
        let store = index.Store
        let mutable entryWorkGuids = []
        let mutable warnings = []

        for readyWorkGuid in readyTargets do
            // Reset predecessor: 이 Work를 Reset시키는 Work (ResetReset 화살표 상대)
            let resetPreds = findOrEmpty readyWorkGuid index.WorkResetPreds
            if resetPreds.IsEmpty then
                let name = index.WorkName |> Map.tryFind readyWorkGuid |> Option.defaultValue (string readyWorkGuid)
                warnings <- $"Device Work '{name}'에 Reset predecessor가 없습니다. 수동 IsFinished 설정이 필요합니다." :: warnings
            else
                for entryGuid in resetPreds do
                    // 진입점에서 역방향(Start predecessor)을 타면서 ApiDef 연결된 Work 찾기
                    let rec findApiDefWork (currentGuid: Guid) (visited: Set<Guid>) =
                        if visited.Contains currentGuid then ()
                        else
                            let visited = visited.Add currentGuid
                            // 이 Work가 ApiDef에 연결돼 있는지 (= TxGuid/RxGuid로 참조됨)
                            let hasApiDef =
                                store.ApiDefs.Values
                                |> Seq.exists (fun def ->
                                    def.TxGuid = Some currentGuid || def.RxGuid = Some currentGuid)
                            if hasApiDef then
                                entryWorkGuids <- currentGuid :: entryWorkGuids
                            else
                                // ApiDef 없으면 Start predecessor를 역추적
                                let startPreds = findOrEmpty currentGuid index.WorkStartPreds
                                if startPreds.IsEmpty then
                                    let name = index.WorkName |> Map.tryFind currentGuid |> Option.defaultValue (string currentGuid)
                                    warnings <- $"Work '{name}'에서 ApiDef 연결 Work를 찾을 수 없습니다." :: warnings
                                else
                                    for predGuid in startPreds do
                                        findApiDefWork predGuid visited
                    findApiDefWork entryGuid Set.empty

        entryWorkGuids |> List.distinct, warnings |> List.distinct

    // ── InitialFlag 헬퍼 ─────────────────────────────────────────────

    /// WorkProperties.IsFinished가 true인 Work들을 찾아 초기 Finish 상태로 설정할 대상 반환.
    /// IsFinished가 하나도 설정되지 않은 기존 프로젝트는 자동 원위치 계산 → RET 이름 기반 폴백 순서.
    let findInitialFlagRxWorkGuids (index: SimIndex) : Set<Guid> =
        let isFinishedWorks =
            index.AllWorkGuids
            |> List.filter (fun workGuid ->
                Queries.getWork workGuid index.Store
                |> Option.bind (fun work -> work.GetSimulationProperties())
                |> Option.map (fun p -> p.IsFinished)
                |> Option.defaultValue false)
        if not isFinishedWorks.IsEmpty then
            isFinishedWorks |> Set.ofList
        else
            // 자동 원위치: Start 화살표 DAG 기반 초기상태 계산
            let autoTargets = computeAutoHomingTargets index
            if not autoTargets.IsEmpty then
                autoTargets
            else
                // Backward compatibility: 위 두 방법 모두 결과 없으면 RET 이름 기반 폴백
                let isRetDirection callGuid =
                    rxWorkGuids index callGuid
                    |> List.exists (fun rxGuid ->
                        Queries.getWork rxGuid index.Store
                        |> Option.map (fun work -> work.Name.ToUpperInvariant().Contains("RET"))
                        |> Option.defaultValue false)
                index.AllCallGuids
                |> List.filter isRetDirection
                |> Seq.collect (fun callGuid -> rxWorkGuids index callGuid)
                |> Set.ofSeq

    /// 토큰 역할(Source/Ignore)이 하나라도 설정되어 있는지 확인
    let hasAnyTokenRole (index: SimIndex) : bool =
        index.WorkTokenRole
        |> Map.exists (fun _ role -> role.HasFlag(TokenRole.Source) || role.HasFlag(TokenRole.Ignore))


