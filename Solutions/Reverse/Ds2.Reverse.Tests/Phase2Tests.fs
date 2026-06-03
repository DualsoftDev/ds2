module Ds2.Reverse.Tests.Phase2Tests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``Phase2 P/V diagnostic`` () =
    let summary, _ = BenchRunner.runAll Phase2Models.all CausationConfig.defaults 20260520 60
    printfn "%s" (BenchRunner.formatSummary summary)
    Assert.True true

[<Fact>]
let ``Phase2 P/V — aggregate F1 >= 0.70`` () =
    let summary, _ = BenchRunner.runAll Phase2Models.all CausationConfig.defaults 20260520 60
    Assert.True(summary.AvgF1 >= 0.70,
        sprintf "expected avgF1 >= 0.70; got %.4f (perfect %d/%d)"
            summary.AvgF1 summary.Perfect summary.Total)

[<Fact>]
let ``Confidence — high N + passes_seq => High tier`` () =
    let sco : CausationScore =
        { NA = 60; NB = 60
          Sufficiency = 0.95; Necessity = 0.95
          LagMean = 300.0; LagStd = 20.0; LagCv = 0.067
          AbsLagMean = 300.0
          IsParallel = false
          PassesSeq = true; PassesGrp = false
          Reason = None }
    let conf = CausationDetection.confidence sco None
    Assert.Equal(High, conf.Tier)
    Assert.True(conf.Score >= 0.7, sprintf "expected score >= 0.7, got %.3f" conf.Score)

[<Fact>]
let ``Confidence — low N => downgrade`` () =
    let sco : CausationScore =
        { NA = 8; NB = 8
          Sufficiency = 0.95; Necessity = 0.95
          LagMean = 300.0; LagStd = 20.0; LagCv = 0.067
          AbsLagMean = 300.0
          IsParallel = false
          PassesSeq = true; PassesGrp = false
          Reason = None }
    let conf = CausationDetection.confidence sco None
    Assert.True(conf.NReliability < 1.0)
    Assert.NotEqual<ConfidenceTier>(High, conf.Tier)

[<Fact>]
let ``Confidence — drop weak => Reject`` () =
    let sco : CausationScore =
        { NA = 60; NB = 60
          Sufficiency = 0.30; Necessity = 0.30
          LagMean = 0.0; LagStd = 200.0; LagCv = 999.0
          AbsLagMean = 999.0
          IsParallel = false
          PassesSeq = false; PassesGrp = false
          Reason = Some "test" }
    let conf = CausationDetection.confidence sco None
    Assert.Equal(Reject, conf.Tier)
