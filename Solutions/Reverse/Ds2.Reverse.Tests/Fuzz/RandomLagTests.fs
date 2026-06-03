/// RL. Random Lag — constant / linear / bimodal / random / cyclic lag 패턴.
module Ds2.Reverse.Tests.Fuzz.RandomLagTests

open System
open Xunit
open Ds2.Reverse.Bench
open Ds2.Reverse.Core

let private runLag (seed: int) (lagKind: LagKind) =
    let spec : ScenarioSpec = {
        Seed = seed; NCalls = 4; NCycles = 60; CycleMs = 3000L
        Topology = Chain
        LagPattern = lagKind
        JitterMs = 15; SpuriousCount = 0
    }
    let scen = RandomScenarioGen.toScenario spec
    BenchRunner.runOne scen CausationConfig.defaults seed 60

[<Fact>]
let ``RL: ConstantLag 200ms → perfect`` () =
    let r = runLag 42 (ConstantLag 200L)
    Assert.True(r.F1 >= 0.99)

[<Fact>]
let ``RL: LinearDrift (300 step 5) → drift detection 인정`` () =
    let r = runLag 42 (LinearDrift(300L, 5L))
    Assert.True(r.F1 >= 0.9, sprintf "linear drift F1=%.3f" r.F1)

[<Fact>]
let ``RL: Bimodal (200/500) → bimodal stable 인정`` () =
    let r = runLag 42 (Bimodal(200L, 500L))
    Assert.True(r.F1 >= 0.9, sprintf "bimodal F1=%.3f" r.F1)

[<Fact>]
let ``RL: CyclicDrift (mean 400 amp 100 period 8) → cyclic stable`` () =
    let r = runLag 42 (CyclicDrift(400L, 100L, 8))
    Assert.True(r.F1 >= 0.8, sprintf "cyclic drift F1=%.3f" r.F1)

[<Fact>]
let ``RL: RandomLag (200~600) — 평균 lag 안정적이면 detection 가능`` () =
    let r = runLag 42 (RandomLag(200L, 600L))
    // 매우 random 한 lag → 알고리즘이 RandomLag 도 처리할 수 있어야
    Assert.True(r.F1 >= 0.5,
        sprintf "random lag F1=%.3f (limit acknowledged)" r.F1)

[<Fact>]
let ``RL: 50 random lag combos avg F1 >= 0.75`` () =
    let rng = Random(42)
    let mutable sumF1 = 0.0
    let n = 50
    for _ in 1 .. n do
        let lagKind =
            match rng.Next(0, 4) with
            | 0 -> ConstantLag (int64 (rng.Next(100, 600)))
            | 1 -> LinearDrift (int64 (rng.Next(200, 400)), int64 (rng.Next(1, 8)))
            | 2 ->
                let l1 = int64 (rng.Next(100, 300))
                let l2 = l1 + int64 (rng.Next(200, 500))
                Bimodal(l1, l2)
            | _ -> CyclicDrift(int64 (rng.Next(300, 500)),
                              int64 (rng.Next(50, 150)),
                              rng.Next(6, 14))
        let r = runLag (rng.Next()) lagKind
        sumF1 <- sumF1 + r.F1
    let avg = sumF1 / float n
    Assert.True(avg >= 0.75, sprintf "50 random lag avg F1=%.3f < 0.75" avg)
