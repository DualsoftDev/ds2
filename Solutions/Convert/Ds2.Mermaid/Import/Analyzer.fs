namespace Ds2.Mermaid

/// Mermaid 그래프 depth 분석 및 검증
module MermaidAnalyzer =

    /// 서브그래프의 최대 depth 계산
    let rec private maxSubgraphDepth (sg: MermaidSubgraph) : int =
        if sg.Children.IsEmpty then 1
        else 1 + (sg.Children |> List.map maxSubgraphDepth |> List.max)

    /// 재귀적으로 전체 노드 수 계산
    let rec private countAllNodes (sg: MermaidSubgraph) : int =
        sg.Nodes.Length + (sg.Children |> List.sumBy countAllNodes)

    /// MermaidGraph에서 depth 정보 분석
    let analyzeDepth (graph: MermaidGraph) : DepthInfo =
        let hasSubgraphs = not graph.Subgraphs.IsEmpty
        let hasNested = graph.Subgraphs |> List.exists (fun sg -> not sg.Children.IsEmpty)
        let maxDepth =
            if graph.Subgraphs.IsEmpty then 0
            else graph.Subgraphs |> List.map maxSubgraphDepth |> List.max
        let subgraphNodes = graph.Subgraphs |> List.sumBy countAllNodes
        {
            HasSubgraphs = hasSubgraphs
            HasNestedSubgraphs = hasNested
            MaxDepth = maxDepth
            SubgraphCount = graph.Subgraphs.Length
            TotalNodeCount = subgraphNodes + graph.GlobalNodes.Length
            GlobalEdgeCount = graph.GlobalEdges.Length
        }

    /// 주어진 depth에서 사용 가능한 ImportLevel 목록
    let availableLevels (depth: DepthInfo) : ImportLevel list =
        if depth.HasNestedSubgraphs then
            [ SystemLevel ]
        elif depth.HasSubgraphs then
            [ SystemLevel; FlowLevel ]
        else
            [ FlowLevel; WorkLevel ]

    /// 그래프 구조 검증
    let validate (graph: MermaidGraph) (level: ImportLevel) : ValidationResult =
        let errors = ResizeArray<string>()
        let depth = analyzeDepth graph

        match level with
        | SystemLevel ->
            if not depth.HasSubgraphs then
                errors.Add("System 레벨은 subgraph가 필요합니다.")
        | FlowLevel ->
            if depth.HasNestedSubgraphs then
                errors.Add("Flow 레벨은 중첩 subgraph를 지원하지 않습니다. System 레벨을 사용하세요.")
        | WorkLevel ->
            if depth.HasSubgraphs then
                errors.Add("Work 레벨은 1-depth(subgraph 없음)만 지원합니다.")

        if depth.TotalNodeCount = 0 && not depth.HasSubgraphs then
            errors.Add("임포트할 노드가 없습니다.")

        if errors.Count = 0 then MermaidValid
        else MermaidInvalid (errors |> Seq.toList)
