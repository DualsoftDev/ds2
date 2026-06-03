module Ds2.Reverse.Tests.Phase3Tests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``Phase3 G/Z diagnostic`` () =
    let summary, _ = BenchRunner.runAll Phase3Models.all CausationConfig.defaults 20260521 60
    printfn "%s" (BenchRunner.formatSummary summary)
    Assert.True true

[<Fact>]
let ``Phase3 G — Graph topology aggregate F1 >= 0.80`` () =
    let graphScenarios =
        Phase3Models.all
        |> List.filter (fun s -> s.Name.StartsWith "g")
    let summary, _ = BenchRunner.runAll graphScenarios CausationConfig.defaults 20260521 60
    Assert.True(summary.AvgF1 >= 0.80,
        sprintf "expected avgF1 >= 0.80; got %.4f (perfect %d/%d)"
            summary.AvgF1 summary.Perfect summary.Total)

[<Fact>]
let ``Phase3 Z — Adversarial false-positive rate <= 30%%`` () =
    let zScenarios =
        Phase3Models.all
        |> List.filter (fun s -> s.Name.StartsWith "z")
    let summary, _ = BenchRunner.runAll zScenarios CausationConfig.defaults 20260521 60
    // Adversarial: precision 가 핵심 — false-positive 가 적어야 함
    Assert.True(summary.AvgPrecision >= 0.70,
        sprintf "expected avgPrecision >= 0.70; got %.4f (perfect %d/%d)"
            summary.AvgPrecision summary.Perfect summary.Total)
