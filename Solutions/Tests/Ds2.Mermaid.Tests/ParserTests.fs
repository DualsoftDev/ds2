module Ds2.Mermaid.Tests.ParserTests

open Xunit
open Ds2.Mermaid

[<Fact>]
let ``2-depth Mermaid 파싱 — subgraph + node + edge`` () =
    let mermaid = """
graph TD
    subgraph Flow1 ["플로우 1"]
        A["Work_A"]
        B["Work_B"]
        A --> B
    end
    subgraph Flow2 ["플로우 2"]
        C["Work_C"]
    end
    A --> C
"""
    match MermaidParser.parse mermaid with
    | Ok graph ->
        Assert.Equal(2, graph.Subgraphs.Length)
        Assert.Equal("Flow1", graph.Subgraphs.[0].Id)
        Assert.Equal(Some "플로우 1", graph.Subgraphs.[0].DisplayName)
        Assert.Equal(2, graph.Subgraphs.[0].Nodes.Length)
        Assert.Equal(1, graph.Subgraphs.[1].Nodes.Length)
        // A→B는 같은 subgraph 내부이므로 InternalEdge
        Assert.True(graph.Subgraphs.[0].InternalEdges.Length >= 1)
        // A→C는 서로 다른 subgraph이므로 GlobalEdge
        Assert.True(graph.GlobalEdges.Length >= 1)
    | Error errors ->
        Assert.Fail($"파싱 실패: {errors}")

[<Fact>]
let ``1-depth Mermaid 파싱 — subgraph + node만 (edge 없음)`` () =
    let mermaid = """
graph TD
    subgraph Main
        X["노드X"]
        Y["노드Y"]
    end
"""
    match MermaidParser.parse mermaid with
    | Ok graph ->
        Assert.Equal(1, graph.Subgraphs.Length)
        Assert.Equal(2, graph.Subgraphs.[0].Nodes.Length)
        Assert.Equal("노드X", graph.Subgraphs.[0].Nodes.[0].Label)
    | Error errors ->
        Assert.Fail($"파싱 실패: {errors}")

[<Fact>]
let ``빈 그래프 — EmptyGraph 에러`` () =
    let mermaid = """
graph TD
"""
    match MermaidParser.parse mermaid with
    | Ok _ -> Assert.Fail("빈 그래프인데 성공함")
    | Error errors ->
        Assert.Contains(errors, fun e -> e = EmptyGraph)

[<Fact>]
let ``화살표 라벨 파싱`` () =
    let mermaid = """
graph TD
    subgraph W ["Work"]
        A["Call_A"]
        B["Call_B"]
        C["Call_C"]
        A -->|interlock| B
        B -->|startReset| C
    end
"""
    match MermaidParser.parse mermaid with
    | Ok graph ->
        let edges = graph.Subgraphs.[0].InternalEdges
        Assert.True(edges.Length >= 2)
        let interlockEdge = edges |> List.find (fun e -> e.SourceId = "A" && e.TargetId = "B")
        Assert.Equal(Interlock, interlockEdge.Label)
        let startResetEdge = edges |> List.find (fun e -> e.SourceId = "B" && e.TargetId = "C")
        Assert.Equal(StartReset, startResetEdge.Label)
    | Error errors ->
        Assert.Fail($"파싱 실패: {errors}")

[<Fact>]
let ``점선 화살표 — autoPre 분류`` () =
    let mermaid = """
graph TD
    subgraph W ["Work"]
        A["Call_A"]
        B["Call_B"]
        A -.->|autoPre| B
    end
"""
    match MermaidParser.parse mermaid with
    | Ok graph ->
        Assert.True(graph.AutoPreEdges.Length >= 1)
        Assert.Equal(AutoPre, graph.AutoPreEdges.[0].Label)
    | Error errors ->
        Assert.Fail($"파싱 실패: {errors}")

[<Fact>]
let ``flowchart 키워드도 파싱 가능`` () =
    let mermaid = """
flowchart LR
    subgraph S
        A["NodeA"]
    end
"""
    match MermaidParser.parse mermaid with
    | Ok graph ->
        Assert.Equal(LR, graph.Direction)
        Assert.Equal(1, graph.Subgraphs.Length)
    | Error errors ->
        Assert.Fail($"파싱 실패: {errors}")

[<Fact>]
let ``depth 분석 — subgraph 있으면 2-depth`` () =
    let mermaid = """
graph TD
    subgraph F1
        A["NodeA"]
    end
    subgraph F2
        B["NodeB"]
    end
"""
    match MermaidParser.parse mermaid with
    | Ok graph ->
        let depth = MermaidAnalyzer.analyzeDepth graph
        Assert.True(depth.HasSubgraphs)
        Assert.Equal(2, depth.SubgraphCount)
        let levels = MermaidAnalyzer.availableLevels depth
        Assert.Equal(2, levels.Length)  // SystemLevel, FlowLevel
        Assert.Contains(SystemLevel, levels)
        Assert.Contains(FlowLevel, levels)
    | Error errors ->
        Assert.Fail($"파싱 실패: {errors}")
