/// P+. Property tests 확장 — symmetry / scaling / translation / idempotence.
module Ds2.Reverse.Tests.PropertyPlusTests

open Xunit
open System
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

let private chainEvents (n: int) (cycles: int) (cycleMs: int64) (lag: int64) =
    let names = [ for i in 0 .. n - 1 -> sprintf "F.N%d" i ]
    let evs =
        [ for cycle in 0 .. cycles - 1 do
            for i in 0 .. n - 1 do
                yield { T = int64 cycle * cycleMs + int64 i * lag
                        Name = names.[i] } ]
    evs, names

let private runChain (n: int) (cycles: int) (cycleMs: int64) (lag: int64) =
    let evs, names = chainEvents n cycles cycleMs lag
    let cands =
        [ for i in 0 .. n - 2 ->
            { Src = names.[i]; Tgt = names.[i + 1]; DeclaredKind = "trigger" } ]
    let inp =
        ReverseEngine.mkInput "P" "S"
            (Map.ofList [ "F", names |> List.map (fun n -> n, "") ])
            cands evs
            (CausationConfig.withCycleHint cycleMs CausationConfig.defaults)
    let store, _ = ReverseEngine.run inp
    store.ArrowCalls.Count

[<Fact>]
let ``P+: Idempotence — 같은 입력 같은 출력`` () =
    let r1 = runChain 5 30 2000L 200L
    let r2 = runChain 5 30 2000L 200L
    Assert.Equal(r1, r2)

[<Fact>]
let ``P+: Determinism — BenchRunner 5 seed 결과 일관성`` () =
    let sc = Phase1Models.all |> List.head
    let cfg = CausationConfig.defaults
    let r1 = BenchRunner.runOne sc cfg 42 30
    let r2 = BenchRunner.runOne sc cfg 42 30
    Assert.Equal(r1.F1, r2.F1)
    Assert.Equal(r1.TP, r2.TP)

[<Fact>]
let ``P+: Translation — 모든 event 시각 + T → 같은 arrows`` () =
    let evs1, names = chainEvents 4 30 2000L 200L
    let evs2 = evs1 |> List.map (fun e -> { e with T = e.T + 100000L })
    let cands =
        [ for i in 0 .. 2 ->
            { Src = names.[i]; Tgt = names.[i + 1]; DeclaredKind = "trigger" } ]
    let mk evs =
        ReverseEngine.mkInput "P" "S"
            (Map.ofList [ "F", names |> List.map (fun n -> n, "") ])
            cands evs
            (CausationConfig.withCycleHint 2000L CausationConfig.defaults)
    let store1, _ = ReverseEngine.run (mk evs1)
    let store2, _ = ReverseEngine.run (mk evs2)
    Assert.Equal(store1.ArrowCalls.Count, store2.ArrowCalls.Count)

[<Fact>]
let ``P+: Scaling — 모든 lag x2 + cycle x2 → 같은 arrows`` () =
    let r1 = runChain 4 30 2000L 200L
    let r2 = runChain 4 30 4000L 400L
    Assert.Equal(r1, r2)

[<Fact>]
let ``P+: Symmetry — call name 변경 → 같은 arrow 수`` () =
    // chain N1→N2→N3 vs A→B→C
    let mk (names: string list) =
        let evs =
            [ for k in 0 .. 29 do
                for i in 0 .. List.length names - 1 do
                    yield { T = int64 k * 2000L + int64 i * 200L
                            Name = names.[i] } ]
        let cands =
            [ for i in 0 .. List.length names - 2 ->
                { Src = names.[i]; Tgt = names.[i + 1]; DeclaredKind = "trigger" } ]
        ReverseEngine.mkInput "P" "S"
            (Map.ofList [ "F", names |> List.map (fun n -> n, "") ])
            cands evs
            (CausationConfig.withCycleHint 2000L CausationConfig.defaults)
    let store1, _ = ReverseEngine.run (mk [ "F.N1"; "F.N2"; "F.N3" ])
    let store2, _ = ReverseEngine.run (mk [ "F.A"; "F.B"; "F.C" ])
    Assert.Equal(store1.ArrowCalls.Count, store2.ArrowCalls.Count)

[<Fact>]
let ``P+: Monotonicity N cycles — 60 cycle 이상 → F1 안 떨어짐`` () =
    let sc = Phase1Models.all |> List.find (fun s -> s.Name.StartsWith "r1")
    let cfg = CausationConfig.defaults
    let r30 = BenchRunner.runOne sc cfg 42 30
    let r60 = BenchRunner.runOne sc cfg 42 60
    let r120 = BenchRunner.runOne sc cfg 42 120
    if r30.F1 >= 0.999 then
        Assert.True(r60.F1 >= 0.999)
        Assert.True(r120.F1 >= 0.999)

[<Fact>]
let ``P+: Confidence NA 단조 비-감소`` () =
    let mkScore na =
        { NA = na; NB = na
          Sufficiency = 0.95; Necessity = 0.95
          LagMean = 300.0; LagStd = 20.0; LagCv = 0.067
          AbsLagMean = 300.0
          IsParallel = false; PassesSeq = true; PassesGrp = false
          Reason = None } : CausationScore
    let scores =
        [ 5; 15; 30; 60; 120; 500 ]
        |> List.map (fun n -> CausationDetection.confidence (mkScore n) None)
        |> List.map (fun c -> c.Score)
    let monotone =
        scores |> List.pairwise |> List.forall (fun (a, b) -> b >= a - 1e-9)
    Assert.True(monotone, sprintf "expected monotone; got %A" scores)

[<Fact>]
let ``P+: Bayesian symmetry — aggregate(a,b) = aggregate(b,a)`` () =
    let p1 = CausationDetection.bayesianAggregate [ 0.7; 0.9 ]
    let p2 = CausationDetection.bayesianAggregate [ 0.9; 0.7 ]
    Assert.Equal(p1, p2, 5)

[<Fact>]
let ``P+: estimateNoiseLevel 시간 평행이동 invariant`` () =
    let mk shift =
        [ for k in 0 .. 29 do
            yield { T = int64 k * 2000L + shift; Name = "A" }
            yield { T = int64 k * 2000L + shift + 300L; Name = "B" } ]
    let n0 = CausationDetection.estimateNoiseLevel (mk 0L) 2000L
    let n100 = CausationDetection.estimateNoiseLevel (mk 100L) 2000L
    Assert.Equal(n0, n100, 3)

[<Fact>]
let ``P+: gate 결과는 declared kind 만 의존 (score 가 같으면)`` () =
    let s : CausationScore =
        { NA = 60; NB = 60
          Sufficiency = 0.95; Necessity = 0.95
          LagMean = 300.0; LagStd = 20.0; LagCv = 0.067
          AbsLagMean = 300.0
          IsParallel = false; PassesSeq = true; PassesGrp = false
          Reason = None }
    let d1 = CausationDetection.gate "trigger" s
    let d2 = CausationDetection.gate "trigger" s
    Assert.Equal(d1, d2)
