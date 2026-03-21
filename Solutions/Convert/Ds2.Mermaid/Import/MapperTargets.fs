namespace Ds2.Mermaid

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Store

module internal MermaidMapperTargets =

    open MermaidMapperCommon

    // ═══════════════════════════════════════════════════
    // Flow 2-depth: subgraph → Work, node → Call
    // ═══════════════════════════════════════════════════

    let mapToFlow (store: DsStore) (flowId: Guid) (systemId: Guid) (projectId: Guid option) (graph: MermaidGraph) : ImportPlan =
        let operations = ResizeArray<ImportPlanOperation>()
        let nodeToCallId = Dictionary<string, Guid>()
        let nodeToWorkId = Dictionary<string, Guid>()
        let createdWorkArrows = HashSet<Guid * Guid>()
        let createdCalls = ResizeArray<Call * string>()
        let callNameGroups = Dictionary<string, ResizeArray<Guid>>()
        let allNodes = ResizeArray<MermaidNode * Call>()
        let callsById = Dictionary<Guid, Call>()

        let addCallName callId callName =
            match callNameGroups.TryGetValue(callName) with
            | true, ids -> ids.Add(callId)
            | _ ->
                let ids = ResizeArray<Guid>()
                ids.Add(callId)
                callNameGroups.[callName] <- ids

        let buildUniqueNameMap () =
            let map = Dictionary<string, Guid>()
            for KeyValue(name, ids) in callNameGroups do
                if ids.Count = 1 then
                    map.[name] <- ids.[0]
            map

        let flowName =
            store.FlowsReadOnly.TryGetValue(flowId)
            |> function true, f -> f.Name | _ -> ""

        for sg in graph.Subgraphs do
            let work = Work(subgraphName sg, flowId)
            operations.Add(AddWork work)

            for node in sg.Nodes do
                let devicesAlias, apiName = splitCallName node.Label
                let call = Call(devicesAlias, apiName, work.Id)
                operations.Add(AddCall call)
                nodeToCallId.[node.Id] <- call.Id
                nodeToWorkId.[node.Id] <- work.Id
                callsById.[call.Id] <- call
                addCallName call.Id call.Name
                allNodes.Add(node, call)
                if apiName <> "" then
                    createdCalls.Add(call, node.Label)

            for edge in sg.InternalEdges do
                match nodeToCallId.TryGetValue(edge.SourceId), nodeToCallId.TryGetValue(edge.TargetId) with
                | (true, srcId), (true, tgtId) ->
                    let arrow = ArrowBetweenCalls(work.Id, srcId, tgtId, mapArrowType edge.Label)
                    operations.Add(AddArrowCall arrow)
                | _ -> ()

        // 조건 참조 복원 (모든 Call 생성 후)
        let uniqueNameToCallId = buildUniqueNameMap ()
        for node, call in allNodes do
            restoreConditions
                (fun apiCall -> operations.Add(AddApiCall apiCall))
                call
                nodeToCallId
                uniqueNameToCallId
                callsById
                node

        // GlobalEdge → ArrowBetweenWorks (Work 간 연결, 중복 방지)
        for edge in graph.GlobalEdges do
            match nodeToWorkId.TryGetValue(edge.SourceId), nodeToWorkId.TryGetValue(edge.TargetId) with
            | (true, srcWorkId), (true, tgtWorkId) when srcWorkId <> tgtWorkId ->
                if createdWorkArrows.Add(srcWorkId, tgtWorkId) then
                    let arrow = ArrowBetweenWorks(systemId, srcWorkId, tgtWorkId, mapArrowType edge.Label)
                    operations.Add(AddArrowWork arrow)
            | (true, _), (true, _) -> ()
            | _ -> ()

        // Device auto-creation
        match projectId with
        | Some pid when createdCalls.Count > 0 ->
            ImportPlanDeviceOps.linkCallsToDevices store pid flowName (createdCalls |> Seq.toList) operations
        | _ -> ()

        ImportPlan.ofSeq operations

    // ═══════════════════════════════════════════════════
    // Flow 1-depth: GlobalNode → Work, GlobalEdge → ArrowBetweenWorks
    // ═══════════════════════════════════════════════════

    let mapToFlowFlat (flowId: Guid) (systemId: Guid) (graph: MermaidGraph) : ImportPlan =
        let operations = ResizeArray<ImportPlanOperation>()
        let nodeToWorkId = Dictionary<string, Guid>()

        for node in graph.GlobalNodes do
            let work = Work(node.Label, flowId)
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
        let nodeToCallId = Dictionary<string, Guid>()
        let createdCalls = ResizeArray<Call * string>()
        let callNameGroups = Dictionary<string, ResizeArray<Guid>>()
        let allNodes = ResizeArray<MermaidNode * Call>()
        let callsById = Dictionary<Guid, Call>()

        let addCallName callId callName =
            match callNameGroups.TryGetValue(callName) with
            | true, ids -> ids.Add(callId)
            | _ ->
                let ids = ResizeArray<Guid>()
                ids.Add(callId)
                callNameGroups.[callName] <- ids

        let buildUniqueNameMap () =
            let map = Dictionary<string, Guid>()
            for KeyValue(name, ids) in callNameGroups do
                if ids.Count = 1 then
                    map.[name] <- ids.[0]
            map

        let flowName =
            store.WorksReadOnly.TryGetValue(workId)
            |> function
                | true, w ->
                    store.FlowsReadOnly.TryGetValue(w.ParentId)
                    |> function true, f -> f.Name | _ -> ""
                | _ -> ""

        for node in graph.GlobalNodes do
            let devicesAlias, apiName = splitCallName node.Label
            let call = Call(devicesAlias, apiName, workId)
            operations.Add(AddCall call)
            nodeToCallId.[node.Id] <- call.Id
            callsById.[call.Id] <- call
            addCallName call.Id call.Name
            allNodes.Add(node, call)
            if apiName <> "" then
                createdCalls.Add(call, node.Label)

        for edge in graph.GlobalEdges do
            match nodeToCallId.TryGetValue(edge.SourceId), nodeToCallId.TryGetValue(edge.TargetId) with
            | (true, srcId), (true, tgtId) ->
                let arrow = ArrowBetweenCalls(workId, srcId, tgtId, mapArrowType edge.Label)
                operations.Add(AddArrowCall arrow)
            | _ -> ()

        // 조건 참조 복원 (모든 Call 생성 후)
        let uniqueNameToCallId = buildUniqueNameMap ()
        for node, call in allNodes do
            restoreConditions
                (fun apiCall -> operations.Add(AddApiCall apiCall))
                call
                nodeToCallId
                uniqueNameToCallId
                callsById
                node

        // Device auto-creation
        match projectId with
        | Some pid when createdCalls.Count > 0 ->
            ImportPlanDeviceOps.linkCallsToDevices store pid flowName (createdCalls |> Seq.toList) operations
        | _ -> ()

        ImportPlan.ofSeq operations

    // ═══════════════════════════════════════════════════
    // System 3-depth: depth1 → System, depth2 → Flow, depth3 → Work, node → Call
    // ═══════════════════════════════════════════════════

    let mapToSystem (store: DsStore) (projectId: Guid) (graph: MermaidGraph) : ImportPlan =
        let operations = ResizeArray<ImportPlanOperation>()
        let subgraphToWorkId = Dictionary<string, Guid>()
        /// (Call * callLabel * flowName)
        let activeCreatedCalls = ResizeArray<Call * string * string>()
        let nodeToCallId = Dictionary<string, Guid>()
        let callNameGroups = Dictionary<string, ResizeArray<Guid>>()
        let allNodes = ResizeArray<MermaidNode * Call>()
        let callsById = Dictionary<Guid, Call>()

        let addCallName callId callName =
            match callNameGroups.TryGetValue(callName) with
            | true, ids -> ids.Add(callId)
            | _ ->
                let ids = ResizeArray<Guid>()
                ids.Add(callId)
                callNameGroups.[callName] <- ids

        let buildUniqueNameMap () =
            let map = Dictionary<string, Guid>()
            for KeyValue(name, ids) in callNameGroups do
                if ids.Count = 1 then
                    map.[name] <- ids.[0]
            map

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
                    let work = Work(subgraphName workSg, flow.Id)
                    operations.Add(AddWork work)
                    subgraphToWorkId.[workSg.Id] <- work.Id

                    for node in workSg.Nodes do
                        let devicesAlias, apiName = splitCallName node.Label
                        let call = Call(devicesAlias, apiName, work.Id)
                        operations.Add(AddCall call)
                        nodeToCallId.[node.Id] <- call.Id
                        callsById.[call.Id] <- call
                        addCallName call.Id call.Name
                        allNodes.Add(node, call)
                        if apiName <> "" && not systemSg.IsPassive then
                            activeCreatedCalls.Add(call, node.Label, flowDisplayName)

                    for edge in workSg.InternalEdges do
                        match nodeToCallId.TryGetValue(edge.SourceId), nodeToCallId.TryGetValue(edge.TargetId) with
                        | (true, srcId), (true, tgtId) ->
                            let arrow = ArrowBetweenCalls(work.Id, srcId, tgtId, mapArrowType edge.Label)
                            operations.Add(AddArrowCall arrow)
                        | _ -> ()

        // 조건 참조 복원 (모든 Call 생성 후)
        let uniqueNameToCallId = buildUniqueNameMap ()
        for node, call in allNodes do
            restoreConditions
                (fun apiCall -> operations.Add(AddApiCall apiCall))
                call
                nodeToCallId
                uniqueNameToCallId
                callsById
                node

        // GlobalEdges → ArrowBetweenWorks (subgraph ID = Work ID)
        for edge in graph.GlobalEdges do
            match subgraphToWorkId.TryGetValue(edge.SourceId), subgraphToWorkId.TryGetValue(edge.TargetId) with
            | (true, srcWorkId), (true, tgtWorkId) when srcWorkId <> tgtWorkId ->
                // Work의 System을 찾아서 parentId로 사용
                match DsQuery.trySystemIdOfWork srcWorkId store with
                | Some systemId ->
                    let arrow = ArrowBetweenWorks(systemId, srcWorkId, tgtWorkId, mapArrowType edge.Label)
                    operations.Add(AddArrowWork arrow)
                | None -> ()
            | _ -> ()

        // Device auto-creation: flowName별로 그룹화해서 linkCallsToDevices 호출
        if activeCreatedCalls.Count > 0 then
            activeCreatedCalls
            |> Seq.groupBy (fun (_, _, flowName) -> flowName)
            |> Seq.iter (fun (flowName, group) ->
                let calls = group |> Seq.map (fun (call, label, _) -> (call, label)) |> Seq.toList
                ImportPlanDeviceOps.linkCallsToDevices store projectId flowName calls operations)

        ImportPlan.ofSeq operations

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
            {
                Level = SystemLevel
                FlowNames = flowNames
                WorkNames = workNames
                CallNames = callNames
                ArrowWorksCount = graph.GlobalEdges.Length
                ArrowCallsCount = arrowCallsCount
                IgnoredEdges = ignored |> Seq.toList
                Warnings = warnings |> Seq.toList
            }

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
            {
                Level = FlowLevel
                FlowNames = []
                WorkNames = workNames
                CallNames = callNames
                ArrowWorksCount = arrowWorksCount
                ArrowCallsCount = arrowCallsCount
                IgnoredEdges = ignored |> Seq.toList
                Warnings = warnings |> Seq.toList
            }

        | FlowLevel ->
            // 1-depth: GlobalNodes → Work
            let workNames = graph.GlobalNodes |> List.map (fun n -> n.Label)
            let arrowWorksCount = graph.GlobalEdges.Length
            {
                Level = FlowLevel
                FlowNames = []
                WorkNames = workNames
                CallNames = []
                ArrowWorksCount = arrowWorksCount
                ArrowCallsCount = 0
                IgnoredEdges = ignored |> Seq.toList
                Warnings = warnings |> Seq.toList
            }

        | WorkLevel ->
            // 1-depth: GlobalNodes → Call
            let callNames = graph.GlobalNodes |> List.map (fun n -> n.Label)
            let arrowCallsCount = graph.GlobalEdges.Length
            {
                Level = WorkLevel
                FlowNames = []
                WorkNames = []
                CallNames = callNames
                ArrowWorksCount = 0
                ArrowCallsCount = arrowCallsCount
                IgnoredEdges = ignored |> Seq.toList
                Warnings = warnings |> Seq.toList
            }
