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
    CallAutoConditions: Map<Guid, ConditionEntry list>
    CallCommonConditions: Map<Guid, ConditionEntry list>
    CallActiveConditions: Map<Guid, ConditionEntry list>
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
        mutable CallAutoConditions: Map<Guid, ConditionEntry list>
        mutable CallCommonConditions: Map<Guid, ConditionEntry list>
        mutable CallActiveConditions: Map<Guid, ConditionEntry list>
    }

    let private emptyBuildState () = {
        AllWorkGuids = []
        AllCallGuids = []
        WorkCallGuids = Map.empty
        WorkStartPreds = Map.empty
        WorkResetPreds = Map.empty
        WorkDuration = Map.empty
        WorkSystemName = Map.empty
        WorkName = Map.empty
        CallStartPreds = Map.empty
        CallWorkGuid = Map.empty
        CallApiCallGuids = Map.empty
        CallAutoConditions = Map.empty
        CallCommonConditions = Map.empty
        CallActiveConditions = Map.empty
    }

    let private findOrEmpty key map =
        map |> Map.tryFind key |> Option.defaultValue []

    let private groupSourcesByTarget arrowTypes getArrowType getSourceId getTargetId arrows =
        arrows
        |> List.filter (fun arrow -> List.contains (getArrowType arrow) arrowTypes)
        |> List.groupBy getTargetId
        |> List.map (fun (targetId, groupedArrows) ->
            targetId, groupedArrows |> List.map getSourceId)
        |> Map.ofList

    let private groupTargetsBySource arrowTypes getArrowType getSourceId getTargetId arrows =
        arrows
        |> List.filter (fun arrow -> List.contains (getArrowType arrow) arrowTypes)
        |> List.groupBy getSourceId
        |> List.map (fun (sourceId, groupedArrows) ->
            sourceId, groupedArrows |> List.map getTargetId)
        |> Map.ofList

    let private mergeGroupedMaps maps =
        maps
        |> List.collect Map.toList
        |> List.groupBy fst
        |> List.map (fun (key, groupedValues) -> key, groupedValues |> List.collect snd)
        |> Map.ofList

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

    let private addCallData (state: BuildState) (store: DsStore) (work: Work) (callStartPreds: Map<Guid, Guid list>) (call: Call) =
        let apiCallIds = call.ApiCalls |> Seq.map (fun apiCall -> apiCall.Id) |> Seq.toList

        state.CallApiCallGuids <- state.CallApiCallGuids.Add(call.Id, apiCallIds)
        state.CallStartPreds <- state.CallStartPreds.Add(call.Id, findOrEmpty call.Id callStartPreds)
        state.CallWorkGuid <- state.CallWorkGuid.Add(call.Id, work.Id)
        state.CallAutoConditions <- state.CallAutoConditions.Add(call.Id, conditionSpecs store CallConditionType.Auto call)
        state.CallCommonConditions <- state.CallCommonConditions.Add(call.Id, conditionSpecs store CallConditionType.Common call)
        state.CallActiveConditions <- state.CallActiveConditions.Add(call.Id, conditionSpecs store CallConditionType.Active call)
        state.AllCallGuids <- call.Id :: state.AllCallGuids

    let private addWorkData (state: BuildState) (system: DsSystem) (work: Work) (callGuids: Guid list) (workStartPreds: Map<Guid, Guid list>) (workResetPreds: Map<Guid, Guid list>) =
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

        let state = emptyBuildState ()

        for system in allSystems do
            let workArrows = DsQuery.arrowWorksOf system.Id store
            let wStartPreds =
                groupSourcesByTarget
                    [ ArrowType.Start; ArrowType.StartReset ]
                    (fun (arrow: ArrowBetweenWorks) -> arrow.ArrowType)
                    (fun arrow -> arrow.SourceId)
                    (fun arrow -> arrow.TargetId)
                    workArrows
            let wResetPreds =
                mergeGroupedMaps [
                    groupSourcesByTarget
                        [ ArrowType.Reset; ArrowType.ResetReset ]
                        (fun (arrow: ArrowBetweenWorks) -> arrow.ArrowType)
                        (fun arrow -> arrow.SourceId)
                        (fun arrow -> arrow.TargetId)
                        workArrows
                    groupTargetsBySource
                        [ ArrowType.StartReset; ArrowType.ResetReset ]
                        (fun (arrow: ArrowBetweenWorks) -> arrow.ArrowType)
                        (fun arrow -> arrow.SourceId)
                        (fun arrow -> arrow.TargetId)
                        workArrows
                ]

            let flows = DsQuery.flowsOf system.Id store
            for flow in flows do
                let works = DsQuery.worksOf flow.Id store
                for work in works do
                    let calls = DsQuery.callsOf work.Id store
                    let callArrows = DsQuery.arrowCallsOf work.Id store
                    let cStartPreds =
                        groupSourcesByTarget
                            [ ArrowType.Start; ArrowType.StartReset ]
                            (fun (arrow: ArrowBetweenCalls) -> arrow.ArrowType)
                            (fun arrow -> arrow.SourceId)
                            (fun arrow -> arrow.TargetId)
                            callArrows

                    let callGuids = calls |> List.map (fun c -> c.Id)
                    for call in calls do
                        addCallData state store work cStartPreds call

                    addWorkData state system work callGuids wStartPreds wResetPreds

        log.Debug($"SimIndex built: {state.AllWorkGuids.Length} works, {state.AllCallGuids.Length} calls")

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
          CallAutoConditions = state.CallAutoConditions
          CallCommonConditions = state.CallCommonConditions
          CallActiveConditions = state.CallActiveConditions
          ActiveSystemNames = activeSystemNames
          TickMs = tickMs }

    // ── InitialFlag 헬퍼 ─────────────────────────────────────────────

    /// RET 방향 Call인지 확인
    let isCallRetDirection (index: SimIndex) (callGuid: Guid) : bool =
        index.CallApiCallGuids |> Map.tryFind callGuid |> Option.defaultValue []
        |> List.exists (fun apiCallGuid ->
            DsQuery.getApiCall apiCallGuid index.Store
            |> Option.bind (fun apiCall -> apiCall.ApiDefId)
            |> Option.bind (fun apiDefId -> DsQuery.getApiDef apiDefId index.Store)
            |> Option.bind (fun apiDef -> apiDef.Properties.RxGuid)
            |> Option.bind (fun rxGuid -> DsQuery.getWork rxGuid index.Store)
            |> Option.map (fun work -> work.Name.ToUpperInvariant().Contains("RET"))
            |> Option.defaultValue false)

    let findInitialFlagCallGuids (index: SimIndex) : Set<Guid> =
        index.AllCallGuids |> List.filter (isCallRetDirection index) |> Set.ofList

    let findInitialFlagRxWorkGuids (index: SimIndex) : Set<Guid> =
        let initialCallGuids = findInitialFlagCallGuids index
        initialCallGuids |> Set.toSeq
        |> Seq.collect (fun callGuid ->
            index.CallApiCallGuids |> Map.tryFind callGuid |> Option.defaultValue []
            |> List.choose (fun apiCallGuid ->
                DsQuery.getApiCall apiCallGuid index.Store
                |> Option.bind (fun apiCall -> apiCall.ApiDefId)
                |> Option.bind (fun apiDefId -> DsQuery.getApiDef apiDefId index.Store)
                |> Option.bind (fun apiDef -> apiDef.Properties.RxGuid)))
        |> Set.ofSeq
