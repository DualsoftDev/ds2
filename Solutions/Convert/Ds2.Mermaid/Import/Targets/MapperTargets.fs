namespace Ds2.Mermaid

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Store
open Ds2.Store.DsQuery

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

    let mapToFlow (store: DsStore) (flowId: Guid) (systemId: Guid) (projectId: Guid option) (graph: MermaidGraph) : ImportPlan =
        let operations = ResizeArray<ImportPlanOperation>()
        let planned = createPlannedCallNodes ()
        let nodeToWorkId = Dictionary<string, Guid>()
        let createdWorkArrows = HashSet<Guid * Guid>()
        let createdCalls = ResizeArray<Call * string>()

        let flowName = flowNameOfFlow store flowId

        for sg in graph.Subgraphs do
            let work = Work(flowName, subgraphName sg, flowId)
            operations.Add(AddWork work)

            planCallsForWork planned operations work.Id sg.Nodes sg.InternalEdges (fun node call apiName ->
                nodeToWorkId.[node.Id] <- work.Id
                if apiName <> "" then
                    createdCalls.Add(call, node.Label))

        // GlobalEdge → ArrowBetweenWorks (Work 간 연결, 중복 방지)
        for edge in graph.GlobalEdges do
            match nodeToWorkId.TryGetValue(edge.SourceId), nodeToWorkId.TryGetValue(edge.TargetId) with
            | (true, srcWorkId), (true, tgtWorkId) when srcWorkId <> tgtWorkId ->
                if createdWorkArrows.Add(srcWorkId, tgtWorkId) then
                    let arrow = ArrowBetweenWorks(systemId, srcWorkId, tgtWorkId, mapArrowType edge.Label)
                    operations.Add(AddArrowWork arrow)
            | (true, _), (true, _) -> ()
            | _ -> ()

        completePlannedImport operations planned (fun () ->
            linkCallsToDevicesIfNeeded store projectId flowName createdCalls operations)

    // ═══════════════════════════════════════════════════
    // Flow 1-depth: GlobalNode → Work, GlobalEdge → ArrowBetweenWorks
    // ═══════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════
    // Work 1-depth: GlobalNode → Call, GlobalEdge → ArrowBetweenCalls
    // ═══════════════════════════════════════════════════

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

    let mapToSystem (store: DsStore) (projectId: Guid) (graph: MermaidGraph) : ImportPlan =
        let operations = ResizeArray<ImportPlanOperation>()
        let planned = createPlannedCallNodes ()
        let subgraphToWorkId = Dictionary<string, Guid>()
        /// (Call * callLabel * flowName)
        let activeCreatedCalls = ResizeArray<Call * string * string>()

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

                    planCallsForWork planned operations work.Id workSg.Nodes workSg.InternalEdges (fun node call apiName ->
                        if apiName <> "" && not systemSg.IsPassive then
                            activeCreatedCalls.Add(call, node.Label, flowDisplayName))

        // GlobalEdges → ArrowBetweenWorks (subgraph ID = Work ID)
        for edge in graph.GlobalEdges do
            match subgraphToWorkId.TryGetValue(edge.SourceId), subgraphToWorkId.TryGetValue(edge.TargetId) with
            | (true, srcWorkId), (true, tgtWorkId) when srcWorkId <> tgtWorkId ->
                // Work의 System을 찾아서 parentId로 사용
                match Queries.trySystemIdOfWork srcWorkId store with
                | Some systemId ->
                    let arrow = ArrowBetweenWorks(systemId, srcWorkId, tgtWorkId, mapArrowType edge.Label)
                    operations.Add(AddArrowWork arrow)
                | None -> ()
            | _ -> ()

        completePlannedImport operations planned (fun () ->
            linkCallsToDevicesByFlow store projectId activeCreatedCalls operations)

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
