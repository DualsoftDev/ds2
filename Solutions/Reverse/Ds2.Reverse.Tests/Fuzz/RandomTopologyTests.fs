/// RT. Random Topology — Tree / DAG / Star / Bipartite.
module Ds2.Reverse.Tests.Fuzz.RandomTopologyTests

open System
open Xunit
open Ds2.Reverse.Bench
open Ds2.Reverse.Core

let private runTopo (seed: int) (n: int) (topo: TopologyKind) =
    let spec : ScenarioSpec = {
        Seed = seed; NCalls = n; NCycles = 60; CycleMs = 3000L
        Topology = topo
        LagPattern = ConstantLag 200L
        JitterMs = 15; SpuriousCount = 0
    }
    let scen = RandomScenarioGen.toScenario spec
    BenchRunner.runOne scen CausationConfig.defaults seed 60

[<Fact>]
let ``RT: Star (N=5) — fan-out 정확`` () =
    let r = runTopo 42 5 Star
    Assert.True(r.F1 >= 0.85, sprintf "star F1=%.3f" r.F1)

[<Fact>]
let ``RT: Tree (depth 2~3, N=8) — F1 합리적`` () =
    let r = runTopo 42 8 Tree
    Assert.True(r.F1 >= 0.7, sprintf "tree F1=%.3f" r.F1)

[<Fact>]
let ``RT: DAG (sparse, N=6) — F1 합리적`` () =
    let r = runTopo 42 6 DAG
    Assert.True(r.F1 >= 0.6, sprintf "dag F1=%.3f" r.F1)

[<Fact>]
let ``RT: Bipartite (N=6, 3x3) — fan-in/out`` () =
    let r = runTopo 42 6 Bipartite
    Assert.True(r.F1 >= 0.7, sprintf "bipartite F1=%.3f" r.F1)

[<Fact>]
let ``RT: 30 random topologies — 평균 F1 >= 0.65`` () =
    let rng = Random(42)
    let topos = [| Chain; Tree; DAG; Star; Bipartite |]
    let mutable sumF1 = 0.0
    let mutable count = 0
    for _ in 1 .. 30 do
        let n = rng.Next(3, 9)
        let topo = topos.[rng.Next(0, topos.Length)]
        let r = runTopo (rng.Next()) n topo
        sumF1 <- sumF1 + r.F1
        count <- count + 1
    let avg = sumF1 / float count
    Assert.True(avg >= 0.65, sprintf "30 random topo avg F1=%.3f" avg)
