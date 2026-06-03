/// Phase 7 — Polling pressure + Multi-modal pressure.
/// 알고리즘 약점 영역 정밀 측정.
module Ds2.Reverse.Tests.Phase7Tests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

// ── Polling pressure diagnostic ──────────────────────────────────────

[<Fact>]
let ``Phase7P Polling pressure diagnostic`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7P — Polling Pressure (NO causation, all should DROP) ═══"
    printfn "  %-30s | F1 | TP FP FN | Truth Detected" "name"
    let mutable totalFP = 0
    for sc in Phase7Models.allPollPressure do
        let r = BenchRunner.runOne sc cfg 42 60
        totalFP <- totalFP + r.FP
        printfn "  %-30s | %.3f | %d %d %d | %d %d"
            sc.Name r.F1 r.TP r.FP r.FN r.Truth r.Detected
    printfn "  Total FP (lower = better): %d / %d scenarios" totalFP (List.length Phase7Models.allPollPressure)
    Assert.True true

[<Fact>]
let ``Phase7P PollPlusCausation — TRG→TGT 검출 + POLL spurious 거부`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7P — Polling + Real Causation ═══"
    printfn "  %-30s | F1 | TP FP FN" "name"
    let mutable correctTP = 0
    let mutable totalFP = 0
    for sc in Phase7Models.allPollPlus do
        let r = BenchRunner.runOne sc cfg 42 60
        if r.TP >= 1 then correctTP <- correctTP + 1
        totalFP <- totalFP + r.FP
        printfn "  %-30s | %.3f | %d %d %d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Correct (TP≥1): %d / %d" correctTP (List.length Phase7Models.allPollPlus)
    printfn "  Total FP (POLL→TRG, POLL→TGT): %d" totalFP

// ── Multi-modal pressure diagnostic ──────────────────────────────────

[<Fact>]
let ``Phase7M Multi-modal pressure diagnostic`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7M — Multi-modal (k=3..6 × sep) ═══"
    printfn "  %-25s | F1 | TP FP FN" "name"
    let mutable perfect = 0
    for sc in Phase7Models.allMultiModal do
        let r = BenchRunner.runOne sc cfg 42 60
        if r.F1 >= 0.99 then perfect <- perfect + 1
        printfn "  %-25s | %.3f | %d %d %d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Perfect: %d / %d" perfect (List.length Phase7Models.allMultiModal)

[<Fact>]
let ``Phase7 baseline — current weakness measurements`` () =
    // Baseline measurement — recording current state for comparison after
    // algorithm strengthening.
    let summary, _ =
        BenchRunner.runAll Phase7Models.allVariants CausationConfig.defaults 42 60
    printfn "═══ Phase 7 BASELINE ═══"
    printfn "Total: %d, Perfect: %d, AvgF1: %.4f, FP=%d FN=%d"
        summary.Total summary.Perfect summary.AvgF1 summary.TotalFp summary.TotalFn
    Assert.True true   // diagnostic only — no threshold

[<Fact>]
let ``Phase7 polling — totalFP <= 5 (algorithm robust to polling)`` () =
    let cfg = CausationConfig.defaults
    let mutable totalFPpoll = 0
    for sc in Phase7Models.allPollPressure do
        let r = BenchRunner.runOne sc cfg 42 60
        totalFPpoll <- totalFPpoll + r.FP
    printfn "Polling pressure totalFP=%d" totalFPpoll
    Assert.True(totalFPpoll <= 5,
        sprintf "polling FP=%d > 5 (algorithm should reject polling)" totalFPpoll)

[<Fact>]
let ``Phase7 BurstPolling — burst/idle polling 거부`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7P — Burst Polling ═══"
    let mutable totalFP = 0
    for sc in Phase7Models.allBurstPolling do
        let r = BenchRunner.runOne sc cfg 42 60
        totalFP <- totalFP + r.FP
        printfn "  %-25s | F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Burst polling totalFP: %d" totalFP

