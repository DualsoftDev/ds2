/// Phase 1 — R / Q 차원 시나리오.
///
/// R (Reset) — 자기-소멸 패턴 (A 발화 후 reset 으로 사라짐)
/// Q (Queue) — Bottleneck variants (queue 길이 / 처리 시간 변동)
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

module Phase1Models =

    let private scenario name flow gt spurious nodes pattern cycleMs : Scenario =
        { Name = name; Flow = flow
          GroundTruth = gt; Spurious = spurious
          AllCalls = nodes |> List.distinct
          Pattern = pattern; PatternCycleAware = None; CycleMs = cycleMs }

    let private mkArrow flow s t kind : VLine.GroundTruthArrow =
        { Src = $"{flow}.{s}"; Tgt = $"{flow}.{t}"; Kind = kind }

    let private full flow node = $"{flow}.{node}"

    // ════════════════════════════════════════════════════════════════════
    // R 차원 — Reset 패턴
    // ════════════════════════════════════════════════════════════════════
    let private resetModels : Scenario list = [
        // r0: A → B sequential + B → A reset (자기-소멸 cycle)
        let arrows0 = [
            mkArrow "F" "A" "B" "Start"
            mkArrow "F" "B" "A" "Reset"
        ]
        let nodes0 = [ full "F" "A"; full "F" "B" ]
        let pattern0 (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "A"; 300L, full "F" "B" ]; Jitter = 20L }
        // 알고리즘이 Reset kind 인정하면 양쪽 다 검출
        yield scenario "r0_aResetByB" "F" arrows0 [] nodes0 pattern0 2000L

        // r1: A 발화 → 일정 시간 후 자동 reset (B 같은 trigger 없음)
        let arrows1 = [ mkArrow "F" "X" "Y" "Start" ]
        let nodes1 = [ full "F" "X"; full "F" "Y" ]
        let pattern1 (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "X"; 200L, full "F" "Y" ]; Jitter = 20L }
        yield scenario "r1_simpleReset" "F" arrows1 [] nodes1 pattern1 2000L

        // r2: Mutex — A 와 B 가 상호 배타
        // A 발화 시 B 없음, B 발화 시 A 없음
        let arrows2 = [
            mkArrow "F" "A" "B" "ResetReset"
        ]
        let nodes2 = [ full "F" "A"; full "F" "B" ]
        let pattern2 (rng: Random) : Simulator.CyclePattern =
            let pickA = rng.Next(0, 2) = 0
            let name = if pickA then "A" else "B"
            { Offsets = [ 0L, full "F" name ]; Jitter = 20L }
        yield scenario "r2_mutexAB" "F" arrows2 [] nodes2 pattern2 2000L

        // r3: 명시적 trigger_reset chain (A → B 면서 A reset)
        let arrows3 = [
            mkArrow "F" "A" "B" "StartReset"
        ]
        let nodes3 = [ full "F" "A"; full "F" "B" ]
        let pattern3 (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "A"; 200L, full "F" "B" ]; Jitter = 15L }
        yield scenario "r3_startReset" "F" arrows3 [] nodes3 pattern3 2000L

        // r4: spurious — random reset (no causation)
        let nodes4 = [ full "F" "P"; full "F" "Q" ]
        let pattern4 (rng: Random) : Simulator.CyclePattern =
            { Offsets = [
                int64 (rng.Next(0, 1000)), full "F" "P"
                int64 (rng.Next(0, 1000)), full "F" "Q"
              ]; Jitter = 15L }
        yield scenario "r4_spuriousReset" "F" [] [ mkArrow "F" "P" "Q" "Reset" ] nodes4 pattern4 2000L
    ]

    // ════════════════════════════════════════════════════════════════════
    // Q 차원 — Queue / Bottleneck variants
    // ════════════════════════════════════════════════════════════════════
    let private queueModels : Scenario list = [
        // q0: 짧은 queue (lag bimodal 200/500ms) — 명확한 분리 위해 gap 확보
        let arrows0 = [ mkArrow "F" "IN" "OUT" "Start" ]
        let nodes0 = [ full "F" "IN"; full "F" "OUT" ]
        let pattern0 (rng: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "IN"
                300L, full "F" "IN"
                200L + int64 (rng.Next(-15, 16)), full "F" "OUT"
                800L + int64 (rng.Next(-15, 16)), full "F" "OUT"
              ]; Jitter = 10L }
        yield scenario "q0_shortQueue" "F" arrows0 [] nodes0 pattern0 2000L

        // q1: 긴 queue — 3 token, 주기적 lag pattern (cyclic drift 로 검출 가능)
        let arrows1 = [ mkArrow "F" "IN" "OUT" "Start" ]
        let nodes1 = [ full "F" "IN"; full "F" "OUT" ]
        let pattern1 (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "IN"
                200L, full "F" "IN"
                400L, full "F" "IN"
                200L, full "F" "OUT"
                800L, full "F" "OUT"
                1400L, full "F" "OUT"
              ]; Jitter = 15L }
        // cycle 마다 [200, 0, 400] 의 주기적 lag → cyclic drift detection 인정
        yield scenario "q1_longQueue" "F" arrows1 [] nodes1 pattern1 2500L

        // q2: 변동 queue (50% 짧음, 50% 김)
        let arrows2 = [ mkArrow "F" "IN" "OUT" "Start" ]
        let nodes2 = [ full "F" "IN"; full "F" "OUT" ]
        let pattern2 (rng: Random) : Simulator.CyclePattern =
            let isShort = rng.Next(0, 2) = 0
            let lag = if isShort then 200L else 600L
            { Offsets = [ 0L, full "F" "IN"; lag, full "F" "OUT" ]; Jitter = 20L }
        // bimodal stable 인정 (50/50 distribution)
        yield scenario "q2_variableQueue" "F" arrows2 [] nodes2 pattern2 2000L

        // q3: queue with rejection (가끔 IN 만 발화)
        let arrows3 = [ mkArrow "F" "IN" "OUT" "Start" ]
        let nodes3 = [ full "F" "IN"; full "F" "OUT" ]
        let pattern3 (rng: Random) : Simulator.CyclePattern =
            let withOut = rng.Next(0, 100) < 90   // 90% 성공
            let base_ = [ 0L, full "F" "IN" ]
            let outEv = if withOut then [ 250L, full "F" "OUT" ] else []
            { Offsets = base_ @ outEv; Jitter = 20L }
        yield scenario "q3_queueRejection10pct" "F" arrows3 [] nodes3 pattern3 2000L

        // q4: deep bottleneck (5 token 깊은 queue).
        // 5-modal well-separated lag (0/100/200/300/200 cycling) — algorithm 강화 후
        // k-means 로 detectable. GroundTruth 로 reclassify (2026-05-25 algorithm enhancement).
        let arrows4 = [ mkArrow "F" "IN" "OUT" "Start" ]
        let nodes4 = [ full "F" "IN"; full "F" "OUT" ]
        let pattern4 (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "IN"; 100L, full "F" "IN"; 200L, full "F" "IN"
                300L, full "F" "IN"; 400L, full "F" "IN"
                200L, full "F" "OUT"; 600L, full "F" "OUT"; 1000L, full "F" "OUT"
                1400L, full "F" "OUT"; 1800L, full "F" "OUT"
              ]; Jitter = 15L }
        yield scenario "q4_deepBottleneck" "F" arrows4 [] nodes4 pattern4 2500L
    ]

    // ════════════════════════════════════════════════════════════════════
    // D 차원 — Drift 패턴 (선형 + 주기적)
    // ════════════════════════════════════════════════════════════════════
    // cycle-aware scenario helper — drift 등 stateful 시나리오용
    let private scenarioCA name flow gt spurious nodes (patternCA: int -> Random -> Simulator.CyclePattern) cycleMs : Scenario =
        { Name = name; Flow = flow
          GroundTruth = gt; Spurious = spurious
          AllCalls = nodes |> List.distinct
          Pattern = (fun rng -> patternCA 0 rng)   // fallback for non-aware caller
          PatternCycleAware = Some patternCA
          CycleMs = cycleMs }

    let private driftModels : Scenario list = [
        // d0: 선형 drift — lag 가 cycle 마다 5ms 증가 (워밍업)
        let arrows0 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes0 = [ full "F" "A"; full "F" "B" ]
        let pattern0 (k: int) (_: Random) : Simulator.CyclePattern =
            let lag = 300L + int64 k * 5L
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        yield scenarioCA "d0_linearDrift" "F" arrows0 [] nodes0 pattern0 2000L

        // d1: 주기적 cosine drift — lag 가 sin/cos 패턴으로 변동
        let arrows1 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes1 = [ full "F" "A"; full "F" "B" ]
        let pattern1 (k: int) (_: Random) : Simulator.CyclePattern =
            // 주기 12 cycles, 진폭 80ms
            let lag = 400L + int64 (80.0 * cos (2.0 * System.Math.PI * float k / 12.0))
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 10L }
        yield scenarioCA "d1_cyclicDrift" "F" arrows1 [] nodes1 pattern1 2000L

        // d2: 강한 cyclic drift — 큰 진폭 (200ms) 짧은 주기 (8)
        let arrows2 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes2 = [ full "F" "A"; full "F" "B" ]
        let pattern2 (k: int) (_: Random) : Simulator.CyclePattern =
            let lag = 500L + int64 (200.0 * sin (2.0 * System.Math.PI * float k / 8.0))
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 10L }
        yield scenarioCA "d2_strongCyclic" "F" arrows2 [] nodes2 pattern2 2500L
    ]

    let all : Scenario list = resetModels @ queueModels @ driftModels

    let stats () =
        [ "R Reset (r0-r4)", List.length resetModels
          "Q Queue (q0-q4)", List.length queueModels
          "D Drift (d0-d2)", List.length driftModels ]
