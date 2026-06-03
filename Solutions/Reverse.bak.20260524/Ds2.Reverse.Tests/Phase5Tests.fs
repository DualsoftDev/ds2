module Ds2.Reverse.Tests.Phase5Tests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``Phase5 O/T diagnostic`` () =
    let summary, _ = BenchRunner.runAll Phase5Models.all CausationConfig.defaults 20260523 60
    printfn "%s" (BenchRunner.formatSummary summary)
    Assert.True true

[<Fact>]
let ``Phase5 O — Overlap F1 >= 0.80`` () =
    let oScenarios = Phase5Models.all |> List.filter (fun s -> s.Name.StartsWith "o")
    let summary, _ = BenchRunner.runAll oScenarios CausationConfig.defaults 20260523 60
    Assert.True(summary.AvgF1 >= 0.80,
        sprintf "expected O avgF1 >= 0.80; got %.4f (perfect %d/%d)"
            summary.AvgF1 summary.Perfect summary.Total)

[<Fact>]
let ``Phase5 T — Temporal F1 >= 0.70`` () =
    let tScenarios = Phase5Models.all |> List.filter (fun s -> s.Name.StartsWith "t")
    let summary, _ = BenchRunner.runAll tScenarios CausationConfig.defaults 20260523 60
    Assert.True(summary.AvgF1 >= 0.70,
        sprintf "expected T avgF1 >= 0.70; got %.4f (perfect %d/%d)"
            summary.AvgF1 summary.Perfect summary.Total)
