/// U-OnlineDetection — Welford streaming + analyzeDrift 단위 테스트.
module Ds2.Reverse.Tests.Unit.OnlineDetectionUnitTests

open Xunit
open Ds2.Reverse.Core

[<Fact>]
let ``OnlineScore: 새로 만들면 모두 0`` () =
    let s = OnlineDetection.OnlineScore()
    let snap = s.Snapshot()
    Assert.Equal(0, snap.NA)
    Assert.Equal(0, snap.NB)
    Assert.Equal(0.0, snap.Sufficiency)
    Assert.False(snap.PassesSeq)

[<Fact>]
let ``OnlineScore: AddA 만 호출 → NA 증가, B 없음`` () =
    let s = OnlineDetection.OnlineScore()
    s.SetWindow 3000L
    for k in 0 .. 19 do s.AddA (int64 k * 1000L)
    let snap = s.Snapshot()
    Assert.Equal(20, snap.NA)
    Assert.Equal(0, snap.NB)
    Assert.False(snap.PassesSeq)

[<Fact>]
let ``OnlineScore: AddB 만 호출 → NB 증가, A 없음`` () =
    let s = OnlineDetection.OnlineScore()
    s.SetWindow 3000L
    for k in 0 .. 19 do s.AddB (int64 k * 1000L)
    let snap = s.Snapshot()
    Assert.Equal(0, snap.NA)
    Assert.Equal(20, snap.NB)

[<Fact>]
let ``OnlineScore: cycle 별 A → B → 정상 converge`` () =
    let s = OnlineDetection.OnlineScore()
    s.SetWindow 2000L
    for k in 0 .. 59 do
        let t0 = int64 k * 2000L
        s.AddA t0
        s.AddB (t0 + 300L)
    let snap = s.Snapshot()
    Assert.True(snap.PassesSeq)
    Assert.True(snap.LagMean > 290.0 && snap.LagMean < 310.0)

[<Fact>]
let ``OnlineScore: window 밖 B 는 매칭 안 됨`` () =
    let s = OnlineDetection.OnlineScore()
    s.SetWindow 500L
    s.AddA 0L
    s.AddB 800L      // > window 500ms (with default parallelLag 50)
    let snap = s.Snapshot()
    Assert.Equal(1, snap.NA)
    Assert.Equal(1, snap.NB)
    // window 500 + parallelLag 50 = 550 < 800. necessity 매칭 실패.
    Assert.Equal(0.0, snap.Necessity)

[<Fact>]
let ``OnlineScore: SnapshotConfidence 반환은 안정`` () =
    let s = OnlineDetection.OnlineScore()
    s.SetWindow 2000L
    for k in 0 .. 49 do
        s.AddA (int64 k * 2000L)
        s.AddB (int64 k * 2000L + 250L)
    let c = s.SnapshotConfidence()
    Assert.True(c.Score >= 0.5,
        sprintf "expected confidence ≥ 0.5 after 50 cycles; got %.3f" c.Score)

[<Fact>]
let ``OnlineScore: SetParallelLag 적용`` () =
    let s = OnlineDetection.OnlineScore()
    s.SetParallelLag 100L
    s.SetWindow 3000L
    // lag ≈ 50ms (parallel zone)
    for k in 0 .. 29 do
        s.AddA (int64 k * 2000L)
        s.AddB (int64 k * 2000L + 30L)
    let snap = s.Snapshot()
    Assert.True(snap.IsParallel)

// ── analyzeDrift (5 tests) ──────────────────────────────────────────

let private mkHistory (scores: float list) : ArrowConfidence list =
    scores |> List.map (fun s ->
        { Score = s; Tier = Medium; Evidence = []; NReliability = 1.0 })

[<Fact>]
let ``analyzeDrift: 너무 짧음 → Stable`` () =
    let h = mkHistory [ 0.5; 0.6 ]   // 2 points only
    Assert.Equal(OnlineDetection.Stable, OnlineDetection.analyzeDrift h)

[<Fact>]
let ``analyzeDrift: 일정 → Stable`` () =
    let h = mkHistory (List.replicate 10 0.85)
    Assert.Equal(OnlineDetection.Stable, OnlineDetection.analyzeDrift h)

[<Fact>]
let ``analyzeDrift: 단조 증가 → Picking`` () =
    let h = mkHistory [ 0.3; 0.4; 0.5; 0.6; 0.7; 0.8; 0.9 ]
    match OnlineDetection.analyzeDrift h with
    | OnlineDetection.Picking(slope, _) -> Assert.True(slope > 0.0)
    | other -> Assert.Fail (sprintf "expected Picking; got %A" other)

[<Fact>]
let ``analyzeDrift: 단조 감소 → Dropping`` () =
    let h = mkHistory [ 0.9; 0.8; 0.7; 0.6; 0.5; 0.4; 0.3 ]
    match OnlineDetection.analyzeDrift h with
    | OnlineDetection.Dropping(slope, _) -> Assert.True(slope < 0.0)
    | other -> Assert.Fail (sprintf "expected Dropping; got %A" other)

[<Fact>]
let ``analyzeDrift: 진동 (oscillation) → Stable (slope ~ 0)`` () =
    let h = mkHistory [ 0.5; 0.7; 0.5; 0.7; 0.5; 0.7; 0.5 ]
    Assert.Equal(OnlineDetection.Stable, OnlineDetection.analyzeDrift h)
