/// C 차원 — 캐파 (capacity, 동시 token 수) 다양 시나리오.
///
/// 캐파 N 의미: N 개 token 이 라인을 pipeline 으로 동시 흐름.
/// 한 사이클 안 같은 변수가 N 번 발화 → token interleaving.
///
/// 도전:
///   캐파 1: 단순 — token 1개씩 통과
///   캐파 2~3: 인접 token 의 같은 station 발화가 자주 → causation lag 측정 안정성
///   캐파 5~10: 매우 빠른 발화 → intra-token vs inter-token 구별 어려움
///
/// 시나리오:
///   c0: 캐파 1, 3-stage chain
///   c1: 캐파 2, 3-stage
///   c2: 캐파 3, 3-stage
///   c3: 캐파 5, 4-stage
///   c4: 캐파 10, 3-stage (stress)
///   c5: 캐파 변동 (라인 시작 1, 중간 3, 끝 1)
///   c6: 캐파 2 + group (parallel A1/A2 → B)
///   c7: 캐파 3 + fan-out
///   c8: 캐파 2, deep pipeline (10 stages)
///   c9: 캐파 5, deep pipeline (8 stages — capacity stress)
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

module CapacityModels =

    let private scenario name flow gt spurious nodes pattern cycleMs : Scenario =
        { Name = name; Flow = flow
          GroundTruth = gt; Spurious = spurious
          AllCalls = nodes |> List.distinct
          Pattern = pattern; PatternCycleAware = None; CycleMs = cycleMs }

    let private mkArrow flow s t kind : VLine.GroundTruthArrow =
        { Src = $"{flow}.{s}"; Tgt = $"{flow}.{t}"; Kind = kind }

    let private full flow node = $"{flow}.{node}"

    /// Pipeline 시뮬 — N token 이 stages 통과.
    /// 각 stage 의 lag = stageLag.
    /// Token 간 진입 간격 = tokenInterval (= cycleMs / capacity 라야 정상 흐름).
    let private pipelineEvents (flow: string) (stages: string list) (capacity: int)
                              (tokenInterval: int64) (stageLag: int64)
                              : (int64 * string) list =
        [ for tokenIdx in 0 .. capacity - 1 do
            let tokenStart = int64 tokenIdx * tokenInterval
            for stageIdx in 0 .. List.length stages - 1 do
                let t = tokenStart + int64 stageIdx * stageLag
                yield t, full flow stages.[stageIdx] ]

    /// Chain ground truth — stages 순차.
    let private chainArrows (flow: string) (stages: string list) : VLine.GroundTruthArrow list =
        [ for i in 0 .. List.length stages - 2 ->
            mkArrow flow stages.[i] stages.[i + 1] "Start" ]

    let private mkPattern (offsets: (int64 * string) list) : Random -> Simulator.CyclePattern =
        fun _ -> { Offsets = offsets; Jitter = 10L }

    // ════════════════════════════════════════════════════════════════════
    // c0 ~ c9: 캐파 변화
    // ════════════════════════════════════════════════════════════════════
    let all : Scenario list = [
        // c0: 캐파 1 — 단순 chain. 한 token 만 라인에 있음.
        let stages = [ "S1"; "S2"; "S3" ]
        let arrows = chainArrows "F" stages
        let nodes = stages |> List.map (full "F")
        let offsets = pipelineEvents "F" stages 1 5000L 200L
        yield scenario "c0_cap1" "F" arrows [] nodes (mkPattern offsets) 5000L

        // c1: 캐파 2 — 두 token interleaved.
        let stages1 = [ "S1"; "S2"; "S3" ]
        let arrows1 = chainArrows "F" stages1
        let nodes1 = stages1 |> List.map (full "F")
        // capacity 2, cycle 2000, tokenInterval 1000 → 한 사이클에 2 token, 각 변수 2번 발화.
        let offsets1 = pipelineEvents "F" stages1 2 1000L 200L
        yield scenario "c1_cap2" "F" arrows1 [] nodes1 (mkPattern offsets1) 2000L

        // c2: 캐파 3 — 3 token interleaved.
        let stages2 = [ "S1"; "S2"; "S3" ]
        let arrows2 = chainArrows "F" stages2
        let nodes2 = stages2 |> List.map (full "F")
        // capacity 3, cycle 3000, tokenInterval 1000 → 3 token per cycle.
        let offsets2 = pipelineEvents "F" stages2 3 1000L 200L
        yield scenario "c2_cap3" "F" arrows2 [] nodes2 (mkPattern offsets2) 3000L

        // c3: 캐파 5, 4-stage.
        let stages3 = [ "S1"; "S2"; "S3"; "S4" ]
        let arrows3 = chainArrows "F" stages3
        let nodes3 = stages3 |> List.map (full "F")
        let offsets3 = pipelineEvents "F" stages3 5 1000L 200L
        yield scenario "c3_cap5_4stage" "F" arrows3 [] nodes3 (mkPattern offsets3) 5500L

        // c4: 캐파 10 (stress) — 매우 짧은 token interval.
        let stages4 = [ "S1"; "S2"; "S3" ]
        let arrows4 = chainArrows "F" stages4
        let nodes4 = stages4 |> List.map (full "F")
        // capacity 10, cycle 5000, tokenInterval 500 → 10 token per cycle.
        let offsets4 = pipelineEvents "F" stages4 10 500L 200L
        yield scenario "c4_cap10_stress" "F" arrows4 [] nodes4 (mkPattern offsets4) 6000L

        // c5: 캐파 변동 — 라인 첫 station 1, 중간 3, 끝 1.
        // 모델 단순화: pipeline 같지만 token interval 변화 표현 → 균등 token 진입은 동일 lag.
        let stages5 = [ "S1"; "S2"; "S3"; "S4"; "S5" ]
        let arrows5 = chainArrows "F" stages5
        let nodes5 = stages5 |> List.map (full "F")
        // capacity 3, 5-stage chain.
        let offsets5 = pipelineEvents "F" stages5 3 800L 200L
        yield scenario "c5_capVar_5stage" "F" arrows5 [] nodes5 (mkPattern offsets5) 4000L

        // c6: 캐파 2 + group (parallel A1/A2 ↔ — 같은 stage 의 두 sub-station).
        // S1A 와 S1B 가 동시 group, S2 가 다음 stage.
        let arrows6 = [
            mkArrow "F" "S1A" "S1B" "Group"
            mkArrow "F" "S1A" "S2" "Start"
            mkArrow "F" "S2" "S3" "Start"
        ]
        let nodes6 = [ for n in ["S1A"; "S1B"; "S2"; "S3"] -> full "F" n ]
        let offsets6 =
            [ for t in 0 .. 1 do
                let s = int64 t * 1000L
                yield s, full "F" "S1A"
                yield s, full "F" "S1B"
                yield s + 200L, full "F" "S2"
                yield s + 400L, full "F" "S3" ]
        yield scenario "c6_cap2_group" "F" arrows6 [] nodes6 (mkPattern offsets6) 2000L

        // c7: 캐파 3 + fan-out (S1 → S2A, S1 → S2B).
        let arrows7 = [
            mkArrow "F" "S1" "S2A" "Start"
            mkArrow "F" "S1" "S2B" "Start"
        ]
        let nodes7 = [ for n in ["S1"; "S2A"; "S2B"] -> full "F" n ]
        let offsets7 =
            [ for t in 0 .. 2 do
                let s = int64 t * 1000L
                yield s, full "F" "S1"
                yield s + 200L, full "F" "S2A"
                yield s + 200L, full "F" "S2B" ]
        yield scenario "c7_cap3_fanOut" "F" arrows7 [] nodes7 (mkPattern offsets7) 3000L

        // c8: 캐파 2, deep pipeline (10 stages).
        let stages8 = [ for i in 1 .. 10 -> $"S{i}" ]
        let arrows8 = chainArrows "F" stages8
        let nodes8 = stages8 |> List.map (full "F")
        let offsets8 = pipelineEvents "F" stages8 2 2000L 150L
        yield scenario "c8_cap2_deep10" "F" arrows8 [] nodes8 (mkPattern offsets8) 4000L

        // c9: 캐파 5, 8-stage (capacity stress).
        let stages9 = [ for i in 1 .. 8 -> $"S{i}" ]
        let arrows9 = chainArrows "F" stages9
        let nodes9 = stages9 |> List.map (full "F")
        let offsets9 = pipelineEvents "F" stages9 5 800L 150L
        yield scenario "c9_cap5_deep8" "F" arrows9 [] nodes9 (mkPattern offsets9) 5500L

        // ────────── c10 ~ c19: 더 까다로운 캐파 케이스 ──────────

        // c10: 캐파 2 + token interval jitter — 진입 간격이 매 사이클 ±200ms 변동.
        let stages10 = [ "S1"; "S2"; "S3" ]
        let arrows10 = chainArrows "F" stages10
        let nodes10 = stages10 |> List.map (full "F")
        let pattern10 (rng: Random) : Simulator.CyclePattern =
            let interval = 1000L + int64 (rng.Next(-200, 201))
            let offs = [ for t in 0 .. 1 do
                            let s = int64 t * interval
                            for i in 0 .. 2 do
                                yield s + int64 i * 200L, full "F" stages10.[i] ]
            { Offsets = offs; Jitter = 15L }
        yield scenario "c10_cap2_tokenJitter" "F" arrows10 [] nodes10 pattern10 2500L

        // c11: 캐파 oscillation — 사이클별 1~3 token 무작위.
        let stages11 = [ "S1"; "S2"; "S3" ]
        let arrows11 = chainArrows "F" stages11
        let nodes11 = stages11 |> List.map (full "F")
        let pattern11 (rng: Random) : Simulator.CyclePattern =
            let n = rng.Next(1, 4)
            let offs = [ for t in 0 .. n - 1 do
                            let s = int64 t * 800L
                            for i in 0 .. 2 do
                                yield s + int64 i * 200L, full "F" stages11.[i] ]
            { Offsets = offs; Jitter = 15L }
        yield scenario "c11_capOscillation" "F" arrows11 [] nodes11 pattern11 3000L

        // c12: bottleneck — S2 에서 latency 큼 (token queue 형성).
        // S1→S2 의 lag 가 큰 jitter (앞 token 처리 중 뒤 token 대기).
        let stages12 = [ "S1"; "S2"; "S3" ]
        let arrows12 = chainArrows "F" stages12
        let nodes12 = stages12 |> List.map (full "F")
        let pattern12 (rng: Random) : Simulator.CyclePattern =
            // 2 token, S2 는 queue 로 대기
            let s1a = 0L
            let s1b = 500L
            let s2a = 200L                          // 첫 token 정상 처리
            let s2b = s2a + 800L + int64 (rng.Next(-50, 51))  // 두 번째는 대기 후
            let s3a = s2a + 300L
            let s3b = s2b + 300L
            { Offsets = [
                s1a, full "F" "S1"; s1b, full "F" "S1"
                s2a, full "F" "S2"; s2b, full "F" "S2"
                s3a, full "F" "S3"; s3b, full "F" "S3"
              ]; Jitter = 15L }
        yield scenario "c12_bottleneck" "F" arrows12 [] nodes12 pattern12 2500L

        // c13: 매우 큰 캐파 (20) — 라인이 token 으로 가득 참.
        let stages13 = [ "S1"; "S2"; "S3" ]
        let arrows13 = chainArrows "F" stages13
        let nodes13 = stages13 |> List.map (full "F")
        let offsets13 = pipelineEvents "F" stages13 20 250L 200L
        yield scenario "c13_cap20_full" "F" arrows13 [] nodes13 (mkPattern offsets13) 6000L

        // c14: 캐파 5 + 모든 stage jitter — 실제 라인 동작 모방.
        let stages14 = [ "S1"; "S2"; "S3"; "S4" ]
        let arrows14 = chainArrows "F" stages14
        let nodes14 = stages14 |> List.map (full "F")
        let pattern14 (rng: Random) : Simulator.CyclePattern =
            let offs = [
                for t in 0 .. 4 do
                    let s = int64 t * 800L + int64 (rng.Next(-30, 31))
                    for i in 0 .. 3 do
                        yield s + int64 i * 200L + int64 (rng.Next(-20, 21)),
                              full "F" stages14.[i] ]
            { Offsets = offs; Jitter = 15L }
        yield scenario "c14_cap5_allJitter" "F" arrows14 [] nodes14 pattern14 5500L
    ]

    let stats () =
        [ "C Capacity (c0-c9)", List.length all ]
