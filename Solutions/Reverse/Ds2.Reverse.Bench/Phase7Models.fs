/// Phase 7 — Polling pressure (P) + Multi-modal pressure (M).
/// 알고리즘의 알려진 약점 영역 압박.
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

module Phase7Models =

    let private mkArrow flow s t kind : VLine.GroundTruthArrow =
        { Src = $"{flow}.{s}"; Tgt = $"{flow}.{t}"; Kind = kind }

    let private full flow node = $"{flow}.{node}"

    // ══════════════════════════════════════════════════════════════════
    // Polling pressure — 다양한 polling rate / actual fire 비율
    // ══════════════════════════════════════════════════════════════════

    /// 한 cycle 안 polling rate 변경:
    ///   pollPerCycle: cycle 마다 POLL 발화 횟수 (1~50)
    ///   actInterval: ACT 가 매 cycle 발화 (1 = 매번, 5 = 5 cycle 마다)
    /// 진짜 인과: 없음. algorithm 이 POLL → ACT 거부해야.
    let private makePollPressure (pollPerCycle: int) (actInterval: int) : Scenario =
        let cycleMs = 2000L
        let pattern : Random -> Simulator.CyclePattern =
            let mutable cycleK = 0
            fun (_: Random) ->
                let pollStep = cycleMs / int64 (pollPerCycle + 1)
                let pollOffs =
                    [ for k in 1 .. pollPerCycle -> int64 k * pollStep, full "F" "POLL" ]
                let actOffs =
                    if cycleK % actInterval = 0 then
                        [ cycleMs / 2L, full "F" "ACT" ]
                    else []
                cycleK <- cycleK + 1
                { Offsets = pollOffs @ actOffs; Jitter = 8L }
        {
            Name = sprintf "fp_poll%02d_act%d" pollPerCycle actInterval
            Flow = "F"
            GroundTruth = []   // 인과 없음
            Spurious = [ mkArrow "F" "POLL" "ACT" "Start" ]
            AllCalls = [ full "F" "POLL"; full "F" "ACT" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    /// Polling rate 1, 2, 5, 10, 20, 50 × Act interval 1, 2, 5 = 18 시나리오.
    let allPollPressure : Scenario list = [
        for pp in [ 1; 2; 5; 10; 20; 50 ] do
            for ai in [ 1; 2; 5 ] ->
                makePollPressure pp ai
    ]

    /// Polling + 진짜 인과 mix.
    ///   POLL: 주기적 (pollPerCycle).
    ///   TRG → TGT: 진짜 인과 (TRG 가 매 cycle 직접 발화).
    ///   algorithm: TRG→TGT 만 인정, POLL→TGT / POLL→TRG 거부.
    let private makePollPlusCausation (pollPerCycle: int) : Scenario =
        let cycleMs = 2000L
        let pattern (_: Random) : Simulator.CyclePattern =
            let pollStep = cycleMs / int64 (pollPerCycle + 1)
            let pollOffs =
                [ for k in 1 .. pollPerCycle -> int64 k * pollStep, full "F" "POLL" ]
            let trueOffs = [
                500L, full "F" "TRG"
                800L, full "F" "TGT"
            ]
            { Offsets = pollOffs @ trueOffs; Jitter = 10L }
        {
            Name = sprintf "fp_pollPlus%02d" pollPerCycle
            Flow = "F"
            GroundTruth = [ mkArrow "F" "TRG" "TGT" "Start" ]
            Spurious = [
                mkArrow "F" "POLL" "TRG" "Start"
                mkArrow "F" "POLL" "TGT" "Start"
            ]
            AllCalls = [ full "F" "POLL"; full "F" "TRG"; full "F" "TGT" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allPollPlus : Scenario list = [
        for pp in [ 2; 5; 10; 20; 40 ] -> makePollPlusCausation pp
    ]

    // ══════════════════════════════════════════════════════════════════
    // Multi-modal pressure — 3-6 peaks 의 다양한 분리도
    // ══════════════════════════════════════════════════════════════════

    /// k modes (k=3..6), 각 mode lag 거리 (separationMs).
    /// algorithm 이 well-separated 3-modal 은 k-means 로 인정.
    /// k=4+ 또는 separation 작으면 거부.
    let private makeMultiModal (k: int) (separationMs: int64) : Scenario =
        let cycleMs = 3000L
        let baseLag = 200L
        let pattern (rng: Random) : Simulator.CyclePattern =
            let modeIdx = rng.Next(0, k)
            let lag = baseLag + int64 modeIdx * separationMs
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        // 진짜 인과 (각 mode 가 lag 가 다를 뿐 A→B 발화 자체는 정상)
        {
            Name = sprintf "fm_modal%d_sep%d" k (int separationMs)
            Flow = "F"
            GroundTruth = [ mkArrow "F" "A" "B" "Start" ]
            Spurious = []
            AllCalls = [ full "F" "A"; full "F" "B" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    /// k=3..6 × separation 100/200/300/500 = 16 시나리오.
    let allMultiModal : Scenario list = [
        for k in [ 3; 4; 5; 6 ] do
            for sep in [ 100L; 200L; 300L; 500L ] ->
                makeMultiModal k sep
    ]

    // ══════════════════════════════════════════════════════════════════
    // Round 2: Burst polling + Multi-modal corner cases
    // ══════════════════════════════════════════════════════════════════

    /// Burst polling — 한 cycle 안 burst phase 동안 polling 많이, idle phase 동안 적음.
    /// burstRate: burst phase 의 ms 당 fire 수.
    /// idleRate: idle phase 의 ms 당 fire 수.
    let private makeBurstPolling (burstFires: int) (idleFires: int) : Scenario =
        let cycleMs = 4000L
        let burstEnd = 1000L   // 첫 1초 burst
        let pattern (_: Random) : Simulator.CyclePattern =
            let burstOffs =
                let step = burstEnd / int64 (burstFires + 1)
                [ for k in 1 .. burstFires -> int64 k * step, full "F" "POLL" ]
            let idleOffs =
                let step = (cycleMs - burstEnd) / int64 (idleFires + 1)
                [ for k in 1 .. idleFires ->
                    burstEnd + int64 k * step, full "F" "POLL" ]
            { Offsets = burstOffs @ idleOffs @ [ 2500L, full "F" "ACT" ]
              Jitter = 8L }
        {
            Name = sprintf "fp_burst_b%d_i%d" burstFires idleFires
            Flow = "F"
            GroundTruth = []
            Spurious = [ mkArrow "F" "POLL" "ACT" "Start" ]
            AllCalls = [ full "F" "POLL"; full "F" "ACT" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allBurstPolling : Scenario list = [
        for (b, i) in [ (20, 2); (10, 1); (30, 5); (50, 0); (5, 5) ] ->
            makeBurstPolling b i
    ]

    /// Phase-shifting polling — cycle 마다 POLL 시작 시각이 shift.
    /// algorithm 이 phase pattern 으로 인과 잘못 인식하지 않아야.
    let private makePhaseShiftPolling (shiftMs: int64) : Scenario =
        let cycleMs = 3000L
        let pattern : Random -> Simulator.CyclePattern =
            let mutable k = 0
            fun (_: Random) ->
                let phase = int64 k * shiftMs % 1000L
                let pollOffs =
                    [ for j in 0 .. 9 ->
                        phase + int64 j * 200L, full "F" "POLL" ]
                k <- k + 1
                { Offsets = pollOffs @ [ 1500L, full "F" "ACT" ]
                  Jitter = 10L }
        {
            Name = sprintf "fp_phaseShift%d" (int shiftMs)
            Flow = "F"
            GroundTruth = []
            Spurious = [ mkArrow "F" "POLL" "ACT" "Start" ]
            AllCalls = [ full "F" "POLL"; full "F" "ACT" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allPhaseShift : Scenario list = [
        for s in [ 50L; 100L; 200L; 500L; 1000L ] -> makePhaseShiftPolling s
    ]

    /// Multi-modal with imbalanced cluster sizes — 한 mode 가 큰 다수 (80%), 나머지 적음.
    /// algorithm 이 작은 cluster 만 보고 거부할 수 있음 (정상).
    let private makeImbalancedModal (k: int) (majorityPercent: int) : Scenario =
        let cycleMs = 3000L
        let pattern (rng: Random) : Simulator.CyclePattern =
            let modeIdx =
                if rng.Next(0, 100) < majorityPercent then 0
                else rng.Next(1, k)
            let lag = 200L + int64 modeIdx * 200L
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        {
            Name = sprintf "fm_imbalanced_k%d_maj%d" k majorityPercent
            Flow = "F"
            GroundTruth = [ mkArrow "F" "A" "B" "Start" ]
            Spurious = []
            AllCalls = [ full "F" "A"; full "F" "B" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allImbalanced : Scenario list = [
        for k in [ 3; 4; 5 ] do
            for maj in [ 60; 70; 80; 90 ] -> makeImbalancedModal k maj
    ]

    // ══════════════════════════════════════════════════════════════════
    // Round 3: 정밀 압박 — algorithm 의 미세한 약점 영역
    // ══════════════════════════════════════════════════════════════════

    /// Low-ratio polling — POLL 과 ACT 가 비슷한 횟수 발화 (ratio < 5).
    /// 진짜 인과 와 통계적으로 거의 구분 불가.
    let private makeLowRatioPolling (pollPerCycle: int) (actPerCycle: int) : Scenario =
        let cycleMs = 2000L
        let pattern (_: Random) : Simulator.CyclePattern =
            let pollStep = cycleMs / int64 (pollPerCycle + 1)
            let actStep = cycleMs / int64 (actPerCycle + 1)
            let pollOffs =
                [ for k in 1 .. pollPerCycle -> int64 k * pollStep, full "F" "POLL" ]
            let actOffs =
                [ for k in 1 .. actPerCycle -> int64 k * actStep, full "F" "ACT" ]
            { Offsets = pollOffs @ actOffs; Jitter = 10L }
        {
            Name = sprintf "fp_lowRatio_p%d_a%d" pollPerCycle actPerCycle
            Flow = "F"
            GroundTruth = []
            Spurious = [ mkArrow "F" "POLL" "ACT" "Start" ]
            AllCalls = [ full "F" "POLL"; full "F" "ACT" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allLowRatioPolling : Scenario list = [
        for (p, a) in [ (2, 1); (3, 2); (4, 3); (5, 4); (3, 3); (4, 4) ] ->
            makeLowRatioPolling p a
    ]

    /// Multi-modal with overlap — separation < std.
    /// 실제로는 noisy lag 인과 (mean 변동) → 진짜 인과.
    let private makeOverlappingModal (k: int) (sep: int64) (jitter: int64) : Scenario =
        let cycleMs = 3000L
        let baseLag = 200L
        let pattern (rng: Random) : Simulator.CyclePattern =
            let modeIdx = rng.Next(0, k)
            let lag = baseLag + int64 modeIdx * sep
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = jitter }
        {
            Name = sprintf "fm_overlap_k%d_s%d_j%d" k (int sep) (int jitter)
            Flow = "F"
            GroundTruth = [ mkArrow "F" "A" "B" "Start" ]
            Spurious = []
            AllCalls = [ full "F" "A"; full "F" "B" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allOverlappingModal : Scenario list = [
        // separation 50ms, jitter 30ms → overlap 큼
        for k in [ 3; 4; 5 ] -> makeOverlappingModal k 50L 30L
    ]

    /// Drift + bimodal mix — lag 가 drift 하면서 두 modes 사이 변동.
    let private makeDriftBimodal () : Scenario =
        let cycleMs = 3000L
        let pattern : Random -> Simulator.CyclePattern =
            let mutable k = 0
            fun (rng: Random) ->
                let drift = int64 k * 3L   // 0 ~ 180 over 60 cycles
                let isPeak1 = rng.Next(0, 2) = 0
                let lag = (if isPeak1 then 200L else 500L) + drift
                k <- k + 1
                { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 12L }
        {
            Name = "fm_driftBimodal"
            Flow = "F"
            GroundTruth = [ mkArrow "F" "A" "B" "Start" ]
            Spurious = []
            AllCalls = [ full "F" "A"; full "F" "B" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allDriftBimodal = [ makeDriftBimodal () ]

    // ══════════════════════════════════════════════════════════════════
    // Round 4: Multi-flow polling + Timing corner cases
    // ══════════════════════════════════════════════════════════════════

    /// Multi-flow with shared polling — 여러 flow 에 같은 POLL.
    /// algorithm 이 각 flow 안 polling 거부해야.
    let private makeMultiFlowPolling (nFlows: int) : Scenario =
        let cycleMs = 3000L
        let pollPerCycle = 10
        let pattern (_: Random) : Simulator.CyclePattern =
            let offsets = ResizeArray<int64 * string>()
            for f in 1 .. nFlows do
                let flow = sprintf "F%d" f
                for k in 1 .. pollPerCycle do
                    offsets.Add(int64 k * (cycleMs / int64 (pollPerCycle + 1)),
                                sprintf "%s.POLL" flow)
                offsets.Add(1500L, sprintf "%s.ACT" flow)
            { Offsets = offsets |> List.ofSeq; Jitter = 10L }
        let allCalls =
            [ for f in 1 .. nFlows do
                yield sprintf "F%d.POLL" f
                yield sprintf "F%d.ACT" f ]
        let spurious =
            [ for f in 1 .. nFlows ->
                let flow = sprintf "F%d" f
                mkArrow flow "POLL" "ACT" "Start" ]
        {
            Name = sprintf "fp_mf%d_poll" nFlows
            Flow = "F1"
            GroundTruth = []
            Spurious = spurious
            AllCalls = allCalls
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allMultiFlowPolling : Scenario list = [
        for nf in [ 2; 5; 10 ] -> makeMultiFlowPolling nf
    ]

    /// Very long lag — large WindowMs config (lag ≤ WindowMs).
    let private makeLongLag (lagMs: int64) : Scenario =
        let cycleMs = lagMs * 3L
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "A"; lagMs, full "F" "B" ]; Jitter = 20L }
        {
            Name = sprintf "fc_longLag_%dms" (int lagMs)
            Flow = "F"
            GroundTruth = [ mkArrow "F" "A" "B" "Start" ]
            Spurious = []
            AllCalls = [ full "F" "A"; full "F" "B" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    /// Default config 에서 lag ≤ 3000ms (default WindowMs) 면 통과.
    /// 큰 lag 는 config.WindowMs 를 키워야 — 본 시나리오는 < 3000ms 범위만.
    let allLongLag : Scenario list = [
        for lag in [ 500L; 1000L; 1500L; 2000L; 2500L ] -> makeLongLag lag
    ]

    /// Very tight jitter (1ms) — algorithm float precision.
    let private makeTightJitter (jitter: int64) : Scenario =
        let cycleMs = 2000L
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "A"; 200L, full "F" "B" ]; Jitter = jitter }
        {
            Name = sprintf "fc_tightJitter_%dms" (int jitter)
            Flow = "F"
            GroundTruth = [ mkArrow "F" "A" "B" "Start" ]
            Spurious = []
            AllCalls = [ full "F" "A"; full "F" "B" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allTightJitter : Scenario list = [
        for j in [ 1L; 3L; 5L; 10L; 30L ] -> makeTightJitter j
    ]

    /// Conditional causation — 특정 condition 시 A→B, 그 외 condition 시 A→C.
    /// X (condition variable) 가 cycle 마다 toggle. A→B (X=true cycles) + A→C (X=false cycles).
    /// algorithm 이 X 의 영향 인식 못하면 A→B, A→C 둘 다 weakly 검출.
    let private makeConditionalCausation () : Scenario =
        let cycleMs = 2000L
        let pattern : Random -> Simulator.CyclePattern =
            let mutable k = 0
            fun (_: Random) ->
                let xTrue = k % 2 = 0
                let trueOffs =
                    [ 0L, full "F" "X"; 100L, full "F" "A" ]
                let target =
                    if xTrue then [ 300L, full "F" "B" ]
                    else [ 300L, full "F" "C" ]
                k <- k + 1
                { Offsets = trueOffs @ target; Jitter = 10L }
        {
            Name = "fc_conditional"
            Flow = "F"
            // truth 는 두 conditional arrows
            GroundTruth = [
                mkArrow "F" "A" "B" "Start"
                mkArrow "F" "A" "C" "Start"
            ]
            Spurious = []
            AllCalls = [ full "F" "X"; full "F" "A"; full "F" "B"; full "F" "C" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allConditional = [ makeConditionalCausation () ]

    // ══════════════════════════════════════════════════════════════════
    // Round 5: 대규모 단일 flow + extreme cycle ratio + adversarial combo
    // ══════════════════════════════════════════════════════════════════

    /// 단일 flow 안 매우 많은 calls (N=50/100 chain).
    let private makeLargeChain (n: int) : Scenario =
        let lagMs = 30L   // 짧은 lag (전체 cycle 안에 들어가도록)
        let cycleMs = int64 n * lagMs + 1000L
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                for i in 0 .. n - 1 ->
                    int64 i * lagMs, full "F" (sprintf "N%03d" i)
              ]; Jitter = 5L }
        let arrows =
            [ for i in 0 .. n - 2 ->
                mkArrow "F" (sprintf "N%03d" i) (sprintf "N%03d" (i + 1)) "Start" ]
        let calls = [ for i in 0 .. n - 1 -> full "F" (sprintf "N%03d" i) ]
        {
            Name = sprintf "fL_large_n%d" n
            Flow = "F"
            GroundTruth = arrows
            Spurious = []
            AllCalls = calls
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allLargeChain : Scenario list = [
        for n in [ 20; 50; 100 ] -> makeLargeChain n
    ]

    /// Polling + multi-modal + spurious combined attack.
    /// 진짜 인과 A→B (bimodal lag), POLL 이 매 cycle 매우 자주 발화, 3 spurious calls.
    let private makeCombinedAttack () : Scenario =
        let cycleMs = 3000L
        let pattern (rng: Random) : Simulator.CyclePattern =
            let pollOffs =
                [ for k in 1 .. 20 -> int64 k * 100L, full "F" "POLL" ]
            let lag = if rng.Next(0, 2) = 0 then 200L else 600L
            let trueOffs = [ 0L, full "F" "A"; lag, full "F" "B" ]
            let spOffs =
                [ for k in 1 .. 3 ->
                    int64 (rng.Next(0, int cycleMs)),
                    sprintf "F.SP%d" k ]
            { Offsets = pollOffs @ trueOffs @ spOffs; Jitter = 12L }
        {
            Name = "fX_combined"
            Flow = "F"
            GroundTruth = [ mkArrow "F" "A" "B" "Start" ]
            Spurious = [
                mkArrow "F" "POLL" "A" "Start"
                mkArrow "F" "POLL" "B" "Start"
                mkArrow "F" "A" "POLL" "Start"
            ]
            AllCalls = [
                full "F" "POLL"; full "F" "A"; full "F" "B"
                "F.SP1"; "F.SP2"; "F.SP3"
            ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allCombinedAttack = [ makeCombinedAttack () ]

    /// Cycle ratio 변동 — A 매 cycle 발화, B 는 5 cycle 마다 한 번.
    /// 진짜 인과는 weak (necc 가 낮음 → drop 기대).
    let private makeRareEffect (cyclesPerEffect: int) : Scenario =
        let cycleMs = 2000L
        let pattern : Random -> Simulator.CyclePattern =
            let mutable k = 0
            fun (_: Random) ->
                let hasB = k % cyclesPerEffect = 0
                let offs =
                    [ 0L, full "F" "A" ]
                    @ (if hasB then [ 200L, full "F" "B" ] else [])
                k <- k + 1
                { Offsets = offs; Jitter = 10L }
        {
            // necc 작음 → 거부 (A→B 가 매번 일어나지 않음)
            Name = sprintf "fr_rareEffect_%dcyc" cyclesPerEffect
            Flow = "F"
            GroundTruth = []
            Spurious = [ mkArrow "F" "A" "B" "Start" ]
            AllCalls = [ full "F" "A"; full "F" "B" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allRareEffect : Scenario list = [
        for c in [ 3; 5; 10; 20 ] -> makeRareEffect c
    ]

    // ══════════════════════════════════════════════════════════════════
    // Round 7: Conditional causation 확장 + non-stationary lag + missing data
    // ══════════════════════════════════════════════════════════════════

    /// Conditional with prob — X true 면 A→B, X false 면 A→C. X 의 true 확률 변경.
    let private makeConditionalProb (xTrueProb: int) : Scenario =
        let cycleMs = 2000L
        let pattern (rng: Random) : Simulator.CyclePattern =
            let xTrue = rng.Next(0, 100) < xTrueProb
            let target =
                if xTrue then [ 300L, full "F" "B" ]
                else [ 300L, full "F" "C" ]
            { Offsets = [
                0L, full "F" "X"; 100L, full "F" "A"
              ] @ target; Jitter = 10L }
        // truth: 두 conditional arrows 모두 (X 가 통계적 분포에 따라)
        let truth =
            [
                if xTrueProb >= 30 then yield mkArrow "F" "A" "B" "Start"
                if xTrueProb <= 70 then yield mkArrow "F" "A" "C" "Start"
            ]
        {
            Name = sprintf "fc_condProb%d" xTrueProb
            Flow = "F"
            GroundTruth = truth
            Spurious = []
            AllCalls = [ full "F" "X"; full "F" "A"; full "F" "B"; full "F" "C" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allConditionalProb : Scenario list = [
        for p in [ 50; 60; 70; 80; 90 ] -> makeConditionalProb p
    ]

    /// Non-stationary lag — 사이클 흐름에 따라 lag pattern 자체 변화.
    /// 첫 20 cycle 은 lag 200ms, 다음 20 은 400ms, 마지막 20 은 300ms.
    let private makeNonStationary () : Scenario =
        let cycleMs = 2000L
        let pattern : Random -> Simulator.CyclePattern =
            let mutable k = 0
            fun (_: Random) ->
                let lag =
                    if k < 20 then 200L
                    elif k < 40 then 400L
                    else 300L
                k <- k + 1
                { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        {
            Name = "fns_threePhase"
            Flow = "F"
            GroundTruth = [ mkArrow "F" "A" "B" "Start" ]
            Spurious = []
            AllCalls = [ full "F" "A"; full "F" "B" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allNonStationary = [ makeNonStationary () ]

    /// Missing data — 일정 비율 cycles 에 events 누락.
    /// missRate %: cycle 마다 그 확률로 모든 events 사라짐 (data loss simulation).
    let private makeMissingData (missRate: int) : Scenario =
        let cycleMs = 2000L
        let pattern (rng: Random) : Simulator.CyclePattern =
            if rng.Next(0, 100) < missRate then
                { Offsets = []; Jitter = 0L }
            else
                { Offsets = [ 0L, full "F" "A"; 250L, full "F" "B" ]
                  Jitter = 12L }
        {
            Name = sprintf "fm_missing%d" missRate
            Flow = "F"
            GroundTruth = [ mkArrow "F" "A" "B" "Start" ]
            Spurious = []
            AllCalls = [ full "F" "A"; full "F" "B" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allMissingData : Scenario list = [
        for r in [ 10; 20; 30; 50 ] -> makeMissingData r
    ]

    // ══════════════════════════════════════════════════════════════════
    // Round 8: 회복 가능한 약점 + 임계 boundary tests
    // ══════════════════════════════════════════════════════════════════

    /// suff/necc 정확 boundary (0.85) 검증 — algorithm threshold 정확성.
    let private makeSuffBoundary (suffPercent: int) : Scenario =
        let cycleMs = 2000L
        let pattern (rng: Random) : Simulator.CyclePattern =
            let hasB = rng.Next(0, 100) < suffPercent
            let offs =
                [ 0L, full "F" "A" ]
                @ (if hasB then [ 200L, full "F" "B" ] else [])
            { Offsets = offs; Jitter = 10L }
        // suff = suffPercent/100. >= 85% → 통과 기대.
        let truth =
            if suffPercent >= 90 then [ mkArrow "F" "A" "B" "Start" ]
            else []
        let spurious =
            if suffPercent >= 90 then []
            else [ mkArrow "F" "A" "B" "Start" ]
        {
            Name = sprintf "fc_suffBoundary%d" suffPercent
            Flow = "F"
            GroundTruth = truth
            Spurious = spurious
            AllCalls = [ full "F" "A"; full "F" "B" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    /// Boundary scenarios — algorithm 정확성 검증.
    /// 80% (drop), 85% (boundary), 90% (pass), 95% (clear pass)
    let allSuffBoundary : Scenario list = [
        for p in [ 70; 80; 85; 90; 95 ] -> makeSuffBoundary p
    ]

    /// Time-resolution stress — 매우 짧은 cycle (500ms) 에서 short lag (50ms).
    let private makeTimeResolution (cycleMs: int64) (lagMs: int64) : Scenario =
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "A"; lagMs, full "F" "B" ]; Jitter = 5L }
        {
            Name = sprintf "fc_timeRes_c%d_l%d" (int cycleMs) (int lagMs)
            Flow = "F"
            GroundTruth = [ mkArrow "F" "A" "B" "Start" ]
            Spurious = []
            AllCalls = [ full "F" "A"; full "F" "B" ]
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allTimeResolution : Scenario list = [
        for (c, l) in [ (300L, 30L); (500L, 50L); (1000L, 100L); (200L, 30L) ] ->
            makeTimeResolution c l
    ]

    /// All combined.
    let allVariants : Scenario list =
        allPollPressure @ allPollPlus @ allMultiModal
        @ allBurstPolling @ allPhaseShift @ allImbalanced
        @ allLowRatioPolling @ allOverlappingModal @ allDriftBimodal
        @ allMultiFlowPolling @ allLongLag @ allTightJitter @ allConditional
        @ allLargeChain @ allCombinedAttack @ allRareEffect
        @ allConditionalProb @ allNonStationary @ allMissingData
        @ allSuffBoundary @ allTimeResolution
