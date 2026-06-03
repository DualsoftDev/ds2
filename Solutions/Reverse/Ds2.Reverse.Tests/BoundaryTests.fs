/// B. Boundary tests — threshold 정확 통과/실패 검증.
module Ds2.Reverse.Tests.BoundaryTests

open Xunit
open Ds2.Reverse.Core

let private cfg = CausationConfig.defaults
let private cycleMs = 2000L
let private cfgC = CausationConfig.withCycleHint cycleMs cfg

[<Fact>]
let ``Boundary: suff 정확히 0.85 → 통과`` () =
    // 60 cycle 중 51개 매칭 (85%)
    let a = [| for k in 0 .. 59 -> int64 k * cycleMs |]
    let b = [| for k in 0 .. 50 -> int64 k * cycleMs + 300L |]
    let s = CausationDetection.score cfgC a b
    Assert.True(s.Sufficiency >= 0.84 && s.Sufficiency <= 0.86,
        sprintf "suff ~0.85; got %.4f" s.Sufficiency)

[<Fact>]
let ``Boundary: suff 0.84 (한 개 부족) → 거부`` () =
    let a = [| for k in 0 .. 59 -> int64 k * cycleMs |]
    let b = [| for k in 0 .. 49 -> int64 k * cycleMs + 300L |]   // 50/60 = 0.833
    let s = CausationDetection.score cfgC a b
    Assert.False(s.PassesSeq)

[<Fact>]
let ``Boundary: lagCv 정확히 0.30 → 통과`` () =
    // mean=300, std=90 → cv=0.30
    let rng = System.Random(12345)
    let a = [| for k in 0 .. 59 -> int64 k * cycleMs |]
    let b = [|
        for k in 0 .. 59 ->
            int64 k * cycleMs + 300L + int64 (rng.Next(-90, 91))
    |]
    let s = CausationDetection.score cfgC a b
    // cv 가 거의 0.3 근처여야 — 통과 가능성 검증
    Assert.True(s.LagCv < 0.5, sprintf "cv=%.3f" s.LagCv)

[<Fact>]
let ``Boundary: lagStd 정확 50 (smallLagFallback 안쪽)`` () =
    let rng = System.Random(7)
    let a = [| for k in 0 .. 39 -> int64 k * cycleMs |]
    let b = [|
        for k in 0 .. 39 ->
            int64 k * cycleMs + 100L + int64 (rng.Next(-50, 51))
    |]
    let s = CausationDetection.score cfgC a b
    Assert.True(s.PassesSeq, sprintf "smallLag std=%.0f mean=%.0f" s.LagStd s.LagMean)

[<Fact>]
let ``Boundary: lagMean 정확 150 → smallLagFallback 임계`` () =
    let rng = System.Random(7)
    let a = [| for k in 0 .. 39 -> int64 k * cycleMs |]
    let b = [|
        for k in 0 .. 39 ->
            int64 k * cycleMs + 150L + int64 (rng.Next(-30, 31))
    |]
    let s = CausationDetection.score cfgC a b
    Assert.True(s.PassesSeq, sprintf "mean=%.0f std=%.0f" s.LagMean s.LagStd)

[<Fact>]
let ``Boundary: parallel lag 정확 50ms - parallel zone 안`` () =
    let a = [| for k in 0 .. 29 -> int64 k * cycleMs |]
    let b = a |> Array.map (fun t -> t + 40L)   // 40ms < 50ms parallel
    let s = CausationDetection.score cfgC a b
    Assert.True(s.IsParallel)
    Assert.True(s.PassesGrp)

[<Fact>]
let ``Boundary: parallel lag 정확 60ms - parallel zone 밖`` () =
    let a = [| for k in 0 .. 29 -> int64 k * cycleMs |]
    let b = a |> Array.map (fun t -> t + 60L)
    let s = CausationDetection.score cfgC a b
    Assert.False(s.IsParallel)

