namespace Ds2.Mermaid

/// Mermaid 그래프 depth 분석 및 검증
module MermaidAnalyzer =

    /// MermaidGraph에서 depth 정보 분석
    let analyzeDepth (graph: MermaidGraph) : DepthInfo =
        let subgraphNodes = graph.Subgraphs |> List.sumBy (fun sg -> sg.Nodes.Length)
        {
            HasSubgraphs = not graph.Subgraphs.IsEmpty
            SubgraphCount = graph.Subgraphs.Length
            TotalNodeCount = subgraphNodes + graph.GlobalNodes.Length
            GlobalEdgeCount = graph.GlobalEdges.Length
        }

    /// 주어진 depth에서 사용 가능한 ImportLevel 목록
    let availableLevels (depth: DepthInfo) : ImportLevel list =
        if depth.HasSubgraphs then
            // 2-depth: System과 Flow만 가능 (Work는 1-depth만 지원)
            [ SystemLevel; FlowLevel ]
        else
            // 1-depth: 모두 가능
            [ SystemLevel; FlowLevel; WorkLevel ]

    /// 그래프 구조 검증
    let validate (graph: MermaidGraph) (level: ImportLevel) : ValidationResult =
        let errors = ResizeArray<string>()

        // Work 레벨은 subgraph가 있으면 거부
        if level = WorkLevel && not graph.Subgraphs.IsEmpty then
            errors.Add("Work 레벨은 1-depth(subgraph 없음)만 지원합니다.")

        // 노드가 하나도 없으면 거부
        let totalNodes =
            (graph.Subgraphs |> List.sumBy (fun sg -> sg.Nodes.Length))
            + graph.GlobalNodes.Length
        if totalNodes = 0 then
            errors.Add("임포트할 노드가 없습니다.")

        if errors.Count = 0 then Valid
        else Invalid (errors |> Seq.toList)
