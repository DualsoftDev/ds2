/// 하이브리드 시나리오 — LogicRungs + 통계 + structural 차원 결합.
///
/// H 차원 (Hybrid):
///   h0~h4: LogicGraph 강도 + 시계열 검증 통합
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

module HybridModels =

    let private scenario name flow gt spurious nodes pattern cycleMs : Scenario =
        { Name = name; Flow = flow
          GroundTruth = gt; Spurious = spurious
          AllCalls = nodes |> List.distinct
          Pattern = pattern; PatternCycleAware = None; CycleMs = cycleMs }

    let private mkArrow flow s t kind : VLine.GroundTruthArrow =
        { Src = $"{flow}.{s}"; Tgt = $"{flow}.{t}"; Kind = kind }

    let private full flow node = $"{flow}.{node}"

    // h0: 강한 AND chain 매 cycle 발화 — 일반 chain.
    let h0 =
        let arrows = [
            mkArrow "F" "A" "B" "Start"
            mkArrow "F" "B" "C" "Start"
        ]
        let nodes = [ full "F" "A"; full "F" "B"; full "F" "C" ]
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "A"; 100L, full "F" "B"; 200L, full "F" "C" ]
              Jitter = 15L }
        scenario "h_x_0_strongChain" "F" arrows [] nodes pattern 2000L

    // h1: 큰 fan-out + group 혼합
    let h1 =
        let arrows = [
            mkArrow "F" "ROOT" "A1" "Start"
            mkArrow "F" "ROOT" "A2" "Start"
            mkArrow "F" "ROOT" "A3" "Start"
            mkArrow "F" "A1" "A2" "Group"
        ]
        let nodes = [ full "F" "ROOT"; full "F" "A1"; full "F" "A2"; full "F" "A3" ]
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "ROOT"
                100L, full "F" "A1"
                100L, full "F" "A2"   // group with A1
                100L, full "F" "A3"
              ]
              Jitter = 10L }
        scenario "h_x_1_fanOutGroup" "F" arrows [] nodes pattern 2000L

    // h2: 두 chain 이 한 sink 으로 join (fan-in 합류)
    let h2 =
        let arrows = [
            mkArrow "F" "A1" "B1" "Start"
            mkArrow "F" "A2" "B2" "Start"
            mkArrow "F" "B1" "SINK" "Start"
            mkArrow "F" "B2" "SINK" "Start"
        ]
        let nodes = [ for n in ["A1"; "B1"; "A2"; "B2"; "SINK"] -> full "F" n ]
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "A1"
                50L, full "F" "A2"
                200L, full "F" "B1"
                250L, full "F" "B2"
                400L, full "F" "SINK"
              ]
              Jitter = 10L }
        scenario "h_x_2_dualJoin" "F" arrows [] nodes pattern 2000L

    // h3: 매우 깊은 chain (15 stages) + jitter
    let h3 =
        let depth = 15
        let stages = [ for i in 1 .. depth -> $"N{i}" ]
        let arrows = [ for i in 0 .. depth - 2 -> mkArrow "F" stages.[i] stages.[i+1] "Start" ]
        let nodes = stages |> List.map (full "F")
        let pattern (_: Random) : Simulator.CyclePattern =
            let offsets = [ for i in 0 .. depth - 1 -> int64 i * 100L, full "F" stages.[i] ]
            { Offsets = offsets; Jitter = 15L }
        scenario "h_x_3_deepChain15" "F" arrows [] nodes pattern 2500L

    // h4: 여러 group cluster + chain 혼합
    let h4 =
        let arrows = [
            mkArrow "F" "S1" "S2" "Start"
            mkArrow "F" "S2" "S3" "Start"
            mkArrow "F" "S3" "S3b" "Group"
            mkArrow "F" "S3" "S4" "Start"
            mkArrow "F" "S4" "S4b" "Group"
            mkArrow "F" "S4" "S5" "Start"
        ]
        let nodes = [ for n in ["S1"; "S2"; "S3"; "S3b"; "S4"; "S4b"; "S5"] -> full "F" n ]
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "S1"
                100L, full "F" "S2"
                200L, full "F" "S3"
                200L, full "F" "S3b"   // group
                300L, full "F" "S4"
                300L, full "F" "S4b"   // group
                400L, full "F" "S5"
              ]
              Jitter = 10L }
        scenario "h_x_4_multiGroupChain" "F" arrows [] nodes pattern 2500L

    let all : Scenario list = [ h0; h1; h2; h3; h4 ]

    let stats () = [ "H Hybrid (h_x)", List.length all ]
