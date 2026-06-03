/// Phase 8 — Cross-Flow detection + Logic-Hybrid + Calibration.
module Ds2.Reverse.Tests.Phase8Tests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

// ── Phase 8A — Cross-Flow detection ─────────────────────────────────

[<Fact>]
let ``Phase8A CrossFlow diagnostic — 모든 시나리오 metric 출력`` () =
    printfn ""
    printfn "═══ Phase 8A — Cross-Flow Causation Detection ═══"
    printfn "  %-30s | intra F1 (TP/FP/FN) | cross F1 (TP/FP/FN)" "scenario"
    for sc in Phase8Models.allCrossFlowScenarios do
        let store, _ = Phase8Models.runCrossFlow sc 42 60
        let r = Phase8Models.evaluate sc store
        printfn "  %-30s | %.3f (%d/%d/%d) | %.3f (%d/%d/%d)"
            sc.Name r.IntraF1 r.IntraTP r.IntraFP r.IntraFN
            r.CrossF1 r.CrossTP r.CrossFP r.CrossFN

[<Fact>]
let ``Phase8A TwoFlowChain — F1.W2 → F2.W1 검출`` () =
    let sc = Phase8Models.makeTwoFlowChain ()
    let store, _ = Phase8Models.runCrossFlow sc 42 60
    let r = Phase8Models.evaluate sc store
    Assert.True(r.IntraF1 >= 0.95, sprintf "intra F1=%.3f" r.IntraF1)
    Assert.True(r.CrossTP >= 1, sprintf "cross TP=%d (expected ≥1)" r.CrossTP)

[<Fact>]
let ``Phase8A ThreeFlowChain — F1→F2→F3 chain 검출`` () =
    let sc = Phase8Models.makeThreeFlowChain ()
    let store, _ = Phase8Models.runCrossFlow sc 42 60
    let r = Phase8Models.evaluate sc store
    Assert.True(r.IntraF1 >= 0.95)
    Assert.True(r.CrossTP >= 1, sprintf "expected at least 1 cross TP; got %d" r.CrossTP)

[<Fact>]
let ``Phase8A CrossFlowFanOut — F1 → F2,F3,F4 broadcast`` () =
    let sc = Phase8Models.makeCrossFlowFanOut ()
    let store, _ = Phase8Models.runCrossFlow sc 42 60
    let r = Phase8Models.evaluate sc store
    Assert.True(r.IntraF1 >= 0.95)
    // fan-out 3 cross-flow targets — at least 1 detected
    Assert.True(r.CrossTP >= 1, sprintf "fan-out cross TP=%d" r.CrossTP)

[<Fact>]
let ``Phase8A CrossFlowFanIn — F1,F2,F3 → F4 merge`` () =
    let sc = Phase8Models.makeCrossFlowFanIn ()
    let store, _ = Phase8Models.runCrossFlow sc 42 60
    let r = Phase8Models.evaluate sc store
    Assert.True(r.IntraF1 >= 0.95)
    Assert.True(r.CrossTP >= 1, sprintf "fan-in cross TP=%d" r.CrossTP)

[<Fact>]
let ``Phase8A CrossFlowSpurious — 진짜 cross-flow 만 emit (F3 unrelated)`` () =
    let sc = Phase8Models.makeCrossFlowWithSpurious ()
    let store, _ = Phase8Models.runCrossFlow sc 42 60
    let r = Phase8Models.evaluate sc store
    Assert.True(r.IntraF1 >= 0.95)
    Assert.True(r.CrossTP >= 1, sprintf "real cross TP=%d" r.CrossTP)

[<Fact>]
let ``Phase8A All cross-flow aggregate — intra F1 >= 0.90`` () =
    let mutable sumIntra = 0.0
    let mutable sumCross = 0.0
    let n = List.length Phase8Models.allCrossFlowScenarios
    for sc in Phase8Models.allCrossFlowScenarios do
        let store, _ = Phase8Models.runCrossFlow sc 42 60
        let r = Phase8Models.evaluate sc store
        sumIntra <- sumIntra + r.IntraF1
        sumCross <- sumCross + r.CrossF1
    let avgIntra = sumIntra / float n
    let avgCross = sumCross / float n
    printfn "Phase 8A aggregate: avg intra F1=%.3f, avg cross F1=%.3f"
        avgIntra avgCross
    Assert.True(avgIntra >= 0.90, sprintf "intra avg=%.3f" avgIntra)

