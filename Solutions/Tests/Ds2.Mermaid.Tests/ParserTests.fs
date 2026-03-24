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
let ``노드 라벨에서 조건 참조 추출`` () =
    let mermaid = """
graph TD
    subgraph W ["Work"]
        A["Call_A"]
        B["Call_B<br>AutoAux: Call_A"]
        C["Call_C<br>ComAux: Call_A, Call_B<br>SkipUnmatch: Call_A"]
    end
"""
    match MermaidParser.parse mermaid with
    | Ok graph ->
        let nodes = graph.Subgraphs.[0].Nodes
        // A: 조건 없음
        let nodeA = nodes |> List.find (fun n -> n.Label = "Call_A")
        Assert.Empty(nodeA.AutoAuxConditionRefs)
        Assert.Empty(nodeA.ComAuxConditionRefs)
        Assert.Empty(nodeA.SkipUnmatchConditionRefs)
        // B: Auto 조건
        let nodeB = nodes |> List.find (fun n -> n.Label = "Call_B")
        Assert.Equal(1, nodeB.AutoAuxConditionRefs.Length)
        Assert.Equal("Call_A", nodeB.AutoAuxConditionRefs.[0])
        // C: Common + Active 조건
        let nodeC = nodes |> List.find (fun n -> n.Label = "Call_C")
        Assert.Equal(2, nodeC.ComAuxConditionRefs.Length)
        Assert.Equal(1, nodeC.SkipUnmatchConditionRefs.Length)
    | Error errors ->
        Assert.Fail($"파싱 실패: {errors}")

[<Fact>]
let ``점선 화살표 — autoPre 호환 (Legacy Ev2)`` () =
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
let ``인라인 노드 정의 양쪽 파싱 - source와 target 모두 노드로 등록`` () =
    let mermaid = """
graph TD
    A["SV.ON"] --> B["SV.OFF"]
"""
    match MermaidParser.parse mermaid with
    | Ok graph ->
        Assert.Equal(2, graph.GlobalNodes.Length)
        Assert.Contains(graph.GlobalNodes, fun n -> n.Id = "A" && n.Label = "SV.ON")
        Assert.Contains(graph.GlobalNodes, fun n -> n.Id = "B" && n.Label = "SV.OFF")
        graph.GlobalEdges |> Assert.Single |> ignore
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
        Assert.Equal(2, levels.Length)  // SystemLevel + FlowLevel
        Assert.Contains(SystemLevel, levels)
        Assert.Contains(FlowLevel, levels)
    | Error errors ->
        Assert.Fail($"파싱 실패: {errors}")

[<Fact>]
let ``닫히지 않은 subgraph — 자동 종료 후 성공`` () =
    let mermaid = """
graph TD
    subgraph F1
        A["NodeA"]
"""
    match MermaidParser.parse mermaid with
    | Ok graph ->
        Assert.Equal(1, graph.Subgraphs.Length)
        Assert.Equal(1, graph.Subgraphs.[0].Nodes.Length)
    | Error _ ->
        Assert.Fail("닫히지 않은 subgraph는 자동 종료되어야 함")

[<Fact>]
let ``알 수 없는 direction — 기본값 TD 사용`` () =
    let mermaid = """
graph XX
    subgraph S
        A["NodeA"]
    end
"""
    match MermaidParser.parse mermaid with
    | Ok graph ->
        Assert.Equal(TD, graph.Direction)
    | Error _ ->
        Assert.Fail("알 수 없는 direction이면 기본값 사용해야 함")

[<Fact>]
let ``Circle 화살표 — Group 라벨`` () =
    let mermaid = """
graph TD
    subgraph S
        A["NodeA"]
        B["NodeB"]
        A o--o B
    end
"""
    match MermaidParser.parse mermaid with
    | Ok graph ->
        let edges = graph.Subgraphs.[0].InternalEdges
        Assert.True(edges.Length >= 1)
        Assert.Equal(Group, edges.[0].Label)
    | Error errors ->
        Assert.Fail($"파싱 실패: {errors}")

[<Fact>]
let ``3-depth Mermaid — 중첩 subgraph`` () =
    let mermaid = """
graph TD
    subgraph System ["시스템"]
        subgraph Flow ["플로우"]
            A["WorkA"]
            B["WorkB"]
            A --> B
        end
    end
"""
    match MermaidParser.parse mermaid with
    | Ok graph ->
        Assert.Equal(1, graph.Subgraphs.Length)
        Assert.Equal(1, graph.Subgraphs.[0].Children.Length)
        let flow = graph.Subgraphs.[0].Children.[0]
        Assert.Equal(2, flow.Nodes.Length)
    | Error errors ->
        Assert.Fail($"파싱 실패: {errors}")

[<Fact>]
let ``Passive 마커 이후 subgraph는 IsPassive`` () =
    let mermaid = """
graph TD
    subgraph Active ["Active"]
        A["NodeA"]
    end
    %% [Passive]
    subgraph Device ["Device"]
        B["NodeB"]
    end
"""
    match MermaidParser.parse mermaid with
    | Ok graph ->
        Assert.Equal(2, graph.Subgraphs.Length)
        let active = graph.Subgraphs |> List.find (fun sg -> sg.Id = "Active")
        let device = graph.Subgraphs |> List.find (fun sg -> sg.Id = "Device")
        Assert.False(active.IsPassive)
        Assert.True(device.IsPassive)
    | Error errors ->
        Assert.Fail($"파싱 실패: {errors}")