[<Fact>]
let ``Phase7 PhaseShiftPolling — phase 가 shift 해도 거부`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7P — Phase Shift Polling ═══"
    let mutable totalFP = 0
    for sc in Phase7Models.allPhaseShift do
        let r = BenchRunner.runOne sc cfg 42 60
        totalFP <- totalFP + r.FP
        printfn "  %-25s | F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Phase shift totalFP: %d" totalFP

[<Fact>]
let ``Phase7 ImbalancedModal — 한 mode dominant, 나머지 minor`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7M — Imbalanced Multi-modal ═══"
    let mutable perfect = 0
    for sc in Phase7Models.allImbalanced do
        let r = BenchRunner.runOne sc cfg 42 60
        if r.F1 >= 0.99 then perfect <- perfect + 1
        printfn "  %-30s | F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Imbalanced perfect: %d / %d" perfect (List.length Phase7Models.allImbalanced)

[<Fact>]
let ``Phase7 LowRatioPolling — POLL/ACT ratio < 5 (algorithm 경계)`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7P — Low-Ratio Polling ═══"
    let mutable totalFP = 0
    for sc in Phase7Models.allLowRatioPolling do
        let r = BenchRunner.runOne sc cfg 42 60
        totalFP <- totalFP + r.FP
        printfn "  %-25s | F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Low-ratio polling totalFP: %d" totalFP

[<Fact>]
let ``Phase7 OverlappingModal — overlap noisy lag (GT) 검출`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7M — Overlapping (noisy lag, GT) ═══"
    let mutable perfect = 0
    for sc in Phase7Models.allOverlappingModal do
        let r = BenchRunner.runOne sc cfg 42 60
        if r.F1 >= 0.99 then perfect <- perfect + 1
        printfn "  %-30s | F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Overlapping (noisy lag) perfect: %d / %d"
        perfect (List.length Phase7Models.allOverlappingModal)
    Assert.True(perfect >= 3,
        sprintf "expected all 3 perfect; got %d" perfect)

[<Fact>]
let ``Phase7 DriftBimodal — drift + bimodal 혼합`` () =
    let cfg = CausationConfig.defaults
    for sc in Phase7Models.allDriftBimodal do
        let r = BenchRunner.runOne sc cfg 42 60
        printfn "  %-30s | F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN

// ── Round 4 ────────────────────────────────────────────────────────

[<Fact>]
let ``Phase7 MultiFlowPolling — 여러 flow 안 polling 거부`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7P — Multi-Flow Polling ═══"
    let mutable totalFP = 0
    for sc in Phase7Models.allMultiFlowPolling do
        let r = BenchRunner.runOne sc cfg 42 60
        totalFP <- totalFP + r.FP
        printfn "  %-25s | F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Multi-flow polling totalFP: %d" totalFP

[<Fact>]
let ``Phase7 LongLag — effective_window 경계 lag 검출`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7C — Long Lag ═══"
    let mutable perfect = 0
    for sc in Phase7Models.allLongLag do
        let r = BenchRunner.runOne sc cfg 42 60
        if r.F1 >= 0.99 then perfect <- perfect + 1
        printfn "  %-25s | F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Long lag perfect: %d / %d" perfect (List.length Phase7Models.allLongLag)

[<Fact>]
let ``Phase7 TightJitter — 매우 작은 jitter (1-30ms)`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7C — Tight Jitter ═══"
    let mutable perfect = 0
    for sc in Phase7Models.allTightJitter do
        let r = BenchRunner.runOne sc cfg 42 60
        if r.F1 >= 0.99 then perfect <- perfect + 1
        printfn "  %-25s | F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Tight jitter perfect: %d / %d" perfect (List.length Phase7Models.allTightJitter)

[<Fact>]
let ``Phase7 Conditional — condition 변수 따라 다른 인과`` () =
    let cfg = CausationConfig.defaults
    for sc in Phase7Models.allConditional do
        let r = BenchRunner.runOne sc cfg 42 60
        printfn "  %-25s | F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN

// ── Round 5 ────────────────────────────────────────────────────────

[<Fact>]
let ``Phase7 LargeChain — 단일 flow 안 N=20/50/100 chain`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7L — Large Chain ═══"
    let mutable perfect = 0
    for sc in Phase7Models.allLargeChain do
        let r = BenchRunner.runOne sc cfg 42 60
        if r.F1 >= 0.99 then perfect <- perfect + 1
        printfn "  %-20s | Truth=%d Detected=%d F1=%.3f TP=%d FP=%d FN=%d"
            sc.Name r.Truth r.Detected r.F1 r.TP r.FP r.FN
    printfn "  LargeChain perfect: %d / %d" perfect (List.length Phase7Models.allLargeChain)

