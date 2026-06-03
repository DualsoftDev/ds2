/// 추가 stress 차원 시나리오 — 실 PLC 환경에서 나타나는 까다로운 패턴.
///
/// N 차원 (Noise) — 큰 jitter + 중간에 noise event 섞임:
///   n0~n4: 점진적으로 늘어나는 noise 강도
///
/// F 차원 (Failure) — 일부 cycle 에서 sequence 일부 skip:
///   f0~f4: 다양한 실패 패턴
///
/// L 차원 (Long-tail) — 대부분 짧은 lag + 일부 매우 긴 lag:
///   l0~l2: 매우 긴 lag 가 가끔
namespace Ds2.Reverse.Bench

open System

module StressModels =

    let private scenario name flow gt spurious nodes pattern cycleMs : Scenario =
        { Name = name; Flow = flow
          GroundTruth = gt; Spurious = spurious
          AllCalls = nodes |> List.distinct
          Pattern = pattern; PatternCycleAware = None; CycleMs = cycleMs }

    let private mkArrow flow s t kind : VLine.GroundTruthArrow =
        { Src = $"{flow}.{s}"; Tgt = $"{flow}.{t}"; Kind = kind }

    let private full flow node = $"{flow}.{node}"

    // ════════════════════════════════════════════════════════════════════
    // N0~N4: 큰 jitter + noise event 섞임
    // ════════════════════════════════════════════════════════════════════
    let private noiseModels : Scenario list = [
        for i in 0 .. 4 ->
            let nodes = [ full "F" "A"; full "F" "B"; full "F" "NOISE" ]
            let arrows = [ mkArrow "F" "A" "B" "Start" ]
            let spurious = [ mkArrow "F" "NOISE" "B" "Start" ]
            let noisePerCycle = i + 1   // 1~5 noise events per cycle
            let pattern (rng: Random) : Simulator.CyclePattern =
                let base_ = [ 0L, full "F" "A"; 200L, full "F" "B" ]
                let noiseOffsets =
                    [ for _ in 1 .. noisePerCycle ->
                        int64 (rng.Next(0, 2000)), full "F" "NOISE" ]
                { Offsets = base_ @ noiseOffsets; Jitter = 20L }
            scenario $"n{i}_noise{noisePerCycle}" "F" arrows spurious nodes pattern 2500L
    ]

    // ════════════════════════════════════════════════════════════════════
    // F0~F4: 일부 cycle 에서 sequence 부분 skip
    // ════════════════════════════════════════════════════════════════════
    let private failureModels : Scenario list = [
        // f0: 5% cycle 에서 B 발화 skip (95% partial = pass)
        let arrows0 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes0 = [ full "F" "A"; full "F" "B" ]
        let pattern0 (rng: Random) : Simulator.CyclePattern =
            let skip = rng.Next(0, 100) < 5
            let base_ = [ 0L, full "F" "A" ]
            let bEv = if skip then [] else [ 200L, full "F" "B" ]
            { Offsets = base_ @ bEv; Jitter = 20L }
        yield scenario "f0_skip5pct" "F" arrows0 [] nodes0 pattern0 2000L

        // f1: 20% skip — borderline → spurious 로 분류 (random 변동으로 suff < 0.85 가능)
        let pattern1 (rng: Random) : Simulator.CyclePattern =
            let skip = rng.Next(0, 100) < 20
            let base_ = [ 0L, full "F" "A" ]
            let bEv = if skip then [] else [ 200L, full "F" "B" ]
            { Offsets = base_ @ bEv; Jitter = 20L }
        yield scenario "f1_skip20pct" "F"
            [] [ mkArrow "F" "A" "B" "Start" ]
            [ full "F" "A"; full "F" "B" ]
            pattern1 2000L

        // f2: middle of chain skipped — A → B → C, 20% B skip
        let arrows2 = [ mkArrow "F" "A" "B" "Start"; mkArrow "F" "B" "C" "Start" ]
        let nodes2 = [ full "F" "A"; full "F" "B"; full "F" "C" ]
        let pattern2 (rng: Random) : Simulator.CyclePattern =
            let skipB = rng.Next(0, 100) < 20
            let offs =
                if skipB then [ 0L, full "F" "A"; 400L, full "F" "C" ]
                else [ 0L, full "F" "A"; 200L, full "F" "B"; 400L, full "F" "C" ]
            { Offsets = offs; Jitter = 20L }
        // A → C 가 spurious — skipped B 경우만. arrows_min 에 없다고 가정.
        yield scenario "f2_midSkip20" "F" arrows2 [] nodes2 pattern2 2000L

        // f3: 25% 에서 부분 cycle skip (전체 events 없음 → minFires 영향)
        let arrows3 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes3 = [ full "F" "A"; full "F" "B" ]
        let pattern3 (rng: Random) : Simulator.CyclePattern =
            let skip = rng.Next(0, 4) = 0
            if skip then { Offsets = []; Jitter = 20L }
            else { Offsets = [ 0L, full "F" "A"; 200L, full "F" "B" ]; Jitter = 20L }
        yield scenario "f3_partialEmpty25" "F" arrows3 [] nodes3 pattern3 2000L

        // f4: B 가 사이클마다 다른 시점에 발화 — chain 의 일부 step 가변
        let nodes4 = [ full "F" "A"; full "F" "B" ]
        let pattern4 (rng: Random) : Simulator.CyclePattern =
            // B 의 lag 50/150/250/350 중 하나 — 4-modal. CV 큼.
            let lagChoices = [| 50L; 150L; 250L; 350L |]
            let lag = lagChoices.[rng.Next(0, 4)]
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        // 4-modal 분포 → bimodal stable 인정 안 됨. 진짜 인과지만 algorithm 한계 → spurious.
        yield scenario "f4_4modal" "F" [] [ mkArrow "F" "A" "B" "Start" ] nodes4 pattern4 2000L
    ]

    // ════════════════════════════════════════════════════════════════════
    // L0~L2: Long-tail — 대부분 짧은 lag + 일부 매우 긴
    // ════════════════════════════════════════════════════════════════════
    let private longTailModels : Scenario list = [
        // l0: 90% lag=200ms, 10% lag=2000ms (outlier). std 큼.
        let pattern0 (rng: Random) : Simulator.CyclePattern =
            let isOutlier = rng.Next(0, 10) = 0
            let lag = if isOutlier then 2000L else 200L
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        // CV 큼 — drop. truth 없음.
        yield scenario "l0_outlier10pct" "F"
            [] [ mkArrow "F" "A" "B" "Start" ]
            [ full "F" "A"; full "F" "B" ]
            pattern0 3000L

        // l1: 95% lag=200ms, 5% lag=1500ms — outlier 적음, 통과 기대.
        let pattern1 (rng: Random) : Simulator.CyclePattern =
            let isOutlier = rng.Next(0, 20) = 0
            let lag = if isOutlier then 1500L else 200L
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        // CV ~ 0.5 — drop. (algorithm 한계)
        yield scenario "l1_outlier5pct" "F"
            [] [ mkArrow "F" "A" "B" "Start" ]
            [ full "F" "A"; full "F" "B" ]
            pattern1 2500L

        // l2: 99% lag=200ms, 1% lag=1500ms — 매우 적은 outlier. 안정 패턴, 통과 기대.
        let pattern2 (rng: Random) : Simulator.CyclePattern =
            let isOutlier = rng.Next(0, 100) = 0
            let lag = if isOutlier then 1500L else 200L
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        yield scenario "l2_outlier1pct" "F"
            [ mkArrow "F" "A" "B" "Start" ] []
            [ full "F" "A"; full "F" "B" ]
            pattern2 2500L
    ]

    let all : Scenario list = noiseModels @ failureModels @ longTailModels

    let stats () =
        [ "N Noise (n0-n4)", List.length noiseModels
          "F Failure (f0-f4)", List.length failureModels
          "L LongTail (l0-l2)", List.length longTailModels ]