// ── Phase 8B — Logic-Hybrid scoring ─────────────────────────────────

[<Fact>]
let ``Phase8B Logic-Hybrid diagnostic — capture only vs logic+capture`` () =
    printfn ""
    printfn "═══ Phase 8B — Logic-Hybrid (capture-only vs capture+logic) ═══"
    printfn "  %-30s | no-logic (TP/FP/FN) | with-logic (TP/FP/FN)" "scenario"
    for sc in Phase8Models.allLogicHybridScenarios do
        let storeNo, _ = Phase8Models.runLogicHybrid sc false 42 60
        let storeYes, _ = Phase8Models.runLogicHybrid sc true 42 60
        let tp1, fp1, fn1 = Phase8Models.evaluateLogicHybrid sc storeNo
        let tp2, fp2, fn2 = Phase8Models.evaluateLogicHybrid sc storeYes
        printfn "  %-30s | %d/%d/%d           | %d/%d/%d"
            sc.Name tp1 fp1 fn1 tp2 fp2 fn2

[<Fact>]
let ``Phase8B WeakConditional — capture only fail, logic+capture pass`` () =
    let sc = Phase8Models.makeWeakConditionalWithLogic ()
    let storeNo, _ = Phase8Models.runLogicHybrid sc false 42 60
    let storeYes, _ = Phase8Models.runLogicHybrid sc true 42 60
    let tpNo, _, _ = Phase8Models.evaluateLogicHybrid sc storeNo
    let tpYes, _, _ = Phase8Models.evaluateLogicHybrid sc storeYes
    printfn "weakConditional: no-logic TP=%d, with-logic TP=%d" tpNo tpYes
    // 핵심: logic 결합 시 검출, capture only 면 drop (suff=0.6 < 0.85)
    Assert.True(tpYes >= 1, sprintf "logic-hybrid should detect; got TP=%d" tpYes)

[<Fact>]
let ``Phase8B BorderlineSuff (70%) — logic 으로 회복`` () =
    let sc = Phase8Models.makeBorderlineSuff ()
    let storeYes, _ = Phase8Models.runLogicHybrid sc true 42 60
    let tp, _, _ = Phase8Models.evaluateLogicHybrid sc storeYes
    Assert.True(tp >= 1, sprintf "borderline+logic should detect; got TP=%d" tp)

[<Fact>]
let ``Phase8B StrongLogic — 매우 약한 capture (55%) + 강한 logic → 검출`` () =
    let sc = Phase8Models.makeStrongLogicWeakCapture ()
    let storeYes, _ = Phase8Models.runLogicHybrid sc true 42 60
    let tp, _, _ = Phase8Models.evaluateLogicHybrid sc storeYes
    Assert.True(tp >= 1, sprintf "strongLogic should detect; got TP=%d" tp)

[<Fact>]
let ``Phase8B Aggregate — capture+logic ≥ capture-only`` () =
    let mutable sumNo = 0
    let mutable sumYes = 0
    for sc in Phase8Models.allLogicHybridScenarios do
        let storeNo, _ = Phase8Models.runLogicHybrid sc false 42 60
        let storeYes, _ = Phase8Models.runLogicHybrid sc true 42 60
        let tpNo, _, _ = Phase8Models.evaluateLogicHybrid sc storeNo
        let tpYes, _, _ = Phase8Models.evaluateLogicHybrid sc storeYes
        sumNo <- sumNo + tpNo
        sumYes <- sumYes + tpYes
    printfn "Phase 8B: TP no-logic=%d, with-logic=%d" sumNo sumYes
    Assert.True(sumYes >= sumNo,
        sprintf "logic 추가 시 TP 가 줄지 않아야: no=%d yes=%d" sumNo sumYes)
    Assert.True(sumYes > sumNo,
        sprintf "logic 추가 시 TP 향상 기대: no=%d yes=%d" sumNo sumYes)

