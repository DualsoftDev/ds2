/// 모든 phase 합산 회귀: 17 신규 시나리오 (R/Q/D/P/V/G/Z) 통합 평균 F1 ≥ 0.85.
module Ds2.Reverse.Tests.AllPhaseSweepTests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``All Phase: R + Q + D + P + V + G + Z aggregate F1 >= 0.85`` () =
    let all =
        Phase1Models.all @
        Phase2Models.all @
        Phase3Models.all
    let summary, _ = BenchRunner.runAll all CausationConfig.defaults 20260522 60
    printfn "%s" (BenchRunner.formatSummary summary)
    Assert.True(summary.AvgF1 >= 0.85,
        sprintf "expected avgF1 >= 0.85; got %.4f (perfect %d/%d)"
            summary.AvgF1 summary.Perfect summary.Total)

[<Fact>]
let ``All Phase: cumulative perfect count >= 80%%`` () =
    let all =
        Phase1Models.all @
        Phase2Models.all @
        Phase3Models.all
    let summary, _ = BenchRunner.runAll all CausationConfig.defaults 20260522 60
    let perfectRate = float summary.Perfect / float summary.Total
    Assert.True(perfectRate >= 0.80,
        sprintf "expected perfect rate >= 80%%; got %.1f%% (%d/%d)"
            (perfectRate * 100.0) summary.Perfect summary.Total)

[<Fact>]
let ``All Phase: scenario distribution stats`` () =
    let p1 = Phase1Models.all |> List.length
    let p2 = Phase2Models.all |> List.length
    let p3 = Phase3Models.all |> List.length
    let p4 = Phase4Models.all |> List.length
    let p5 = Phase5Models.all |> List.length
    Assert.Equal(13, p1)   // R(5) + Q(5) + D(3)
    Assert.Equal(6, p2)    // P(3) + V(3)
    Assert.Equal(9, p3)    // G(4) + Z(5)
    Assert.Equal(8, p4)    // K(4) + S(4 incl. kmeans 3-modal)
    Assert.Equal(6, p5)    // O(3) + T(3)
    Assert.Equal(42, p1 + p2 + p3 + p4 + p5)

[<Fact>]
let ``All Phase 1-5: combined aggregate F1 >= 0.90`` () =
    let all =
        Phase1Models.all @
        Phase2Models.all @
        Phase3Models.all @
        Phase4Models.all @
        Phase5Models.all
    let summary, _ = BenchRunner.runAll all CausationConfig.defaults 20260523 60
    Assert.True(summary.AvgF1 >= 0.90,
        sprintf "expected combined avgF1 >= 0.90; got %.4f (perfect %d/%d)"
            summary.AvgF1 summary.Perfect summary.Total)
