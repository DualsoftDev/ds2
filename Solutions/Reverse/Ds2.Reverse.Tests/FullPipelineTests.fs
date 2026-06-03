/// 전체 파이프라인 통합 테스트 — 모든 알고리즘 컴포넌트 함께 작동 확인.
module Ds2.Reverse.Tests.FullPipelineTests

open Xunit
open System
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``Full Pipeline: chain + auto-tune + confidence + anomaly`` () =
    // 시뮬: A→B→C chain, 60 cycles, 그 중 10 cycle 은 비정상 (shifted)
    let cycleMs = 2000L
    let normalEvents =
        [ for k in 0 .. 49 do
            yield int64 k * cycleMs, "A"
            yield int64 k * cycleMs + 300L, "B"
            yield int64 k * cycleMs + 600L, "C" ]
    let anomalyEvents =
        [ for k in 50 .. 59 do
            // shifted 비정상 cycles
            yield int64 k * cycleMs, "A"
            yield int64 k * cycleMs + 1500L, "B"     // 매우 지연
            yield int64 k * cycleMs + 1800L, "C" ]
    let allEvents = normalEvents @ anomalyEvents

    // 1) Auto-tune noise level
    let noise = CausationDetection.estimateNoiseLevel
                    (allEvents |> List.map (fun (t, n) -> { T = t; Name = n }))
                    cycleMs
    Assert.True(noise > 0.0, "expected nonzero noise level from mixed data")

    // 2) Causation score with cycle-hint
    let cfg = CausationConfig.withCycleHint cycleMs CausationConfig.defaults
    let cfg = CausationConfig.withNoiseLevel noise cfg
    let aTimes = allEvents |> List.filter (fun (_, n) -> n = "A") |> List.map fst
    let bTimes = allEvents |> List.filter (fun (_, n) -> n = "B") |> List.map fst
    let scoreAB = CausationDetection.score cfg aTimes bTimes
    Assert.True(scoreAB.NA >= 50)
    // anomaly cycles 가 섞여 있어 suff 가 완벽하지는 않지만 ~0.7 이상은 기대
    Assert.True(scoreAB.Sufficiency >= 0.7,
        sprintf "expected suff >= 0.7 with mixed normal+anomaly; got %.3f"
            scoreAB.Sufficiency)

    // 3) Confidence
    let conf = CausationDetection.confidence scoreAB None
    Assert.True(conf.Score >= 0.5,
        sprintf "expected confidence >= 0.5; got %.3f (tier=%A)" conf.Score conf.Tier)

    // 4) Anomaly detection on cycles
    let pattern = AnomalyDetection.learn allEvents cycleMs 30
    let _, anomalous = AnomalyDetection.analyzeAllCycles pattern allEvents cycleMs 3.0
    let lateCycles = anomalous |> List.filter (fun i -> i >= 50)
    Assert.True(List.length lateCycles >= 8,
        sprintf "expected 8+ anomalous cycles in 50-59 range; got %d"
            (List.length lateCycles))

    // 5) Bayesian aggregate — capture confidence + (가상 logic strength 0.7)
    let posterior = CausationDetection.bayesianAggregate [ conf.Score; 0.7 ]
    Assert.True(posterior > conf.Score,
        sprintf "Bayesian (capture %.3f + logic 0.7) -> %.3f, expected > capture alone"
            conf.Score posterior)

[<Fact>]
let ``Full Pipeline: online + drift end-to-end`` () =
    // 60 cycles streaming - 모두 정상
    let online = OnlineDetection.OnlineScore()
    online.SetWindow 2000L
    let snapshots = ResizeArray<ArrowConfidence>()
    for k in 0 .. 59 do
        let t0 = int64 k * 2000L
        online.AddA t0
        online.AddB (t0 + 300L)
        if (k + 1) % 10 = 0 then
            snapshots.Add(online.SnapshotConfidence())
    Assert.Equal(6, snapshots.Count)

    // Drift analysis — converging confidence → Picking or Stable
    let alert = OnlineDetection.analyzeDrift (List.ofSeq snapshots)
    match alert with
    | OnlineDetection.Stable -> Assert.True true
    | OnlineDetection.Picking(slope, _) ->
        Assert.True(slope > 0.0, "expected positive slope for converging confidence")
    | other -> Assert.Fail (sprintf "expected Stable or Picking; got %A" other)
