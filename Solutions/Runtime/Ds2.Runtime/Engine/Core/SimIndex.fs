namespace Ds2.Runtime.Engine.Core

open System
open Ds2.Core
open Ds2.Core.Store

type ConditionEntry = {
    RxWorkGuid: Guid
    ApiCallGuid: Guid option
    InputSpec: ValueSpec
}

/// CallCondition 트리 구조 보존 — isOR 플래그를 evaluate 단계까지 전달.
/// And/Or 중첩으로 사용자 모델의 `A | (B|C)` 같은 표현 정확히 평가.
/// 빈 And 는 true (= 조건 없음 통과), 빈 Or 는 false.
type ConditionExpression =
    | Leaf of ConditionEntry
    | And of ConditionExpression list
    | Or of ConditionExpression list

type SimIndex = {
    Store: DsStore
    AllWorkGuids: Guid list
    AllCallGuids: Guid list
    AllFlowGuids: Guid list
    WorkCanonicalGuids: Map<Guid, Guid>
    WorkCallGuids: Map<Guid, Guid list>
    mutable WorkStartPreds: Map<Guid, Guid list>
    mutable WorkPureStartPreds: Map<Guid, Guid list>
    mutable WorkResetPreds: Map<Guid, Guid list>
    mutable WorkDuration: Map<Guid, float>
    WorkSystemName: Map<Guid, string>
    WorkName: Map<Guid, string>
    WorkFlowGuid: Map<Guid, Guid>
    mutable CallStartPreds: Map<Guid, Guid list>
    CallWorkGuid: Map<Guid, Guid>
    CallApiCallGuids: Map<Guid, Guid list>
    CallAutoAuxConditions: Map<Guid, ConditionExpression>
    CallComAuxConditions: Map<Guid, ConditionExpression>
    CallSkipUnmatchConditions: Map<Guid, ConditionExpression>
    WorkReferenceGroups: Map<Guid, Guid list>
    WorkGroupSets: Map<Guid, Set<Guid>>
    CallCanonicalGuids: Map<Guid, Guid>
    CallReferenceGroups: Map<Guid, Guid list>
    ActiveSystemNames: Set<string>
    TickMs: int
    WorkTokenRole: Map<Guid, TokenRole>
    mutable WorkTokenSuccessors: Map<Guid, Guid list>
    TokenSourceGuids: Guid list
    TokenSinkGuids: Set<Guid>
    mutable TokenPathGuids: Set<Guid>
    CallRaceExclusions: Map<Guid, Set<Guid>>
    CallTypeMap: Map<Guid, CallType>
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
        mutable CallAutoAuxConditions: Map<Guid, ConditionExpression>
        mutable CallComAuxConditions: Map<Guid, ConditionExpression>
        mutable CallSkipUnmatchConditions: Map<Guid, ConditionExpression>
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

    let private resolveApiDefGuids = SimIndexAlgorithms.resolveApiDefGuids

    let txWorkGuids (index: SimIndex) (callGuid: Guid) =
        resolveApiDefGuids index.Store (findOrEmpty callGuid index.CallApiCallGuids) (fun d -> d.TxGuid)

    let rxWorkGuids (index: SimIndex) (callGuid: Guid) =
        resolveApiDefGuids index.Store (findOrEmpty callGuid index.CallApiCallGuids) (fun d -> d.RxGuid)

    let private toEntry (data: SimIndexAlgorithms.ConditionEntryData) : ConditionEntry = {
        RxWorkGuid = data.RxWorkGuid
        ApiCallGuid = data.ApiCallGuid
        InputSpec = data.InputSpec
    }

    /// CallCondition 트리를 ConditionExpression 으로 변환. cc.IsOR 보존.
    /// 여러 callCondition (top-level) 끼리는 AND (모두 충족 필요).
    let private buildConditionExpression store conditionType (call: Call) : ConditionExpression =
        let rec convertOne (cc: CallCondition) : ConditionExpression =
            let leafExprs =
                SimIndexAlgorithms.convertApiCallsToEntries store cc.Conditions
                |> List.map (toEntry >> Leaf)
            let childExprs = cc.Children |> Seq.map convertOne |> Seq.toList
            let all = leafExprs @ childExprs
            if cc.IsOR then Or all else And all

        let topExprs =
            call.CallConditions
            |> Seq.filter (fun cc -> cc.Type = Some conditionType)
            |> Seq.map convertOne
            |> Seq.toList
        And topExprs

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
        let mutable workGroupSets = Map.empty<Guid, Set<Guid>>

        let state = {
            AllWorkGuids = []
            AllCallGuids = []
            AllFlowGuids = []
            WorkCallGuids = Map.empty
            WorkStartPreds = Map.empty
            WorkPureStartPreds = Map.empty
            WorkResetPreds = Map.empty
            WorkDuration = Map.empty
            WorkSystemName = Map.empty
            WorkName = Map.empty
            WorkFlowGuid = Map.empty
            CallStartPreds = Map.empty
            CallWorkGuid = Map.empty
            CallApiCallGuids = Map.empty
            CallAutoAuxConditions = Map.empty
            CallComAuxConditions = Map.empty
            CallSkipUnmatchConditions = Map.empty
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
            state.CallAutoAuxConditions <- state.CallAutoAuxConditions.Add(call.Id, buildConditionExpression store CallConditionType.AutoAux dataSource)
            state.CallComAuxConditions <- state.CallComAuxConditions.Add(call.Id, buildConditionExpression store CallConditionType.ComAux dataSource)
            state.CallSkipUnmatchConditions <- state.CallSkipUnmatchConditions.Add(call.Id, buildConditionExpression store CallConditionType.SkipUnmatch dataSource)
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
            state.WorkSystemName <- state.WorkSystemName.Add(work.Id, system.Name)
            state.WorkName <- state.WorkName.Add(work.Id, work.Name)
            state.WorkFlowGuid <- state.WorkFlowGuid.Add(work.Id, flowId)
            state.AllWorkGuids <- work.Id :: state.AllWorkGuids

        for system in allSystems do
            let workArrows = Queries.arrowWorksOf system.Id store
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

            let flows = Queries.flowsOf system.Id store
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

    let reloadDurations (index: SimIndex) (skipGuids: Set<Guid>) =
        index.WorkDuration <-
            SimIndexAlgorithms.reloadDurations
                index.Store
                index.WorkCallGuids
                index.AllWorkGuids
                index.WorkDuration
                skipGuids

    let private activeWorkDeviceCalls (index: SimIndex) =
        let allWorkGuids = index.AllWorkGuids |> Set.ofList
        let resolveDeviceSystemId (call: Call) =
            call.ApiCalls
            |> Seq.tryHead
            |> Option.bind (fun apiCall -> apiCall.ApiDefId)
            |> Option.bind (fun defId -> Queries.getApiDef defId index.Store)
            |> Option.map (fun apiDef -> apiDef.ParentId)
        let toDeviceCall (call: Call) : SimIndexAutoHoming.DeviceCall = {
            CallGuid = call.Id
            DeviceSystemId = resolveDeviceSystemId call
            RxWorkGuids =
                call.ApiCalls
                |> Seq.map (fun apiCall -> apiCall.Id)
                |> Seq.toList
                |> fun apiCallGuids -> resolveApiDefGuids index.Store apiCallGuids (fun d -> d.RxGuid)
                |> List.filter allWorkGuids.Contains
        }
        Queries.allProjects index.Store
        |> List.collect (fun project -> Queries.activeSystemsOf project.Id index.Store)
        |> List.collect (fun system -> Queries.flowsOf system.Id index.Store)
        |> List.collect (fun flow -> Queries.worksOf flow.Id index.Store)
        |> List.map (fun activeWork -> Queries.callsOf activeWork.Id index.Store |> List.map toDeviceCall)

    let computeAutoHomingTargets (index: SimIndex) : Set<Guid> =
        SimIndexAutoHoming.computeAutoHomingTargets index.CallStartPreds (activeWorkDeviceCalls index)

    let computeAutoHomingPlan (index: SimIndex) : Set<Guid> * Set<Guid> =
        SimIndexAutoHoming.computeAutoHomingPlan index.CallStartPreds (activeWorkDeviceCalls index)

    let computeAutoHomingCallPlan (index: SimIndex) : Set<Guid> * Set<Guid> =
        SimIndexAutoHoming.computeAutoHomingCallPlan index.CallStartPreds (activeWorkDeviceCalls index)

    let findHomingEntryPoints (index: SimIndex) (readyTargets: Set<Guid>) : Guid list * string list =
        SimIndexAutoHoming.findHomingEntryPoints
            (fun workGuid -> index.WorkName |> Map.tryFind workGuid |> Option.defaultValue (string workGuid))
            index.WorkResetPreds
            index.WorkStartPreds
            (fun workGuid ->
                index.Store.ApiDefs.Values
                |> Seq.exists (fun apiDef -> apiDef.TxGuid = Some workGuid || apiDef.RxGuid = Some workGuid))
            readyTargets

    let findInitialFlagRxWorkGuids (index: SimIndex) : Set<Guid> =
        let isFinishedWorks =
            index.AllWorkGuids
            |> List.filter (fun workGuid ->
                Queries.getWork workGuid index.Store
                |> Option.bind (fun work -> work.GetSimulationProperties())
                |> Option.map (fun simProps -> simProps.IsFinished)
                |> Option.defaultValue false)
        if not isFinishedWorks.IsEmpty then
            isFinishedWorks |> Set.ofList
        else
            let autoTargets = computeAutoHomingTargets index
            if not autoTargets.IsEmpty then
                autoTargets
            else
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

    let hasAnyTokenRole (index: SimIndex) : bool =
        index.WorkTokenRole
        |> Map.exists (fun _ role -> role.HasFlag(TokenRole.Source) || role.HasFlag(TokenRole.Ignore))
