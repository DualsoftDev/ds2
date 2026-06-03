/// K 차원 — Multi-source cluster causation 시나리오.
///
/// 한 sink (B) 가 여러 source 로부터 트리거 되는 경우:
/// 각 source 의 cluster (가장 가까운 preceding source 가 자기 자신인 B 들) 단위로 인과 평가.
///
/// k0: A1 50% + A2 50% race (모두 B 트리거) — 둘 다 cluster size ~50%
/// k1: A1 70% + A2 30% — 둘 다 인정 (cluster 가 충분 크면)
/// k2: A1 always + A2 occasionally (외부 trigger) — A1 만 인정, A2 spurious
/// k3: 3 sources A1/A2/A3 → B (각 33%)
/// k4: A1 fires + B fires once per cycle, A2 fires occasionally without B follow → A2 spurious
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

module ClusterModels =

    let private scenario name flow gt spurious nodes pattern cycleMs : Scenario =
        { Name = name; Flow = flow
          GroundTruth = gt; Spurious = spurious
          AllCalls = nodes |> List.distinct
          Pattern = pattern; PatternCycleAware = None; CycleMs = cycleMs }

    let private mkArrow flow s t kind : VLine.GroundTruthArrow =
        { Src = $"{flow}.{s}"; Tgt = $"{flow}.{t}"; Kind = kind }

    let private full flow node = $"{flow}.{node}"

    // k0: A1 / A2 race - 매 cycle 한 src 만 fires + 해당 B fires.
    let k0 =
        let arrows = [
            mkArrow "F" "A1" "B" "Start"
            mkArrow "F" "A2" "B" "Start"
        ]
        let nodes = [ full "F" "A1"; full "F" "A2"; full "F" "B" ]
        let pattern (rng: Random) : Simulator.CyclePattern =
            let pickA1 = rng.Next(0, 2) = 0
            let src = if pickA1 then "A1" else "A2"
            { Offsets = [ 0L, full "F" src; 100L, full "F" "B" ]; Jitter = 15L }
        scenario "k0_50_50_race" "F" arrows [] nodes pattern 2000L

    // k1: A1 70%, A2 30% — 양쪽 cluster 모두 충분 크면 인정
    let k1 =
        let arrows = [
            mkArrow "F" "A1" "B" "Start"
            mkArrow "F" "A2" "B" "Start"
        ]
        let nodes = [ full "F" "A1"; full "F" "A2"; full "F" "B" ]
        let pattern (rng: Random) : Simulator.CyclePattern =
            let useA1 = rng.Next(0, 10) < 7
            let src = if useA1 then "A1" else "A2"
            { Offsets = [ 0L, full "F" src; 100L, full "F" "B" ]; Jitter = 15L }
        scenario "k1_70_30_split" "F" arrows [] nodes pattern 2000L

    // k2: A1 매 cycle + B 매 cycle. A2 가끔 random timing 발화 (spurious — B 와 timing 무관).
    // 알고리즘: A1 cluster=100%, A2 cluster size 작음 OR lag std 큼 → drop ✓
    let k2 =
        let arrows = [ mkArrow "F" "A1" "B" "Start" ]
        let spurious = [ mkArrow "F" "A2" "B" "Start" ]
        let nodes = [ full "F" "A1"; full "F" "A2"; full "F" "B" ]
        let pattern (rng: Random) : Simulator.CyclePattern =
            let withA2 = rng.Next(0, 3) = 0
            let base_ = [ 0L, full "F" "A1"; 100L, full "F" "B" ]
            // A2 가 random cycle 위치 — B 와 timing 무관
            let a2Ev =
                if withA2 then [ int64 (rng.Next(200, 1800)), full "F" "A2" ]
                else []
            { Offsets = base_ @ a2Ev; Jitter = 15L }
        scenario "k2_a1_main_a2_random" "F" arrows spurious nodes pattern 2000L

    // k3: 3-way race — A1, A2, A3 (각 ~33%) → B
    let k3 =
        let arrows = [
            mkArrow "F" "A1" "B" "Start"
            mkArrow "F" "A2" "B" "Start"
            mkArrow "F" "A3" "B" "Start"
        ]
        let nodes = [ full "F" "A1"; full "F" "A2"; full "F" "A3"; full "F" "B" ]
        let pattern (rng: Random) : Simulator.CyclePattern =
            let pick = rng.Next(0, 3)
            let src = [| "A1"; "A2"; "A3" |].[pick]
            { Offsets = [ 0L, full "F" src; 100L, full "F" "B" ]; Jitter = 15L }
        scenario "k3_threeWayRace" "F" arrows [] nodes pattern 2000L

    // k4: A1 always + B always, A2 always + 다른 시점에 (A2 → B 무관). A1 cluster=100%, A2 cluster=0.
    let k4 =
        let arrows = [ mkArrow "F" "A1" "B" "Start" ]
        let spurious = [ mkArrow "F" "A2" "B" "Start" ]
        let nodes = [ full "F" "A1"; full "F" "A2"; full "F" "B" ]
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "A1"; 100L, full "F" "B"
                500L, full "F" "A2"   // A2 가 B 후. 인과 X (B 직전 src 가 A1)
              ]; Jitter = 15L }
        scenario "k4_a2_after_b" "F" arrows spurious nodes pattern 2000L

    let all : Scenario list = [ k0; k1; k2; k3; k4 ]

    let stats () = [ "K Cluster (k0~k4)", List.length all ]
