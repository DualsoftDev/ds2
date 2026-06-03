/// U-LogicGraph — boolean expression expand + strength 계산 단위 테스트.
module Ds2.Reverse.Tests.Unit.LogicGraphUnitTests

open Xunit
open Ds2.Reverse.Core

[<Fact>]
let ``extractCandidates: empty rungs → empty`` () =
    let cands = LogicGraph.extractCandidates [] 5 0.0
    Assert.Empty cands

[<Fact>]
let ``extractCandidates: 단순 LOAD (B = A) → A→B strength 1.0`` () =
    let rungs = [ { Output = "B"; Expr = LVar "A" } ]
    let cands = LogicGraph.extractCandidates rungs 5 0.0
    Assert.Equal(1, List.length cands)
    let (src, tgt, s) = List.head cands
    Assert.Equal("A", src)
    Assert.Equal("B", tgt)
    Assert.Equal(1.0, s, 3)

[<Fact>]
let ``extractCandidates: AND (B = A AND C) → 둘 다 strength 1.0`` () =
    let rungs = [ { Output = "B"; Expr = LAnd [ LVar "A"; LVar "C" ] } ]
    let cands = LogicGraph.extractCandidates rungs 5 0.0
    Assert.Equal(2, List.length cands)
    let strengths = cands |> List.map (fun (_, _, s) -> s)
    for s in strengths do Assert.Equal(1.0, s, 3)

[<Fact>]
let ``extractCandidates: OR (B = A OR C) → 각 strength 0.5`` () =
    let rungs = [ { Output = "B"; Expr = LOr [ LVar "A"; LVar "C" ] } ]
    let cands = LogicGraph.extractCandidates rungs 5 0.0
    Assert.Equal(2, List.length cands)
    let strengths = cands |> List.map (fun (_, _, s) -> s)
    for s in strengths do Assert.Equal(0.5, s, 3)

[<Fact>]
let ``extractCandidates: NOT 는 strength 불변`` () =
    let rungs = [ { Output = "B"; Expr = LNot (LVar "A") } ]
    let cands = LogicGraph.extractCandidates rungs 5 0.0
    Assert.Equal(1, List.length cands)
    let (_, _, s) = List.head cands
    Assert.Equal(1.0, s, 3)

[<Fact>]
let ``extractCandidates: 재귀 expand (B = A AND C, A = X AND Y)`` () =
    let rungs = [
        { Output = "B"; Expr = LAnd [ LVar "A"; LVar "C" ] }
        { Output = "A"; Expr = LAnd [ LVar "X"; LVar "Y" ] }
    ]
    let cands = LogicGraph.extractCandidates rungs 5 0.0
    // expanded: B 의 inputs = {A→B, X→B, Y→B, C→B}, A 의 inputs = {X→A, Y→A}
    // 모두 strength 1.0 (AND chain)
    Assert.True(List.length cands >= 4, sprintf "expected ≥4 candidates; got %d" (List.length cands))
    let cands_B = cands |> List.filter (fun (_, t, _) -> t = "B")
    let names_B = cands_B |> List.map (fun (s, _, _) -> s) |> Set.ofList
    Assert.True(Set.contains "X" names_B)
    Assert.True(Set.contains "Y" names_B)

[<Fact>]
let ``extractCandidates: strength threshold 적용`` () =
    let rungs = [ { Output = "B"; Expr = LOr [ LVar "A"; LVar "C"; LVar "D"; LVar "E" ] } ]
    // 각 input strength = 0.25 (OR with 4 children)
    let cands = LogicGraph.extractCandidates rungs 5 0.3   // threshold 0.3
    Assert.Empty cands   // 0.25 < 0.3 모두 제외

[<Fact>]
let ``extractCandidates: self-loop 제외`` () =
    let rungs = [ { Output = "A"; Expr = LAnd [ LVar "A"; LVar "B" ] } ]
    let cands = LogicGraph.extractCandidates rungs 5 0.0
    let selfLoops = cands |> List.filter (fun (s, t, _) -> s = t)
    Assert.Empty selfLoops

[<Fact>]
let ``extractCandidates: cycle 방지 (A=B AND C, B=A)`` () =
    let rungs = [
        { Output = "A"; Expr = LAnd [ LVar "B"; LVar "C" ] }
        { Output = "B"; Expr = LVar "A" }
    ]
    // visited set 으로 무한 재귀 방지
    let cands = LogicGraph.extractCandidates rungs 5 0.0
    Assert.True(List.length cands >= 2)   // 적어도 B→A, C→A
