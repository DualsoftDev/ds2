/// B6 Online detection — streaming + anytime snapshot 검증.
module Ds2.Reverse.Tests.OnlineTests

open Xunit
open Ds2.Reverse.Core

[<Fact>]
let ``Online: empty stream snapshot is zero`` () =
    let s = OnlineDetection.OnlineScore()
    let snap = s.Snapshot()
    Assert.Equal(0, snap.NA)
    Assert.Equal(0, snap.NB)
    Assert.False snap.PassesSeq

[<Fact>]
let ``Online: chain A->B converges to high confidence`` () =
    let s = OnlineDetection.OnlineScore()
    s.SetWindow 2000L
    // 60 cycles: A@t, B@t+300, cycle = 2000ms
    for k in 0 .. 59 do
        let t0 = int64 k * 2000L
        s.AddA t0
        s.AddB (t0 + 300L)
    let snap = s.Snapshot()
    Assert.True(snap.PassesSeq, sprintf "expected passes_seq; got %A" snap)
    Assert.True(snap.Sufficiency >= 0.95)
    Assert.True(snap.LagMean > 290.0 && snap.LagMean < 310.0)

[<Fact>]
let ``Online: anytime snapshot reflects partial data`` () =
    let s = OnlineDetection.OnlineScore()
    s.SetWindow 2000L
    let snapshots = ResizeArray<ArrowConfidence>()
    for k in 0 .. 49 do
        let t0 = int64 k * 2000L
        s.AddA t0
        s.AddB (t0 + 250L)
        if (k + 1) % 10 = 0 then
            snapshots.Add (s.SnapshotConfidence())
    // confidence 는 시간이 지날수록 증가하거나 유지 (NA 증가 → nReliability 증가)
    Assert.True(snapshots.Count = 5)
    let scores = snapshots |> Seq.map (fun c -> c.Score) |> Seq.toList
    // 단조 비-감소
    let isMonotone =
        scores
        |> List.pairwise
        |> List.forall (fun (a, b) -> b >= a - 0.05)   // tolerance
    Assert.True(isMonotone,
        sprintf "expected ~monotone non-decreasing; got %A" scores)

[<Fact>]
let ``Drift Alert: stable history -> Stable`` () =
    // 모두 같은 score → stable
    let history =
        [ for _ in 1 .. 10 ->
            { Score = 0.85; Tier = Medium; Evidence = []; NReliability = 1.0 } ]
    let alert = OnlineDetection.analyzeDrift history
    match alert with
    | OnlineDetection.Stable -> Assert.True true
    | _ -> Assert.Fail (sprintf "expected Stable; got %A" alert)

[<Fact>]
let ``Drift Alert: dropping confidence -> Dropping`` () =
    // 0.95 → 0.4 점진 감소
    let history =
        [ for i in 0 .. 9 ->
            let s = 0.95 - float i * 0.06
            { Score = s; Tier = High; Evidence = []; NReliability = 1.0 } ]
    let alert = OnlineDetection.analyzeDrift history
    match alert with
    | OnlineDetection.Dropping(slope, _) ->
        Assert.True(slope < -0.04, sprintf "expected steep drop; got slope=%.4f" slope)
    | _ -> Assert.Fail (sprintf "expected Dropping; got %A" alert)

[<Fact>]
let ``Drift Alert: rising confidence -> Picking`` () =
    let history =
        [ for i in 0 .. 9 ->
            let s = 0.3 + float i * 0.06
            { Score = s; Tier = Low; Evidence = []; NReliability = 1.0 } ]
    let alert = OnlineDetection.analyzeDrift history
    match alert with
    | OnlineDetection.Picking(slope, _) ->
        Assert.True(slope > 0.04, sprintf "expected steep rise; got slope=%.4f" slope)
    | _ -> Assert.Fail (sprintf "expected Picking; got %A" alert)

[<Fact>]
let ``Online: no causation -> low confidence`` () =
    let s = OnlineDetection.OnlineScore()
    s.SetWindow 2000L
    let rng = System.Random(42)
    // A 와 B 가 무관 random 발화
    for k in 0 .. 99 do
        s.AddA (int64 (rng.Next(0, 10000)))
        s.AddB (int64 (rng.Next(0, 10000)))
    let snap = s.Snapshot()
    let conf = s.SnapshotConfidence()
    Assert.False(snap.PassesSeq,
        sprintf "random data shouldn't pass seq; got %A" snap)
    Assert.NotEqual<ConfidenceTier>(High, conf.Tier)
