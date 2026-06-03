/// U-CausationDetection — 각 public API 의 단위 테스트.
module Ds2.Reverse.Tests.Unit.CausationDetectionUnitTests

open Xunit
open Ds2.Reverse.Core

// ── score (15 tests) ────────────────────────────────────────────────

let private cfg = CausationConfig.defaults
let private cfgWithCycle = CausationConfig.withCycleHint 2000L cfg

[<Fact>]
let ``score: 정상 단일 페어 60 cycle`` () =
    let a = [| for k in 0 .. 59 -> int64 k * 2000L |]
    let b = [| for k in 0 .. 59 -> int64 k * 2000L + 300L |]
    let s = CausationDetection.score cfgWithCycle a b
    Assert.Equal(60, s.NA)
    Assert.Equal(60, s.NB)
    Assert.True(s.Sufficiency >= 0.95)
    Assert.True(s.Necessity >= 0.95)
    Assert.True(s.PassesSeq)
    Assert.True(s.LagMean > 290.0 && s.LagMean < 310.0)

[<Fact>]
let ``score: 양쪽 모두 비어있음`` () =
    let s = CausationDetection.score cfg [||] [||]
    Assert.Equal(0, s.NA)
    Assert.Equal(0, s.NB)
    Assert.False(s.PassesSeq)
    Assert.False(s.PassesGrp)
    Assert.True(s.Reason.IsSome)

[<Fact>]
let ``score: A 만 있음 → low_n B`` () =
    let a = [| for k in 0 .. 19 -> int64 k * 1000L |]
    let s = CausationDetection.score cfg a [||]
    Assert.Equal(20, s.NA)
    Assert.Equal(0, s.NB)
    Assert.False(s.PassesSeq)
    Assert.True(s.Reason.IsSome)

[<Fact>]
let ``score: B 만 있음 → low_n A`` () =
    let b = [| for k in 0 .. 19 -> int64 k * 1000L |]
    let s = CausationDetection.score cfg [||] b
    Assert.Equal(0, s.NA)
    Assert.Equal(20, s.NB)
    Assert.False(s.PassesSeq)

[<Fact>]
let ``score: minFires 미만 → low_n`` () =
    let cfg5 = { cfg with MinFires = 10 }
    let a = [| 0L; 1000L; 2000L; 3000L; 4000L |]   // 5 < 10
    let b = [| 300L; 1300L; 2300L; 3300L; 4300L |]
    let s = CausationDetection.score cfg5 a b
    Assert.False(s.PassesSeq)
    Assert.Contains("low_n", s.Reason |> Option.defaultValue "")

[<Fact>]
let ``score: 완벽 parallel (lag=0) → PassesGrp`` () =
    let a = [| for k in 0 .. 29 -> int64 k * 2000L |]
    let b = a |> Array.map (fun t -> t + 5L)   // 5ms lag, parallel zone
    let s = CausationDetection.score cfgWithCycle a b
    Assert.True(s.IsParallel)
    Assert.True(s.PassesGrp)

[<Fact>]
let ``score: 무관 events (lag random) → drop`` () =
    let rng = System.Random(42)
    let a = [| for k in 0 .. 29 -> int64 k * 2000L |]
    let b = [| for k in 0 .. 29 -> int64 (rng.Next(0, 60000)) |]   // random
    let s = CausationDetection.score (CausationConfig.withCycleHint 2000L cfg) a b
    Assert.False(s.PassesSeq)

[<Fact>]
let ``score: bimodal lag (200/500) → PassesSeq via bimodal stable`` () =
    let rng = System.Random(42)
    let a = [| for k in 0 .. 59 -> int64 k * 2000L |]
    let b = [| for k in 0 .. 59 ->
                let lag = if rng.Next(0, 2) = 0 then 200L else 500L
                int64 k * 2000L + lag |]
    let s = CausationDetection.score cfgWithCycle a b
    Assert.True(s.PassesSeq,
        sprintf "expected bimodal pass; got cv=%.3f mean=%.0f std=%.0f" s.LagCv s.LagMean s.LagStd)

[<Fact>]
let ``score: 작은 lag (50ms, tight jitter) → smallLagFallback`` () =
    let rng = System.Random(42)
    let a = [| for k in 0 .. 29 -> int64 k * 2000L |]
    let b = [| for k in 0 .. 29 ->
                int64 k * 2000L + 50L + int64 (rng.Next(-10, 11)) |]
    let s = CausationDetection.score cfgWithCycle a b
    Assert.True(s.PassesSeq)

