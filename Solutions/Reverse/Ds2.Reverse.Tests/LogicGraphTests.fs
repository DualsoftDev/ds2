module Ds2.Reverse.Tests.LogicGraphTests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``simple AND — both inputs strength 1.0`` () =
    // C = A AND B
    let rungs = [ { Output = "C"; Expr = LAnd [ LVar "A"; LVar "B" ] } ]
    let strengths = LogicGraph.inputStrengths rungs 5 "C"
    Assert.Equal(1.0, Map.find "A" strengths)
    Assert.Equal(1.0, Map.find "B" strengths)

[<Fact>]
let ``simple OR — both inputs strength 0.5`` () =
    let rungs = [ { Output = "C"; Expr = LOr [ LVar "A"; LVar "B" ] } ]
    let strengths = LogicGraph.inputStrengths rungs 5 "C"
    Assert.Equal(0.5, Map.find "A" strengths)
    Assert.Equal(0.5, Map.find "B" strengths)

[<Fact>]
let ``recursive AND — multi-level keeps 1.0`` () =
    // B = A AND X; C = B AND Y → C 는 A, X, Y 모두 1.0
    let rungs = [
        { Output = "B"; Expr = LAnd [ LVar "A"; LVar "X" ] }
        { Output = "C"; Expr = LAnd [ LVar "B"; LVar "Y" ] }
    ]
    let strengths = LogicGraph.inputStrengths rungs 5 "C"
    Assert.Equal(1.0, Map.find "A" strengths)
    Assert.Equal(1.0, Map.find "X" strengths)
    Assert.Equal(1.0, Map.find "Y" strengths)

[<Fact>]
let ``AND of OR — strength 0.5 for OR branches`` () =
    // E = (A AND B) OR (C AND D) → each input 0.5 (OR 2 branches)
    let rungs = [
        { Output = "E"; Expr = LOr [
            LAnd [ LVar "A"; LVar "B" ]
            LAnd [ LVar "C"; LVar "D" ] ] }
    ]
    let strengths = LogicGraph.inputStrengths rungs 5 "E"
    Assert.Equal(0.5, Map.find "A" strengths)
    Assert.Equal(0.5, Map.find "B" strengths)
    Assert.Equal(0.5, Map.find "C" strengths)
    Assert.Equal(0.5, Map.find "D" strengths)

[<Fact>]
let ``NOT does not affect strength`` () =
    // P = A AND NOT B → both 1.0
    let rungs = [ { Output = "P"; Expr = LAnd [ LVar "A"; LNot (LVar "B") ] } ]
    let strengths = LogicGraph.inputStrengths rungs 5 "P"
    Assert.Equal(1.0, Map.find "A" strengths)
    Assert.Equal(1.0, Map.find "B" strengths)

[<Fact>]
let ``cycle in rungs — no infinite recursion`` () =
    // B = A AND C; C = B AND D (cycle: B depends on C, C depends on B)
    let rungs = [
        { Output = "B"; Expr = LAnd [ LVar "A"; LVar "C" ] }
        { Output = "C"; Expr = LAnd [ LVar "B"; LVar "D" ] }
    ]
    // Should not stack overflow
    let strengths = LogicGraph.inputStrengths rungs 5 "C"
    Assert.True(strengths.Count >= 1)

[<Fact>]
let ``extractCandidates — strength filter works`` () =
    // 5-way OR — each input 0.2 strength
    let rungs = [
        { Output = "Q"; Expr = LOr [
            LVar "A"; LVar "B"; LVar "C"; LVar "D"; LVar "E" ] }
    ]
    // threshold 0.3 → 모두 제외
    let cands = LogicGraph.extractCandidates rungs 5 0.3
    Assert.Empty cands
    // threshold 0.1 → 5 개 모두 포함
    let cands2 = LogicGraph.extractCandidates rungs 5 0.1
    Assert.Equal(5, List.length cands2)

[<Fact>]
let ``LogicModels — all S scenarios pass causation`` () =
    let summary, _results = BenchRunner.runAll LogicModels.all CausationConfig.defaults 20260519 60
    printfn "%s" (BenchRunner.formatSummary summary)
    Assert.True(summary.AvgF1 >= 0.85,
        sprintf "expected avgF1 >= 0.85; got %.4f (perfect %d/%d)"
            summary.AvgF1 summary.Perfect summary.Total)
