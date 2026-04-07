module Ds2.Mermaid.Tests.ParserTests

open Xunit
open Ds2.Mermaid

let private parseOk (content: string) =
    match MermaidParser.parse content with
    | Ok graph -> graph
    | Error errors -> failwithf "Parse failed: %A" errors

[<Fact>]
let ``nested subgraphs preserve exact hierarchy and internal edge`` () =
    let graph =
        parseOk """
graph TD
    subgraph SYS1["System 1"]
        subgraph FLOW1["Flow 1"]
            A["WorkA"]
            B["WorkB"]
            A --> B
        end
    end
"""

    Assert.Equal(TD, graph.Direction)
    Assert.Single graph.Subgraphs |> ignore
    let system = List.exactlyOne graph.Subgraphs
    Assert.Equal("SYS1", system.Id)
    Assert.Equal(Some "System 1", system.DisplayName)
    Assert.Single system.Children |> ignore
    let flow = List.exactlyOne system.Children
    Assert.Equal("FLOW1", flow.Id)
    Assert.Equal(Some "Flow 1", flow.DisplayName)

    Assert.Equal(2, flow.Nodes.Length)
    let firstNode : MermaidNode = flow.Nodes.[0]
    let secondNode : MermaidNode = flow.Nodes.[1]
    Assert.Equal("A", firstNode.Id)
    Assert.Equal("WorkA", firstNode.Label)
    Assert.Equal("B", secondNode.Id)
    Assert.Equal("WorkB", secondNode.Label)

    Assert.Single flow.InternalEdges |> ignore
    let edge = List.exactlyOne flow.InternalEdges
    Assert.Equal("A", edge.SourceId)
    Assert.Equal("B", edge.TargetId)
    Assert.Equal(NoLabel, edge.Label)
    Assert.Empty(graph.GlobalEdges)

[<Fact>]
let ``inline node definitions register both endpoints exactly`` () =
    let graph =
        parseOk """
graph TD
    A["SV.ON"] -->|startReset| B["SV.OFF"]
"""

    Assert.Equal(2, graph.GlobalNodes.Length)
    let firstNode : MermaidNode = graph.GlobalNodes.[0]
    let secondNode : MermaidNode = graph.GlobalNodes.[1]
    Assert.Equal("A", firstNode.Id)
    Assert.Equal("SV.ON", firstNode.Label)
    Assert.Equal("B", secondNode.Id)
    Assert.Equal("SV.OFF", secondNode.Label)

    Assert.Single graph.GlobalEdges |> ignore
    let edge = List.exactlyOne graph.GlobalEdges
    Assert.Equal("A", edge.SourceId)
    Assert.Equal("B", edge.TargetId)
    Assert.Equal(StartReset, edge.Label)

[<Fact>]
let ``condition references are parsed into exact fields`` () =
    let graph =
        parseOk """
graph TD
    subgraph W["Work"]
        A["Call_A"]
        B["Call_B<br>AutoAux: Call_A"]
        C["Call_C<br>ComAux: Call_A, Call_B<br>SkipUnmatch: Call_A"]
    end
"""

    Assert.Single graph.Subgraphs |> ignore
    let work = List.exactlyOne graph.Subgraphs
    let nodeA = work.Nodes |> List.find (fun node -> node.Id = "A")
    let nodeB = work.Nodes |> List.find (fun node -> node.Id = "B")
    let nodeC = work.Nodes |> List.find (fun node -> node.Id = "C")

    Assert.Empty(nodeA.AutoAuxConditionRefs)
    Assert.Empty(nodeA.ComAuxConditionRefs)
    Assert.Empty(nodeA.SkipUnmatchConditionRefs)

    Assert.Equal(1, nodeB.AutoAuxConditionRefs.Length)
    Assert.Equal("Call_A", nodeB.AutoAuxConditionRefs.[0])
    Assert.Empty(nodeB.ComAuxConditionRefs)
    Assert.Empty(nodeB.SkipUnmatchConditionRefs)

    Assert.Empty(nodeC.AutoAuxConditionRefs)
    Assert.Equal(2, nodeC.ComAuxConditionRefs.Length)
    Assert.Equal("Call_A", nodeC.ComAuxConditionRefs.[0])
    Assert.Equal("Call_B", nodeC.ComAuxConditionRefs.[1])
    Assert.Equal(1, nodeC.SkipUnmatchConditionRefs.Length)
    Assert.Equal("Call_A", nodeC.SkipUnmatchConditionRefs.[0])

[<Fact>]
let ``passive marker applies to following top level subgraph only`` () =
    let graph =
        parseOk """
graph TD
    subgraph ACTIVE["Active"]
        A["NodeA"]
    end
    %% [Passive]
    subgraph DEVICE["Device"]
        B["NodeB"]
    end
"""

    Assert.Equal(2, graph.Subgraphs.Length)
    Assert.False(graph.Subgraphs.[0].IsPassive)
    Assert.True(graph.Subgraphs.[1].IsPassive)

[<Fact>]
let ``circle arrow parses as group edge exactly`` () =
    let graph =
        parseOk """
graph TD
    subgraph W["Work"]
        A["NodeA"]
        B["NodeB"]
        A o--o B
    end
"""

    Assert.Single graph.Subgraphs |> ignore
    let work = List.exactlyOne graph.Subgraphs
    Assert.Single work.InternalEdges |> ignore
    let edge = List.exactlyOne work.InternalEdges
    Assert.Equal("A", edge.SourceId)
    Assert.Equal("B", edge.TargetId)
    Assert.Equal(Group, edge.Label)