[<Fact>]
let ``score: 5-modal well-separated → 통과 (강화된 k-means)`` () =
    let rng = System.Random(42)
    let a = [| for k in 0 .. 59 -> int64 k * 2500L |]
    let b = [| for k in 0 .. 59 ->
                let lag = int64 (rng.Next(0, 5)) * 250L + 100L   // 100/350/600/850/1100
                int64 k * 2500L + lag |]
    let s = CausationDetection.score (CausationConfig.withCycleHint 2500L cfg) a b
    // 2026-05-25: k-means 확장으로 well-separated 5-modal 도 인정.
    Assert.True(s.PassesSeq,
        sprintf "5-modal well-separated should pass after k-means upgrade; cv=%.3f" s.LagCv)

[<Fact>]
let ``score: 진짜 uniform continuous spread → 거부`` () =
    // 100~1100ms 연속 uniform — 명확한 mode 없음. 거부되어야.
    let rng = System.Random(42)
    let a = [| for k in 0 .. 59 -> int64 k * 2500L |]
    let b = [| for k in 0 .. 59 ->
                let lag = int64 (rng.Next(100, 1101))   // uniform 연속
                int64 k * 2500L + lag |]
    let s = CausationDetection.score (CausationConfig.withCycleHint 2500L cfg) a b
    Assert.False(s.PassesSeq,
        sprintf "continuous uniform 거부; got cv=%.3f passes=%b" s.LagCv s.PassesSeq)

[<Fact>]
let ``score: linear drift (lag 가 단조 증가) → driftStable 인정`` () =
    let a = [| for k in 0 .. 49 -> int64 k * 2000L |]
    let b = [| for k in 0 .. 49 ->
                int64 k * 2000L + 300L + int64 k * 5L |]   // 300, 305, ..., 545
    let s = CausationDetection.score cfgWithCycle a b
    Assert.True(s.PassesSeq)

[<Fact>]
let ``score: 큰 jitter 만, lag 작음 → smallLagFallback`` () =
    let rng = System.Random(42)
    let a = [| for k in 0 .. 39 -> int64 k * 2000L |]
    let b = [| for k in 0 .. 39 ->
                int64 k * 2000L + 100L + int64 (rng.Next(-60, 61)) |]
    let s = CausationDetection.score cfgWithCycle a b
    Assert.True(s.PassesSeq)

[<Fact>]
let ``score: outlier 한 cycle → Tukey IQR 제거 후 정상`` () =
    let a = [| for k in 0 .. 59 -> int64 k * 2000L |]
    let b = [| for k in 0 .. 59 ->
                let lag = if k = 25 then 1500L else 300L   // 1 cycle outlier
                int64 k * 2000L + lag |]
    let s = CausationDetection.score cfgWithCycle a b
    Assert.True(s.PassesSeq,
        sprintf "outlier 제거 후 정상이어야: cv=%.3f mean=%.0f" s.LagCv s.LagMean)

[<Fact>]
let ``score: lag 가 일관되게 음수 (B 가 항상 A 전) → suff 매우 낮음`` () =
    let a = [| for k in 0 .. 29 -> int64 k * 2000L + 500L |]
    let b = [| for k in 0 .. 29 -> int64 k * 2000L |]
    let s = CausationDetection.score cfgWithCycle a b
    // 이 경우 A→B 페어가 매우 큰 lag (cross-cycle) → suff 낮음
    Assert.False(s.PassesSeq)

[<Fact>]
let ``score: lag std 가 mean 의 거의 100% → cv 큼, 거부`` () =
    let rng = System.Random(42)
    let a = [| for k in 0 .. 39 -> int64 k * 3000L |]
    let b = [| for k in 0 .. 39 ->
                let lag = int64 (rng.Next(50, 1500))   // 매우 큰 분산
                int64 k * 3000L + lag |]
    let s = CausationDetection.score (CausationConfig.withCycleHint 3000L cfg) a b
    Assert.True(s.LagCv > 0.4)
    Assert.False(s.PassesSeq)

// ── gate (5 tests) ──────────────────────────────────────────────────

