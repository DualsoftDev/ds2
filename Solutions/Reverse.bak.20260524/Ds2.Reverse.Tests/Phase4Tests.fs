module Ds2.Reverse.Tests.Phase4Tests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``Phase4 K/S diagnostic`` () =
    let summary, _ = BenchRunner.runAll Phase4Models.all CausationConfig.defaults 20260522 60
    printfn "%s" (BenchRunner.formatSummary summary)
    Assert.True true

[<Fact>]
let ``Phase4 K — Kombinatorial F1 >= 0.80`` () =
    let kScenarios = Phase4Models.all |> List.filter (fun s -> s.Name.StartsWith "k")
    let summary, _ = BenchRunner.runAll kScenarios CausationConfig.defaults 20260522 60
    Assert.True(summary.AvgF1 >= 0.80,
        sprintf "expected K avgF1 >= 0.80; got %.4f (perfect %d/%d)"
            summary.AvgF1 summary.Perfect summary.Total)

[<Fact>]
let ``Phase4 s3 k-means diagnostic`` () =
    // 직접 score 함수 호출 — k-means 가 작동하는지 확인
    let rng = System.Random(42)
    let cycleMs = 2000L
    let nCycles = 60
    let aTimes = [| for k in 0 .. nCycles - 1 -> int64 k * cycleMs |]
    let bTimes = [|
        for k in 0 .. nCycles - 1 ->
            let lag = [| 200L; 500L; 800L |].[rng.Next(0, 3)]
            int64 k * cycleMs + lag |]
    let cfg = CausationConfig.withCycleHint cycleMs CausationConfig.defaults
    let sco = CausationDetection.score cfg aTimes bTimes
    printfn "score: NA=%d NB=%d suff=%.3f necc=%.3f lagMean=%.1f lagStd=%.1f lagCv=%.3f PassesSeq=%b"
        sco.NA sco.NB sco.Sufficiency sco.Necessity sco.LagMean sco.LagStd sco.LagCv sco.PassesSeq
    Assert.True(sco.NA >= 50)
    Assert.True(sco.PassesSeq,
        sprintf "k-means 3-modal 인정 안 됨: suff=%.3f necc=%.3f cv=%.3f"
            sco.Sufficiency sco.Necessity sco.LagCv)

[<Fact>]
let ``Phase4 S — Stress edge cases F1 >= 0.70`` () =
    let sScenarios = Phase4Models.all |> List.filter (fun s -> s.Name.StartsWith "s")
    let summary, _ = BenchRunner.runAll sScenarios CausationConfig.defaults 20260522 60
    Assert.True(summary.AvgF1 >= 0.70,
        sprintf "expected S avgF1 >= 0.70; got %.4f (perfect %d/%d)"
            summary.AvgF1 summary.Perfect summary.Total)
