/// Phase 2 — P / V 차원 시나리오.
///
/// P (Polling) — 주기적 polling + 간헐 actual fire (인과 vs polling 구분)
/// V (Variable duration) — Cycle 마다 다른 device duration 패턴
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

module Phase2Models =

    let private scenario name flow gt spurious nodes pattern cycleMs : Scenario =
        { Name = name; Flow = flow
          GroundTruth = gt; Spurious = spurious
          AllCalls = nodes |> List.distinct
          Pattern = pattern; PatternCycleAware = None; CycleMs = cycleMs }

    let private mkArrow flow s t kind : VLine.GroundTruthArrow =
        { Src = $"{flow}.{s}"; Tgt = $"{flow}.{t}"; Kind = kind }

    let private full flow node = $"{flow}.{node}"

    // ════════════════════════════════════════════════════════════════════
    // P 차원 — Polling (주기적 polling vs 실제 인과)
    // ════════════════════════════════════════════════════════════════════
    let private pollingModels : Scenario list = [
        // p0: 주기적 polling 만 — 인과 없음 (spurious)
        // POLL 이 100ms 주기로 발화, ACT 가 1000ms 주기 (cycle 마다 1회)
        let nodes0 = [ full "F" "POLL"; full "F" "ACT" ]
        let pattern0 (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "POLL"
                100L, full "F" "POLL"
                200L, full "F" "POLL"
                300L, full "F" "POLL"
                400L, full "F" "POLL"
                500L, full "F" "POLL"
                600L, full "F" "POLL"
                700L, full "F" "POLL"
                800L, full "F" "POLL"
                900L, full "F" "POLL"
                500L, full "F" "ACT"
              ]; Jitter = 10L }
        // POLL → ACT 는 spurious — POLL 은 그냥 polling, ACT 와 인과 없음
        yield scenario "p0_pollingOnly" "F" [] [ mkArrow "F" "POLL" "ACT" "Start" ]
                       nodes0 pattern0 2000L

        // p1: POLL 자체는 polling, ACT 다음 OUT 은 실제 인과
        let arrows1 = [ mkArrow "F" "ACT" "OUT" "Start" ]
        let nodes1 = [ full "F" "POLL"; full "F" "ACT"; full "F" "OUT" ]
        let pattern1 (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "POLL"
                100L, full "F" "POLL"
                200L, full "F" "POLL"
                300L, full "F" "POLL"
                500L, full "F" "ACT"
                700L, full "F" "OUT"
              ]; Jitter = 15L }
        // ACT→OUT 진짜 인과, POLL→ACT 와 POLL→OUT 은 spurious
        yield scenario "p1_pollingPlusCausation" "F" arrows1
                       [ mkArrow "F" "POLL" "ACT" "Start"
                         mkArrow "F" "POLL" "OUT" "Start" ]
                       nodes1 pattern1 2000L

        // p2: Heartbeat polling — 짧은 주기 (50ms) 의 polling 이 실제 trigger 와 무관
        let arrows2 = [ mkArrow "F" "TRG" "TGT" "Start" ]
        let nodes2 = [ full "F" "HB"; full "F" "TRG"; full "F" "TGT" ]
        let pattern2 (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "HB"
                50L, full "F" "HB"
                100L, full "F" "HB"
                150L, full "F" "HB"
                200L, full "F" "HB"
                250L, full "F" "HB"
                300L, full "F" "HB"
                350L, full "F" "HB"
                400L, full "F" "TRG"
                600L, full "F" "TGT"
              ]; Jitter = 15L }
        yield scenario "p2_heartbeatNoise" "F" arrows2
                       [ mkArrow "F" "HB" "TRG" "Start"
                         mkArrow "F" "HB" "TGT" "Start" ]
                       nodes2 pattern2 2000L
    ]

    // ════════════════════════════════════════════════════════════════════
    // V 차원 — Variable duration (cycle 마다 device 처리 시간 변동)
    // ════════════════════════════════════════════════════════════════════
    let private variableModels : Scenario list = [
        // v0: lag 가 cycle 마다 random 변동 (200~500ms, uniform)
        let arrows0 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes0 = [ full "F" "A"; full "F" "B" ]
        let pattern0 (rng: Random) : Simulator.CyclePattern =
            let lag = int64 (rng.Next(200, 501))
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 10L }
        // CV 가 큼 (range 300/mean 350 ≈ 0.86) — 다 통과해야 변동 인정
        yield scenario "v0_uniformRange" "F" arrows0 [] nodes0 pattern0 2000L

        // v1: bimodal lag — 짧은/긴 cycle 50:50
        let arrows1 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes1 = [ full "F" "A"; full "F" "B" ]
        let pattern1 (rng: Random) : Simulator.CyclePattern =
            let lag = if rng.Next(0, 2) = 0 then 200L else 500L
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 20L }
        yield scenario "v1_bimodal50_50" "F" arrows1 [] nodes1 pattern1 2000L

        // v2: warming-up drift — cycle 0~10 까지 lag 점점 증가, 그 후 안정
        let arrows2 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes2 = [ full "F" "A"; full "F" "B" ]
        let pattern2 (k: int) (_: Random) : Simulator.CyclePattern =
            let lag =
                if k < 10 then 200L + int64 k * 20L   // 200, 220, ..., 380
                else 400L
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        let scenarioCA name flow gt spurious nodes patternCA cycleMs : Scenario =
            { Name = name; Flow = flow; GroundTruth = gt; Spurious = spurious
              AllCalls = nodes |> List.distinct
              Pattern = (fun rng -> patternCA 0 rng)
              PatternCycleAware = Some patternCA; CycleMs = cycleMs }
        yield scenarioCA "v2_warmupDrift" "F" arrows2 [] nodes2 pattern2 2000L
    ]

    let all : Scenario list = pollingModels @ variableModels

    let stats () =
        [ "P Polling (p0-p2)", List.length pollingModels
          "V Variable (v0-v2)", List.length variableModels ]