let private mkPassingScore () : CausationScore =
    { NA = 60; NB = 60
      Sufficiency = 0.95; Necessity = 0.95
      LagMean = 300.0; LagStd = 20.0; LagCv = 0.067
      AbsLagMean = 300.0
      IsParallel = false; PassesSeq = true; PassesGrp = false
      Reason = None }

let private mkParallelScore () : CausationScore =
    { NA = 60; NB = 60
      Sufficiency = 0.95; Necessity = 0.95
      LagMean = 5.0; LagStd = 10.0; LagCv = 0.1
      AbsLagMean = 5.0
      IsParallel = true; PassesSeq = false; PassesGrp = true
      Reason = None }

[<Fact>]
let ``gate: declared trigger → EmitSequential code 1`` () =
    let d = CausationDetection.gate "trigger" (mkPassingScore())
    match d with
    | EmitSequential(1, _) -> Assert.True true
    | other -> Assert.Fail (sprintf "expected EmitSequential(1, _); got %A" other)

[<Fact>]
let ``gate: declared reset → EmitSequential code 2`` () =
    let d = CausationDetection.gate "reset" (mkPassingScore())
    match d with
    | EmitSequential(2, _) -> Assert.True true
    | other -> Assert.Fail (sprintf "expected EmitSequential(2, _); got %A" other)

[<Fact>]
let ``gate: declared trigger_reset / startreset → code 3`` () =
    let d1 = CausationDetection.gate "trigger_reset" (mkPassingScore())
    let d2 = CausationDetection.gate "startreset" (mkPassingScore())
    match d1 with EmitSequential(3, _) -> () | other -> Assert.Fail (sprintf "%A" other)
    match d2 with EmitSequential(3, _) -> () | other -> Assert.Fail (sprintf "%A" other)

[<Fact>]
let ``gate: declared mutex / resetreset → code 4`` () =
    let d1 = CausationDetection.gate "mutex" (mkPassingScore())
    let d2 = CausationDetection.gate "resetreset" (mkPassingScore())
    match d1 with EmitSequential(4, _) -> () | other -> Assert.Fail (sprintf "%A" other)
    match d2 with EmitSequential(4, _) -> () | other -> Assert.Fail (sprintf "%A" other)

[<Fact>]
let ``gate: declared group + IsParallel → EmitGroup`` () =
    let d = CausationDetection.gate "group" (mkParallelScore())
    match d with
    | EmitGroup _ -> Assert.True true
    | other -> Assert.Fail (sprintf "%A" other)

// ── mutexScore (4 tests) ────────────────────────────────────────────

[<Fact>]
let ``mutexScore: 정확 mutex (A 와 B 가 cycle 별 교대) → passes`` () =
    let cycleMs = 2000L
    let cfgMx = CausationConfig.withCycleHint cycleMs cfg
    let a = [| for k in 0 .. 59 do if k % 2 = 0 then yield int64 k * cycleMs |]
    let b = [| for k in 0 .. 59 do if k % 2 = 1 then yield int64 k * cycleMs |]
    let passes, rate, nA, nB = CausationDetection.mutexScore cfgMx a b
    Assert.True(passes, sprintf "expected mutex; rate=%.3f nA=%d nB=%d" rate nA nB)
    Assert.True(rate < 0.10)

[<Fact>]
let ``mutexScore: A 와 B 가 항상 같이 발화 → not mutex`` () =
    let cfgMx = CausationConfig.withCycleHint 2000L cfg
    let a = [| for k in 0 .. 29 -> int64 k * 2000L |]
    let b = [| for k in 0 .. 29 -> int64 k * 2000L + 30L |]   // co-occurrence
    let passes, _, _, _ = CausationDetection.mutexScore cfgMx a b
    Assert.False(passes)

[<Fact>]
let ``mutexScore: A 만 있고 B 적음 → low_n`` () =
    let a = [| for k in 0 .. 29 -> int64 k * 2000L |]
    let b = [| 100L; 5000L |]
    let passes, _, _, _ = CausationDetection.mutexScore cfg a b
    Assert.False(passes)

[<Fact>]
let ``mutexScore: 양쪽 비어있음 → not mutex`` () =
    let passes, _, _, _ = CausationDetection.mutexScore cfg [||] [||]
    Assert.False(passes)

// ── confidence (5 tests) ────────────────────────────────────────────

