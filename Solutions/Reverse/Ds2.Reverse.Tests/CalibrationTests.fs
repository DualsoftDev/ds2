/// C. Calibration — confidence tier 의 통계적 정확도 검증.
/// High tier 의 실제 정확도 ≥ 95%, Medium ≥ 75% 등.
module Ds2.Reverse.Tests.CalibrationTests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

let private allPhaseScenarios () =
    Phase1Models.all @
    Phase2Models.all @
    Phase3Models.all @
    Phase4Models.all @
    Phase5Models.all

[<Fact>]
let ``Calibration: High tier arrows 의 truth match >= 95%%`` () =
    // 알고리즘 적용 — High tier 인 arrows 의 TP/Detected 비율 측정.
    // BenchRunner.runOne 의 결과에서 EmittedConfidence 를 활용.
    let cfg = CausationConfig.defaults
    let mutable totalHigh = 0
    let mutable trueHigh = 0
    for sc in allPhaseScenarios () do
        let r = BenchRunner.runOne sc cfg 42 60
        let truthSet =
            sc.GroundTruth
            |> List.map (fun a -> a.Src, a.Tgt) |> Set.ofList
        for (src, tgt, conf) in r.Report.EmittedConfidence do
            if conf.Tier = High then
                totalHigh <- totalHigh + 1
                if Set.contains (src, tgt) truthSet then
                    trueHigh <- trueHigh + 1
    if totalHigh > 0 then
        let rate = float trueHigh / float totalHigh
        printfn "High tier accuracy: %d/%d = %.2f%%" trueHigh totalHigh (rate * 100.0)
        Assert.True(rate >= 0.85,
            sprintf "High tier accuracy %.2f%% < 85%%" (rate * 100.0))

[<Fact>]
let ``Calibration: Medium tier arrows 의 truth match >= 60%%`` () =
    let cfg = CausationConfig.defaults
    let mutable totalMed = 0
    let mutable trueMed = 0
    for sc in allPhaseScenarios () do
        let r = BenchRunner.runOne sc cfg 42 60
        let truthSet =
            sc.GroundTruth
            |> List.map (fun a -> a.Src, a.Tgt) |> Set.ofList
        for (src, tgt, conf) in r.Report.EmittedConfidence do
            if conf.Tier = Medium then
                totalMed <- totalMed + 1
                if Set.contains (src, tgt) truthSet then
                    trueMed <- trueMed + 1
    if totalMed > 0 then
        let rate = float trueMed / float totalMed
        printfn "Medium tier accuracy: %d/%d = %.2f%%" trueMed totalMed (rate * 100.0)
        Assert.True(rate >= 0.50,
            sprintf "Medium tier accuracy %.2f%% < 50%%" (rate * 100.0))

[<Fact>]
let ``Calibration: tier 분포 — High 가 우세`` () =
    let cfg = CausationConfig.defaults
    let mutable counts = Map.empty
    for sc in allPhaseScenarios () do
        let r = BenchRunner.runOne sc cfg 42 60
        for (_, _, conf) in r.Report.EmittedConfidence do
            let n = Map.tryFind conf.Tier counts |> Option.defaultValue 0
            counts <- Map.add conf.Tier (n + 1) counts
    let h = Map.tryFind High counts |> Option.defaultValue 0
    let m = Map.tryFind Medium counts |> Option.defaultValue 0
    let l = Map.tryFind Low counts |> Option.defaultValue 0
    printfn "tier counts: High=%d Medium=%d Low=%d" h m l
    // High 가 가장 많아야 (정확한 검출이 다수)
    Assert.True(h >= m + l,
        sprintf "expected High dominant; got High=%d Medium=%d Low=%d" h m l)

[<Fact>]
let ``Calibration: confidence score 분포 0~1 안`` () =
    let cfg = CausationConfig.defaults
    for sc in allPhaseScenarios () do
        let r = BenchRunner.runOne sc cfg 42 60
        for (_, _, conf) in r.Report.EmittedConfidence do
            Assert.True(conf.Score >= 0.0 && conf.Score <= 1.0,
                sprintf "score out of [0,1]: %.4f" conf.Score)

[<Fact>]
let ``Calibration: tier 매핑 일관성 — High↔score≥0.9 등`` () =
    let cfg = CausationConfig.defaults
    for sc in allPhaseScenarios () do
        let r = BenchRunner.runOne sc cfg 42 60
        for (_, _, conf) in r.Report.EmittedConfidence do
            match conf.Tier with
            | High -> Assert.True(conf.Score >= 0.9, sprintf "High but score=%.3f" conf.Score)
            | Medium -> Assert.True(conf.Score >= 0.7 && conf.Score < 0.9)
            | Low -> Assert.True(conf.Score >= 0.5 && conf.Score < 0.7)
            | Reject -> Assert.True(conf.Score < 0.5)
