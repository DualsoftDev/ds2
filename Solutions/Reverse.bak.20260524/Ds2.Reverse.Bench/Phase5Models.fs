/// Phase 5 — O (Overlap) + T (Temporal shift) 차원.
///
/// O — 여러 인과 페어가 같은 시간 영역에 겹쳐 발생 (overlap)
/// T — 인과 관계가 시간 따라 점차 이동 (regime change)
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

module Phase5Models =

    let private scenario name flow gt spurious nodes pattern cycleMs : Scenario =
        { Name = name; Flow = flow
          GroundTruth = gt; Spurious = spurious
          AllCalls = nodes |> List.distinct
          Pattern = pattern; PatternCycleAware = None; CycleMs = cycleMs }

    let private mkArrow flow s t kind : VLine.GroundTruthArrow =
        { Src = $"{flow}.{s}"; Tgt = $"{flow}.{t}"; Kind = kind }

    let private full flow node = $"{flow}.{node}"

    // ════════════════════════════════════════════════════════════════════
    // O 차원 — Overlap (여러 페어 동시 진행)
    // ════════════════════════════════════════════════════════════════════
    let private overlapModels : Scenario list = [
        // o0: 두 chain 평행 진행 — A→B 와 X→Y 동시
        let arrows0 = [
            mkArrow "F" "A" "B" "Start"
            mkArrow "F" "X" "Y" "Start"
        ]
        let nodes0 = [ full "F" "A"; full "F" "B"; full "F" "X"; full "F" "Y" ]
        let pattern0 (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "A"
                50L, full "F" "X"
                300L, full "F" "B"
                350L, full "F" "Y"
              ]; Jitter = 15L }
        yield scenario "o0_twoChainsParallel" "F" arrows0 [] nodes0 pattern0 2000L

        // o1: chain 간 cross 없음 검증 — A→B 와 X→Y 가 인과 X 양방향 spurious 후보
        let arrows1 = [
            mkArrow "F" "A" "B" "Start"
            mkArrow "F" "X" "Y" "Start"
        ]
        let nodes1 = [ full "F" "A"; full "F" "B"; full "F" "X"; full "F" "Y" ]
        let pattern1 (rng: Random) : Simulator.CyclePattern =
            // 두 chain 의 timing 이 다른 cycle 간 무관 — A→Y, X→B 는 spurious
            { Offsets = [
                int64 (rng.Next(0, 100)), full "F" "A"
                int64 (rng.Next(0, 100)) + 250L, full "F" "B"
                int64 (rng.Next(500, 700)), full "F" "X"
                int64 (rng.Next(500, 700)) + 250L, full "F" "Y"
              ]; Jitter = 10L }
        yield scenario "o1_independentChains" "F" arrows1
                       [ mkArrow "F" "A" "Y" "Start"
                         mkArrow "F" "X" "B" "Start" ]
                       nodes1 pattern1 2000L

        // o2: nested overlap — outer A→D, inner B→C, B,C 가 A→D 사이 발화
        let arrows2 = [
            mkArrow "F" "A" "B" "Start"
            mkArrow "F" "B" "C" "Start"
            mkArrow "F" "C" "D" "Start"
        ]
        let nodes2 = [
            full "F" "A"; full "F" "B"; full "F" "C"; full "F" "D"
        ]
        let pattern2 (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "A"
                100L, full "F" "B"
                200L, full "F" "C"
                300L, full "F" "D"
              ]; Jitter = 15L }
        yield scenario "o2_nestedChain" "F" arrows2 [] nodes2 pattern2 2000L
    ]

    // ════════════════════════════════════════════════════════════════════
    // T 차원 — Temporal shift (regime change)
    // ════════════════════════════════════════════════════════════════════
    let private scenarioCA name flow gt spurious nodes patternCA cycleMs : Scenario =
        { Name = name; Flow = flow; GroundTruth = gt; Spurious = spurious
          AllCalls = nodes |> List.distinct
          Pattern = (fun rng -> patternCA 0 rng)
          PatternCycleAware = Some patternCA; CycleMs = cycleMs }

    let private temporalModels : Scenario list = [
        // t0: regime change — 처음 30 cycle 은 A→B lag=300, 다음 30 cycle 은 lag=600
        let arrows0 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes0 = [ full "F" "A"; full "F" "B" ]
        let pattern0 (k: int) (_: Random) : Simulator.CyclePattern =
            let lag = if k < 30 then 300L else 600L
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        yield scenarioCA "t0_regimeChange" "F" arrows0 [] nodes0 pattern0 2000L

        // t1: 점진적 shift — lag 가 cycle 마다 5ms 증가 (linear drift)
        let arrows1 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes1 = [ full "F" "A"; full "F" "B" ]
        let pattern1 (k: int) (_: Random) : Simulator.CyclePattern =
            let lag = 300L + int64 k * 5L
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        yield scenarioCA "t1_gradualShift" "F" arrows1 [] nodes1 pattern1 2500L

        // t2: 사이클 길이 변동 — cycle 마다 다른 period (variable cycle)
        let arrows2 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes2 = [ full "F" "A"; full "F" "B" ]
        let pattern2 (rng: Random) : Simulator.CyclePattern =
            // 같은 lag 지만 cycle 시작 시각이 변동 (cycle = 1500~2500)
            let offset = int64 (rng.Next(-200, 201))
            { Offsets = [
                offset, full "F" "A"
                offset + 300L, full "F" "B"
              ]; Jitter = 15L }
        yield scenario "t2_variableCycle" "F" arrows2 [] nodes2 pattern2 2000L
    ]

    let all : Scenario list = overlapModels @ temporalModels

    let stats () =
        [ "O Overlap (o0-o2)", List.length overlapModels
          "T Temporal (t0-t2)", List.length temporalModels ]
