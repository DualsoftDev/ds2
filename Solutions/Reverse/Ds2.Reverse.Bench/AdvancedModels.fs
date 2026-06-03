/// 고급 차원 시나리오 — Mutex / Idle / Latched / Cascading.
///
/// M 차원 (Mutex) — 두 work 가 mutex (한 번에 하나만):
///   m0~m2: 3 가지 mutex 패턴
///
/// I 차원 (Idle period) — 가끔 라인 일시 정지 (긴 빈 시간):
///   i0~i2: idle gap 다양
///
/// X 차원 (Cascading) — 한 cause 가 deep cascade 전파:
///   x0~x2: 5/8/12-level cascade
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

module AdvancedModels =

    let private scenario name flow gt spurious nodes pattern cycleMs : Scenario =
        { Name = name; Flow = flow
          GroundTruth = gt; Spurious = spurious
          AllCalls = nodes |> List.distinct
          Pattern = pattern; PatternCycleAware = None; CycleMs = cycleMs }

    let private mkArrow flow s t kind : VLine.GroundTruthArrow =
        { Src = $"{flow}.{s}"; Tgt = $"{flow}.{t}"; Kind = kind }

    let private full flow node = $"{flow}.{node}"

    /// M 차원 — Mutex 패턴 (서로 배타적 발화).
    let private mutexModels : Scenario list = [
        // m0: A 와 B 가 서로 배타. cycle 마다 둘 중 하나만 발화. 인과 분리.
        let arrows0 = []   // 진짜 인과 없음
        let spurious0 = [
            mkArrow "F" "A" "B" "Start"
            mkArrow "F" "B" "A" "Start"
        ]
        let nodes0 = [ full "F" "A"; full "F" "B" ]
        let pattern0 (rng: Random) : Simulator.CyclePattern =
            let pickA = rng.Next(0, 2) = 0
            let n = if pickA then "A" else "B"
            { Offsets = [ 0L, full "F" n ]; Jitter = 20L }
        yield scenario "m_x_0_mutexNoCause" "F" arrows0 spurious0 nodes0 pattern0 2000L

        // m1: trigger → A or B (mutex). A trigger → A, B trigger → B.
        let arrows1 = [
            mkArrow "F" "TA" "A" "Start"
            mkArrow "F" "TB" "B" "Start"
        ]
        let nodes1 = [ full "F" "TA"; full "F" "TB"; full "F" "A"; full "F" "B" ]
        let pattern1 (rng: Random) : Simulator.CyclePattern =
            let pickA = rng.Next(0, 2) = 0
            let tName = if pickA then "TA" else "TB"
            let aName = if pickA then "A" else "B"
            { Offsets = [ 0L, full "F" tName; 200L, full "F" aName ]; Jitter = 15L }
        yield scenario "m_x_1_mutexBranch" "F" arrows1 [] nodes1 pattern1 2000L

        // m2: 단순 mutex 변형 — 시간 분리. cycle 전반 A, 후반 B.
        let nodes2 = [ full "F" "A"; full "F" "B" ]
        let pattern2 (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "A"; 1500L, full "F" "B" ]; Jitter = 20L }
        // A 와 B 사이 lag 1500ms ± 20 stable → 진짜 인과처럼 보임. truth 로 인정.
        yield scenario "m_x_2_seqMutex" "F"
            [ mkArrow "F" "A" "B" "Start" ] []
            nodes2 pattern2 3000L
    ]

    /// I 차원 — Idle period.
    let private idleModels : Scenario list = [
        // i0: 매 5 사이클마다 1 사이클 idle (전체 events 없음).
        let arrows0 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes0 = [ full "F" "A"; full "F" "B" ]
        let mutable cycleIdx0 = 0
        let pattern0 (_: Random) : Simulator.CyclePattern =
            let isIdle = cycleIdx0 % 5 = 4
            cycleIdx0 <- cycleIdx0 + 1
            if isIdle then { Offsets = []; Jitter = 20L }
            else { Offsets = [ 0L, full "F" "A"; 200L, full "F" "B" ]; Jitter = 20L }
        yield scenario "i_x_0_idle5pct" "F" arrows0 [] nodes0 pattern0 2000L

        // i1: 절반의 사이클은 idle (50% 빈)
        let mutable cycleIdx1 = 0
        let pattern1 (_: Random) : Simulator.CyclePattern =
            let isIdle = cycleIdx1 % 2 = 0
            cycleIdx1 <- cycleIdx1 + 1
            if isIdle then { Offsets = []; Jitter = 20L }
            else { Offsets = [ 0L, full "F" "A"; 200L, full "F" "B" ]; Jitter = 20L }
        yield scenario "i_x_1_idle50pct" "F"
            [ mkArrow "F" "A" "B" "Start" ] []
            [ full "F" "A"; full "F" "B" ]
            pattern1 2000L

        // i2: 라인 startup 후 일정 cycle 이후 idle 영역 (긴 정지)
        // simulation: cycles 0~30 active, 31~50 idle, 51~ active.
        let mutable cycleIdx2 = 0
        let pattern2 (_: Random) : Simulator.CyclePattern =
            let idx = cycleIdx2
            cycleIdx2 <- cycleIdx2 + 1
            let isIdle = idx >= 30 && idx < 50
            if isIdle then { Offsets = []; Jitter = 20L }
            else { Offsets = [ 0L, full "F" "A"; 200L, full "F" "B" ]; Jitter = 20L }
        yield scenario "i_x_2_idleBlock" "F"
            [ mkArrow "F" "A" "B" "Start" ] []
            [ full "F" "A"; full "F" "B" ]
            pattern2 2000L
    ]

    /// X 차원 — Deep cascade (8 level chain in 1 cycle).
    let private cascadeModels : Scenario list = [
        for depth in [5; 8; 12] ->
            let stages = [ for i in 1 .. depth -> $"S{i}" ]
            let arrows =
                [ for i in 0 .. depth - 2 -> mkArrow "F" stages.[i] stages.[i+1] "Start" ]
            let nodes = stages |> List.map (full "F")
            let pattern (_: Random) : Simulator.CyclePattern =
                let offsets =
                    [ for i in 0 .. depth - 1 ->
                        int64 i * 100L, full "F" stages.[i] ]
                { Offsets = offsets; Jitter = 10L }
            scenario $"x_x_{depth}_cascade" "F" arrows [] nodes pattern (int64 depth * 200L + 1000L)
    ]

    let all : Scenario list = mutexModels @ idleModels @ cascadeModels

    let stats () =
        [ "M Mutex (m_x)", List.length mutexModels
          "I Idle (i_x)", List.length idleModels
          "X Cascade (x_x)", List.length cascadeModels ]
