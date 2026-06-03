module Ds2.Reverse.Tests.HybridTests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``Hybrid scenarios — all perfect`` () =
    let summary, _ = BenchRunner.runAll HybridModels.all CausationConfig.defaults 20260519 60
    printfn "%s" (BenchRunner.formatSummary summary)
    Assert.True(summary.AvgF1 >= 0.95,
        sprintf "expected avgF1 >= 0.95; got %.4f (perfect %d/%d)"
            summary.AvgF1 summary.Perfect summary.Total)
