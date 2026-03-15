namespace Ds2.Mermaid

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.UI.Core

/// Mermaid 그래프를 DsStore 엔티티로 변환하는 매퍼.
/// 모든 함수는 WithTransaction 내부에서 호출되어야 함 (TrackAdd 직접 사용).
module MermaidMapper =

    /// ArrowLabel → ds2 ArrowType 변환
    let mapArrowType (label: ArrowLabel) : ArrowType =
        match label with
        | NoLabel     -> ArrowType.Start
        | Interlock   -> ArrowType.Reset
        | SelfReset   -> ArrowType.Reset
        | StartReset  -> ArrowType.StartReset
        | StartEdge   -> ArrowType.Start
        | ResetEdge   -> ArrowType.Reset
        | AutoPre     -> ArrowType.Start
        | ResetReset  -> ArrowType.ResetReset
        | Group       -> ArrowType.Group
        | Custom _    -> ArrowType.Start

    /// Mermaid 노드 라벨에서 Call 이름 분리
    let private splitCallName (label: string) : string * string =
        match label.IndexOf('.') with
        | -1  -> ("imported", label)
        | idx -> (label.[..idx - 1], label.[idx + 1..])

    /// 노드의 조건 참조를 CallCondition으로 복원
    let private restoreConditions
        (store: DsStore) (targetCall: Call)
        (callNameToId: Dictionary<string, Guid>)
        (node: MermaidNode) =
        let addCondition (condType: CallConditionType) (sourceNames: string list) =
            if sourceNames.IsEmpty then ()
            else
                let cond = CallCondition()
                cond.Type <- Some condType
                for srcName in sourceNames do
                    match callNameToId.TryGetValue(srcName) with
                    | true, srcCallId ->
                        match store.CallsReadOnly.TryGetValue(srcCallId) with
                        | true, srcCall ->
                            // source Call에 ApiCall이 있으면 첫 번째를 사용, 없으면 생성
                            let apiCall =
                                if srcCall.ApiCalls.Count > 0 then
                                    srcCall.ApiCalls.[0]
                                else
                                    let ac = ApiCall(srcCall.Name)
                                    srcCall.ApiCalls.Add(ac)
                                    ac
                            cond.Conditions.Add(apiCall)
                        | _ -> ()
                    | _ -> ()
                if cond.Conditions.Count > 0 then
                    targetCall.CallConditions.Add(cond)

        addCondition CallConditionType.Auto   node.AutoConditionRefs
        addCondition CallConditionType.Common node.CommonConditionRefs
        addCondition CallConditionType.Active node.ActiveConditionRefs

    /// subgraph 표시 이름 결정
    let private subgraphName (sg: MermaidSubgraph) : string =
        sg.DisplayName |> Option.defaultValue sg.Id

    // ═══════════════════════════════════════════════════
    // Flow 2-depth: subgraph → Work, node → Call
    // ═══════════════════════════════════════════════════

    let mapToFlow (store: DsStore) (flowId: Guid) (systemId: Guid) (projectId: Guid option) (graph: MermaidGraph) : (string * string) list =
        let ignored = ResizeArray<string * string>()
        let nodeToCallId = Dictionary<string, Guid>()
        let nodeToWorkId = Dictionary<string, Guid>()
        let createdWorkArrows = HashSet<Guid * Guid>()
        let createdCalls = ResizeArray<Call * string>()
        let callNameToId = Dictionary<string, Guid>()
        let allNodes = ResizeArray<MermaidNode * Call>()

        let flowName =
            store.FlowsReadOnly.TryGetValue(flowId)
            |> function true, f -> f.Name | _ -> ""

        for sg in graph.Subgraphs do
            let work = Work(subgraphName sg, flowId)
            store.TrackAdd(store.Works, work)

            for node in sg.Nodes do
                let devicesAlias, apiName = splitCallName node.Label
                let call = Call(devicesAlias, apiName, work.Id)
                store.TrackAdd(store.Calls, call)
                nodeToCallId.[node.Id] <- call.Id
                nodeToWorkId.[node.Id] <- work.Id
                callNameToId.[call.Name] <- call.Id
                allNodes.Add(node, call)
                if apiName <> "" then
                    createdCalls.Add(call, node.Label)

            for edge in sg.InternalEdges do
                match nodeToCallId.TryGetValue(edge.SourceId), nodeToCallId.TryGetValue(edge.TargetId) with
                | (true, srcId), (true, tgtId) ->
                    let arrow = ArrowBetweenCalls(work.Id, srcId, tgtId, mapArrowType edge.Label)
                    store.TrackAdd(store.ArrowCalls, arrow)
                | _ -> ()

        // 조건 참조 복원 (모든 Call 생성 후)
        for node, call in allNodes do
            restoreConditions store call callNameToId node

        // GlobalEdge → ArrowBetweenWorks (Work 간 연결, 중복 방지)
        for edge in graph.GlobalEdges do
            match nodeToWorkId.TryGetValue(edge.SourceId), nodeToWorkId.TryGetValue(edge.TargetId) with
            | (true, srcWorkId), (true, tgtWorkId) when srcWorkId <> tgtWorkId ->
                if createdWorkArrows.Add(srcWorkId, tgtWorkId) then
                    let arrow = ArrowBetweenWorks(systemId, srcWorkId, tgtWorkId, mapArrowType edge.Label)
                    store.TrackAdd(store.ArrowWorks, arrow)
            | (true, _), (true, _) -> ()
            | _ ->
                ignored.Add($"{edge.SourceId} → {edge.TargetId}", "매핑할 수 없는 edge")

        // Device auto-creation
        match projectId with
        | Some pid when createdCalls.Count > 0 ->
            DirectDeviceOps.linkCallsToDevices store pid flowName (createdCalls |> Seq.toList)
        | _ -> ()

        ignored |> Seq.toList

    // ═══════════════════════════════════════════════════
    // Flow 1-depth: GlobalNode → Work, GlobalEdge → ArrowBetweenWorks
    // ═══════════════════════════════════════════════════

    let mapToFlowFlat (store: DsStore) (flowId: Guid) (systemId: Guid) (graph: MermaidGraph) : (string * string) list =
        let nodeToWorkId = Dictionary<string, Guid>()

        for node in graph.GlobalNodes do
            let work = Work(node.Label, flowId)
            store.TrackAdd(store.Works, work)
            nodeToWorkId.[node.Id] <- work.Id

        for edge in graph.GlobalEdges do
            match nodeToWorkId.TryGetValue(edge.SourceId), nodeToWorkId.TryGetValue(edge.TargetId) with
            | (true, srcId), (true, tgtId) ->
                let arrow = ArrowBetweenWorks(systemId, srcId, tgtId, mapArrowType edge.Label)
                store.TrackAdd(store.ArrowWorks, arrow)
            | _ -> ()

        []

    // ═══════════════════════════════════════════════════
    // Work 1-depth: GlobalNode → Call, GlobalEdge → ArrowBetweenCalls
    // ═══════════════════════════════════════════════════

    let mapToWork (store: DsStore) (workId: Guid) (projectId: Guid option) (graph: MermaidGraph) : (string * string) list =
        let nodeToCallId = Dictionary<string, Guid>()
        let createdCalls = ResizeArray<Call * string>()
        let callNameToId = Dictionary<string, Guid>()
        let allNodes = ResizeArray<MermaidNode * Call>()

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
            store.TrackAdd(store.Calls, call)
            nodeToCallId.[node.Id] <- call.Id
            callNameToId.[call.Name] <- call.Id
            allNodes.Add(node, call)
            if apiName <> "" then
                createdCalls.Add(call, node.Label)

        for edge in graph.GlobalEdges do
            match nodeToCallId.TryGetValue(edge.SourceId), nodeToCallId.TryGetValue(edge.TargetId) with
            | (true, srcId), (true, tgtId) ->
                let arrow = ArrowBetweenCalls(workId, srcId, tgtId, mapArrowType edge.Label)
                store.TrackAdd(store.ArrowCalls, arrow)
            | _ -> ()

        // 조건 참조 복원 (모든 Call 생성 후)
        for node, call in allNodes do
            restoreConditions store call callNameToId node

        // Device auto-creation
        match projectId with
        | Some pid when createdCalls.Count > 0 ->
            DirectDeviceOps.linkCallsToDevices store pid flowName (createdCalls |> Seq.toList)
        | _ -> ()

        []

    // ═══════════════════════════════════════════════════
    // System 3-depth: depth1 → System, depth2 → Flow, depth3 → Work, node → Call
    // ═══════════════════════════════════════════════════

    let mapToSystem (store: DsStore) (projectId: Guid) (graph: MermaidGraph) : (string * string) list =
        let ignored = ResizeArray<string * string>()
        let subgraphToWorkId = Dictionary<string, Guid>()
        /// (Call * callLabel * flowName)
        let activeCreatedCalls = ResizeArray<Call * string * string>()
        let callNameToId = Dictionary<string, Guid>()
        let allNodes = ResizeArray<MermaidNode * Call>()

        for systemSg in graph.Subgraphs do
            // depth 1 → System (Active or Passive)
            let system = DsSystem(subgraphName systemSg)
            store.TrackAdd(store.Systems, system)
            let project = store.Projects.[projectId]
            if systemSg.IsPassive then
                project.PassiveSystemIds.Add(system.Id)
            else
                project.ActiveSystemIds.Add(system.Id)

            for flowSg in systemSg.Children do
                // depth 2 → Flow
                let flowDisplayName = subgraphName flowSg
                let flow = Flow(flowDisplayName, system.Id)
                store.TrackAdd(store.Flows, flow)

                for workSg in flowSg.Children do
                    // depth 3 → Work
                    let work = Work(subgraphName workSg, flow.Id)
                    store.TrackAdd(store.Works, work)
                    subgraphToWorkId.[workSg.Id] <- work.Id

                    let nodeToCallId = Dictionary<string, Guid>()

                    for node in workSg.Nodes do
                        let devicesAlias, apiName = splitCallName node.Label
                        let call = Call(devicesAlias, apiName, work.Id)
                        store.TrackAdd(store.Calls, call)
                        nodeToCallId.[node.Id] <- call.Id
                        callNameToId.[call.Name] <- call.Id
                        allNodes.Add(node, call)
                        if apiName <> "" && not systemSg.IsPassive then
                            activeCreatedCalls.Add(call, node.Label, flowDisplayName)

                    for edge in workSg.InternalEdges do
                        match nodeToCallId.TryGetValue(edge.SourceId), nodeToCallId.TryGetValue(edge.TargetId) with
                        | (true, srcId), (true, tgtId) ->
                            let arrow = ArrowBetweenCalls(work.Id, srcId, tgtId, mapArrowType edge.Label)
                            store.TrackAdd(store.ArrowCalls, arrow)
                        | _ -> ()

        // 조건 참조 복원 (모든 Call 생성 후)
        for node, call in allNodes do
            restoreConditions store call callNameToId node

        // GlobalEdges → ArrowBetweenWorks (subgraph ID = Work ID)
        for edge in graph.GlobalEdges do
            match subgraphToWorkId.TryGetValue(edge.SourceId), subgraphToWorkId.TryGetValue(edge.TargetId) with
            | (true, srcWorkId), (true, tgtWorkId) when srcWorkId <> tgtWorkId ->
                // Work의 System을 찾아서 parentId로 사용
                match DsQuery.trySystemIdOfWork srcWorkId store with
                | Some systemId ->
                    let arrow = ArrowBetweenWorks(systemId, srcWorkId, tgtWorkId, mapArrowType edge.Label)
                    store.TrackAdd(store.ArrowWorks, arrow)
                | None -> ()
            | _ ->
                ignored.Add($"{edge.SourceId} → {edge.TargetId}", "매핑할 수 없는 edge (subgraph ID?)")

        // Device auto-creation: flowName별로 그룹화해서 linkCallsToDevices 호출
        if activeCreatedCalls.Count > 0 then
            activeCreatedCalls
            |> Seq.groupBy (fun (_, _, flowName) -> flowName)
            |> Seq.iter (fun (flowName, group) ->
                let calls = group |> Seq.map (fun (call, label, _) -> (call, label)) |> Seq.toList
                DirectDeviceOps.linkCallsToDevices store projectId flowName calls)

        ignored |> Seq.toList

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

