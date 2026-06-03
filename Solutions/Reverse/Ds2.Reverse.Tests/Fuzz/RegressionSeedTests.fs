/// RS. Regression Seeds — 알려진 "good" seeds 재실행 + Fuzz 발견 시 추가.
module Ds2.Reverse.Tests.Fuzz.RegressionSeedTests

open Xunit
open Ds2.Reverse.Bench
open Ds2.Reverse.Core

/// 회귀용 고정 seed 의 chain scenario.
let private mkChain (seed: int) (n: int) (lag: int64) : ScenarioSpec =
    {
        Seed = seed; NCalls = n; NCycles = 60; CycleMs = 3000L
        Topology = Chain; LagPattern = ConstantLag lag
        JitterMs = 15; SpuriousCount = 0
    }

[<Fact>]
let ``RS: chain seed 1/42/12345/999999 — 모두 perfect`` () =
    for seed in [ 1; 42; 12345; 999999 ] do
        let scen = RandomScenarioGen.toScenario (mkChain seed 5 200L)
        let r = BenchRunner.runOne scen CausationConfig.defaults seed 60
        Assert.True(r.F1 >= 0.99,
            sprintf "seed=%d F1=%.3f (expected ≥0.99)" seed r.F1)

[<Fact>]
let ``RS: 다양한 N (3, 5, 8, 15) — perfect`` () =
    for n in [ 3; 5; 8; 15 ] do
        let scen = RandomScenarioGen.toScenario (mkChain 42 n 200L)
        let r = BenchRunner.runOne scen CausationConfig.defaults 42 60
        Assert.True(r.F1 >= 0.99,
            sprintf "N=%d F1=%.3f" n r.F1)

[<Fact>]
let ``RS: Topology 확장 — Star/Tree/Bipartite seed 42 stable`` () =
    let mkSpec topo =
        {
            Seed = 42; NCalls = 5; NCycles = 60; CycleMs = 3000L
            Topology = topo; LagPattern = ConstantLag 200L
            JitterMs = 15; SpuriousCount = 0
        }
    for topo in [ Star; Tree; Bipartite ] do
        let scen = RandomScenarioGen.toScenario (mkSpec topo)
        let r = BenchRunner.runOne scen CausationConfig.defaults 42 60
        Assert.True(r.F1 >= 0.7,
            sprintf "%A F1=%.3f" topo r.F1)

[<Fact>]
let ``RS: 알려진 lag 패턴 모두 detection (Constant/Linear/Bimodal/Cyclic)`` () =
    let lags = [
        ConstantLag 250L
        LinearDrift(300L, 3L)
        Bimodal(200L, 500L)
        CyclicDrift(400L, 100L, 10)
    ]
    for lag in lags do
        let spec : ScenarioSpec = {
            Seed = 42; NCalls = 3; NCycles = 60; CycleMs = 3000L
            Topology = Chain; LagPattern = lag
            JitterMs = 15; SpuriousCount = 0
        }
        let scen = RandomScenarioGen.toScenario spec
        let r = BenchRunner.runOne scen CausationConfig.defaults 42 60
        Assert.True(r.F1 >= 0.8,
            sprintf "lag=%A F1=%.3f" lag r.F1)
