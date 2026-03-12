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
        | Custom _    -> ArrowType.Start

    /// Mermaid 노드 라벨에서 Call 이름 분리
    let private splitCallName (label: string) : string * string =
        match label.IndexOf('.') with
        | -1  -> ("imported", label)
        | idx -> (label.[..idx - 1], label.[idx + 1..])

    /// subgraph 표시 이름 결정
    let private subgraphName (sg: MermaidSubgraph) : string =
        sg.DisplayName |> Option.defaultValue sg.Id

    // ═══════════════════════════════════════════════════
    // System 2-depth: subgraph → Flow, node → Work
    // ═══════════════════════════════════════════════════

    let mapToSystem (store: DsStore) (systemId: Guid) (graph: MermaidGraph) : (string * string) list =
        let ignored = ResizeArray<string * string>()

        for sg in graph.Subgraphs do
            let flow = Flow(subgraphName sg, systemId)
            store.TrackAdd(store.Flows, flow)

            let nodeToWork = Dictionary<string, Guid>()

            for node in sg.Nodes do
                let work = Work(node.Label, flow.Id)
                store.TrackAdd(store.Works, work)
                nodeToWork.[node.Id] <- work.Id

            for edge in sg.InternalEdges do
                match nodeToWork.TryGetValue(edge.SourceId), nodeToWork.TryGetValue(edge.TargetId) with
                | (true, srcId), (true, tgtId) ->
                    let arrow = ArrowBetweenWorks(systemId, srcId, tgtId, mapArrowType edge.Label)
                    store.TrackAdd(store.ArrowWorks, arrow)
                | _ -> ()

        for edge in graph.GlobalEdges do
            ignored.Add($"{edge.SourceId} → {edge.TargetId}", "Flow 간 화살표는 지원하지 않습니다")

        ignored |> Seq.toList

    // ═══════════════════════════════════════════════════
    // System 1-depth: GlobalNode → Flow, edge → 무시
    // ═══════════════════════════════════════════════════

    let mapToSystemFlat (store: DsStore) (systemId: Guid) (graph: MermaidGraph) : (string * string) list =
        let ignored = ResizeArray<string * string>()

        for node in graph.GlobalNodes do
            let flow = Flow(node.Label, systemId)
            store.TrackAdd(store.Flows, flow)

        for edge in graph.GlobalEdges do
            ignored.Add($"{edge.SourceId} → {edge.TargetId}", "Flow 간 화살표는 지원하지 않습니다")

        ignored |> Seq.toList

    // ═══════════════════════════════════════════════════
    // Flow 2-depth: subgraph → Work, node → Call
    // ═══════════════════════════════════════════════════

    let mapToFlow (store: DsStore) (flowId: Guid) (systemId: Guid) (graph: MermaidGraph) : (string * string) list =
        let ignored = ResizeArray<string * string>()
        let nodeToCallId = Dictionary<string, Guid>()
        let nodeToWorkId = Dictionary<string, Guid>()
        let createdWorkArrows = HashSet<Guid * Guid>()

        for sg in graph.Subgraphs do
            let work = Work(subgraphName sg, flowId)
            store.TrackAdd(store.Works, work)

            for node in sg.Nodes do
                let devicesAlias, apiName = splitCallName node.Label
                let call = Call(devicesAlias, apiName, work.Id)
                store.TrackAdd(store.Calls, call)
                nodeToCallId.[node.Id] <- call.Id
                nodeToWorkId.[node.Id] <- work.Id

            for edge in sg.InternalEdges do
                match nodeToCallId.TryGetValue(edge.SourceId), nodeToCallId.TryGetValue(edge.TargetId) with
                | (true, srcId), (true, tgtId) ->
                    let arrow = ArrowBetweenCalls(work.Id, srcId, tgtId, mapArrowType edge.Label)
                    store.TrackAdd(store.ArrowCalls, arrow)
                | _ -> ()

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

    let mapToWork (store: DsStore) (workId: Guid) (graph: MermaidGraph) : (string * string) list =
        let nodeToCallId = Dictionary<string, Guid>()

        for node in graph.GlobalNodes do
            let devicesAlias, apiName = splitCallName node.Label
            let call = Call(devicesAlias, apiName, workId)
            store.TrackAdd(store.Calls, call)
            nodeToCallId.[node.Id] <- call.Id

        for edge in graph.GlobalEdges do
            match nodeToCallId.TryGetValue(edge.SourceId), nodeToCallId.TryGetValue(edge.TargetId) with
            | (true, srcId), (true, tgtId) ->
                let arrow = ArrowBetweenCalls(workId, srcId, tgtId, mapArrowType edge.Label)
                store.TrackAdd(store.ArrowCalls, arrow)
            | _ -> ()

        []

    // ═══════════════════════════════════════════════════
    // 프리뷰 생성 (store 변경 없이)
    // ═══════════════════════════════════════════════════

    let buildPreview (graph: MermaidGraph) (level: ImportLevel) : ImportPreview =
        let ignored = ResizeArray<string * string>()
        let warnings = ResizeArray<string>()

        match level with
        | SystemLevel when not graph.Subgraphs.IsEmpty ->
            // 2-depth
            let flowNames = graph.Subgraphs |> List.map subgraphName
            let workNames = graph.Subgraphs |> List.collect (fun sg -> sg.Nodes |> List.map (fun n -> n.Label))
            let arrowWorksCount = graph.Subgraphs |> List.sumBy (fun sg -> sg.InternalEdges.Length)
            for edge in graph.GlobalEdges do
                ignored.Add($"{edge.SourceId} → {edge.TargetId}", "Flow 간 화살표 미지원")
            {
                Level = SystemLevel
                FlowNames = flowNames
                WorkNames = workNames
                CallNames = []
                ArrowWorksCount = arrowWorksCount
                ArrowCallsCount = 0
                IgnoredEdges = ignored |> Seq.toList
                Warnings = warnings |> Seq.toList
            }

        | SystemLevel ->
            // 1-depth: GlobalNodes → Flow
            let flowNames = graph.GlobalNodes |> List.map (fun n -> n.Label)
            for edge in graph.GlobalEdges do
                ignored.Add($"{edge.SourceId} → {edge.TargetId}", "Flow 간 화살표 미지원")
            {
                Level = SystemLevel
                FlowNames = flowNames
                WorkNames = []
                CallNames = []
                ArrowWorksCount = 0
                ArrowCallsCount = 0
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