[<Fact>]
let ``confidence: high N + passes → High tier`` () =
    let s = { mkPassingScore() with NA = 100; NB = 100 }
    let c = CausationDetection.confidence s None
    Assert.Equal(High, c.Tier)
    Assert.True(c.Score >= 0.9)

[<Fact>]
let ``confidence: medium N + passes → Medium 또는 High tier`` () =
    let s = { mkPassingScore() with NA = 20; NB = 20 }
    let c = CausationDetection.confidence s None
    Assert.True(c.Tier = High || c.Tier = Medium,
        sprintf "expected High/Medium; got %A" c.Tier)

[<Fact>]
let ``confidence: low N + passes → 신뢰도 감소 (nReliability 0.5)`` () =
    let s = { mkPassingScore() with NA = 5; NB = 5 }
    let c = CausationDetection.confidence s None
    Assert.Equal(0.5, c.NReliability)
    Assert.NotEqual<ConfidenceTier>(High, c.Tier)

[<Fact>]
let ``confidence: high N + fail → Reject tier`` () =
    let s =
        { mkPassingScore() with
            PassesSeq = false; PassesGrp = false
            Sufficiency = 0.2; Necessity = 0.2 }
    let c = CausationDetection.confidence s None
    Assert.Equal(Reject, c.Tier)

[<Fact>]
let ``confidence: logic strength 추가 → 가중 결합`` () =
    let s =
        { mkPassingScore() with
            PassesSeq = false   // capture-only weak
            Sufficiency = 0.5; Necessity = 0.5 }
    let cNoLogic = CausationDetection.confidence s None
    let cWithLogic = CausationDetection.confidence s (Some 0.95)
    Assert.True(cWithLogic.Score > cNoLogic.Score,
        sprintf "logic 추가 시 score 증가: %.3f → %.3f" cNoLogic.Score cWithLogic.Score)

// ── bayesianAggregate (4 tests) ─────────────────────────────────────

[<Fact>]
let ``bayesianAggregate: empty → 0.5 prior`` () =
    Assert.Equal(0.5, CausationDetection.bayesianAggregate [], 3)

[<Fact>]
let ``bayesianAggregate: 합의 (agreement) → 극단으로 이동`` () =
    let p = CausationDetection.bayesianAggregate [ 0.8; 0.8; 0.8 ]
    Assert.True(p > 0.95)

[<Fact>]
let ``bayesianAggregate: 상충 → 중간`` () =
    let p = CausationDetection.bayesianAggregate [ 0.9; 0.1 ]
    Assert.True(p > 0.4 && p < 0.6)

[<Fact>]
let ``bayesianAggregate: 극값 clamp (1.0 → 0.99)`` () =
    let p1 = CausationDetection.bayesianAggregate [ 1.0 ]
    let p99 = CausationDetection.bayesianAggregate [ 0.99 ]
    Assert.Equal(p99, p1, 4)
    Assert.True(p1 < 1.0)

// ── estimateNoiseLevel (4 tests) ────────────────────────────────────

[<Fact>]
let ``estimateNoiseLevel: empty events → 0`` () =
    let n = CausationDetection.estimateNoiseLevel [] 2000L
    Assert.Equal(0.0, n)

[<Fact>]
let ``estimateNoiseLevel: clean (small jitter) → low`` () =
    let events =
        [ for k in 0 .. 29 do
            yield { T = int64 k * 2000L; Name = "A" }
            yield { T = int64 k * 2000L + 300L; Name = "B" } ]
    let n = CausationDetection.estimateNoiseLevel events 2000L
    Assert.True(n < 0.1, sprintf "clean noise=%.3f" n)

[<Fact>]
let ``estimateNoiseLevel: very jittery → high (~1.0)`` () =
    let rng = System.Random(42)
    let events =
        [ for k in 0 .. 29 do
            yield { T = int64 k * 2000L + int64 (rng.Next(0, 1500)); Name = "A" }
            yield { T = int64 k * 2000L + int64 (rng.Next(0, 1500)); Name = "B" } ]
    let n = CausationDetection.estimateNoiseLevel events 2000L
    Assert.True(n > 0.5, sprintf "jittery noise=%.3f" n)

[<Fact>]
let ``estimateNoiseLevel: cycleMs=0 → 0 (safety)`` () =
    let events = [ for k in 0 .. 9 -> { T = int64 k * 100L; Name = "A" } ]
    let n = CausationDetection.estimateNoiseLevel events 0L
    Assert.Equal(0.0, n)
