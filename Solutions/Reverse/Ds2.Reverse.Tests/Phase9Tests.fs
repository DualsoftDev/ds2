/// Phase 9 — FFT signal analysis + Domain polling library + Bayesian confidence.
module Ds2.Reverse.Tests.Phase9Tests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

// ── Phase 9A — FFT polling detector ──────────────────────────────────

[<Fact>]
let ``Phase9A FFT - periodic signal high peak ratio`` () =
    // 정확한 100ms 주기 신호 (32개 fire)
    let times = [| for k in 0 .. 31 -> int64 k * 100L |]
    let isPoll, ratio, peakBin = SignalAnalysis.detectPollingFromTimes times 20L
    printfn "FFT 100ms periodic: isPoll=%b ratio=%.2f bin=%d" isPoll ratio peakBin
    Assert.True(isPoll, "perfectly periodic signal should be detected")
    Assert.True(ratio > 5.0)

[<Fact>]
let ``Phase9A FFT - random signal low peak ratio`` () =
    let rng = System.Random(42)
    let times =
        [| for _ in 0 .. 31 -> int64 (rng.Next(0, 10000)) |]
        |> Array.sort
    let isPoll, ratio, _ = SignalAnalysis.detectPollingFromTimes times 200L
    printfn "FFT random: isPoll=%b ratio=%.2f" isPoll ratio
    Assert.False(isPoll, sprintf "random should not be detected; ratio=%.2f" ratio)

[<Fact>]
let ``Phase9A FFT - interArrival CV computation`` () =
    let periodic = [| for k in 0 .. 19 -> int64 k * 100L |]
    let cv = SignalAnalysis.interArrivalCV periodic
    Assert.True(cv < 0.05, sprintf "periodic CV should be ~0; got %.3f" cv)
    let irregular = [| 0L; 100L; 250L; 500L; 1000L; 1700L; 2700L |]
    let cv2 = SignalAnalysis.interArrivalCV irregular
    Assert.True(cv2 > 0.3, sprintf "irregular CV should be larger; got %.3f" cv2)

// ── Phase 9B — Domain polling pattern library ────────────────────────

[<Fact>]
let ``Phase9B Domain - 100ms polling 인식`` () =
    let times = [| for k in 0 .. 30 -> int64 k * 100L |]
    let pattern = PollingPatterns.matchPattern times
    Assert.True(pattern.IsSome)
    let p = pattern.Value
    printfn "Matched: %s" p.Name
    Assert.Contains("100ms", p.Name)

[<Fact>]
let ``Phase9B Domain - 50ms scan 인식`` () =
    let times = [| for k in 0 .. 30 -> int64 k * 50L + int64 (k % 3) |]
    let pattern = PollingPatterns.matchPattern times
    Assert.True(pattern.IsSome)

[<Fact>]
let ``Phase9B Domain - 500ms scan 인식`` () =
    let times = [| for k in 0 .. 12 -> int64 k * 500L + int64 (k % 5) |]
    let pattern = PollingPatterns.matchPattern times
    Assert.True(pattern.IsSome)

[<Fact>]
let ``Phase9B Domain - 2000ms (not in library) 무매치`` () =
    let times = [| for k in 0 .. 20 -> int64 k * 2000L |]
    let pattern = PollingPatterns.matchPattern times
    Assert.True(pattern.IsNone, sprintf "2000ms should not match; got %A" pattern)

[<Fact>]
let ``Phase9B Domain - irregular intervals 무매치`` () =
    let rng = System.Random(42)
    let times =
        [| for _ in 0 .. 20 -> int64 (rng.Next(50, 200)) |]
        |> Array.scan (+) 0L
        |> Array.skip 1
    let pattern = PollingPatterns.matchPattern times
    Assert.True(pattern.IsNone, "irregular should not match domain pattern")

// ── Phase 9C — Bayesian confidence ───────────────────────────────────

let private mkScoreFull (suff: float) (necc: float) (passes: bool) (n: int) =
    { NA = n; NB = n
      Sufficiency = suff; Necessity = necc
      LagMean = 300.0; LagStd = 20.0; LagCv = 0.067
      AbsLagMean = 300.0
      IsParallel = false; PassesSeq = passes; PassesGrp = false
      Reason = None } : CausationScore

[<Fact>]
let ``Phase9C Bayesian - strong evidence + strong prior → High tier`` () =
    let s = mkScoreFull 0.95 0.95 true 60
    let c = CausationDetection.bayesianConfidence s (Some 0.9) false
    printfn "Bayesian: score=%.3f tier=%A" c.Score c.Tier
    Assert.Equal(High, c.Tier)

[<Fact>]
let ``Phase9C Bayesian - weak evidence but strong prior → Medium`` () =
    let s = mkScoreFull 0.6 0.6 false 30
    let c = CausationDetection.bayesianConfidence s (Some 0.95) false
    printfn "Bayesian weak+prior: score=%.3f tier=%A" c.Score c.Tier
    // logic prior 0.95 + weak capture → 중간 tier 기대
    Assert.True(c.Tier = Medium || c.Tier = High)

[<Fact>]
let ``Phase9C Bayesian - polling suspect → score 낮음`` () =
    let s = mkScoreFull 0.95 0.95 true 60
    let cNormal = CausationDetection.bayesianConfidence s None false
    let cPolling = CausationDetection.bayesianConfidence s None true
    printfn "Normal=%.3f Polling=%.3f" cNormal.Score cPolling.Score
    Assert.True(cPolling.Score < cNormal.Score,
        sprintf "polling penalty: normal=%.3f polling=%.3f"
            cNormal.Score cPolling.Score)

[<Fact>]
let ``Phase9C Bayesian - low N → confidence reduction`` () =
    let s = mkScoreFull 0.95 0.95 true 5
    let c = CausationDetection.bayesianConfidence s None false
    Assert.True(c.NReliability < 1.0)
    Assert.NotEqual<ConfidenceTier>(High, c.Tier)

[<Fact>]
let ``Phase9C Bayesian - empty evidence → low score`` () =
    let s = mkScoreFull 0.2 0.2 false 30
    let c = CausationDetection.bayesianConfidence s None false
    Assert.True(c.Score < 0.5,
        sprintf "weak evidence should be Reject; got %.3f %A" c.Score c.Tier)

[<Fact>]
let ``Phase9C Bayesian - polling 의심 + 약한 evidence → Reject`` () =
    let s = mkScoreFull 0.7 0.7 false 30
    let c = CausationDetection.bayesianConfidence s None true
    printfn "polling+weak: score=%.3f tier=%A" c.Score c.Tier
    Assert.True(c.Tier = Reject || c.Tier = Low)