[<Fact>]
let ``Phase7 CombinedAttack — polling + bimodal + spurious 모두 혼합`` () =
    let cfg = CausationConfig.defaults
    for sc in Phase7Models.allCombinedAttack do
        let r = BenchRunner.runOne sc cfg 42 60
        printfn "  %-25s | F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
        Assert.True(r.TP >= 1,
            sprintf "expected A→B detected; got TP=%d" r.TP)

[<Fact>]
let ``Phase7 RareEffect — necc 낮음 → 거부`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7R — Rare Effect (necc < 0.85) ═══"
    let mutable totalFP = 0
    for sc in Phase7Models.allRareEffect do
        let r = BenchRunner.runOne sc cfg 42 60
        totalFP <- totalFP + r.FP
        printfn "  %-25s | F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Rare effect totalFP: %d" totalFP

// ── Round 7 ────────────────────────────────────────────────────────

[<Fact>]
let ``Phase7 ConditionalProb — X 의 확률에 따라 A→B/C 검출`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7C — Conditional Probability ═══"
    for sc in Phase7Models.allConditionalProb do
        let r = BenchRunner.runOne sc cfg 42 60
        printfn "  %-25s | Truth=%d Detected=%d F1=%.3f TP=%d FP=%d FN=%d"
            sc.Name r.Truth r.Detected r.F1 r.TP r.FP r.FN

[<Fact>]
let ``Phase7 NonStationary — 3-phase lag 변동`` () =
    let cfg = CausationConfig.defaults
    for sc in Phase7Models.allNonStationary do
        let r = BenchRunner.runOne sc cfg 42 60
        printfn "  %-25s | F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN

[<Fact>]
let ``Phase7 MissingData — 일부 cycles events 누락`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7M — Missing Data ═══"
    let mutable perfect = 0
    for sc in Phase7Models.allMissingData do
        let r = BenchRunner.runOne sc cfg 42 60
        if r.F1 >= 0.99 then perfect <- perfect + 1
        printfn "  %-20s | F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Missing data perfect: %d / %d" perfect (List.length Phase7Models.allMissingData)

// ── Round 8 ────────────────────────────────────────────────────────

[<Fact>]
let ``Phase7 SuffBoundary — suff threshold 0.85 정확성`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7B — Suff Boundary ═══"
    for sc in Phase7Models.allSuffBoundary do
        let r = BenchRunner.runOne sc cfg 42 60
        printfn "  %-25s | Truth=%d Detected=%d F1=%.3f TP=%d FP=%d FN=%d"
            sc.Name r.Truth r.Detected r.F1 r.TP r.FP r.FN

[<Fact>]
let ``Phase7 TimeResolution — 매우 짧은 cycle + lag`` () =
    let cfg = CausationConfig.defaults
    printfn ""
    printfn "═══ Phase 7T — Time Resolution ═══"
    let mutable perfect = 0
    for sc in Phase7Models.allTimeResolution do
        let r = BenchRunner.runOne sc cfg 42 60
        if r.F1 >= 0.99 then perfect <- perfect + 1
        printfn "  %-25s | F1=%.3f TP=%d FP=%d FN=%d" sc.Name r.F1 r.TP r.FP r.FN
    printfn "  Time resolution perfect: %d / %d" perfect (List.length Phase7Models.allTimeResolution)
