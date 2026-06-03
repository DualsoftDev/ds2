module Ds2.Reverse.Tests.DebugBenchTests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``print failing scenarios in detail`` () =
    let summary, _results = BenchRunner.runAll Models.all CausationConfig.defaults 20260519 60
    printfn "━━ Failed scenarios ━━"
    for r in summary.Failed do
        printfn "  %s — F1=%.3f  TP=%d FP=%d FN=%d" r.Name r.F1 r.TP r.FP r.FN
        printfn "    Cands=%d, PassedSeq=%d, PassedGrp=%d, Dropped=%d, Trans=%d"
            r.Report.TotalCandidates r.Report.PassedSeq r.Report.PassedGrp
            r.Report.DroppedCausation r.Report.RemovedTransitive
        for (s, t, sco, reason) in r.Report.DroppedDetail do
            printfn "    Dropped %s → %s (reason=%s, suff=%.2f necc=%.2f lag=%.0f cv=%.2f std=%.0f)"
                s t reason sco.Sufficiency sco.Necessity sco.LagMean sco.LagCv sco.LagStd
        for (s, t) in r.FpDetail do
            printfn "    FP: %s → %s" s t
    // 의도적으로 통과시키지 않음 — diagnostic only
    Assert.True true
