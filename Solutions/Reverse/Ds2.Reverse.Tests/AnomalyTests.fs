/// B7.1 Anomaly pattern learning tests.
module Ds2.Reverse.Tests.AnomalyTests

open Xunit
open Ds2.Reverse.Core

[<Fact>]
let ``Anomaly: empty events -> no pattern`` () =
    let p = AnomalyDetection.learn [] 2000L 10
    Assert.Equal(0, p.NCyclesLearned)

[<Fact>]
let ``Anomaly: clean pattern -> normal cycles low score`` () =
    // 30 cycles, A@0, B@300 normal
    let events =
        [ for k in 0 .. 29 do
            yield int64 k * 2000L, "A"
            yield int64 k * 2000L + 300L, "B" ]
    let pattern = AnomalyDetection.learn events 2000L 20
    // 21~30 cycle 측정 (학습 후)
    let cycleStart = 25L * 2000L
    let cycleEvents = [ cycleStart, "A"; cycleStart + 300L, "B" ]
    let score = AnomalyDetection.scoreCycle pattern cycleEvents cycleStart
    Assert.True(score < 1.0, sprintf "normal cycle score=%.3f expected <1.0" score)

[<Fact>]
let ``Anomaly: missing event -> high score`` () =
    // 30 cycles A+B 학습, 새 cycle 에 B 없음
    let events =
        [ for k in 0 .. 29 do
            yield int64 k * 2000L, "A"
            yield int64 k * 2000L + 300L, "B" ]
    let pattern = AnomalyDetection.learn events 2000L 20
    let cycleStart = 100L * 2000L
    let cycleEvents = [ cycleStart, "A" ]    // B missing
    let score = AnomalyDetection.scoreCycle pattern cycleEvents cycleStart
    Assert.True(score > 1.5, sprintf "missing-event cycle score=%.3f expected >1.5" score)

[<Fact>]
let ``Anomaly: unknown extra event -> some penalty`` () =
    let events =
        [ for k in 0 .. 29 do
            yield int64 k * 2000L, "A"
            yield int64 k * 2000L + 300L, "B" ]
    let pattern = AnomalyDetection.learn events 2000L 20
    let cycleStart = 100L * 2000L
    let cycleEvents = [
        cycleStart, "A"
        cycleStart + 300L, "B"
        cycleStart + 1000L, "X_UNKNOWN"    // 학습 없던 event
    ]
    let score = AnomalyDetection.scoreCycle pattern cycleEvents cycleStart
    Assert.True(score > 0.5, sprintf "extra-event cycle score=%.3f expected >0.5" score)

[<Fact>]
let ``Anomaly: shifted timing -> moderate score`` () =
    // 정상 30 cycle A@0, B@300 → 이상 cycle B@1500 (shifted 1200ms)
    let events =
        [ for k in 0 .. 29 do
            yield int64 k * 2000L, "A"
            yield int64 k * 2000L + 300L, "B" ]
    let pattern = AnomalyDetection.learn events 2000L 20
    let cycleStart = 100L * 2000L
    let cycleEvents = [
        cycleStart, "A"
        cycleStart + 1500L, "B"
    ]
    let score = AnomalyDetection.scoreCycle pattern cycleEvents cycleStart
    // 1200ms shift / 10ms floor std = 120 sigma → very high (avg over 2 events)
    Assert.True(score > 10.0, sprintf "shifted cycle score=%.3f expected >10.0" score)

[<Fact>]
let ``Anomaly: analyzeAllCycles flags shifted cycle`` () =
    // 60 cycle: 50 정상 + 10 shifted (마지막 10개)
    let events =
        [ for k in 0 .. 49 do
            yield int64 k * 2000L, "A"
            yield int64 k * 2000L + 300L, "B"
          for k in 50 .. 59 do
            yield int64 k * 2000L, "A"
            yield int64 k * 2000L + 1500L, "B" ]    // shifted
    let pattern = AnomalyDetection.learn events 2000L 30
    let _scores, anomalous =
        AnomalyDetection.analyzeAllCycles pattern events 2000L 3.0
    Assert.True(List.length anomalous >= 10,
        sprintf "expected >= 10 anomalous cycles; got %d" (List.length anomalous))
    // 50~59 cycle 들이 anomalous 인지 확인
    let lastTen = anomalous |> List.filter (fun i -> i >= 50)
    Assert.True(List.length lastTen >= 10,
        sprintf "expected last 10 cycles (50-59) to all be flagged; got %d" (List.length lastTen))
