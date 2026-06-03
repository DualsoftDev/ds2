/// Phase 4 — K (Kombinatorial / 복합 패턴) + S (Stress edge cases).
/// 여러 알고리즘 컴포넌트가 같이 동작해야 통과하는 시나리오.
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

module Phase4Models =

    let private scenario name flow gt spurious nodes pattern cycleMs : Scenario =
        { Name = name; Flow = flow
          GroundTruth = gt; Spurious = spurious
          AllCalls = nodes |> List.distinct
          Pattern = pattern; PatternCycleAware = None; CycleMs = cycleMs }

    let private mkArrow flow s t kind : VLine.GroundTruthArrow =
        { Src = $"{flow}.{s}"; Tgt = $"{flow}.{t}"; Kind = kind }

    let private full flow node = $"{flow}.{node}"

    // ════════════════════════════════════════════════════════════════════
    // K 차원 — Kombinatorial (chain + bipartite + group + reset 혼합)
    // ════════════════════════════════════════════════════════════════════
    let private kombinatorialModels : Scenario list = [
        // k0: chain + group pair — A→B→C, B&D group
        let arrows0 = [
            mkArrow "F" "A" "B" "Start"
            mkArrow "F" "B" "C" "Start"
            mkArrow "F" "B" "D" "Group"
        ]
        let nodes0 = [ full "F" "A"; full "F" "B"; full "F" "C"; full "F" "D" ]
        let pattern0 (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "A"
                200L, full "F" "B"; 200L, full "F" "D"
                400L, full "F" "C"
              ]; Jitter = 15L }
        yield scenario "k0_chainPlusGroup" "F" arrows0 [] nodes0 pattern0 2000L

        // k1: fan-out + fan-in (diamond + cross)
        let arrows1 = [
            mkArrow "F" "S" "T1" "Start"
            mkArrow "F" "S" "T2" "Start"
            mkArrow "F" "T1" "X" "Start"
            mkArrow "F" "T2" "X" "Start"
            mkArrow "F" "X" "Y" "Start"
        ]
        let nodes1 = [
            full "F" "S"; full "F" "T1"; full "F" "T2"
            full "F" "X"; full "F" "Y"
        ]
        let pattern1 (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "S"
                200L, full "F" "T1"; 200L, full "F" "T2"
                400L, full "F" "X"
                600L, full "F" "Y"
              ]; Jitter = 15L }
        yield scenario "k1_diamondCross" "F" arrows1 [] nodes1 pattern1 2000L

        // k2: chain + drift + spurious — drift detection 필요
        let arrows2 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes2 = [ full "F" "A"; full "F" "B"; full "F" "NOISE" ]
        let pattern2 (k: int) (rng: Random) : Simulator.CyclePattern =
            // lag 가 시간 따라 drift (300→500ms)
            let lag = 300L + int64 k * 3L
            { Offsets = [
                0L, full "F" "A"
                lag, full "F" "B"
                int64 (rng.Next(0, 1500)), full "F" "NOISE"
              ]; Jitter = 15L }
        let scenarioCA name flow gt spurious nodes patternCA cycleMs : Scenario =
            { Name = name; Flow = flow; GroundTruth = gt; Spurious = spurious
              AllCalls = nodes |> List.distinct
              Pattern = (fun rng -> patternCA 0 rng)
              PatternCycleAware = Some patternCA; CycleMs = cycleMs }
        yield scenarioCA "k2_driftPlusNoise" "F" arrows2
                       [ mkArrow "F" "A" "NOISE" "Start"
                         mkArrow "F" "NOISE" "B" "Start" ]
                       nodes2 pattern2 2500L

        // k3: reset cycle + chain — A→B→C + C→A reset
        let arrows3 = [
            mkArrow "F" "A" "B" "Start"
            mkArrow "F" "B" "C" "Start"
            mkArrow "F" "C" "A" "Reset"
        ]
        let nodes3 = [ full "F" "A"; full "F" "B"; full "F" "C" ]
        let pattern3 (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "A"
                200L, full "F" "B"
                400L, full "F" "C"
              ]; Jitter = 15L }
        yield scenario "k3_chainPlusReset" "F" arrows3 [] nodes3 pattern3 2000L
    ]

    // ════════════════════════════════════════════════════════════════════
    // S 차원 — Stress edge cases (low NA, tight jitter 등)
    // ════════════════════════════════════════════════════════════════════
    let private stressModels : Scenario list = [
        // s0: 매우 짧은 lag (50ms) — parallel 경계
        let arrows0 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes0 = [ full "F" "A"; full "F" "B" ]
        let pattern0 (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "A"; 80L, full "F" "B" ]; Jitter = 10L }
        yield scenario "s0_tightLag80ms" "F" arrows0 [] nodes0 pattern0 1500L

        // s1: 매우 긴 lag — window 경계
        let arrows1 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes1 = [ full "F" "A"; full "F" "B" ]
        let pattern1 (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "A"; 1800L, full "F" "B" ]; Jitter = 15L }
        // cycle 3000ms, effective window = 0.7 * 3000 = 2100. 1800 ≤ 2100 → 통과해야
        yield scenario "s1_longLag1800ms" "F" arrows1 [] nodes1 pattern1 3000L

        // s2: 큰 jitter (50ms 절반의 lag)
        let arrows2 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes2 = [ full "F" "A"; full "F" "B" ]
        let pattern2 (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "A"; 300L, full "F" "B" ]; Jitter = 80L }
        yield scenario "s2_largeJitter80ms" "F" arrows2 [] nodes2 pattern2 2000L

        // s3: B1.2 k-means — 3-modal (clean clusters) — well-separated 200/500/800ms
        let arrows3 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes3 = [ full "F" "A"; full "F" "B" ]
        let pattern3 (rng: Random) : Simulator.CyclePattern =
            let mode = rng.Next(0, 3)
            let lag = [| 200L; 500L; 800L |].[mode]
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        yield scenario "s3_kmeans3modal" "F" arrows3 [] nodes3 pattern3 2000L
    ]

    let all : Scenario list = kombinatorialModels @ stressModels

    let stats () =
        [ "K Kombinatorial (k0-k3)", List.length kombinatorialModels
          "S Stress (s0-s2)", List.length stressModels ]
