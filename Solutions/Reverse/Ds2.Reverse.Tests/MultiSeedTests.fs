/// 여러 random seed 에서 시나리오 robustness 검증.
module Ds2.Reverse.Tests.MultiSeedTests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

let private allPhaseScenarios () =
    Phase1Models.all @
    Phase2Models.all @
    Phase3Models.all @
    Phase4Models.all @
    Phase5Models.all

[<Fact>]
let ``Multi-seed robustness: 42 scenarios across 5 seeds avgF1 >= 0.85`` () =
    let seeds = [ 1; 42; 314; 12345; 999999 ]
    let scenarios = allPhaseScenarios ()
    let allF1s =
        [ for seed in seeds do
            let summary, _ = BenchRunner.runAll scenarios CausationConfig.defaults seed 60
            yield seed, summary.AvgF1 ]
    let perSeedMin = allF1s |> List.map snd |> List.min
    let perSeedAvg = allF1s |> List.averageBy snd
    printfn "seeds=%A" allF1s
    Assert.True(perSeedMin >= 0.80,
        sprintf "min F1 across seeds=%.4f, expected >= 0.80; per-seed=%A"
            perSeedMin allF1s)
    Assert.True(perSeedAvg >= 0.85,
        sprintf "avg F1 across seeds=%.4f, expected >= 0.85" perSeedAvg)

[<Fact>]
let ``Multi-seed: F1 std across seeds < 0.10 (stable)`` () =
    let seeds = [ 1; 42; 314; 12345; 999999 ]
    let scenarios = allPhaseScenarios ()
    let f1s =
        [| for seed in seeds ->
            let summary, _ = BenchRunner.runAll scenarios CausationConfig.defaults seed 60
            summary.AvgF1 |]
    let mean = Array.average f1s
    let std = sqrt (Array.averageBy (fun x -> (x - mean) ** 2.0) f1s)
    Assert.True(std < 0.10,
        sprintf "F1 std across seeds=%.4f, expected <0.10 (means scenarios are deterministic-ish); values=%A"
            std f1s)

[<Fact>]
let ``Multi-cycle count: 28 scenarios with N=20/40/60/100 stable`` () =
    let scenarios =
        Phase1Models.all @ Phase2Models.all @ Phase3Models.all
    let cycleCounts = [ 20; 40; 60; 100 ]
    let f1s =
        [| for n in cycleCounts ->
            let summary, _ = BenchRunner.runAll scenarios CausationConfig.defaults 42 n
            n, summary.AvgF1 |]
    printfn "cycle counts → F1: %A" f1s
    let f1Values = f1s |> Array.map snd
    let mean = Array.average f1Values
    let std = sqrt (Array.averageBy (fun x -> (x - mean) ** 2.0) f1Values)
    Assert.True(std < 0.10,
        sprintf "F1 std across cycle counts=%.4f, expected <0.10 — algorithm should not depend heavily on N cycles"
            std)
