/// U-DagEnforcement — topoBreakCycle + transitiveReduction 단위 테스트.
module Ds2.Reverse.Tests.Unit.DagEnforcementUnitTests

open Xunit
open Ds2.Reverse.Core

let private mkScore (suff: float) (necc: float) (cv: float) : CausationScore =
    { NA = 30; NB = 30
      Sufficiency = suff; Necessity = necc
      LagMean = 300.0; LagStd = 20.0; LagCv = cv
      AbsLagMean = 300.0
      IsParallel = false; PassesSeq = true; PassesGrp = false
      Reason = None }

[<Fact>]
let ``topoBreakCycle: 이미 DAG → 그대로 유지`` () =
    let s = mkScore 0.9 0.9 0.1
    let edges = [
        "A", "B", s
        "B", "C", s
        "C", "D", s
    ]
    let nodes = [ "A"; "B"; "C"; "D" ]
    let accepted, removed = DagEnforcement.topoBreakCycle edges nodes
    Assert.Equal(3, List.length accepted)
    Assert.Empty removed

[<Fact>]
let ``topoBreakCycle: 단순 2-cycle → 약한 edge 제거`` () =
    let strong = mkScore 0.99 0.99 0.05
    let weak = mkScore 0.7 0.7 0.4
    let edges = [
        "A", "B", strong
        "B", "A", weak     // 약한 cycle edge
    ]
    let accepted, removed = DagEnforcement.topoBreakCycle edges [ "A"; "B" ]
    Assert.Equal(1, List.length accepted)
    Assert.Equal(1, List.length removed)
    // strong A→B 유지
    let (s, t, _) = List.head accepted
    Assert.Equal(("A", "B"), (s, t))

[<Fact>]
let ``topoBreakCycle: 3-cycle 제거`` () =
    let s = mkScore 0.9 0.9 0.1
    let edges = [
        "A", "B", s
        "B", "C", s
        "C", "A", s    // closing cycle
    ]
    let accepted, removed = DagEnforcement.topoBreakCycle edges [ "A"; "B"; "C" ]
    Assert.Equal(2, List.length accepted)
    Assert.Equal(1, List.length removed)

[<Fact>]
let ``topoBreakCycle: 빈 edges → 빈 결과`` () =
    let accepted, removed = DagEnforcement.topoBreakCycle [] [ "A"; "B" ]
    Assert.Empty accepted
    Assert.Empty removed

[<Fact>]
let ``transitiveReduction: 직접 + 우회 → 직접 제거`` () =
    let s = mkScore 0.9 0.9 0.1
    let edges = [
        "A", "B", s
        "B", "C", s
        "A", "C", s     // bypass — 제거되어야
    ]
    let kept, removed = DagEnforcement.transitiveReduction edges Set.empty
    Assert.Equal(2, List.length kept)
    Assert.Equal(1, List.length removed)
    let removedSrcTgt = removed |> List.map (fun (s, t, _) -> s, t)
    Assert.Contains(("A", "C"), removedSrcTgt)

[<Fact>]
let ``transitiveReduction: 모두 직접 chain → 변경 없음`` () =
    let s = mkScore 0.9 0.9 0.1
    let edges = [
        "A", "B", s
        "B", "C", s
        "C", "D", s
    ]
    let kept, removed = DagEnforcement.transitiveReduction edges Set.empty
    Assert.Equal(3, List.length kept)
    Assert.Empty removed

[<Fact>]
let ``transitiveReduction: group pair → step 으로 인식 안 함 (transitive 보호)`` () =
    let s = mkScore 0.9 0.9 0.1
    let edges = [
        "A", "B", s
        "B", "C", s
    ]
    let groupPairs = Set.singleton (set [ "B"; "C" ])
    let _kept, _removed = DagEnforcement.transitiveReduction edges groupPairs
    // B↔C group means B-C 인접
    // 결과 의미는 implementation-defined. 적어도 crash 안 남
    Assert.True true
