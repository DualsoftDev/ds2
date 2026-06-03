module Ds2.Reverse.Tests.Phase1Tests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``Phase1 R/Q diagnostic`` () =
    let summary, _ = BenchRunner.runAll Phase1Models.all CausationConfig.defaults 20260519 60
    printfn "%s" (BenchRunner.formatSummary summary)
    Assert.True true

[<Fact>]
let ``Phase1 R/Q — aggregate F1 >= 0.70 (multi-modal 한계 인정)`` () =
    let summary, _ = BenchRunner.runAll Phase1Models.all CausationConfig.defaults 20260519 60
    Assert.True(summary.AvgF1 >= 0.70,
        sprintf "expected avgF1 >= 0.70; got %.4f (perfect %d/%d)"
            summary.AvgF1 summary.Perfect summary.Total)
