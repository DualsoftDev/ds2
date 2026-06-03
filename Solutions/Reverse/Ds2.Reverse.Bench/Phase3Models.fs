/// Phase 3 — G (Graph topology) + Z (Adversarial) 차원 시나리오.
///
/// G — Tree / Star / Mesh / Bipartite topology
/// Z — 알고리즘 약점 노출: noise + partial + spurious 등 mix
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

module Phase3Models =

    let private scenario name flow gt spurious nodes pattern cycleMs : Scenario =
        { Name = name; Flow = flow
          GroundTruth = gt; Spurious = spurious
          AllCalls = nodes |> List.distinct
          Pattern = pattern; PatternCycleAware = None; CycleMs = cycleMs }

    let private mkArrow flow s t kind : VLine.GroundTruthArrow =
        { Src = $"{flow}.{s}"; Tgt = $"{flow}.{t}"; Kind = kind }

    let private full flow node = $"{flow}.{node}"

    // ════════════════════════════════════════════════════════════════════
    // G 차원 — Graph topology variants
    // ════════════════════════════════════════════════════════════════════
    let private graphModels : Scenario list = [
        // g0: Star — center 1 source → N targets (fan-out)
        let arrows0 = [
            mkArrow "F" "S" "T1" "Start"
            mkArrow "F" "S" "T2" "Start"
            mkArrow "F" "S" "T3" "Start"
            mkArrow "F" "S" "T4" "Start"
        ]
        let nodes0 = [
            full "F" "S"; full "F" "T1"; full "F" "T2"; full "F" "T3"; full "F" "T4"
        ]
        let pattern0 (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "S"
                200L, full "F" "T1"
                200L, full "F" "T2"
                200L, full "F" "T3"
                200L, full "F" "T4"
              ]; Jitter = 15L }
        yield scenario "g0_star4" "F" arrows0 [] nodes0 pattern0 2000L

        // g1: Tree depth 3 — A → B,C → D,E,F
        let arrows1 = [
            mkArrow "F" "A" "B" "Start"
            mkArrow "F" "A" "C" "Start"
            mkArrow "F" "B" "D" "Start"
            mkArrow "F" "B" "E" "Start"
            mkArrow "F" "C" "F1" "Start"
        ]
        let nodes1 = [
            full "F" "A"; full "F" "B"; full "F" "C"
            full "F" "D"; full "F" "E"; full "F" "F1"
        ]
        let pattern1 (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "A"
                200L, full "F" "B"; 200L, full "F" "C"
                400L, full "F" "D"; 400L, full "F" "E"; 400L, full "F" "F1"
              ]; Jitter = 15L }
        yield scenario "g1_treeDepth3" "F" arrows1 [] nodes1 pattern1 2500L

        // g2: Bipartite — 3 sources × 2 sinks (each src→both sinks)
        let arrows2 = [
            mkArrow "F" "S1" "T1" "Start"
            mkArrow "F" "S1" "T2" "Start"
            mkArrow "F" "S2" "T1" "Start"
            mkArrow "F" "S2" "T2" "Start"
            mkArrow "F" "S3" "T1" "Start"
            mkArrow "F" "S3" "T2" "Start"
        ]
        let nodes2 = [
            full "F" "S1"; full "F" "S2"; full "F" "S3"
            full "F" "T1"; full "F" "T2"
        ]
        let pattern2 (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "S1"; 0L, full "F" "S2"; 0L, full "F" "S3"
                200L, full "F" "T1"; 200L, full "F" "T2"
              ]; Jitter = 15L }
        yield scenario "g2_bipartite3x2" "F" arrows2 [] nodes2 pattern2 2000L

        // g3: Diamond (parallel merge) — A → B,C → D
        let arrows3 = [
            mkArrow "F" "A" "B" "Start"
            mkArrow "F" "A" "C" "Start"
            mkArrow "F" "B" "D" "Start"
            mkArrow "F" "C" "D" "Start"
        ]
        let nodes3 = [ full "F" "A"; full "F" "B"; full "F" "C"; full "F" "D" ]
        let pattern3 (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "A"
                200L, full "F" "B"; 200L, full "F" "C"
                400L, full "F" "D"
              ]; Jitter = 15L }
        yield scenario "g3_diamond" "F" arrows3 [] nodes3 pattern3 2000L
    ]

    // ════════════════════════════════════════════════════════════════════
    // Z 차원 — Adversarial mix (의도된 false-positive trap)
    // ════════════════════════════════════════════════════════════════════
    let private adversarialModels : Scenario list = [
        // z0: 진짜 인과 + 두 개의 confounded spurious
        let arrows0 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes0 = [
            full "F" "A"; full "F" "B"; full "F" "X1"; full "F" "X2"
        ]
        let pattern0 (rng: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "A"
                300L, full "F" "B"
                int64 (rng.Next(0, 1000)), full "F" "X1"
                int64 (rng.Next(0, 1000)), full "F" "X2"
              ]; Jitter = 20L }
        yield scenario "z0_realPlusNoise2" "F" arrows0
                       [ mkArrow "F" "X1" "B" "Start"
                         mkArrow "F" "X2" "B" "Start" ]
                       nodes0 pattern0 2500L

        // z1: 단일 진짜 인과 + 5개 noise calls
        let arrows1 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes1 = [
            full "F" "A"; full "F" "B"
            full "F" "N1"; full "F" "N2"; full "F" "N3"; full "F" "N4"; full "F" "N5"
        ]
        let pattern1 (rng: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "A"
                250L, full "F" "B"
                for n in [ "N1"; "N2"; "N3"; "N4"; "N5" ] do
                    int64 (rng.Next(0, 2000)), full "F" n
              ]; Jitter = 20L }
        yield scenario "z1_noise5" "F" arrows1 [] nodes1 pattern1 2500L

        // z2: 모두 spurious — 진짜 인과 0개, 무작위 발화
        let nodes2 = [ full "F" "P"; full "F" "Q"; full "F" "R" ]
        let pattern2 (rng: Random) : Simulator.CyclePattern =
            { Offsets = [
                int64 (rng.Next(0, 1500)), full "F" "P"
                int64 (rng.Next(0, 1500)), full "F" "Q"
                int64 (rng.Next(0, 1500)), full "F" "R"
              ]; Jitter = 20L }
        yield scenario "z2_allSpurious" "F" []
                       [ mkArrow "F" "P" "Q" "Start"
                         mkArrow "F" "Q" "R" "Start"
                         mkArrow "F" "P" "R" "Start" ]
                       nodes2 pattern2 2500L

        // z3: 진짜 chain + 1개 outlier (drift 가 늦거나 매우 빠른 한 cycle)
        let arrows3 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes3 = [ full "F" "A"; full "F" "B" ]
        let pattern3 (k: int) (_: Random) : Simulator.CyclePattern =
            let lag =
                if k = 5 || k = 25 then 1500L     // 두 cycle outlier
                else 300L
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        let scenarioCA name flow gt spurious nodes patternCA cycleMs : Scenario =
            { Name = name; Flow = flow; GroundTruth = gt; Spurious = spurious
              AllCalls = nodes |> List.distinct
              Pattern = (fun rng -> patternCA 0 rng)
              PatternCycleAware = Some patternCA; CycleMs = cycleMs }
        yield scenarioCA "z3_outlier2of60" "F" arrows3 [] nodes3 pattern3 2500L

        // z4: 진짜 인과 + noise overlap (B 가 가끔 자체 fire)
        let arrows4 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes4 = [ full "F" "A"; full "F" "B" ]
        let pattern4 (rng: Random) : Simulator.CyclePattern =
            // 10% 확률로 B 가 추가 발화 (random)
            let extraB =
                if rng.Next(0, 100) < 10 then
                    [ int64 (rng.Next(800, 1500)), full "F" "B" ]
                else []
            { Offsets = [
                0L, full "F" "A"
                300L, full "F" "B"
              ] @ extraB; Jitter = 15L }
        yield scenario "z4_doubleFire10pct" "F" arrows4 [] nodes4 pattern4 2000L
    ]

    let all : Scenario list = graphModels @ adversarialModels

    let stats () =
        [ "G Graph (g0-g3)", List.length graphModels
          "Z Adversarial (z0-z4)", List.length adversarialModels ]