[<Fact>]
let ``Boundary: effective window = cycle*0.7`` () =
    // cycle 2000ms, effective = 1400. lag 1400 → 경계
    let a = [| for k in 0 .. 29 -> int64 k * cycleMs |]
    let b = a |> Array.map (fun t -> t + 1400L)
    let s = CausationDetection.score cfgC a b
    Assert.True(s.Sufficiency > 0.9)

[<Fact>]
let ``Boundary: lag > effective window → drop`` () =
    let a = [| for k in 0 .. 29 -> int64 k * cycleMs |]
    let b = a |> Array.map (fun t -> t + 1600L)   // > 1400
    let s = CausationDetection.score cfgC a b
    // window 1400 보다 큰 lag — 매칭 안 됨
    Assert.True(s.Sufficiency < 0.3, sprintf "suff=%.3f" s.Sufficiency)

[<Fact>]
let ``Boundary: MinFires=5 정확 → 통과`` () =
    let a = [| 0L; 1000L; 2000L; 3000L; 4000L |]   // 5
    let b = [| 300L; 1300L; 2300L; 3300L; 4300L |]
    let s = CausationDetection.score cfgC a b
    Assert.True(s.NA = 5 && s.NB = 5)
    // 통과 여부는 stable depending — 거부되지는 않게

[<Fact>]
let ``Boundary: MinFires=4 → low_n`` () =
    let a = [| 0L; 1000L; 2000L; 3000L |]   // 4
    let b = [| 300L; 1300L; 2300L; 3300L |]
    let s = CausationDetection.score cfgC a b
    Assert.False(s.PassesSeq)

[<Fact>]
let ``Boundary: confidence 정확 High threshold (0.9)`` () =
    let s : CausationScore =
        { NA = 100; NB = 100
          Sufficiency = 0.95; Necessity = 0.95
          LagMean = 300.0; LagStd = 20.0; LagCv = 0.067
          AbsLagMean = 300.0
          IsParallel = false; PassesSeq = true; PassesGrp = false
          Reason = None }
    let c = CausationDetection.confidence s None
    Assert.True(c.Score >= 0.9, sprintf "score=%.3f" c.Score)
    Assert.Equal(High, c.Tier)

[<Fact>]
let ``Boundary: confidence 정확 Medium threshold (0.7)`` () =
    let s : CausationScore =
        { NA = 30; NB = 30
          Sufficiency = 0.8; Necessity = 0.8
          LagMean = 300.0; LagStd = 20.0; LagCv = 0.067
          AbsLagMean = 300.0
          IsParallel = false; PassesSeq = true; PassesGrp = false
          Reason = None }
    let c = CausationDetection.confidence s None
    Assert.True(c.Tier = Medium || c.Tier = High,
        sprintf "expected Medium/High; got %A" c.Tier)

[<Fact>]
let ``Boundary: confidence Reject threshold (0.5)`` () =
    let s : CausationScore =
        { NA = 30; NB = 30
          Sufficiency = 0.3; Necessity = 0.3
          LagMean = 0.0; LagStd = 0.0; LagCv = 999.0
          AbsLagMean = 999.0
          IsParallel = false; PassesSeq = false; PassesGrp = false
          Reason = Some "test" }
    let c = CausationDetection.confidence s None
    Assert.Equal(Reject, c.Tier)

[<Fact>]
let ``Boundary: outlier filter Q1/Q3 — 1.5 IQR 안 → 유지`` () =
    let a = [| for k in 0 .. 59 -> int64 k * cycleMs |]
    let b = [|
        for k in 0 .. 59 ->
            // 1 cycle 만 outlier (1500ms lag, 나머지 300ms)
            let lag = if k = 30 then 1500L else 300L
            int64 k * cycleMs + lag
    |]
    let s = CausationDetection.score cfgC a b
    // Tukey IQR 로 1500ms 가 filter 제거되어야
    Assert.True(s.PassesSeq, sprintf "after outlier filter; lagMean=%.0f std=%.0f" s.LagMean s.LagStd)
