/// Phase 6 — F (Flow) 차원. Case C 상응 — multi-flow inline chain.
/// nFlows = 1..20 까지 변화시키며 algorithm 강화.
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

module Phase6Models =

    let private mkArrow flow s t kind : VLine.GroundTruthArrow =
        { Src = $"{flow}.{s}"; Tgt = $"{flow}.{t}"; Kind = kind }

    /// 한 flow 의 (arrows, calls, offsets per cycle) 생성.
    /// flowIdx: 1-based. stagesPerFlow: 각 flow 안 stage 수.
    let private buildFlow (flowIdx: int) (stagesPerFlow: int) (lagMs: int64) =
        let flow = sprintf "F%d" flowIdx
        let arrows = ResizeArray<VLine.GroundTruthArrow>()
        let calls = ResizeArray<string>()
        let offsets = ResizeArray<int64 * string>()
        // 각 stage 의 ADV / RET pair
        for i in 1 .. stagesPerFlow do
            let adv = sprintf "S%d%d.ADV" flowIdx i
            let ret = sprintf "S%d%d.RET" flowIdx i
            arrows.Add(mkArrow flow adv ret "Start")
            calls.Add(sprintf "%s.%s" flow adv)
            calls.Add(sprintf "%s.%s" flow ret)
            offsets.Add(int64 (i - 1) * lagMs * 2L, sprintf "%s.%s" flow adv)
            offsets.Add(int64 (i - 1) * lagMs * 2L + lagMs, sprintf "%s.%s" flow ret)
        arrows |> List.ofSeq, calls |> List.ofSeq, offsets |> List.ofSeq

    /// MultiFlow scenario: nFlows 개 flow 가 동시에 안에서 chain 진행.
    /// Cross-flow arrows 없이 (단순) — flow 간 독립.
    let private makeMultiFlow (nFlows: int) (stagesPerFlow: int) (lagMs: int64)
                              (cycleMs: int64) (jitter: int64) : Scenario =
        let allArrows = ResizeArray<VLine.GroundTruthArrow>()
        let allCalls = ResizeArray<string>()
        let allOffsets = ResizeArray<int64 * string>()
        for f in 1 .. nFlows do
            let arrows, calls, offsets = buildFlow f stagesPerFlow lagMs
            allArrows.AddRange arrows
            allCalls.AddRange calls
            allOffsets.AddRange offsets
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = allOffsets |> List.ofSeq; Jitter = jitter }
        {
            Name = sprintf "f%02d_multiFlow_n%d" nFlows nFlows
            Flow = "F1"   // primary flow name (BenchRunner 요구사항)
            GroundTruth = allArrows |> List.ofSeq
            Spurious = []
            AllCalls = allCalls |> Seq.distinct |> List.ofSeq
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    /// All F (flow) 차원 시나리오 — nFlows 1..20, stagesPerFlow=3, lag=200ms.
    let all : Scenario list = [
        for nFlows in 1 .. 20 ->
            makeMultiFlow nFlows 3 200L 3000L 15L
    ]

    // ── Variant: Async flows (각 flow 가 다른 cycle period) ──────────────
    /// 각 flow 마다 다른 sub-cycle period — flow 간 async (cycle 별로 시작점 다름).
    let private makeAsyncFlows (nFlows: int) (stagesPerFlow: int) : Scenario =
        let baseLag = 200L
        let cycleMs = 4000L
        let allArrows = ResizeArray<VLine.GroundTruthArrow>()
        let allCalls = ResizeArray<string>()
        let flowOffsets =
            // 각 flow 시작 시각이 다름 (offset = (flowIdx-1) * 50ms)
            [| for f in 0 .. nFlows - 1 -> int64 f * 50L |]
        let pattern (_: Random) : Simulator.CyclePattern =
            let offsets = ResizeArray<int64 * string>()
            for f in 1 .. nFlows do
                let flow = sprintf "F%d" f
                let foff = flowOffsets.[f - 1]
                for i in 1 .. stagesPerFlow do
                    let adv = sprintf "%s.S%d%d.ADV" flow f i
                    let ret = sprintf "%s.S%d%d.RET" flow f i
                    offsets.Add(foff + int64 (i - 1) * baseLag * 2L, adv)
                    offsets.Add(foff + int64 (i - 1) * baseLag * 2L + baseLag, ret)
            { Offsets = offsets |> List.ofSeq; Jitter = 15L }
        for f in 1 .. nFlows do
            let flow = sprintf "F%d" f
            for i in 1 .. stagesPerFlow do
                let adv = sprintf "S%d%d.ADV" f i
                let ret = sprintf "S%d%d.RET" f i
                allArrows.Add(mkArrow flow adv ret "Start")
                allCalls.Add(sprintf "%s.%s" flow adv)
                allCalls.Add(sprintf "%s.%s" flow ret)
        {
            Name = sprintf "fa%02d_asyncFlow_n%d" nFlows nFlows
            Flow = "F1"
            GroundTruth = allArrows |> List.ofSeq
            Spurious = []
            AllCalls = allCalls |> List.ofSeq
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    /// Async flow 변형 (1..20).
    let allAsync : Scenario list = [
        for nFlows in 1 .. 20 -> makeAsyncFlows nFlows 3
    ]

    // ── Variant: Heterogeneous lag (각 flow 가 다른 lag) ─────────────────
    /// 각 flow 의 stage lag 이 모두 다름 (flow1=150ms, flow2=200ms, ...).
    let private makeHeteroLag (nFlows: int) (stagesPerFlow: int) : Scenario =
        let cycleMs = 5000L
        let allArrows = ResizeArray<VLine.GroundTruthArrow>()
        let allCalls = ResizeArray<string>()
        let pattern (_: Random) : Simulator.CyclePattern =
            let offsets = ResizeArray<int64 * string>()
            for f in 1 .. nFlows do
                let flow = sprintf "F%d" f
                let lag = 100L + int64 f * 30L   // flow1=130ms, flow2=160ms ... flow20=700ms
                for i in 1 .. stagesPerFlow do
                    let adv = sprintf "%s.S%d%d.ADV" flow f i
                    let ret = sprintf "%s.S%d%d.RET" flow f i
                    offsets.Add(int64 (i - 1) * lag * 2L, adv)
                    offsets.Add(int64 (i - 1) * lag * 2L + lag, ret)
            { Offsets = offsets |> List.ofSeq; Jitter = 15L }
        for f in 1 .. nFlows do
            let flow = sprintf "F%d" f
            for i in 1 .. stagesPerFlow do
                let adv = sprintf "S%d%d.ADV" f i
                let ret = sprintf "S%d%d.RET" f i
                allArrows.Add(mkArrow flow adv ret "Start")
                allCalls.Add(sprintf "%s.%s" flow adv)
                allCalls.Add(sprintf "%s.%s" flow ret)
        {
            Name = sprintf "fh%02d_heteroLag_n%d" nFlows nFlows
            Flow = "F1"
            GroundTruth = allArrows |> List.ofSeq
            Spurious = []
            AllCalls = allCalls |> List.ofSeq
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allHetero : Scenario list = [
        for nFlows in 1 .. 20 -> makeHeteroLag nFlows 3
    ]

    // ── Variant: With spurious cross-flow events (algorithm 가 cross-flow 인과로
    //            오인식하지 않아야) ────────────────────────────────────────
    let private makeWithSpurious (nFlows: int) (stagesPerFlow: int) : Scenario =
        let baseLag = 200L
        let cycleMs = 3000L
        let allArrows = ResizeArray<VLine.GroundTruthArrow>()
        let allCalls = ResizeArray<string>()
        let spuriousCalls = ResizeArray<string>()
        for f in 1 .. nFlows do
            let flow = sprintf "F%d" f
            for i in 1 .. stagesPerFlow do
                let adv = sprintf "S%d%d.ADV" f i
                let ret = sprintf "S%d%d.RET" f i
                allArrows.Add(mkArrow flow adv ret "Start")
                allCalls.Add(sprintf "%s.%s" flow adv)
                allCalls.Add(sprintf "%s.%s" flow ret)
            // 각 flow 에 spurious noise call (random)
            spuriousCalls.Add(sprintf "%s.NOISE" flow)
            allCalls.Add(sprintf "%s.NOISE" flow)
        let pattern (rng: Random) : Simulator.CyclePattern =
            let offsets = ResizeArray<int64 * string>()
            for f in 1 .. nFlows do
                let flow = sprintf "F%d" f
                for i in 1 .. stagesPerFlow do
                    offsets.Add(int64 (i - 1) * baseLag * 2L,
                                sprintf "%s.S%d%d.ADV" flow f i)
                    offsets.Add(int64 (i - 1) * baseLag * 2L + baseLag,
                                sprintf "%s.S%d%d.RET" flow f i)
                offsets.Add(int64 (rng.Next(0, int cycleMs)), sprintf "%s.NOISE" flow)
            { Offsets = offsets |> List.ofSeq; Jitter = 15L }
        {
            Name = sprintf "fs%02d_withSpurious_n%d" nFlows nFlows
            Flow = "F1"
            GroundTruth = allArrows |> List.ofSeq
            Spurious = []
            AllCalls = allCalls |> List.ofSeq
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allSpurious : Scenario list = [
        for nFlows in 1 .. 20 -> makeWithSpurious nFlows 3
    ]

    // ── Variant: Cross-flow chain (F1.last → F2.first chain) ────────────
    /// nFlows 가 직렬 chain: F1 → F2 → ... → Fn. 각 flow 끝나면 다음 flow 시작.
    /// 한 cycle 안 flow 들이 sequential 진행.
    let private makeCrossFlowChain (nFlows: int) (stagesPerFlow: int) : Scenario =
        let lagMs = 200L
        let flowDuration = int64 stagesPerFlow * lagMs * 2L   // 한 flow 가 차지하는 시간
        let cycleMs = flowDuration * int64 nFlows + 1000L
        let allArrows = ResizeArray<VLine.GroundTruthArrow>()
        let allCalls = ResizeArray<string>()
        let pattern (_: Random) : Simulator.CyclePattern =
            let offsets = ResizeArray<int64 * string>()
            for f in 1 .. nFlows do
                let flow = sprintf "F%d" f
                let foff = int64 (f - 1) * flowDuration
                for i in 1 .. stagesPerFlow do
                    offsets.Add(foff + int64 (i - 1) * lagMs * 2L,
                                sprintf "%s.S%d%d.ADV" flow f i)
                    offsets.Add(foff + int64 (i - 1) * lagMs * 2L + lagMs,
                                sprintf "%s.S%d%d.RET" flow f i)
            { Offsets = offsets |> List.ofSeq; Jitter = 15L }
        for f in 1 .. nFlows do
            let flow = sprintf "F%d" f
            for i in 1 .. stagesPerFlow do
                let adv = sprintf "S%d%d.ADV" f i
                let ret = sprintf "S%d%d.RET" f i
                allArrows.Add(mkArrow flow adv ret "Start")
                allCalls.Add(sprintf "%s.%s" flow adv)
                allCalls.Add(sprintf "%s.%s" flow ret)
        {
            Name = sprintf "fx%02d_crossFlowChain_n%d" nFlows nFlows
            Flow = "F1"
            GroundTruth = allArrows |> List.ofSeq
            Spurious = []
            AllCalls = allCalls |> List.ofSeq
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allCrossFlow : Scenario list = [
        for nFlows in 1 .. 20 -> makeCrossFlowChain nFlows 2
    ]

    // ── Variant: Synchronized barrier (모든 flow 동시 시작 / 동시 종료) ──
    /// 모든 flow 가 정확히 같은 시각에 시작 — flow 간 강한 sync.
    /// algorithm 이 cross-flow 인과 잘못 인식하지 않아야.
    let private makeSyncBarrier (nFlows: int) (stagesPerFlow: int) : Scenario =
        let lagMs = 200L
        let cycleMs = int64 stagesPerFlow * lagMs * 3L + 500L
        let allArrows = ResizeArray<VLine.GroundTruthArrow>()
        let allCalls = ResizeArray<string>()
        let pattern (_: Random) : Simulator.CyclePattern =
            let offsets = ResizeArray<int64 * string>()
            // 모든 flow 가 동일 시각 출발
            for f in 1 .. nFlows do
                let flow = sprintf "F%d" f
                for i in 1 .. stagesPerFlow do
                    offsets.Add(int64 (i - 1) * lagMs * 2L,
                                sprintf "%s.S%d%d.ADV" flow f i)
                    offsets.Add(int64 (i - 1) * lagMs * 2L + lagMs,
                                sprintf "%s.S%d%d.RET" flow f i)
            { Offsets = offsets |> List.ofSeq; Jitter = 15L }
        for f in 1 .. nFlows do
            let flow = sprintf "F%d" f
            for i in 1 .. stagesPerFlow do
                let adv = sprintf "S%d%d.ADV" f i
                let ret = sprintf "S%d%d.RET" f i
                allArrows.Add(mkArrow flow adv ret "Start")
                allCalls.Add(sprintf "%s.%s" flow adv)
                allCalls.Add(sprintf "%s.%s" flow ret)
        {
            Name = sprintf "fb%02d_syncBarrier_n%d" nFlows nFlows
            Flow = "F1"
            GroundTruth = allArrows |> List.ofSeq
            Spurious = []
            AllCalls = allCalls |> List.ofSeq
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allSyncBarrier : Scenario list = [
        for nFlows in 1 .. 20 -> makeSyncBarrier nFlows 3
    ]

    // ── Variant: Burst (cycle 마다 random 한 일부 flow 만 발화) ──────────
    /// 각 cycle 에서 nFlows 중 일부만 발화 (50% 확률).
    let private makeBurst (nFlows: int) (stagesPerFlow: int) : Scenario =
        let lagMs = 200L
        let cycleMs = 3000L
        let allArrows = ResizeArray<VLine.GroundTruthArrow>()
        let allCalls = ResizeArray<string>()
        let pattern (rng: Random) : Simulator.CyclePattern =
            let offsets = ResizeArray<int64 * string>()
            for f in 1 .. nFlows do
                // 50% 확률로 이 cycle 에서 발화
                if rng.Next(0, 2) = 0 then
                    let flow = sprintf "F%d" f
                    for i in 1 .. stagesPerFlow do
                        offsets.Add(int64 (i - 1) * lagMs * 2L,
                                    sprintf "%s.S%d%d.ADV" flow f i)
                        offsets.Add(int64 (i - 1) * lagMs * 2L + lagMs,
                                    sprintf "%s.S%d%d.RET" flow f i)
            { Offsets = offsets |> List.ofSeq; Jitter = 15L }
        for f in 1 .. nFlows do
            let flow = sprintf "F%d" f
            for i in 1 .. stagesPerFlow do
                let adv = sprintf "S%d%d.ADV" f i
                let ret = sprintf "S%d%d.RET" f i
                allArrows.Add(mkArrow flow adv ret "Start")
                allCalls.Add(sprintf "%s.%s" flow adv)
                allCalls.Add(sprintf "%s.%s" flow ret)
        {
            Name = sprintf "fu%02d_burst_n%d" nFlows nFlows
            Flow = "F1"
            GroundTruth = allArrows |> List.ofSeq
            Spurious = []
            AllCalls = allCalls |> List.ofSeq
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allBurst : Scenario list = [
        for nFlows in 1 .. 20 -> makeBurst nFlows 3
    ]

    // ── Round 3 adversarial variants ────────────────────────────────────

    /// Confounded multi-flow — 모든 flow 가 동일한 external trigger 따라 발화 시간
    /// 변동 → 통계적으로 cross-flow 인과처럼 보임. algorithm 이 거부해야.
    let private makeConfoundedFlows (nFlows: int) (stagesPerFlow: int) : Scenario =
        let lagMs = 200L
        let cycleMs = 3000L
        let allArrows = ResizeArray<VLine.GroundTruthArrow>()
        let allCalls = ResizeArray<string>()
        let pattern (rng: Random) : Simulator.CyclePattern =
            // 모든 flow 가 cycle 마다 random offset 따라 함께 shift
            // (외부 timer 같은 효과)
            let cycleShift = int64 (rng.Next(0, 500))
            let offsets = ResizeArray<int64 * string>()
            for f in 1 .. nFlows do
                let flow = sprintf "F%d" f
                for i in 1 .. stagesPerFlow do
                    offsets.Add(cycleShift + int64 (i - 1) * lagMs * 2L,
                                sprintf "%s.S%d%d.ADV" flow f i)
                    offsets.Add(cycleShift + int64 (i - 1) * lagMs * 2L + lagMs,
                                sprintf "%s.S%d%d.RET" flow f i)
            { Offsets = offsets |> List.ofSeq; Jitter = 15L }
        for f in 1 .. nFlows do
            let flow = sprintf "F%d" f
            for i in 1 .. stagesPerFlow do
                let adv = sprintf "S%d%d.ADV" f i
                let ret = sprintf "S%d%d.RET" f i
                allArrows.Add(mkArrow flow adv ret "Start")
                allCalls.Add(sprintf "%s.%s" flow adv)
                allCalls.Add(sprintf "%s.%s" flow ret)
        {
            Name = sprintf "fc%02d_confoundedFlows_n%d" nFlows nFlows
            Flow = "F1"
            GroundTruth = allArrows |> List.ofSeq
            Spurious = []
            AllCalls = allCalls |> List.ofSeq
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allConfounded : Scenario list = [
        for nFlows in 1 .. 20 -> makeConfoundedFlows nFlows 3
    ]

    /// Tight cycle — flow 가 많을 때 cycle 이 너무 짧아 events 끼리 매우 가까움.
    /// algorithm 이 어떤 flow 에 속하는지 정확 분리해야.
    let private makeTightCycle (nFlows: int) (stagesPerFlow: int) : Scenario =
        let lagMs = 100L
        let cycleMs = max 1500L (int64 stagesPerFlow * lagMs * 2L + 300L)   // 매우 tight
        let allArrows = ResizeArray<VLine.GroundTruthArrow>()
        let allCalls = ResizeArray<string>()
        let pattern (_: Random) : Simulator.CyclePattern =
            let offsets = ResizeArray<int64 * string>()
            for f in 1 .. nFlows do
                let flow = sprintf "F%d" f
                for i in 1 .. stagesPerFlow do
                    offsets.Add(int64 (i - 1) * lagMs * 2L,
                                sprintf "%s.S%d%d.ADV" flow f i)
                    offsets.Add(int64 (i - 1) * lagMs * 2L + lagMs,
                                sprintf "%s.S%d%d.RET" flow f i)
            { Offsets = offsets |> List.ofSeq; Jitter = 8L }
        for f in 1 .. nFlows do
            let flow = sprintf "F%d" f
            for i in 1 .. stagesPerFlow do
                let adv = sprintf "S%d%d.ADV" f i
                let ret = sprintf "S%d%d.RET" f i
                allArrows.Add(mkArrow flow adv ret "Start")
                allCalls.Add(sprintf "%s.%s" flow adv)
                allCalls.Add(sprintf "%s.%s" flow ret)
        {
            Name = sprintf "ft%02d_tightCycle_n%d" nFlows nFlows
            Flow = "F1"
            GroundTruth = allArrows |> List.ofSeq
            Spurious = []
            AllCalls = allCalls |> List.ofSeq
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allTightCycle : Scenario list = [
        for nFlows in 1 .. 20 -> makeTightCycle nFlows 3
    ]

    /// Heavy noise — 각 flow 에 다수 spurious calls (call 당 5개 noise).
    /// algorithm 이 noise 모두 거부해야.
    let private makeHeavyNoise (nFlows: int) (stagesPerFlow: int) : Scenario =
        let lagMs = 200L
        let cycleMs = 3000L
        let allArrows = ResizeArray<VLine.GroundTruthArrow>()
        let allCalls = ResizeArray<string>()
        for f in 1 .. nFlows do
            let flow = sprintf "F%d" f
            for i in 1 .. stagesPerFlow do
                let adv = sprintf "S%d%d.ADV" f i
                let ret = sprintf "S%d%d.RET" f i
                allArrows.Add(mkArrow flow adv ret "Start")
                allCalls.Add(sprintf "%s.%s" flow adv)
                allCalls.Add(sprintf "%s.%s" flow ret)
            for k in 1 .. 5 do
                allCalls.Add(sprintf "%s.NOISE%d" flow k)
        let pattern (rng: Random) : Simulator.CyclePattern =
            let offsets = ResizeArray<int64 * string>()
            for f in 1 .. nFlows do
                let flow = sprintf "F%d" f
                for i in 1 .. stagesPerFlow do
                    offsets.Add(int64 (i - 1) * lagMs * 2L,
                                sprintf "%s.S%d%d.ADV" flow f i)
                    offsets.Add(int64 (i - 1) * lagMs * 2L + lagMs,
                                sprintf "%s.S%d%d.RET" flow f i)
                for k in 1 .. 5 do
                    offsets.Add(int64 (rng.Next(0, int cycleMs)),
                                sprintf "%s.NOISE%d" flow k)
            { Offsets = offsets |> List.ofSeq; Jitter = 15L }
        {
            Name = sprintf "fn%02d_heavyNoise_n%d" nFlows nFlows
            Flow = "F1"
            GroundTruth = allArrows |> List.ofSeq
            Spurious = []
            AllCalls = allCalls |> List.ofSeq
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allHeavyNoise : Scenario list = [
        for nFlows in 1 .. 20 -> makeHeavyNoise nFlows 3
    ]

    // ── Round 4 stress variants ─────────────────────────────────────────

    /// High-stage flows — 각 flow 가 10 stages (대형 flow).
    let private makeHighStage (nFlows: int) : Scenario =
        let stagesPerFlow = 10
        let lagMs = 100L
        let cycleMs = int64 stagesPerFlow * lagMs * 2L + 500L
        let allArrows = ResizeArray<VLine.GroundTruthArrow>()
        let allCalls = ResizeArray<string>()
        let pattern (_: Random) : Simulator.CyclePattern =
            let offsets = ResizeArray<int64 * string>()
            for f in 1 .. nFlows do
                let flow = sprintf "F%d" f
                for i in 1 .. stagesPerFlow do
                    offsets.Add(int64 (i - 1) * lagMs * 2L,
                                sprintf "%s.S%d%02d.ADV" flow f i)
                    offsets.Add(int64 (i - 1) * lagMs * 2L + lagMs,
                                sprintf "%s.S%d%02d.RET" flow f i)
            { Offsets = offsets |> List.ofSeq; Jitter = 10L }
        for f in 1 .. nFlows do
            let flow = sprintf "F%d" f
            for i in 1 .. stagesPerFlow do
                let adv = sprintf "S%d%02d.ADV" f i
                let ret = sprintf "S%d%02d.RET" f i
                allArrows.Add(mkArrow flow adv ret "Start")
                allCalls.Add(sprintf "%s.%s" flow adv)
                allCalls.Add(sprintf "%s.%s" flow ret)
        {
            Name = sprintf "fH%02d_highStage_n%d" nFlows nFlows
            Flow = "F1"
            GroundTruth = allArrows |> List.ofSeq
            Spurious = []
            AllCalls = allCalls |> List.ofSeq
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allHighStage : Scenario list = [
        for nFlows in 1 .. 20 -> makeHighStage nFlows
    ]

    /// Long chain — 각 flow 가 15 stages 의 chain (간단 inline 길게).
    let private makeLongChain (nFlows: int) : Scenario =
        let stagesPerFlow = 15
        let lagMs = 80L
        let cycleMs = int64 stagesPerFlow * lagMs * 2L + 800L
        let allArrows = ResizeArray<VLine.GroundTruthArrow>()
        let allCalls = ResizeArray<string>()
        let pattern (_: Random) : Simulator.CyclePattern =
            let offsets = ResizeArray<int64 * string>()
            for f in 1 .. nFlows do
                let flow = sprintf "F%d" f
                // chain N1 → N2 → ... → Nn
                for i in 1 .. stagesPerFlow do
                    offsets.Add(int64 (i - 1) * lagMs,
                                sprintf "%s.N%02d" flow i)
            { Offsets = offsets |> List.ofSeq; Jitter = 8L }
        for f in 1 .. nFlows do
            let flow = sprintf "F%d" f
            for i in 1 .. stagesPerFlow - 1 do
                let a = sprintf "N%02d" i
                let b = sprintf "N%02d" (i + 1)
                allArrows.Add(mkArrow flow a b "Start")
            for i in 1 .. stagesPerFlow do
                allCalls.Add(sprintf "%s.N%02d" flow i)
        {
            Name = sprintf "fL%02d_longChain_n%d" nFlows nFlows
            Flow = "F1"
            GroundTruth = allArrows |> List.ofSeq
            Spurious = []
            AllCalls = allCalls |> List.ofSeq
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allLongChain : Scenario list = [
        for nFlows in 1 .. 20 -> makeLongChain nFlows
    ]

    /// Ratio stress — flow 마다 cycle ratio 가 다른 효과 (event count 다름).
    /// flow1 은 stages 2, flow2 는 stages 3, ..., flow20 은 stages 21.
    let private makeRatioStress (nFlows: int) : Scenario =
        let lagMs = 150L
        let cycleMs = 5000L
        let allArrows = ResizeArray<VLine.GroundTruthArrow>()
        let allCalls = ResizeArray<string>()
        let pattern (_: Random) : Simulator.CyclePattern =
            let offsets = ResizeArray<int64 * string>()
            for f in 1 .. nFlows do
                let stagesPerFlow = f + 1   // flow1=2 stages, flow2=3, ...
                let flow = sprintf "F%d" f
                for i in 1 .. stagesPerFlow do
                    offsets.Add(int64 (i - 1) * lagMs * 2L,
                                sprintf "%s.S%d%02d.ADV" flow f i)
                    offsets.Add(int64 (i - 1) * lagMs * 2L + lagMs,
                                sprintf "%s.S%d%02d.RET" flow f i)
            { Offsets = offsets |> List.ofSeq; Jitter = 12L }
        for f in 1 .. nFlows do
            let stagesPerFlow = f + 1
            let flow = sprintf "F%d" f
            for i in 1 .. stagesPerFlow do
                let adv = sprintf "S%d%02d.ADV" f i
                let ret = sprintf "S%d%02d.RET" f i
                allArrows.Add(mkArrow flow adv ret "Start")
                allCalls.Add(sprintf "%s.%s" flow adv)
                allCalls.Add(sprintf "%s.%s" flow ret)
        {
            Name = sprintf "fR%02d_ratioStress_n%d" nFlows nFlows
            Flow = "F1"
            GroundTruth = allArrows |> List.ofSeq
            Spurious = []
            AllCalls = allCalls |> List.ofSeq
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allRatioStress : Scenario list = [
        for nFlows in 1 .. 20 -> makeRatioStress nFlows
    ]

    // ── Round 5 intra-flow adversarial (transitive + cycle + tight) ─────

    /// 각 flow 가 chain + 의도된 transitive (N1→N3 false) 포함.
    /// algorithm 의 transitive reduction 이 정확 동작해야.
    let private makeTransitiveBait (nFlows: int) : Scenario =
        let stagesPerFlow = 5
        let lagMs = 150L
        let cycleMs = 3000L
        let allArrows = ResizeArray<VLine.GroundTruthArrow>()
        let spuriousArrows = ResizeArray<VLine.GroundTruthArrow>()
        let allCalls = ResizeArray<string>()
        let pattern (_: Random) : Simulator.CyclePattern =
            let offsets = ResizeArray<int64 * string>()
            for f in 1 .. nFlows do
                let flow = sprintf "F%d" f
                for i in 1 .. stagesPerFlow do
                    offsets.Add(int64 (i - 1) * lagMs,
                                sprintf "%s.N%d" flow i)
            { Offsets = offsets |> List.ofSeq; Jitter = 12L }
        for f in 1 .. nFlows do
            let flow = sprintf "F%d" f
            // real: N1→N2, N2→N3, N3→N4, N4→N5
            for i in 1 .. stagesPerFlow - 1 do
                allArrows.Add(mkArrow flow (sprintf "N%d" i) (sprintf "N%d" (i + 1)) "Start")
            // spurious candidates: N1→N3, N1→N4, N2→N5 (transitive bait)
            spuriousArrows.Add(mkArrow flow "N1" "N3" "Start")
            spuriousArrows.Add(mkArrow flow "N1" "N4" "Start")
            spuriousArrows.Add(mkArrow flow "N2" "N5" "Start")
            for i in 1 .. stagesPerFlow do
                allCalls.Add(sprintf "%s.N%d" flow i)
        {
            Name = sprintf "fT%02d_transitiveBait_n%d" nFlows nFlows
            Flow = "F1"
            GroundTruth = allArrows |> List.ofSeq
            Spurious = spuriousArrows |> List.ofSeq
            AllCalls = allCalls |> List.ofSeq
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allTransitiveBait : Scenario list = [
        for nFlows in 1 .. 20 -> makeTransitiveBait nFlows
    ]

    /// Cycle bait — N1→N2→N3→N1 형태 spurious. algorithm DAG break 정확 동작.
    let private makeCycleBait (nFlows: int) : Scenario =
        let stagesPerFlow = 4
        let lagMs = 150L
        let cycleMs = 3000L
        let allArrows = ResizeArray<VLine.GroundTruthArrow>()
        let spuriousArrows = ResizeArray<VLine.GroundTruthArrow>()
        let allCalls = ResizeArray<string>()
        let pattern (_: Random) : Simulator.CyclePattern =
            let offsets = ResizeArray<int64 * string>()
            for f in 1 .. nFlows do
                let flow = sprintf "F%d" f
                for i in 1 .. stagesPerFlow do
                    offsets.Add(int64 (i - 1) * lagMs,
                                sprintf "%s.N%d" flow i)
            { Offsets = offsets |> List.ofSeq; Jitter = 12L }
        for f in 1 .. nFlows do
            let flow = sprintf "F%d" f
            // real chain: N1→N2→N3→N4
            for i in 1 .. stagesPerFlow - 1 do
                allArrows.Add(mkArrow flow (sprintf "N%d" i) (sprintf "N%d" (i + 1)) "Start")
            // spurious: N4→N1 (cycle back — cross-cycle false detection)
            // algorithm 이 effective_window 차단으로 drop 해야
            spuriousArrows.Add(mkArrow flow "N4" "N1" "Start")
            for i in 1 .. stagesPerFlow do
                allCalls.Add(sprintf "%s.N%d" flow i)
        {
            Name = sprintf "fY%02d_cycleBait_n%d" nFlows nFlows
            Flow = "F1"
            GroundTruth = allArrows |> List.ofSeq
            Spurious = spuriousArrows |> List.ofSeq
            AllCalls = allCalls |> List.ofSeq
            Pattern = pattern
            PatternCycleAware = None
            CycleMs = cycleMs
        }

    let allCycleBait : Scenario list = [
        for nFlows in 1 .. 20 -> makeCycleBait nFlows
    ]

    /// All variants combined.
    /// Round 1+2: simple/async/hetero/spurious/crossFlow/syncBarrier/burst = 140
    /// Round 3 adversarial: confounded/tight/heavyNoise = 60
    /// Round 4 stress: highStage/longChain/ratioStress = 60
    /// Round 5 intra-flow adversarial: transitiveBait/cycleBait = 40
    /// Total: 300 scenarios.
    let allVariants : Scenario list =
        all @ allAsync @ allHetero @ allSpurious
        @ allCrossFlow @ allSyncBarrier @ allBurst
        @ allConfounded @ allTightCycle @ allHeavyNoise
        @ allHighStage @ allLongChain @ allRatioStress
        @ allTransitiveBait @ allCycleBait

    /// 부속: 특정 nFlows 만 추출.
    let withNFlows (nFlows: int) : Scenario =
        makeMultiFlow nFlows 3 200L 3000L 15L

    /// 부속: stages 변동.
    let withStages (nFlows: int) (stages: int) : Scenario =
        makeMultiFlow nFlows stages 200L 3000L 15L

    let stats () =
        [ "F MultiFlow (f01-f20)", List.length all ]