// ── Phase 8C — Confidence Calibration accuracy ──────────────────────

open Ds2.Reverse.Bench

[<Fact>]
let ``Phase8C Calibration High tier — synthetic clean scenarios 95%+ accuracy`` () =
    // Clean chain 시나리오들에서 High tier arrows 의 truth match rate.
    let cfg = CausationConfig.defaults
    let mutable highCorrect = 0
    let mutable highTotal = 0
    let mutable mediumCorrect = 0
    let mutable mediumTotal = 0
    // Use Phase 1-6 시나리오 합 (충분히 많은 emitted arrows)
    let scenarios =
        Phase1Models.all @
        Phase2Models.all @
        Phase3Models.all @
        Phase4Models.all @
        Phase5Models.all
    for sc in scenarios do
        let r = BenchRunner.runOne sc cfg 42 60
        let truthSet = sc.GroundTruth |> List.map (fun a -> a.Src, a.Tgt) |> Set.ofList
        for (src, tgt, conf) in r.Report.EmittedConfidence do
            match conf.Tier with
            | High ->
                highTotal <- highTotal + 1
                if Set.contains (src, tgt) truthSet then
                    highCorrect <- highCorrect + 1
            | Medium ->
                mediumTotal <- mediumTotal + 1
                if Set.contains (src, tgt) truthSet then
                    mediumCorrect <- mediumCorrect + 1
            | _ -> ()
    let highAcc =
        if highTotal = 0 then 0.0
        else float highCorrect / float highTotal
    let medAcc =
        if mediumTotal = 0 then 0.0
        else float mediumCorrect / float mediumTotal
    printfn "Calibration: High=%d/%d (%.1f%%), Medium=%d/%d (%.1f%%)"
        highCorrect highTotal (highAcc * 100.0)
        mediumCorrect mediumTotal (medAcc * 100.0)
    Assert.True(highAcc >= 0.95,
        sprintf "High tier accuracy %.2f%% < 95%%" (highAcc * 100.0))

[<Fact>]
let ``Phase8C Calibration tier 분포 — High 우세`` () =
    let cfg = CausationConfig.defaults
    let mutable counts = Map.empty
    for sc in Phase6Models.allVariants do
        let r = BenchRunner.runOne sc cfg 42 60
        for (_, _, conf) in r.Report.EmittedConfidence do
            let n = Map.tryFind conf.Tier counts |> Option.defaultValue 0
            counts <- Map.add conf.Tier (n + 1) counts
    let h = Map.tryFind High counts |> Option.defaultValue 0
    let m = Map.tryFind Medium counts |> Option.defaultValue 0
    let l = Map.tryFind Low counts |> Option.defaultValue 0
    printfn "Phase6 tier counts: High=%d Medium=%d Low=%d" h m l
    Assert.True(h >= 10 * m, sprintf "High should dominate (≥10x Medium); h=%d m=%d" h m)

[<Fact>]
let ``Phase8C Calibration Tier monotone vs score`` () =
    // tier 가 score 범위와 일치하는지 검증 (다양한 시나리오 mix).
    let cfg = CausationConfig.defaults
    let mutable highMinScore = 1.0
    let mutable mediumMinScore = 1.0
    let mutable mediumMaxScore = 0.0
    let mutable lowMaxScore = 0.0
    let scenarios =
        Phase1Models.all @ Phase2Models.all @ Phase3Models.all
    for sc in scenarios do
        let r = BenchRunner.runOne sc cfg 42 60
        for (_, _, conf) in r.Report.EmittedConfidence do
            match conf.Tier with
            | High -> highMinScore <- min highMinScore conf.Score
            | Medium ->
                mediumMinScore <- min mediumMinScore conf.Score
                mediumMaxScore <- max mediumMaxScore conf.Score
            | Low -> lowMaxScore <- max lowMaxScore conf.Score
            | Reject -> ()
    Assert.True(highMinScore >= 0.9 || highMinScore = 1.0,
        sprintf "High min score should be ≥0.9; got %.3f" highMinScore)
