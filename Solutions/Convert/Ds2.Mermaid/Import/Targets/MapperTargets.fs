namespace Ds2.Mermaid

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store

/// System-level mermaid 임포트 결과 — ImportPlan + IoTag 바인딩용 callPath 인덱스.
/// callPath = `<SystemName>.<FlowName>.<WorkName>.<DeviceAlias>.<ApiName>` (escape-aware).
type SystemImportResult = {
    Plan: ImportPlan
    CallIndex: IReadOnlyDictionary<string, Guid>
}

module internal MermaidMapperTargets =

    open MermaidMapperCommon
    open MermaidTargetPlanning

    let private planCallsForWork
        (planned: PlannedCallNodes)
        (operations: ResizeArray<ImportPlanOperation>)
        workId
        nodes
        internalEdges
        onRegistered
        =
        for node in nodes do
            let call, apiName = registerCallNode planned operations workId node
            onRegistered node call apiName

        addInternalCallArrows operations workId planned internalEdges

    let private createPreview
        level
        flowNames
        workNames
        callNames
        arrowWorksCount
        arrowCallsCount
        ignored
        warnings
        =
        {
            Level = level
            FlowNames = flowNames
            WorkNames = workNames
            CallNames = callNames
            ArrowWorksCount = arrowWorksCount
            ArrowCallsCount = arrowCallsCount
            IgnoredEdges = ignored |> Seq.toList
            Warnings = warnings |> Seq.toList
        }

    let private completePlannedImport
        (operations: ResizeArray<ImportPlanOperation>)
        (planned: PlannedCallNodes)
        finalize
        =
        restorePlannedConditions operations planned
        finalize ()
        ImportPlan.ofSeq operations

    // ═══════════════════════════════════════════════════
    // Flow 2-depth: subgraph → Work, node → Call
    // ═══════════════════════════════════════════════════

    /// Work subgraph 의 SkipActionConditionRefs 를 work.Conditions 에 박는 후처리.
    /// label("Dev.ApiName") → Call lookup 으로 srcCall.ApiCalls.[0] 을 Condition.ApiCalls 에 추가.
    let private applyWorkSkipActionRefs
        (workSkipRefs: ResizeArray<Work * string list>)
        (createdCalls: seq<Call * string>) =
        if workSkipRefs.Count = 0 then () else
        let labelToCall = Dictionary<string, Call>()
        for (call, label) in createdCalls do
            if not (labelToCall.ContainsKey label) then labelToCall.[label] <- call
        for (work, refs) in workSkipRefs do
            if refs.IsEmpty then () else
            let cond = Condition()
            cond.Type <- Some ConditionType.SkipAction
            for refName in refs do
                match labelToCall.TryGetValue(refName) with
                | true, srcCall when srcCall.ApiCalls.Count > 0 ->
                    cond.ApiCalls.Add(srcCall.ApiCalls.[0])
                | _ -> ()
            if cond.ApiCalls.Count > 0 then
                work.Conditions.Add(cond)

    // [Obsolete] mapToFlow / mapToFlowFlat / mapToWork 는 deprecated — UI 진입점은 제거됨,
    //            테스트 호환성을 위해 함수는 남김. 후속 PR 에서 함수 삭제 예정.
    [<System.Obsolete("Flow-level mermaid import 는 deprecated — 프로젝트 단위(mapToSystem)만 사용.")>]
    let mapToFlow (store: DsStore) (flowId: Guid) (systemId: Guid) (projectId: Guid option) (graph: MermaidGraph) : ImportPlan =
        let operations = ResizeArray<ImportPlanOperation>()
        let planned = createPlannedCallNodes ()
        let nodeToWorkId = Dictionary<string, Guid>()
        let createdWorkArrows = HashSet<Guid * Guid>()
        let createdCalls = ResizeArray<Call * string>()
        let workSkipRefs = ResizeArray<Work * string list>()

        let flowName = flowNameOfFlow store flowId

        for sg in graph.Subgraphs do
            let work = Work(flowName, subgraphName sg, flowId)
            operations.Add(AddWork work)
            if not sg.SkipActionConditionRefs.IsEmpty then
                workSkipRefs.Add(work, sg.SkipActionConditionRefs)

            planCallsForWork planned operations work.Id sg.Nodes sg.InternalEdges (fun node call apiName ->
                nodeToWorkId.[node.Id] <- work.Id
                if apiName <> "" then
                    createdCalls.Add(call, node.Label))

        for edge in graph.GlobalEdges do
            match nodeToWorkId.TryGetValue(edge.SourceId), nodeToWorkId.TryGetValue(edge.TargetId) with
            | (true, srcWorkId), (true, tgtWorkId) when srcWorkId <> tgtWorkId ->
                if createdWorkArrows.Add(srcWorkId, tgtWorkId) then
                    let arrow = ArrowBetweenWorks(systemId, srcWorkId, tgtWorkId, mapArrowType edge.Label)
                    operations.Add(AddArrowWork arrow)
            | (true, _), (true, _) -> ()
            | _ -> ()

        completePlannedImport operations planned (fun () ->
            linkCallsToDevicesIfNeeded store projectId flowName createdCalls operations
            applyWorkSkipActionRefs workSkipRefs createdCalls)

    [<System.Obsolete("Flow-level mermaid import 는 deprecated — 프로젝트 단위(mapToSystem)만 사용.")>]
    let mapToFlowFlat (store: DsStore) (flowId: Guid) (systemId: Guid) (graph: MermaidGraph) : ImportPlan =
        let operations = ResizeArray<ImportPlanOperation>()
        let nodeToWorkId = Dictionary<string, Guid>()

        let flatFlowName = flowNameOfFlow store flowId
        for node in graph.GlobalNodes do
            let work = Work(flatFlowName, node.Label, flowId)
            operations.Add(AddWork work)
            nodeToWorkId.[node.Id] <- work.Id

        for edge in graph.GlobalEdges do
            match nodeToWorkId.TryGetValue(edge.SourceId), nodeToWorkId.TryGetValue(edge.TargetId) with
            | (true, srcId), (true, tgtId) ->
                let arrow = ArrowBetweenWorks(systemId, srcId, tgtId, mapArrowType edge.Label)
                operations.Add(AddArrowWork arrow)
            | _ -> ()

        ImportPlan.ofSeq operations

    [<System.Obsolete("Work-level mermaid import 는 deprecated — 프로젝트 단위(mapToSystem)만 사용.")>]
    let mapToWork (store: DsStore) (workId: Guid) (projectId: Guid option) (graph: MermaidGraph) : ImportPlan =
        let operations = ResizeArray<ImportPlanOperation>()
        let planned = createPlannedCallNodes ()
        let createdCalls = ResizeArray<Call * string>()

        let flowName = flowNameOfWork store workId

        planCallsForWork planned operations workId graph.GlobalNodes graph.GlobalEdges (fun node call apiName ->
            if apiName <> "" then
                createdCalls.Add(call, node.Label))

        completePlannedImport operations planned (fun () ->
            linkCallsToDevicesIfNeeded store projectId flowName createdCalls operations)

    // ═══════════════════════════════════════════════════
    // System 3-depth: depth1 → System, depth2 → Flow, depth3 → Work, node → Call
    // ═══════════════════════════════════════════════════

    /// 확장형 — Plan + callIndex 페어 반환 (IoTag sidecar 바인딩용).
    /// `mapToSystem` 는 이 함수를 호출하고 Plan 만 꺼내는 facade.
    let mapToSystemEx (store: DsStore) (projectId: Guid) (graph: MermaidGraph) : SystemImportResult =
        let operations = ResizeArray<ImportPlanOperation>()
        let planned = createPlannedCallNodes ()
        let subgraphToWorkId = Dictionary<string, Guid>()
        /// (Call * callLabel * flowName)
        let activeCreatedCalls = ResizeArray<Call * string * string>()
        let workSkipRefs = ResizeArray<Work * string list>()
        // IoTag sidecar 매칭용 callPath → CallId 인덱스
        // callPath = `<SystemName>.<FlowName>.<WorkName>.<DeviceAlias>.<ApiName>` (escape-aware)
        let callIndex = Dictionary<string, Guid>(StringComparer.Ordinal)
        let recordCall (systemName: string) (flowName: string) (workName: string) (callLabel: string) (callId: Guid) =
            // callLabel = "Device.Api" (또는 "Device.Api<br>코멘트") — splitCallName 결과의 device.api 형식 복원
            let head = callLabel.Split([| "<br>" |], 2, StringSplitOptions.None).[0].Trim()
            let path = IoTagBinder.joinSegments [ systemName; flowName; workName; head ]
            // 첫 등록만 유지 (중복 라벨이면 첫 Call 에 바인딩)
            if not (callIndex.ContainsKey path) then callIndex.[path] <- callId

        // ─────────────────────────────────────────────────────────────────
        // 구조 자동 감지 — ai-core.md §1 §3.1: System 은 mermaid 에 subgraph 로 그리지 않음
        // (ID prefix `Main_` 로만 표현). 따라서 표준 mermaid 는 2-depth (Flow → Work → Call).
        //
        // 감지 기준:
        //   - 2-depth: top subgraph 의 모든 children 이 leaf (= grandchildren 없음)
        //              → 단일 암묵적 "Main" Active System 생성, top = Flow, child = Work, node = Call
        //   - 3-depth: top 의 children 중 또 자식 subgraph 가 있음 → top = System (기존 로직)
        // ─────────────────────────────────────────────────────────────────
        let isImplicitSystem =
            not graph.Subgraphs.IsEmpty &&
            graph.Subgraphs |> List.forall (fun topSg ->
                topSg.Children |> List.forall (fun child -> child.Children.IsEmpty))

        // implicit System 모드일 때 Work → System.Id 직접 조회용 캐시
        let workToSystemId = Dictionary<Guid, Guid>()

        if isImplicitSystem then
            // 2-depth: 단일 implicit Active System + top → Flow + child → Work + node → Call.
            let system = DsSystem(Model.DefaultSystemName)
            operations.Add(AddSystem system)
            operations.Add(LinkSystemToProject(projectId, system.Id, true))  // Active

            for flowSg in graph.Subgraphs do
                let flowDisplayName = subgraphName flowSg
                let flow = Flow(flowDisplayName, system.Id)
                operations.Add(AddFlow flow)

                for workSg in flowSg.Children do
                    let work = Work(flowDisplayName, subgraphName workSg, flow.Id)
                    operations.Add(AddWork work)
                    subgraphToWorkId.[workSg.Id] <- work.Id
                    workToSystemId.[work.Id] <- system.Id    // Work → System 즉시 캐시
                    if not workSg.SkipActionConditionRefs.IsEmpty then
                        workSkipRefs.Add(work, workSg.SkipActionConditionRefs)

                    planCallsForWork planned operations work.Id workSg.Nodes workSg.InternalEdges (fun node call apiName ->
                        recordCall Model.DefaultSystemName flowDisplayName (subgraphName workSg) node.Label call.Id
                        if apiName <> "" then
                            activeCreatedCalls.Add(call, node.Label, flowDisplayName))

                // Flow 내부 Work 간 화살표 (group / startReset 등)
                for edge in flowSg.InternalEdges do
                    match subgraphToWorkId.TryGetValue(edge.SourceId), subgraphToWorkId.TryGetValue(edge.TargetId) with
                    | (true, srcWorkId), (true, tgtWorkId) when srcWorkId <> tgtWorkId ->
                        let arrow = ArrowBetweenWorks(system.Id, srcWorkId, tgtWorkId, mapArrowType edge.Label)
                        operations.Add(AddArrowWork arrow)
                    | _ -> ()
        else
            // 3-depth: 기존 로직 (graph.Subgraphs 각각이 System)
            for systemSg in graph.Subgraphs do
                // depth 1 → System (Active or Passive)
                let system = DsSystem(subgraphName systemSg)
                operations.Add(AddSystem system)
                operations.Add(LinkSystemToProject(projectId, system.Id, not systemSg.IsPassive))

                for flowSg in systemSg.Children do
                    // depth 2 → Flow
                    let flowDisplayName = subgraphName flowSg
                    let flow = Flow(flowDisplayName, system.Id)
                    operations.Add(AddFlow flow)

                    for workSg in flowSg.Children do
                        // depth 3 → Work
                        let work = Work(flowDisplayName, subgraphName workSg, flow.Id)
                        operations.Add(AddWork work)
                        subgraphToWorkId.[workSg.Id] <- work.Id
                        workToSystemId.[work.Id] <- system.Id
                        if not workSg.SkipActionConditionRefs.IsEmpty then
                            workSkipRefs.Add(work, workSg.SkipActionConditionRefs)

                        planCallsForWork planned operations work.Id workSg.Nodes workSg.InternalEdges (fun node call apiName ->
                            recordCall (subgraphName systemSg) flowDisplayName (subgraphName workSg) node.Label call.Id
                            if apiName <> "" && not systemSg.IsPassive then
                                activeCreatedCalls.Add(call, node.Label, flowDisplayName))

        // GlobalEdges → ArrowBetweenWorks (subgraph ID = Work ID)
        // Cross-Flow Work 간 화살표 (예: Main_Flow1_Loading -->|startReset| Main_Flow2_Loading)
        for edge in graph.GlobalEdges do
            match subgraphToWorkId.TryGetValue(edge.SourceId), subgraphToWorkId.TryGetValue(edge.TargetId) with
            | (true, srcWorkId), (true, tgtWorkId) when srcWorkId <> tgtWorkId ->
                // System ID 조회 — 1차: 캐시 (Add operation 이 아직 store 에 반영되지 않은 시점 대응),
                //                   2차 폴백: store query (기존 호환성)
                let systemIdOpt =
                    match workToSystemId.TryGetValue(srcWorkId) with
                    | true, sid -> Some sid
                    | _ -> Queries.trySystemIdOfWork srcWorkId store
                match systemIdOpt with
                | Some systemId ->
                    let arrow = ArrowBetweenWorks(systemId, srcWorkId, tgtWorkId, mapArrowType edge.Label)
                    operations.Add(AddArrowWork arrow)
                | None -> ()
            | _ -> ()

        let plan =
            completePlannedImport operations planned (fun () ->
                linkCallsToDevicesByFlow store projectId activeCreatedCalls operations
                let createdCalls =
                    activeCreatedCalls |> Seq.map (fun (call, label, _) -> call, label)
                applyWorkSkipActionRefs workSkipRefs createdCalls)

        { Plan = plan
          CallIndex = (callIndex :> IReadOnlyDictionary<string, Guid>) }

    /// Facade — Plan 만 필요한 호출자용 (Importer.buildImportPlan 등).
    let mapToSystem (store: DsStore) (projectId: Guid) (graph: MermaidGraph) : ImportPlan =
        (mapToSystemEx store projectId graph).Plan

    // ═══════════════════════════════════════════════════
    // 프리뷰 생성 (store 변경 없이)
    // ═══════════════════════════════════════════════════

    let buildPreview (graph: MermaidGraph) (level: ImportLevel) : ImportPreview =
        let ignored = ResizeArray<string * string>()
        let warnings = ResizeArray<string>()

        match level with
        | SystemLevel ->
            let rec collectWorkNames (sg: MermaidSubgraph) =
                if sg.Children.IsEmpty then [subgraphName sg]
                else sg.Children |> List.collect collectWorkNames
            let rec collectCallNames (sg: MermaidSubgraph) =
                let direct = sg.Nodes |> List.map (fun n -> n.Label)
                let fromChildren = sg.Children |> List.collect collectCallNames
                direct @ fromChildren
            let rec collectArrowCallsCount (sg: MermaidSubgraph) =
                sg.InternalEdges.Length + (sg.Children |> List.sumBy collectArrowCallsCount)
            let flowNames =
                graph.Subgraphs
                |> List.collect (fun sys -> sys.Children |> List.map subgraphName)
            let workNames = graph.Subgraphs |> List.collect (fun sys -> sys.Children |> List.collect collectWorkNames)
            let callNames = graph.Subgraphs |> List.collect collectCallNames
            let arrowCallsCount = graph.Subgraphs |> List.sumBy collectArrowCallsCount
            createPreview
                SystemLevel
                flowNames
                workNames
                callNames
                graph.GlobalEdges.Length
                arrowCallsCount
                ignored
                warnings

        | FlowLevel when not graph.Subgraphs.IsEmpty ->
            // 2-depth
            let workNames = graph.Subgraphs |> List.map subgraphName
            let callNames = graph.Subgraphs |> List.collect (fun sg -> sg.Nodes |> List.map (fun n -> n.Label))
            let arrowCallsCount = graph.Subgraphs |> List.sumBy (fun sg -> sg.InternalEdges.Length)
            let workPairs = HashSet<string * string>()
            let mutable arrowWorksCount = 0
            for edge in graph.GlobalEdges do
                if workPairs.Add(edge.SourceId, edge.TargetId) then
                    arrowWorksCount <- arrowWorksCount + 1
            createPreview
                FlowLevel
                []
                workNames
                callNames
                arrowWorksCount
                arrowCallsCount
                ignored
                warnings

        | FlowLevel ->
            // 1-depth: GlobalNodes → Work
            let workNames = graph.GlobalNodes |> List.map (fun n -> n.Label)
            let arrowWorksCount = graph.GlobalEdges.Length
            createPreview
                FlowLevel
                []
                workNames
                []
                arrowWorksCount
                0
                ignored
                warnings

        | WorkLevel ->
            // 1-depth: GlobalNodes → Call
            let callNames = graph.GlobalNodes |> List.map (fun n -> n.Label)
            let arrowCallsCount = graph.GlobalEdges.Length
            createPreview
                WorkLevel
                []
                []
                callNames
                0
                arrowCallsCount
                ignored
                warnings
