module Ds2.Reverse.Tests.CapacityTests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``Capacity scenarios diagnostic`` () =
    let summary, results = BenchRunner.runAll CapacityModels.all CausationConfig.defaults 20260519 60
    printfn "%s" (BenchRunner.formatSummary summary)
    // c12 만 상세 출력
    for r in results do
        if r.Name.Contains "c12" then
            printfn "=== %s detail ===" r.Name
            for (s, t, sco, reason) in r.Report.DroppedDetail do
                printfn "  Dropped %s → %s (reason=%s)" s t reason
                printfn "    suff=%.3f necc=%.3f lag_mean=%.0f std=%.0f cv=%.3f isParallel=%b passesSeq=%b"
                    sco.Sufficiency sco.Necessity sco.LagMean sco.LagStd sco.LagCv
                    sco.IsParallel sco.PassesSeq
    Assert.True true

[<Fact>]
let ``Capacity scenarios — aggregate F1 >= 0.85`` () =
    let summary, _ = BenchRunner.runAll CapacityModels.all CausationConfig.defaults 20260519 60
    Assert.True(summary.AvgF1 >= 0.85,
        sprintf "expected avgF1 >= 0.85; got %.4f (perfect %d/%d)"
            summary.AvgF1 summary.Perfect summary.Total)
