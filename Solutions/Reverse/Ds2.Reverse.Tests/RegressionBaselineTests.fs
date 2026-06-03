/// R. Regression baseline — 알고리즘 변경 감지를 위한 기준선.
/// 의미: 이 테스트가 깨지면 알고리즘 동작이 달라진 것 (의도된 변경이어야).
module Ds2.Reverse.Tests.RegressionBaselineTests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``Regression: m0-100 perfect rate = 100%%`` () =
    let summary, _ = BenchRunner.runAll Models.all CausationConfig.defaults 20260519 60
    Assert.Equal(101, summary.Total)
    Assert.Equal(101, summary.Perfect)
    Assert.Equal(1.0, summary.AvgF1, 4)

[<Fact>]
let ``Regression: m0-100 robust across seeds`` () =
    let seeds = [ 20260519; 1; 42; 12345; 999999 ]
    for seed in seeds do
        let summary, _ = BenchRunner.runAll Models.all CausationConfig.defaults seed 60
        Assert.True(summary.Perfect >= 95,
            sprintf "seed=%d perfect=%d (expected ≥95)" seed summary.Perfect)

[<Fact>]
let ``Regression: Phase 1 (R/Q/D) aggregate F1 >= 0.95`` () =
    let summary, _ =
        BenchRunner.runAll Phase1Models.all CausationConfig.defaults 20260523 60
    Assert.True(summary.AvgF1 >= 0.95,
        sprintf "Phase1 avgF1=%.4f < 0.95" summary.AvgF1)

[<Fact>]
let ``Regression: Phase 1-5 통합 perfect rate >= 80%%`` () =
    let all =
        Phase1Models.all @
        Phase2Models.all @
        Phase3Models.all @
        Phase4Models.all @
        Phase5Models.all
    let summary, _ = BenchRunner.runAll all CausationConfig.defaults 20260523 60
    let rate = float summary.Perfect / float summary.Total
    Assert.True(rate >= 0.80,
        sprintf "Phase 1-5 perfect rate %.1f%% < 80%%" (rate * 100.0))

[<Fact>]
let ``Regression: Phase 1-5 통합 avg F1 >= 0.90`` () =
    let all =
        Phase1Models.all @
        Phase2Models.all @
        Phase3Models.all @
        Phase4Models.all @
        Phase5Models.all
    let summary, _ = BenchRunner.runAll all CausationConfig.defaults 20260523 60
    Assert.True(summary.AvgF1 >= 0.90,
        sprintf "Phase 1-5 avgF1=%.4f < 0.90" summary.AvgF1)

[<Fact>]
let ``Regression: defaults config 값 유지`` () =
    let d = CausationConfig.defaults
    Assert.Equal(3000L, d.WindowMs)
    Assert.Equal(0.85, d.SufficiencyMin)
    Assert.Equal(0.85, d.NecessityMin)
    Assert.Equal(0.30, d.LagCvMax)
    Assert.Equal(150.0, d.LagStdAbsMs)
    Assert.Equal(5, d.MinFires)
    Assert.Equal(50.0, d.ParallelLagMs)
    Assert.Equal(None, d.CycleHintMs)

[<Fact>]
let ``Regression: confidence tier 경계값 유지`` () =
    let mkS suff passes =
        { NA = 100; NB = 100
          Sufficiency = suff; Necessity = suff
          LagMean = 300.0; LagStd = 20.0; LagCv = 0.067
          AbsLagMean = 300.0
          IsParallel = false; PassesSeq = passes; PassesGrp = false
          Reason = None } : CausationScore
    let high = CausationDetection.confidence (mkS 0.95 true) None
    let med = CausationDetection.confidence (mkS 0.78 true) None    // passes 이지만 suff 낮음
    let rej = CausationDetection.confidence (mkS 0.20 false) None
    Assert.Equal(High, high.Tier)
    Assert.True(med.Tier = Medium || med.Tier = High,
        sprintf "expected Medium/High; got %A (score=%.3f)" med.Tier med.Score)
    Assert.Equal(Reject, rej.Tier)
