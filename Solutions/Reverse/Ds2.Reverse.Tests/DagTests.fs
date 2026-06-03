module Ds2.Reverse.Tests.DagTests

open Xunit
open Ds2.Reverse.Core

let mkScore suff necc cv =
    { NA = 10; NB = 10
      Sufficiency = suff; Necessity = necc
      LagMean = 100.0; LagStd = 10.0; LagCv = cv
      AbsLagMean = 100.0
      IsParallel = false
      PassesSeq = true; PassesGrp = false
      Reason = None }

[<Fact>]
let ``topoBreakCycle — no cycle keeps all edges`` () =
    let nodes = ["A"; "B"; "C"; "D"]
    let edges = [
        "A", "B", mkScore 1.0 1.0 0.1
        "B", "C", mkScore 1.0 1.0 0.1
        "C", "D", mkScore 1.0 1.0 0.1
    ]
    let kept, removed = DagEnforcement.topoBreakCycle edges nodes
    Assert.Equal(3, List.length kept)
    Assert.Equal(0, List.length removed)

[<Fact>]
let ``topoBreakCycle — removes weakest edge in cycle`` () =
    let nodes = ["A"; "B"; "C"]
    // A→B, B→C, C→A (cycle) — C→A 가 가장 약함
    let edges = [
        "A", "B", mkScore 1.0 1.0 0.05
        "B", "C", mkScore 1.0 1.0 0.05
        "C", "A", mkScore 0.5 0.5 0.50    // 약함
    ]
    let kept, removed = DagEnforcement.topoBreakCycle edges nodes
    Assert.Equal(2, List.length kept)
    Assert.Equal(1, List.length removed)
    Assert.Equal(("C", "A"), removed |> List.head |> fun (s, t, _) -> s, t)

[<Fact>]
let ``transitiveReduction — removes A→C if A→B→C exists`` () =
    let edges = [
        "A", "B", mkScore 1.0 1.0 0.1
        "B", "C", mkScore 1.0 1.0 0.1
        "A", "C", mkScore 1.0 1.0 0.1   // transitive
    ]
    let kept, removed = DagEnforcement.transitiveReduction edges Set.empty
    Assert.Equal(2, List.length kept)
    Assert.Equal(1, List.length removed)
    Assert.Equal(("A", "C"), removed |> List.head |> fun (s, t, _) -> s, t)

[<Fact>]
let ``transitiveReduction — minimal DAG keeps all`` () =
    let edges = [
        "A", "B", mkScore 1.0 1.0 0.1
        "B", "C", mkScore 1.0 1.0 0.1
        "B", "D", mkScore 1.0 1.0 0.1
    ]
    let kept, removed = DagEnforcement.transitiveReduction edges Set.empty
    Assert.Equal(3, List.length kept)
    Assert.Equal(0, List.length removed)
