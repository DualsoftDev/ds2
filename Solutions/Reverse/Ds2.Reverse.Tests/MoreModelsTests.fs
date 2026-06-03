module Ds2.Reverse.Tests.MoreModelsTests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``MoreModels (D1-D5) diagnostic`` () =
    let summary, _results = BenchRunner.runAll MoreModels.all CausationConfig.defaults 20260519 60
    printfn "=== MoreModels category stats ==="
    for (cat, n) in MoreModels.stats () do
        printfn "  %s: %d" cat n
    printfn ""
    printfn "%s" (BenchRunner.formatSummary summary)
    // Diagnostic — 항상 통과. 어떤 시나리오가 실패하는지 본다.
    Assert.True true

[<Fact>]
let ``MoreModels — aggregate F1 >= 0.85`` () =
    let summary, _ = BenchRunner.runAll MoreModels.all CausationConfig.defaults 20260519 60
    Assert.True(summary.AvgF1 >= 0.85,
        sprintf "expected avgF1 >= 0.85; got %.4f (perfect %d/%d)"
            summary.AvgF1 summary.Perfect summary.Total)
