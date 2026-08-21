namespace Ds2.Runtime.Engine.Core

open System
open Ds2.Core
open Ds2.Core.Store

module internal SimIndexBuild =

    let private log = log4net.LogManager.GetLogger("SimIndex")

    let private findOrEmpty = SimIndexAlgorithms.findOrEmpty

    let private resolveApiDefGuids = SimIndexAlgorithms.resolveApiDefGuids

    /// Condition 트리를 ConditionExpression 으로 변환. cc.IsOR / cc.IsInverted 보존.
    /// 여러 condition (top-level) 끼리는 AND (모두 충족 필요).
    /// Call/Work 공용 — `conditions` 컬렉션만 받음.
    let private buildConditionExpression store (conditionType: ConditionType) (conditions: ResizeArray<Condition>) : ConditionExpression =
        let rec convertOne (cc: Condition) : ConditionExpression =
            let leafExprs =
                SimIndexAlgorithms.convertApiCallsToExpressions store cc.ApiCalls
            let childExprs = cc.Children |> Seq.map convertOne |> Seq.toList
            let all = leafExprs @ childExprs
            let grouped = if cc.IsOR then Or all else And all
            if cc.IsInverted then Not grouped else grouped

        let topExprs =
            conditions
            |> Seq.filter (fun cc -> cc.Type = Some conditionType)
            |> Seq.map convertOne
            |> Seq.toList
        And topExprs

    /// targetSystemIds 가 Some 이면 그 집합의 시스템만 인덱스에 담는다 (System 단위 실행 — 멀티 PLC).
    /// 인과가 끊기지 않도록 호출측이 Queries.systemClosureOf 로 폐포를 넘겨야 한다.
    /// None 이면 프로젝트 전체 (기존 동작).
    let buildScoped (store: DsStore) (tickMs: int) (targetSystemIds: Set<Guid> option) : SimIndex =
        let project = Queries.allProjects store |> List.tryHead

        let inTarget (systemId: Guid) =
            match targetSystemIds with
            | Some ids -> ids.Contains systemId
            | None -> true

        let activeSystemNames =
            match project with
            | Some p ->
                Queries.activeSystemsOf p.Id store
                |> List.filter (fun s -> inTarget s.Id)
                |> List.map (fun s -> s.Name)
                |> Set.ofList
            | None -> Set.empty

        let allSystems =
            match project with
            | Some p -> Queries.projectSystemsOf p.Id store |> List.filter (fun s -> inTarget s.Id)
            | None -> []

        let mutable tokenRoleMap = Map.empty<Guid, TokenRole>
        let mutable tokenSuccMap = Map.empty<Guid, Guid list>
        let mutable workGroupSets = Map.empty<Guid, Set<Guid>>

        let state : SimIndexBuildState = {
            AllWorkGuids = []
            AllCallGuids = []
            AllFlowGuids = []
            WorkCallGuids = Map.empty
            WorkStartPreds = Map.empty
            WorkPureStartPreds = Map.empty
            WorkResetPreds = Map.empty
            WorkDuration = Map.empty
            WorkDurationRange = Map.empty
            WorkSystemName = Map.empty
            WorkName = Map.empty
            WorkFlowGuid = Map.empty
            CallStartPreds = Map.empty
            CallWorkGuid = Map.empty
            CallApiCallGuids = Map.empty
            CallAutoAuxConditions = Map.empty
            CallComAuxConditions = Map.empty
            CallSkipActionConditions = Map.empty
            WorkSkipActionConditions = Map.empty
            CallTypeMap = Map.empty
            CallTimeoutMap = Map.empty
        }

        let addCallData (work: Work) (callStartPreds: Map<Guid, Guid list>) (call: Call) =
            let dataSource =
                match call.ReferenceOf with
                | Some origId -> Queries.getCall origId store |> Option.defaultValue call
                | None -> call
            let apiCallIds = dataSource.ApiCalls |> Seq.map (fun apiCall -> apiCall.Id) |> Seq.toList
            state.CallApiCallGuids <- state.CallApiCallGuids.Add(call.Id, apiCallIds)
            state.CallStartPreds <- state.CallStartPreds.Add(call.Id, findOrEmpty dataSource.Id callStartPreds)
            state.CallWorkGuid <- state.CallWorkGuid.Add(call.Id, work.Id)
            state.CallAutoAuxConditions <- state.CallAutoAuxConditions.Add(call.Id, buildConditionExpression store ConditionType.AutoAux dataSource.Conditions)
            state.CallComAuxConditions <- state.CallComAuxConditions.Add(call.Id, buildConditionExpression store ConditionType.ComAux dataSource.Conditions)
            state.CallSkipActionConditions <- state.CallSkipActionConditions.Add(call.Id, buildConditionExpression store ConditionType.SkipAction dataSource.Conditions)
            let simProps = dataSource.GetSimulationProperties()
            let callType = simProps |> Option.map (fun p -> p.CallType) |> Option.defaultValue CallType.WaitForCompletion
            state.CallTypeMap <- state.CallTypeMap.Add(call.Id, callType)

            match simProps |> Option.bind (fun p -> p.Timeout) with
            | Some timeout when timeout > TimeSpan.Zero ->
                state.CallTimeoutMap <- state.CallTimeoutMap.Add(call.Id, timeout)
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
            let resolvedId = work.ReferenceOf |> Option.defaultValue work.Id
            // device work(leaf)의 plan Going 지속 = work.Duration 만(순수 가동시간).
            // ActionType timeAppend(출력 유지)는 Going 막대를 늘이지 않고, 간트에 빨간 채워진 사각형(시각화)으로만 표기한다(사용자 확정 2026-06-06).
            let duration =
                if callGuids.IsEmpty then
                    userDurationMs
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
            match Queries.tryGetDeviceDurationRangeMs resolvedId store with
            | Some range -> state.WorkDurationRange <- state.WorkDurationRange.Add(work.Id, range)
            | None -> ()
            state.WorkSystemName <- state.WorkSystemName.Add(work.Id, system.Name)
            state.WorkName <- state.WorkName.Add(work.Id, work.Name)
            state.WorkFlowGuid <- state.WorkFlowGuid.Add(work.Id, flowId)
            let conditionsSource =
                match work.ReferenceOf with
                | Some origId -> Queries.getWork origId store |> Option.map (fun w -> w.Conditions) |> Option.defaultValue work.Conditions
                | None -> work.Conditions
            state.WorkSkipActionConditions <- state.WorkSkipActionConditions.Add(work.Id, buildConditionExpression store ConditionType.SkipAction conditionsSource)
            state.AllWorkGuids <- work.Id :: state.AllWorkGuids

        let hidden = Queries.hiddenWorkIds store
        for system in allSystems do
            let workArrows =
                Queries.arrowWorksOf system.Id store
                // 비활성 Flow 의 Work 가 끝점인 전이 제거 → 활성 Work 의 선행조건에 dangling 으로 남지 않음
                |> List.filter (fun a -> not (hidden.Contains a.SourceId || hidden.Contains a.TargetId))
            let wType = fun (a: ArrowBetweenWorks) -> a.ArrowType
            let wSrc = fun (a: ArrowBetweenWorks) -> a.SourceId
            let wTgt = fun (a: ArrowBetweenWorks) -> a.TargetId
            let wStartPreds =
                SimIndexAlgorithms.groupArrows [ ArrowType.Start; ArrowType.StartReset ] wType wTgt wSrc workArrows
            let wPureStartPreds =
                SimIndexAlgorithms.groupArrows [ ArrowType.Start ] wType wTgt wSrc workArrows
            let wResetPreds =
                SimIndexAlgorithms.mergeGroupedMaps [
                    SimIndexAlgorithms.groupArrows [ ArrowType.Reset; ArrowType.ResetReset ] wType wTgt wSrc workArrows
                    SimIndexAlgorithms.groupArrows [ ArrowType.StartReset; ArrowType.ResetReset ] wType wSrc wTgt workArrows
                ]
            let workGroupArrows = workArrows |> List.filter (fun a -> a.ArrowType = ArrowType.Group)
            let wStartPreds, wResetPreds = SimIndexGroupExpansion.expandWorkGroupArrows workGroupArrows wStartPreds wResetPreds
            let wPureStartPreds, _ = SimIndexGroupExpansion.expandWorkGroupArrows workGroupArrows wPureStartPreds Map.empty

            let groupSourceIds = workGroupArrows |> List.map (fun a -> a.SourceId)
            let groupTargetIds = workGroupArrows |> List.map (fun a -> a.TargetId)
            for groupSet in SimIndexGroupExpansion.buildGroupSets groupSourceIds groupTargetIds do
                for member' in groupSet do
                    workGroupSets <- workGroupSets.Add(member', groupSet)

            tokenSuccMap <- SimIndexTokenGraph.appendSuccessorsFromStartPreds tokenSuccMap wStartPreds

            let flows = Queries.flowsOf system.Id store |> List.filter (fun f -> not f.IsDisabled)
            let allCallArrows =
                flows
                |> List.collect (fun flow -> Queries.worksOf flow.Id store)
                |> List.collect (fun work -> Queries.arrowCallsOf work.Id store)
            let cType = fun (a: ArrowBetweenCalls) -> a.ArrowType
            let cSrc = fun (a: ArrowBetweenCalls) -> a.SourceId
            let cTgt = fun (a: ArrowBetweenCalls) -> a.TargetId
            let cStartPreds =
                SimIndexAlgorithms.groupArrows [ ArrowType.Start; ArrowType.StartReset ] cType cTgt cSrc allCallArrows
            let callGroupArrows = allCallArrows |> List.filter (fun a -> a.ArrowType = ArrowType.Group)
            let cStartPreds = SimIndexGroupExpansion.expandCallGroupArrows callGroupArrows cStartPreds

            for flow in flows do
                state.AllFlowGuids <- flow.Id :: state.AllFlowGuids
                let works = Queries.worksOf flow.Id store

                for work in works do
                    let resolvedWorkId = work.ReferenceOf |> Option.defaultValue work.Id
                    let calls = Queries.callsOf resolvedWorkId store
                    let callGuids = calls |> List.map (fun c -> c.Id)

                    if work.ReferenceOf.IsNone then
                        for call in calls do
                            addCallData work cStartPreds call

                    addWorkData system flow.Id work callGuids wStartPreds wPureStartPreds wResetPreds

                    if work.TokenRole <> TokenRole.None then
                        tokenRoleMap <- tokenRoleMap.Add(work.Id, work.TokenRole)

        log.Debug($"SimIndex built: {state.AllWorkGuids.Length} works, {state.AllCallGuids.Length} calls")

        let workCanonicalGuids =
            state.AllWorkGuids
            |> List.choose (fun workGuid ->
                Queries.getWork workGuid store
                |> Option.map (fun work -> workGuid, (work.ReferenceOf |> Option.defaultValue workGuid)))
            |> Map.ofList
        let workReferenceGroups = SimIndexAlgorithms.buildReferenceGroups workCanonicalGuids

        let callCanonicalGuids =
            state.AllCallGuids
            |> List.choose (fun callGuid ->
                Queries.getCall callGuid store
                |> Option.map (fun call -> callGuid, (call.ReferenceOf |> Option.defaultValue callGuid)))
            |> Map.ofList
        let callReferenceGroups = SimIndexAlgorithms.buildReferenceGroups callCanonicalGuids

        let expandedTokenRoleMap = SimIndexAlgorithms.buildExpandedTokenRoleMap workCanonicalGuids tokenRoleMap
        let expandedTokenSuccMap = SimIndexAlgorithms.expandByCanonical workCanonicalGuids tokenSuccMap
        let expandedWorkStartPreds = SimIndexAlgorithms.expandByCanonical workCanonicalGuids state.WorkStartPreds
        let expandedWorkPureStartPreds = SimIndexAlgorithms.expandByCanonical workCanonicalGuids state.WorkPureStartPreds
        let expandedWorkResetPreds = SimIndexAlgorithms.expandByCanonical workCanonicalGuids state.WorkResetPreds

        let tokenSources =
            expandedTokenRoleMap
            |> Map.toSeq
            |> Seq.choose (fun (workGuid, role) ->
                if role.HasFlag(TokenRole.Source) then Some workGuid else None)
            |> Seq.toList
        let tokenSinkGuids =
            expandedTokenRoleMap
            |> Map.toSeq
            |> Seq.choose (fun (workGuid, role) ->
                if role.HasFlag(TokenRole.Sink) then Some workGuid else None)
            |> Set.ofSeq
        let tokenPathGuids =
            SimIndexTokenGraph.buildTokenPathGuids tokenSources expandedTokenSuccMap
            |> Set.ofSeq

        let allWorkGuidsRev = state.AllWorkGuids |> List.rev
        let nameOf guid = state.WorkName |> Map.tryFind guid |> Option.defaultValue (string guid)

        for workGuid in allWorkGuidsRev do
            let preds = expandedWorkStartPreds |> Map.tryFind workGuid |> Option.defaultValue []
            let succs = expandedTokenSuccMap |> Map.tryFind workGuid |> Option.defaultValue []
            let inPath = tokenPathGuids.Contains workGuid
            let canonical = workCanonicalGuids |> Map.tryFind workGuid |> Option.defaultValue workGuid
            let isRef = canonical <> workGuid
            let role = expandedTokenRoleMap |> Map.tryFind workGuid |> Option.defaultValue TokenRole.None

            if preds.Length > 0 || succs.Length > 0 || inPath || isRef then
                let predsStr = preds |> List.map nameOf |> String.concat ","
                let succsStr = succs |> List.map nameOf |> String.concat ","
                let canonStr = nameOf canonical
                log.Debug($"[SimIndex] {nameOf workGuid}: preds=[{predsStr}] tokenSucc=[{succsStr}] inTokenPath={inPath} role={role} isRef={isRef} canonical={canonStr}")

        {
            Store = store
            AllWorkGuids = allWorkGuidsRev
            AllCallGuids = state.AllCallGuids |> List.rev
            AllFlowGuids = state.AllFlowGuids |> List.rev
            WorkCanonicalGuids = workCanonicalGuids
            WorkCallGuids = state.WorkCallGuids
            WorkStartPreds = expandedWorkStartPreds
            WorkPureStartPreds = expandedWorkPureStartPreds
            WorkResetPreds = expandedWorkResetPreds
            WorkDuration = state.WorkDuration
            WorkDurationRange = state.WorkDurationRange
            WorkSystemName = state.WorkSystemName
            WorkName = state.WorkName
            WorkFlowGuid = state.WorkFlowGuid
            CallStartPreds = state.CallStartPreds
            CallWorkGuid = state.CallWorkGuid
            CallApiCallGuids = state.CallApiCallGuids
            CallAutoAuxConditions = state.CallAutoAuxConditions
            CallComAuxConditions = state.CallComAuxConditions
            CallSkipActionConditions = state.CallSkipActionConditions
            WorkSkipActionConditions = state.WorkSkipActionConditions
            WorkReferenceGroups = workReferenceGroups
            WorkGroupSets = workGroupSets
            CallCanonicalGuids = callCanonicalGuids
            CallReferenceGroups = callReferenceGroups
            ActiveSystemNames = activeSystemNames
            TickMs = tickMs
            WorkTokenRole = expandedTokenRoleMap
            WorkTokenSuccessors = expandedTokenSuccMap
            TokenSourceGuids = tokenSources |> List.distinct |> List.sort
            TokenSinkGuids = tokenSinkGuids
            TokenPathGuids = tokenPathGuids
            CallRaceExclusions =
                SimIndexAlgorithms.buildRaceExclusions
                    state.AllCallGuids
                    state.CallApiCallGuids
                    state.CallWorkGuid
                    state.CallStartPreds
                    state.WorkStartPreds
                    state.WorkResetPreds
                    (fun apiCallGuids -> resolveApiDefGuids store apiCallGuids (fun d -> d.TxGuid))
            CallTypeMap = state.CallTypeMap
            CallTimeoutMap = state.CallTimeoutMap
        }

    /// 프로젝트 전체 인덱스 (기존 동작 유지용 래퍼).
    let build (store: DsStore) (tickMs: int) : SimIndex =
        buildScoped store tickMs None
