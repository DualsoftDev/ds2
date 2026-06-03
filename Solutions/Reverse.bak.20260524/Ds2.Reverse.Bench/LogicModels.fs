/// S 차원 — Symbolic Logic (AND/OR/재귀) 시나리오.
///
/// PLC 래더 로직을 LogicRung 으로 표현 → 재귀 expand + 강도 계산.
/// 강도 ≥ threshold 인 input 만 candidate 로 채택.
///
/// 시나리오:
///   s0: 단순 AND (A AND B → C)
///   s1: 단순 OR (A OR B → C)
///   s2: AND of OR ((A AND B) OR (C AND D) → E)
///   s3: 2-level 재귀 (B = A AND X, C = B AND Y → C 가 A, X, Y 의존)
///   s4: 3-level 재귀
///   s5: AND-OR 혼합 4-level
///   s6: NOT 포함
///   s7: 5-level 깊은 chain
///   s8: 약한 strength input drop 검증
///   s9: 복합 — AND 강한 + OR 분기 + 재귀
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

module LogicModels =

    let private scenario name flow gt spurious nodes pattern cycleMs : Scenario =
        { Name = name; Flow = flow
          GroundTruth = gt; Spurious = spurious
          AllCalls = nodes |> List.distinct
          Pattern = pattern; PatternCycleAware = None; CycleMs = cycleMs }

    let private mkArrow flow s t kind : VLine.GroundTruthArrow =
        { Src = $"{flow}.{s}"; Tgt = $"{flow}.{t}"; Kind = kind }

    let private full flow node = $"{flow}.{node}"

    /// 한 event pattern (offset, name) list 의 events 를 한 사이클 안에 발화.
    let private mkPattern (offsets: (int64 * string) list) : Random -> Simulator.CyclePattern =
        fun _ -> { Offsets = offsets; Jitter = 15L }

    /// LogicRung 추가 — Scenario 에 LogicRungs 메타데이터 옵션이 필요하지만,
    /// Scenario 타입 변경 없이 ReverseEngine 에 직접 전달하기 위해
    /// LogicRungs 를 시나리오 정의 시점에 함께 정의 후 별도 dict 으로 보관.
    let logicRungsBySc = System.Collections.Generic.Dictionary<string, LogicRung list>()

    let private addLogic (sc: Scenario) (rungs: LogicRung list) =
        logicRungsBySc.[sc.Name] <- rungs
        sc

    /// s0: A AND B → C 단순 AND. 모두 발화해야 C 발화.
    let s0 =
        let arrows = [ mkArrow "F" "A" "C" "Start"; mkArrow "F" "B" "C" "Start" ]
        let nodes = [ full "F" "A"; full "F" "B"; full "F" "C" ]
        let pattern = mkPattern [ 0L, full "F" "A"; 50L, full "F" "B"; 200L, full "F" "C" ]
        let sc = scenario "s0_simpleAnd" "F" arrows [] nodes pattern 2000L
        let rungs = [ { Output = "C"; Expr = LAnd [ LVar "A"; LVar "B" ] } ]
        addLogic sc rungs

    /// s1: A OR B → C. 각각 1/2 strength.
    /// strength 0.5 >= 0.3 → 둘 다 candidate. 둘 다 통계로 검증되면 채택.
    let s1 =
        let arrows = [ mkArrow "F" "A" "C" "Start"; mkArrow "F" "B" "C" "Start" ]
        let nodes = [ full "F" "A"; full "F" "B"; full "F" "C" ]
        // 매 사이클 A, B 둘 다 발화 → 둘 다 인과로 검증됨
        let pattern = mkPattern [
            0L, full "F" "A"; 30L, full "F" "B"; 200L, full "F" "C" ]
        let sc = scenario "s1_simpleOr" "F" arrows [] nodes pattern 2000L
        let rungs = [ { Output = "C"; Expr = LOr [ LVar "A"; LVar "B" ] } ]
        addLogic sc rungs

    /// s2: (A AND B) OR (C AND D) → E. 4 inputs 모두 strength 0.5 (OR 2 branch).
    let s2 =
        let arrows = [
            mkArrow "F" "A" "E" "Start"
            mkArrow "F" "B" "E" "Start"
            mkArrow "F" "C" "E" "Start"
            mkArrow "F" "D" "E" "Start"
        ]
        let nodes = [ full "F" "A"; full "F" "B"; full "F" "C"; full "F" "D"; full "F" "E" ]
        let pattern = mkPattern [
            0L, full "F" "A"; 20L, full "F" "B"
            40L, full "F" "C"; 60L, full "F" "D"
            200L, full "F" "E" ]
        let sc = scenario "s2_andOfOr" "F" arrows [] nodes pattern 2000L
        let rungs = [
            { Output = "E"; Expr = LOr [
                LAnd [ LVar "A"; LVar "B" ]
                LAnd [ LVar "C"; LVar "D" ] ] }
        ]
        addLogic sc rungs

    /// s3: 2-level 재귀.
    /// B = A AND X (rung 1)
    /// C = B AND Y (rung 2)
    /// → C 의 inputs: A, X, Y 모두 strength 1.0 (모두 AND chain)
    /// 단, B 도 노드이므로 B → C 직접 candidate 도 있음 (logic 에 명시됨).
    /// 알고리즘이 재귀 expand 하면 A → C, X → C, Y → C 도 candidate.
    let s3 =
        // 정답: A → B, X → B, B → C, Y → C
        // 재귀 expand 의 결과 A → C, X → C, Y → C 가 candidate 되지만
        // transitive reduction 으로 제거되어야 (A → B → C 경로 있음).
        let arrows = [
            mkArrow "F" "A" "B" "Start"
            mkArrow "F" "X" "B" "Start"
            mkArrow "F" "B" "C" "Start"
            mkArrow "F" "Y" "C" "Start"
        ]
        let nodes = [ full "F" "A"; full "F" "X"; full "F" "B"; full "F" "Y"; full "F" "C" ]
        let pattern = mkPattern [
            0L, full "F" "A"; 20L, full "F" "X"; 100L, full "F" "B"
            120L, full "F" "Y"; 250L, full "F" "C" ]
        let sc = scenario "s3_recursive2Lvl" "F" arrows [] nodes pattern 2000L
        let rungs = [
            { Output = "B"; Expr = LAnd [ LVar "A"; LVar "X" ] }
            { Output = "C"; Expr = LAnd [ LVar "B"; LVar "Y" ] }
        ]
        addLogic sc rungs

    /// s4: 3-level 재귀. M = A AND B, N = M AND C, O = N AND D
    /// → O 의 모든 inputs (A, B, C, D, M, N) candidate. transitive 로 다 정리.
    let s4 =
        let arrows = [
            mkArrow "F" "A" "M" "Start"
            mkArrow "F" "B" "M" "Start"
            mkArrow "F" "M" "N" "Start"
            mkArrow "F" "C" "N" "Start"
            mkArrow "F" "N" "O" "Start"
            mkArrow "F" "D" "O" "Start"
        ]
        let nodes = [ for n in ["A"; "B"; "M"; "C"; "N"; "D"; "O"] -> full "F" n ]
        let pattern = mkPattern [
            0L, full "F" "A"; 20L, full "F" "B"; 100L, full "F" "M"
            120L, full "F" "C"; 200L, full "F" "N"; 220L, full "F" "D"
            300L, full "F" "O" ]
        let sc = scenario "s4_recursive3Lvl" "F" arrows [] nodes pattern 2000L
        let rungs = [
            { Output = "M"; Expr = LAnd [ LVar "A"; LVar "B" ] }
            { Output = "N"; Expr = LAnd [ LVar "M"; LVar "C" ] }
            { Output = "O"; Expr = LAnd [ LVar "N"; LVar "D" ] }
        ]
        addLogic sc rungs

    /// s5: AND-OR 혼합 4-level. K = (A AND B) OR C, L = K AND D
    /// L 의 inputs: A(0.5), B(0.5), C(0.5), D(1.0), K(1.0)
    let s5 =
        let arrows = [
            mkArrow "F" "A" "K" "Start"
            mkArrow "F" "B" "K" "Start"
            mkArrow "F" "C" "K" "Start"
            mkArrow "F" "K" "L" "Start"
            mkArrow "F" "D" "L" "Start"
        ]
        let nodes = [ for n in ["A"; "B"; "C"; "K"; "D"; "L"] -> full "F" n ]
        let pattern = mkPattern [
            0L, full "F" "A"; 20L, full "F" "B"; 40L, full "F" "C"
            150L, full "F" "K"; 170L, full "F" "D"; 300L, full "F" "L" ]
        let sc = scenario "s5_andOrMixed" "F" arrows [] nodes pattern 2000L
        let rungs = [
            { Output = "K"; Expr = LOr [
                LAnd [ LVar "A"; LVar "B" ]
                LVar "C" ] }
            { Output = "L"; Expr = LAnd [ LVar "K"; LVar "D" ] }
        ]
        addLogic sc rungs

    /// s6: NOT 포함. P = A AND NOT B → A 와 B 모두 candidate (NOT 은 강도 영향 없음).
    let s6 =
        let arrows = [
            mkArrow "F" "A" "P" "Start"
            mkArrow "F" "B" "P" "Start"
        ]
        let nodes = [ full "F" "A"; full "F" "B"; full "F" "P" ]
        let pattern = mkPattern [
            0L, full "F" "A"; 50L, full "F" "B"; 200L, full "F" "P" ]
        let sc = scenario "s6_notIncluded" "F" arrows [] nodes pattern 2000L
        let rungs = [
            { Output = "P"; Expr = LAnd [ LVar "A"; LNot (LVar "B") ] }
        ]
        addLogic sc rungs

    /// s7: 5-level 깊은 chain. R1 = A AND B, R2 = R1 AND C, ..., R5 = R4 AND F.
    /// 모든 input 이 R5 의 candidate (강도 1.0).
    let s7 =
        let arrows = [
            mkArrow "F" "A" "R1" "Start";  mkArrow "F" "B" "R1" "Start"
            mkArrow "F" "R1" "R2" "Start"; mkArrow "F" "C" "R2" "Start"
            mkArrow "F" "R2" "R3" "Start"; mkArrow "F" "D" "R3" "Start"
            mkArrow "F" "R3" "R4" "Start"; mkArrow "F" "E" "R4" "Start"
            mkArrow "F" "R4" "R5" "Start"; mkArrow "F" "G" "R5" "Start"
        ]
        let nodes = [ for n in ["A"; "B"; "R1"; "C"; "R2"; "D"; "R3"; "E"; "R4"; "G"; "R5"] -> full "F" n ]
        let pattern = mkPattern [
            0L, full "F" "A"; 20L, full "F" "B"; 80L, full "F" "R1"
            100L, full "F" "C"; 160L, full "F" "R2"
            180L, full "F" "D"; 240L, full "F" "R3"
            260L, full "F" "E"; 320L, full "F" "R4"
            340L, full "F" "G"; 400L, full "F" "R5"
        ]
        let sc = scenario "s7_deep5Lvl" "F" arrows [] nodes pattern 2500L
        let rungs = [
            { Output = "R1"; Expr = LAnd [ LVar "A"; LVar "B" ] }
            { Output = "R2"; Expr = LAnd [ LVar "R1"; LVar "C" ] }
            { Output = "R3"; Expr = LAnd [ LVar "R2"; LVar "D" ] }
            { Output = "R4"; Expr = LAnd [ LVar "R3"; LVar "E" ] }
            { Output = "R5"; Expr = LAnd [ LVar "R4"; LVar "G" ] }
        ]
        addLogic sc rungs

    /// s8: 약한 strength input drop 검증.
    /// Q = A OR B OR C OR D OR E → 5-branch OR, 각 strength 0.2 < threshold 0.3 → 모두 drop?
    /// → Q 후보 0 개. ground truth 빈.
    let s8 =
        let nodes = [ for n in ["A"; "B"; "C"; "D"; "E"; "Q"] -> full "F" n ]
        // Q 가 실제로는 매 cycle 한 input 으로만 트리거 — 진짜 인과는 weak (각 입력 20% suff)
        let pattern (rng: Random) : Simulator.CyclePattern =
            let pickIdx = rng.Next(0, 5)
            let trigger = [| "A"; "B"; "C"; "D"; "E" |].[pickIdx]
            { Offsets = [ 0L, full "F" trigger; 200L, full "F" "Q" ]; Jitter = 15L }
        let sc = scenario "s8_weakOrDrop" "F" [] [] nodes pattern 2000L
        let rungs = [
            { Output = "Q"; Expr = LOr [ LVar "A"; LVar "B"; LVar "C"; LVar "D"; LVar "E" ] }
        ]
        addLogic sc rungs

    /// s9: 복합 — AND 강한 + OR 분기 + 재귀.
    /// V = A AND B (강한)
    /// W = V OR (C AND D)  → V 0.5, C 0.5, D 0.5 (AND child)
    /// X = W AND E
    /// X 의 inputs 합산: A(0.5*1=0.5? 재귀 expand 후), B, C, D, E, V, W
    let s9 =
        let arrows = [
            mkArrow "F" "A" "V" "Start"; mkArrow "F" "B" "V" "Start"
            mkArrow "F" "V" "W" "Start"
            mkArrow "F" "C" "W" "Start"; mkArrow "F" "D" "W" "Start"
            mkArrow "F" "W" "X" "Start"; mkArrow "F" "E" "X" "Start"
        ]
        let nodes = [ for n in ["A"; "B"; "V"; "C"; "D"; "W"; "E"; "X"] -> full "F" n ]
        let pattern = mkPattern [
            0L, full "F" "A"; 20L, full "F" "B"; 100L, full "F" "V"
            120L, full "F" "C"; 140L, full "F" "D"; 220L, full "F" "W"
            240L, full "F" "E"; 320L, full "F" "X"
        ]
        let sc = scenario "s9_composite" "F" arrows [] nodes pattern 2500L
        let rungs = [
            { Output = "V"; Expr = LAnd [ LVar "A"; LVar "B" ] }
            { Output = "W"; Expr = LOr [
                LVar "V"
                LAnd [ LVar "C"; LVar "D" ] ] }
            { Output = "X"; Expr = LAnd [ LVar "W"; LVar "E" ] }
        ]
        addLogic sc rungs

    let all : Scenario list = [
        s0; s1; s2; s3; s4; s5; s6; s7; s8; s9
    ]

    let stats () =
        [ "S Symbolic Logic (s0-s9)", List.length all ]
