/// Phase 6 — F (Flow) 차원. nFlows 1..20 알고리즘 강화 추적.
module Ds2.Reverse.Tests.Phase6Tests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``Phase6 F dimension diagnostic — flow 1~20 per-scenario F1`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 6 F (Flow) Dimension — nFlows 1..20 ═══"
    printfn ""
    printfn "  %-8s | %5s | %5s | %4s %4s %4s | %s"
            "nFlows" "F1" "P/R" "TP" "FP" "FN" "Truth"
    printfn "  -------- | ----- | ----- | ---- ---- ---- | -----"
    let mutable sumF1 = 0.0
    let mutable perfect = 0
    for sc in Phase6Models.all do
        let r = BenchRunner.runOne sc cfg 42 60
        sumF1 <- sumF1 + r.F1
        if r.F1 >= 0.9999 then perfect <- perfect + 1
        printfn "  %-8s | %.3f | %.2f/%.2f | %4d %4d %4d | %4d"
            sc.Name r.F1 r.Precision r.Recall r.TP r.FP r.FN r.Truth
    let avg = sumF1 / float (List.length Phase6Models.all)
    printfn ""
    printfn "  Avg F1: %.4f  Perfect: %d/20" avg perfect
    Assert.True true

[<Fact>]
let ``Phase6 F1: flow=1 → perfect (single flow case)`` () =
    let sc = Phase6Models.withNFlows 1
    let r = BenchRunner.runOne sc CausationConfig.defaults 42 60
    Assert.True(r.F1 >= 0.99, sprintf "f1 F1=%.3f" r.F1)

[<Fact>]
let ``Phase6 F1: flow=5 → high F1`` () =
    let sc = Phase6Models.withNFlows 5
    let r = BenchRunner.runOne sc CausationConfig.defaults 42 60
    Assert.True(r.F1 >= 0.95, sprintf "f5 F1=%.3f" r.F1)

[<Fact>]
let ``Phase6 F1: flow=10 → still high`` () =
    let sc = Phase6Models.withNFlows 10
    let r = BenchRunner.runOne sc CausationConfig.defaults 42 60
    Assert.True(r.F1 >= 0.90, sprintf "f10 F1=%.3f" r.F1)

[<Fact>]
let ``Phase6 F1: flow=20 → degradation acceptable`` () =
    let sc = Phase6Models.withNFlows 20
    let r = BenchRunner.runOne sc CausationConfig.defaults 42 60
    Assert.True(r.F1 >= 0.80, sprintf "f20 F1=%.3f" r.F1)

[<Fact>]
let ``Phase6 monotonicity — F1 nFlows 증가해도 1.0 유지`` () =
    let cfg = CausationConfig.defaults
    // Phase 6 의 본질: flow 간 독립 → algorithm 이 각각 정확 처리해야
    let mutable allPerfect = true
    for nFlows in 1 .. 20 do
        let sc = Phase6Models.withNFlows nFlows
        let r = BenchRunner.runOne sc cfg 42 60
        if r.F1 < 0.99 then
            printfn "  ❌ nFlows=%d F1=%.3f TP=%d FP=%d FN=%d" nFlows r.F1 r.TP r.FP r.FN
            allPerfect <- false
    Assert.True(allPerfect, "all flow counts 1..20 should produce perfect F1")

// ── Variants: Async / Hetero lag / Spurious ───────────────────────────

[<Fact>]
let ``Phase6 Async — 각 flow async cycle 1..20 diagnostic`` () =
    let cfg = CausationConfig.defaults
    let mutable sum = 0.0
    let mutable perfect = 0
    printfn ""
    printfn "═══ Phase 6 Async Variant ═══"
    for sc in Phase6Models.allAsync do
        let r = BenchRunner.runOne sc cfg 42 60
        sum <- sum + r.F1
        if r.F1 >= 0.9999 then perfect <- perfect + 1
        printfn "  %-30s F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Async Avg F1: %.4f  Perfect: %d/20" (sum / 20.0) perfect

[<Fact>]
let ``Phase6 Hetero — flow 별 lag 변동 1..20 diagnostic`` () =
    let cfg = CausationConfig.defaults
    let mutable sum = 0.0
    let mutable perfect = 0
    printfn ""
    printfn "═══ Phase 6 HeteroLag Variant ═══"
    for sc in Phase6Models.allHetero do
        let r = BenchRunner.runOne sc cfg 42 60
        sum <- sum + r.F1
        if r.F1 >= 0.9999 then perfect <- perfect + 1
        printfn "  %-30s F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Hetero Avg F1: %.4f  Perfect: %d/20" (sum / 20.0) perfect

[<Fact>]
let ``Phase6 Spurious — 각 flow noise call 추가 1..20 diagnostic`` () =
    let cfg = CausationConfig.defaults
    let mutable sum = 0.0
    let mutable perfect = 0
    printfn ""
    printfn "═══ Phase 6 Spurious Variant ═══"
    for sc in Phase6Models.allSpurious do
        let r = BenchRunner.runOne sc cfg 42 60
        sum <- sum + r.F1
        if r.F1 >= 0.9999 then perfect <- perfect + 1
        printfn "  %-30s F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Spurious Avg F1: %.4f  Perfect: %d/20" (sum / 20.0) perfect

[<Fact>]
let ``Phase6 allVariants — aggregate avg F1 >= 0.85 (broader set)`` () =
    let summary, _ =
        BenchRunner.runAll Phase6Models.allVariants CausationConfig.defaults 42 60
    printfn "%s" (BenchRunner.formatSummary summary)
    Assert.True(summary.AvgF1 >= 0.85,
        sprintf "Phase6 all variants avg F1=%.4f < 0.85" summary.AvgF1)

// ── Round 2 variants ────────────────────────────────────────────────

[<Fact>]
let ``Phase6 CrossFlowChain — F1→F2→...Fn sequential, 1..20`` () =
    let cfg = CausationConfig.defaults
    let mutable sum = 0.0
    let mutable perfect = 0
    printfn ""
    printfn "═══ Phase 6 Cross-Flow Chain Variant ═══"
    for sc in Phase6Models.allCrossFlow do
        let r = BenchRunner.runOne sc cfg 42 60
        sum <- sum + r.F1
        if r.F1 >= 0.9999 then perfect <- perfect + 1
        printfn "  %-35s F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  CrossFlow Avg F1: %.4f  Perfect: %d/20" (sum / 20.0) perfect

[<Fact>]
let ``Phase6 SyncBarrier — 모든 flow 동시 발화, 1..20`` () =
    let cfg = CausationConfig.defaults
    let mutable sum = 0.0
    let mutable perfect = 0
    printfn ""
    printfn "═══ Phase 6 Sync Barrier Variant ═══"
    for sc in Phase6Models.allSyncBarrier do
        let r = BenchRunner.runOne sc cfg 42 60
        sum <- sum + r.F1
        if r.F1 >= 0.9999 then perfect <- perfect + 1
        printfn "  %-35s F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  SyncBarrier Avg F1: %.4f  Perfect: %d/20" (sum / 20.0) perfect

[<Fact>]
let ``Phase6 Burst — 일부 flow 만 발화 (50%%), 1..20`` () =
    let cfg = CausationConfig.defaults
    let mutable sum = 0.0
    let mutable perfect = 0
    printfn ""
    printfn "═══ Phase 6 Burst Variant ═══"
    for sc in Phase6Models.allBurst do
        let r = BenchRunner.runOne sc cfg 42 60
        sum <- sum + r.F1
        if r.F1 >= 0.9999 then perfect <- perfect + 1
        printfn "  %-35s F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Burst Avg F1: %.4f  Perfect: %d/20" (sum / 20.0) perfect

// ── Round 3 adversarial variants ────────────────────────────────────

[<Fact>]
let ``Phase6 Confounded — 모든 flow 가 external trigger 따라 함께 shift`` () =
    let cfg = CausationConfig.defaults
    let mutable sum = 0.0
    let mutable perfect = 0
    let mutable fpTotal = 0
    printfn ""
    printfn "═══ Phase 6 Confounded Variant ═══"
    for sc in Phase6Models.allConfounded do
        let r = BenchRunner.runOne sc cfg 42 60
        sum <- sum + r.F1
        if r.F1 >= 0.9999 then perfect <- perfect + 1
        fpTotal <- fpTotal + r.FP
        printfn "  %-35s F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Confounded Avg F1: %.4f  Perfect: %d/20  TotalFP: %d"
        (sum / 20.0) perfect fpTotal

[<Fact>]
let ``Phase6 TightCycle — 매우 짧은 cycle, flow 들이 매우 가까움`` () =
    let cfg = CausationConfig.defaults
    let mutable sum = 0.0
    let mutable perfect = 0
    printfn ""
    printfn "═══ Phase 6 Tight Cycle Variant ═══"
    for sc in Phase6Models.allTightCycle do
        let r = BenchRunner.runOne sc cfg 42 60
        sum <- sum + r.F1
        if r.F1 >= 0.9999 then perfect <- perfect + 1
        printfn "  %-35s F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  TightCycle Avg F1: %.4f  Perfect: %d/20" (sum / 20.0) perfect

[<Fact>]
let ``Phase6 HeavyNoise — 각 flow 5 noise calls`` () =
    let cfg = CausationConfig.defaults
    let mutable sum = 0.0
    let mutable perfect = 0
    let mutable fpTotal = 0
    printfn ""
    printfn "═══ Phase 6 Heavy Noise Variant ═══"
    for sc in Phase6Models.allHeavyNoise do
        let r = BenchRunner.runOne sc cfg 42 60
        sum <- sum + r.F1
        if r.F1 >= 0.9999 then perfect <- perfect + 1
        fpTotal <- fpTotal + r.FP
        printfn "  %-35s F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  HeavyNoise Avg F1: %.4f  Perfect: %d/20  TotalFP: %d"
        (sum / 20.0) perfect fpTotal

// ── Round 4 stress variants ─────────────────────────────────────────

[<Fact>]
let ``Phase6 HighStage — 각 flow 10 stages 1..20`` () =
    let cfg = CausationConfig.defaults
    let mutable sum = 0.0
    let mutable perfect = 0
    printfn ""
    printfn "═══ Phase 6 HighStage Variant (10 stages × N flows) ═══"
    for sc in Phase6Models.allHighStage do
        let r = BenchRunner.runOne sc cfg 42 60
        sum <- sum + r.F1
        if r.F1 >= 0.9999 then perfect <- perfect + 1
        printfn "  %-35s F1=%.3f Truth=%d TP=%d FP=%d FN=%d"
            sc.Name r.F1 r.Truth r.TP r.FP r.FN
    printfn "  HighStage Avg F1: %.4f  Perfect: %d/20" (sum / 20.0) perfect

[<Fact>]
let ``Phase6 LongChain — 각 flow 15 nodes chain 1..20`` () =
    let cfg = CausationConfig.defaults
    let mutable sum = 0.0
    let mutable perfect = 0
    printfn ""
    printfn "═══ Phase 6 LongChain Variant (15 nodes chain × N flows) ═══"
    for sc in Phase6Models.allLongChain do
        let r = BenchRunner.runOne sc cfg 42 60
        sum <- sum + r.F1
        if r.F1 >= 0.9999 then perfect <- perfect + 1
        printfn "  %-35s F1=%.3f Truth=%d TP=%d FP=%d FN=%d"
            sc.Name r.F1 r.Truth r.TP r.FP r.FN
    printfn "  LongChain Avg F1: %.4f  Perfect: %d/20" (sum / 20.0) perfect

[<Fact>]
let ``Phase6 RatioStress — flow별 stages 비대칭 1..20`` () =
    let cfg = CausationConfig.defaults
    let mutable sum = 0.0
    let mutable perfect = 0
    printfn ""
    printfn "═══ Phase 6 RatioStress Variant (stages = flowIdx + 1) ═══"
    for sc in Phase6Models.allRatioStress do
        let r = BenchRunner.runOne sc cfg 42 60
        sum <- sum + r.F1
        if r.F1 >= 0.9999 then perfect <- perfect + 1
        printfn "  %-35s F1=%.3f Truth=%d TP=%d FP=%d FN=%d"
            sc.Name r.F1 r.Truth r.TP r.FP r.FN
    printfn "  RatioStress Avg F1: %.4f  Perfect: %d/20" (sum / 20.0) perfect

// ── Round 5 intra-flow adversarial ──────────────────────────────────

[<Fact>]
let ``Phase6 TransitiveBait — N1→N3 spurious 거부 (transitive reduction)`` () =
    let cfg = CausationConfig.defaults
    let mutable sum = 0.0
    let mutable perfect = 0
    let mutable totalFP = 0
    printfn ""
    printfn "═══ Phase 6 TransitiveBait Variant ═══"
    for sc in Phase6Models.allTransitiveBait do
        let r = BenchRunner.runOne sc cfg 42 60
        sum <- sum + r.F1
        totalFP <- totalFP + r.FP
        if r.F1 >= 0.9999 then perfect <- perfect + 1
        printfn "  %-35s F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  TransitiveBait Avg F1: %.4f  Perfect: %d/20  TotalFP: %d"
        (sum / 20.0) perfect totalFP

[<Fact>]
let ``Phase6 CycleBait — N4→N1 cross-cycle spurious 거부`` () =
    let cfg = CausationConfig.defaults
    let mutable sum = 0.0
    let mutable perfect = 0
    let mutable totalFP = 0
    printfn ""
    printfn "═══ Phase 6 CycleBait Variant ═══"
    for sc in Phase6Models.allCycleBait do
        let r = BenchRunner.runOne sc cfg 42 60
        sum <- sum + r.F1
        totalFP <- totalFP + r.FP
        if r.F1 >= 0.9999 then perfect <- perfect + 1
        printfn "  %-35s F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  CycleBait Avg F1: %.4f  Perfect: %d/20  TotalFP: %d"
        (sum / 20.0) perfect totalFP

[<Fact>]
let ``Phase6 Final aggregate — 300 scenarios avg F1 >= 0.95`` () =
    let summary, _ =
        BenchRunner.runAll Phase6Models.allVariants CausationConfig.defaults 42 60
    printfn "═══ Phase 6 FINAL — 15 variants × 20 flows = 300 scenarios ═══"
    printfn "Total: %d  Perfect: %d/%d (%.1f%%)  Avg F1: %.4f"
        summary.Total summary.Perfect summary.Total
        (float summary.Perfect * 100.0 / float summary.Total)
        summary.AvgF1
    Assert.True(summary.AvgF1 >= 0.95,
        sprintf "300 scenarios avg F1=%.4f < 0.95" summary.AvgF1)
