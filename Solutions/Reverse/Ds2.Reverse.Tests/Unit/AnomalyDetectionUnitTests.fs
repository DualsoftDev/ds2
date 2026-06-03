/// U-AnomalyDetection — pattern learn + scoreCycle + analyzeAllCycles 단위 테스트.
module Ds2.Reverse.Tests.Unit.AnomalyDetectionUnitTests

open Xunit
open Ds2.Reverse.Core

[<Fact>]
let ``learn: 너무 짧은 입력 → 0 cycles`` () =
    let p = AnomalyDetection.learn [] 2000L 10
    Assert.Equal(0, p.NCyclesLearned)
    Assert.True(Map.isEmpty p.Offsets)

[<Fact>]
let ``learn: 정상 chain 학습 → mean offset 정확`` () =
    let events =
        [ for k in 0 .. 29 do
            yield int64 k * 2000L, "A"
            yield int64 k * 2000L + 300L, "B" ]
    let p = AnomalyDetection.learn events 2000L 20
    Assert.Equal(20, p.NCyclesLearned)
    let mA, _ = p.Offsets.["A"]
    let mB, _ = p.Offsets.["B"]
    Assert.True(mA < 50.0, sprintf "A offset ~0; got %.1f" mA)
    Assert.True(mB > 280.0 && mB < 320.0, sprintf "B offset ~300; got %.1f" mB)

[<Fact>]
let ``learn: 잡음 데이터 → std 큼`` () =
    let rng = System.Random(42)
    let events =
        [ for k in 0 .. 29 do
            yield int64 k * 2000L + int64 (rng.Next(0, 800)), "A" ]
    let p = AnomalyDetection.learn events 2000L 20
    let _, stdA = p.Offsets.["A"]
    Assert.True(stdA > 100.0, sprintf "noisy std ≥ 100; got %.1f" stdA)

[<Fact>]
let ``scoreCycle: 정확 정상 cycle → low score`` () =
    let events =
        [ for k in 0 .. 29 do
            yield int64 k * 2000L, "A"
            yield int64 k * 2000L + 300L, "B" ]
    let p = AnomalyDetection.learn events 2000L 20
    let cycleStart = 50L * 2000L
    let cevents = [ cycleStart, "A"; cycleStart + 300L, "B" ]
    let score = AnomalyDetection.scoreCycle p cevents cycleStart
    Assert.True(score < 1.0)

[<Fact>]
let ``scoreCycle: 누락 event → 페널티 5 sigma`` () =
    let events =
        [ for k in 0 .. 29 do
            yield int64 k * 2000L, "A"
            yield int64 k * 2000L + 300L, "B" ]
    let p = AnomalyDetection.learn events 2000L 20
    let cycleStart = 100L * 2000L
    let cevents = [ cycleStart, "A" ]   // B 누락
    let score = AnomalyDetection.scoreCycle p cevents cycleStart
    Assert.True(score > 1.5)

[<Fact>]
let ``scoreCycle: 추가 unknown event → 페널티 3`` () =
    let events =
        [ for k in 0 .. 29 do
            yield int64 k * 2000L, "A"
            yield int64 k * 2000L + 300L, "B" ]
    let p = AnomalyDetection.learn events 2000L 20
    let cycleStart = 100L * 2000L
    let cevents = [ cycleStart, "A"; cycleStart + 300L, "B"; cycleStart + 1000L, "X" ]
    let score = AnomalyDetection.scoreCycle p cevents cycleStart
    Assert.True(score > 0.5)

[<Fact>]
let ``scoreCycle: shifted timing → high score`` () =
    let events =
        [ for k in 0 .. 29 do
            yield int64 k * 2000L, "A"
            yield int64 k * 2000L + 300L, "B" ]
    let p = AnomalyDetection.learn events 2000L 20
    let cycleStart = 100L * 2000L
    let cevents = [ cycleStart, "A"; cycleStart + 1500L, "B" ]   // 1200ms shift
    let score = AnomalyDetection.scoreCycle p cevents cycleStart
    Assert.True(score > 10.0)

[<Fact>]
let ``analyzeAllCycles: threshold 미만 → 빈 anomalous`` () =
    let events =
        [ for k in 0 .. 49 do
            yield int64 k * 2000L, "A"
            yield int64 k * 2000L + 300L, "B" ]
    let p = AnomalyDetection.learn events 2000L 30
    let _, anomalous = AnomalyDetection.analyzeAllCycles p events 2000L 3.0
    Assert.True(List.length anomalous <= 5,
        sprintf "expected few/no anomalous cycles in clean data; got %d" (List.length anomalous))

[<Fact>]
let ``analyzeAllCycles: 절반 anomalous → flag`` () =
    let events =
        [ for k in 0 .. 29 do
            yield int64 k * 2000L, "A"
            yield int64 k * 2000L + 300L, "B"
          for k in 30 .. 59 do
            yield int64 k * 2000L, "A"
            yield int64 k * 2000L + 1500L, "B" ]   // shifted
    let p = AnomalyDetection.learn events 2000L 20
    let _, anomalous = AnomalyDetection.analyzeAllCycles p events 2000L 3.0
    let lateAnomalous = anomalous |> List.filter (fun k -> k >= 30) |> List.length
    Assert.True(lateAnomalous >= 25,
        sprintf "expected most of cycles 30+ flagged; got %d" lateAnomalous)
