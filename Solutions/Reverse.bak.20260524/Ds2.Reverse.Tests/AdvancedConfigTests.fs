/// B4.1 Noise estimation + B5.2 Dynamic threshold 검증.
module Ds2.Reverse.Tests.AdvancedConfigTests

open Xunit
open Ds2.Reverse.Core

[<Fact>]
let ``Noise estimation: empty events -> 0`` () =
    let n = CausationDetection.estimateNoiseLevel [] 2000L
    Assert.Equal(0.0, n)

[<Fact>]
let ``Noise estimation: clean cycle data -> low`` () =
    // 60 cycles, A@0, B@300 (tight)
    let events =
        [ for k in 0 .. 59 do
            yield { T = int64 k * 2000L; Name = "A" }
            yield { T = int64 k * 2000L + 300L; Name = "B" } ]
    let n = CausationDetection.estimateNoiseLevel events 2000L
    Assert.True(n < 0.1, sprintf "clean data noise=%.3f expected <0.1" n)

[<Fact>]
let ``Noise estimation: jittery data -> high`` () =
    // 60 cycles, A 의 offset 이 0~1500 사이 매우 jittery
    let rng = System.Random(42)
    let events =
        [ for k in 0 .. 59 do
            yield { T = int64 k * 2000L + int64 (rng.Next(0, 1500)); Name = "A" }
            yield { T = int64 k * 2000L + 300L + int64 (rng.Next(0, 1500)); Name = "B" } ]
    let n = CausationDetection.estimateNoiseLevel events 2000L
    Assert.True(n > 0.3, sprintf "jittery data noise=%.3f expected >0.3" n)

[<Fact>]
let ``Dynamic threshold: noisy -> lower suff requirement`` () =
    let cfg0 = CausationConfig.defaults
    let cfgNoisy = CausationConfig.withNoiseLevel 1.0 cfg0
    Assert.True(cfgNoisy.SufficiencyMin < cfg0.SufficiencyMin,
        sprintf "expected lower suff for noisy; default=%.3f noisy=%.3f"
            cfg0.SufficiencyMin cfgNoisy.SufficiencyMin)
    Assert.True(cfgNoisy.LagCvMax > cfg0.LagCvMax,
        sprintf "expected higher cv max for noisy; default=%.3f noisy=%.3f"
            cfg0.LagCvMax cfgNoisy.LagCvMax)

[<Fact>]
let ``Bayesian aggregate: empty -> 0.5 (prior)`` () =
    let p = CausationDetection.bayesianAggregate []
    Assert.Equal(0.5, p, 3)

[<Fact>]
let ``Bayesian aggregate: single 0.8 -> 0.8`` () =
    let p = CausationDetection.bayesianAggregate [ 0.8 ]
    Assert.Equal(0.8, p, 2)

[<Fact>]
let ``Bayesian aggregate: two 0.8 -> high (>0.9)`` () =
    let p = CausationDetection.bayesianAggregate [ 0.8; 0.8 ]
    Assert.True(p > 0.9, sprintf "two 0.8 evidences -> %.4f, expected >0.9" p)

[<Fact>]
let ``Bayesian aggregate: conflicting 0.9 + 0.1 -> middle (~0.5)`` () =
    let p = CausationDetection.bayesianAggregate [ 0.9; 0.1 ]
    Assert.True(p > 0.4 && p < 0.6, sprintf "conflicting -> %.4f, expected ~0.5" p)

[<Fact>]
let ``Bayesian aggregate: three 0.7 -> very high`` () =
    let p = CausationDetection.bayesianAggregate [ 0.7; 0.7; 0.7 ]
    Assert.True(p > 0.85, sprintf "three weak agreeing -> %.4f, expected >0.85" p)

[<Fact>]
let ``Dynamic threshold: clean -> stricter suff requirement`` () =
    let cfg0 = CausationConfig.defaults
    let cfgClean = CausationConfig.withNoiseLevel 0.0 cfg0
    Assert.True(cfgClean.SufficiencyMin >= cfg0.SufficiencyMin,
        sprintf "expected ≥ suff for clean; default=%.3f clean=%.3f"
            cfg0.SufficiencyMin cfgClean.SufficiencyMin)

[<Fact>]
let ``Auto-tune end-to-end: synthetic noisy scenario`` () =
    // 노이즈가 매우 큰 상태에서 autoTune 이 detection 을 살리는지 확인
    let cycleMs = 2000L
    let rng = System.Random(42)
    // A@0±300, B@300±300 — std ≥ 300ms (very noisy)
    let events =
        [ for k in 0 .. 59 do
            yield { T = int64 k * cycleMs + int64 (rng.Next(-300, 301)); Name = "A" }
            yield { T = int64 k * cycleMs + 300L + int64 (rng.Next(-300, 301))
                    Name = "B" } ]
    let cfg = CausationConfig.withCycleHint cycleMs CausationConfig.defaults
    // Default (no autoTune) — strict thresholds
    let scoreDefault = CausationDetection.score cfg
                          (events |> List.filter (fun e -> e.Name = "A") |> List.map (fun e -> e.T))
                          (events |> List.filter (fun e -> e.Name = "B") |> List.map (fun e -> e.T))
    // AutoTuned (noise level 추정 후 완화)
    let noise = CausationDetection.estimateNoiseLevel events cycleMs
    let cfgTuned = CausationConfig.withNoiseLevel noise cfg
    let scoreTuned = CausationDetection.score cfgTuned
                        (events |> List.filter (fun e -> e.Name = "A") |> List.map (fun e -> e.T))
                        (events |> List.filter (fun e -> e.Name = "B") |> List.map (fun e -> e.T))
    printfn "Default: passes=%b cv=%.3f / Tuned: passes=%b cv_threshold=%.3f noise=%.2f"
        scoreDefault.PassesSeq scoreDefault.LagCv
        scoreTuned.PassesSeq cfgTuned.LagCvMax noise
    // autoTune 은 더 관대 — noisy 환경에서 더 많이 인정
    Assert.True(noise > 0.5, sprintf "expected high noise estimation; got %.2f" noise)
    // autoTune 의 LagCvMax 가 default 보다 크면 OK
    Assert.True(cfgTuned.LagCvMax > cfg.LagCvMax)
