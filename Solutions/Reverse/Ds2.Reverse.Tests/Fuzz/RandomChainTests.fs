/// RC. Random Chain — 다양한 N, lag 조합 chain.
module Ds2.Reverse.Tests.Fuzz.RandomChainTests

open System
open Xunit
open Ds2.Reverse.Bench
open Ds2.Reverse.Core

/// Chain N 길이 + 고정 lag.
let private runRandomChain (seed: int) (n: int) (cycleMs: int64) (lag: int64) =
    let spec : ScenarioSpec = {
        Seed = seed
        NCalls = n
        NCycles = 60
        CycleMs = cycleMs
        Topology = Chain
        LagPattern = ConstantLag lag
        JitterMs = 15
        SpuriousCount = 0
    }
    let scen = RandomScenarioGen.toScenario spec
    BenchRunner.runOne scen CausationConfig.defaults seed 60

[<Fact>]
let ``RC: chain N=2 ~ 20, lag=200, 모두 perfect`` () =
    let mutable allPerfect = true
    for n in 2 .. 20 do
        let r = runRandomChain 42 n 2000L 200L
        if r.F1 < 0.999 then
            printfn "N=%d F1=%.3f TP=%d FP=%d FN=%d" n r.F1 r.TP r.FP r.FN
            allPerfect <- false
    Assert.True(allPerfect, "all chain N=2~20 should be perfect")

[<Fact>]
let ``RC: 100 random chain (N=3~10) avg F1 >= 0.9`` () =
    let rng = Random(42)
    let mutable sumF1 = 0.0
    let n = 100
    for _ in 1 .. n do
        let nCalls = rng.Next(3, 11)
        let lag = int64 (rng.Next(100, 800))
        let r = runRandomChain (rng.Next()) nCalls 2000L lag
        sumF1 <- sumF1 + r.F1
    let avg = sumF1 / float n
    Assert.True(avg >= 0.9, sprintf "avg F1=%.3f < 0.9" avg)

[<Fact>]
let ``RC: cycle 길이 변동 (1s~10s) chain N=5, perfect`` () =
    for cycleMs in [ 1000L; 2000L; 4000L; 6000L; 10000L ] do
        let r = runRandomChain 42 5 cycleMs (cycleMs / 10L)
        Assert.True(r.F1 >= 0.95,
            sprintf "cycle=%dms F1=%.3f" cycleMs r.F1)

[<Fact>]
let ``RC: 큰 jitter (50ms) chain N=4 — robust`` () =
    let spec : ScenarioSpec = {
        Seed = 99; NCalls = 4; NCycles = 60; CycleMs = 2000L
        Topology = Chain; LagPattern = ConstantLag 300L
        JitterMs = 50; SpuriousCount = 0
    }
    let scen = RandomScenarioGen.toScenario spec
    let r = BenchRunner.runOne scen CausationConfig.defaults 99 60
    Assert.True(r.F1 >= 0.8, sprintf "large jitter F1=%.3f" r.F1)

[<Fact>]
let ``RC: spurious 추가 (3 calls) — chain detection 유지`` () =
    let spec : ScenarioSpec = {
        Seed = 42; NCalls = 5; NCycles = 60; CycleMs = 3000L
        Topology = Chain; LagPattern = ConstantLag 200L
        JitterMs = 15; SpuriousCount = 3
    }
    let scen = RandomScenarioGen.toScenario spec
    let r = BenchRunner.runOne scen CausationConfig.defaults 42 60
    // truth 4 arrows (N=5 chain). 3 spurious calls 가 noise.
    Assert.True(r.Recall >= 0.9, sprintf "recall %.3f" r.Recall)
