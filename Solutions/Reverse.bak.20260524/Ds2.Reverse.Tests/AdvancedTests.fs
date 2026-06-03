module Ds2.Reverse.Tests.AdvancedTests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``Advanced scenarios diagnostic`` () =
    let summary, _ = BenchRunner.runAll AdvancedModels.all CausationConfig.defaults 20260519 60
    printfn "%s" (BenchRunner.formatSummary summary)
    Assert.True true

[<Fact>]
let ``Advanced scenarios — aggregate F1 >= 0.80`` () =
    let summary, _ = BenchRunner.runAll AdvancedModels.all CausationConfig.defaults 20260519 60
    Assert.True(summary.AvgF1 >= 0.80,
        sprintf "expected avgF1 >= 0.80; got %.4f (perfect %d/%d)"
            summary.AvgF1 summary.Perfect summary.Total)
