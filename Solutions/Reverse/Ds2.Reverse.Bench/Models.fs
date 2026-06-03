/// m0 ~ m100 — 알고리즘 정합성 회귀 테스트용 합성 모델 모음.
///
/// 카테고리:
///   m0  ~ m9   기본 체인 (길이 변화)
///   m10 ~ m19  Fan-out
///   m20 ~ m29  Fan-in
///   m30 ~ m39  Group + 시퀀스 혼합
///   m40 ~ m49  Confounded (jitter level 변화)
///   m50 ~ m59  Spurious 노이즈 변화
///   m60 ~ m69  Long chain + multiple group
///   m70 ~ m79  Cycle 후보 (DAG enforcement 검증)
///   m80 ~ m89  엣지 — 짧은 lag, 긴 lag, 큰 sample
///   m90 ~ m100 복합 시나리오 (대규모)
namespace Ds2.Reverse.Bench

open System

module Models =

    open Primitives

    /// Helper — 한 사이클의 (offset, name) 리스트 → CyclePattern (jitter 30ms 기본).
    let private mkPattern (offsets: (int64 * string) list) : Random -> Simulator.CyclePattern =
        fun _ -> { Offsets = offsets; Jitter = 30L }

    /// 사이클마다 일부 dynamic component (spurious / confounded) 가 있는 패턴.
    let private mkPatternDynamic
        (staticOffsets: (int64 * string) list)
        (dynamicGen: Random -> (int64 * string) list)
        : Random -> Simulator.CyclePattern =
        fun rng ->
            { Offsets = staticOffsets @ (dynamicGen rng); Jitter = 30L }

    let private scenario name flow gt spurious nodes pattern cycleMs : Scenario =
        { Name = name; Flow = flow
          GroundTruth = gt; Spurious = spurious
          AllCalls = nodes |> List.distinct
          Pattern = pattern; PatternCycleAware = None; CycleMs = cycleMs }

    // ────────── m0 ~ m9: 기본 chain (길이 2 ~ 10) ──────────
    let private chainModels : Scenario list =
        [ for n in 2 .. 11 ->
            let i = n - 2
            let arrows, offsets, nodes = chain "F" "N" n 0L 200L
            scenario $"m%02d{i}_chain{n}" "F" arrows [] nodes (mkPattern offsets) 5000L ]

    // ────────── m10 ~ m19: Fan-out (target 2 ~ 6 개) ──────────
    let private fanOutModels : Scenario list =
        [ for k in 2 .. 6 do
            for variant in 0 .. 1 do
                let i = 10 + (k - 2) * 2 + variant
                if i <= 19 then
                    let targets = [ for t in 1 .. k -> $"T{t}" ]
                    let arrows, offsets, nodes = fanOut "F" "S" targets 0L (200L + int64 variant * 100L)
                    yield scenario $"m{i}_fanOut{k}" "F" arrows [] nodes (mkPattern offsets) 5000L ]

    // ────────── m20 ~ m29: Fan-in (source 2 ~ 6 개) ──────────
    let private fanInModels : Scenario list =
        [ for k in 2 .. 6 do
            for variant in 0 .. 1 do
                let i = 20 + (k - 2) * 2 + variant
                if i <= 29 then
                    let sources = [ for s in 1 .. k -> $"S{s}" ]
                    let arrows, offsets, nodes = fanIn "F" sources "T" 0L (300L + int64 variant * 100L)
                    yield scenario $"m{i}_fanIn{k}" "F" arrows [] nodes (mkPattern offsets) 5000L ]

    // ────────── m30 ~ m39: Group + sequential 혼합 ──────────
    let private groupMixModels : Scenario list =
        [ for i in 0 .. 9 ->
            // pattern: PRE → A ↔ B (group) → POST
            let groupT = 300L
            let postT = groupT + 200L + int64 i * 50L
            let arrows = [
                { VLine.Src = "F.PRE";  VLine.Tgt = "F.A";    VLine.Kind = "Start" }
                { VLine.Src = "F.A";    VLine.Tgt = "F.B";    VLine.Kind = "Group" }
                { VLine.Src = "F.A";    VLine.Tgt = "F.POST"; VLine.Kind = "Start" }
            ]
            let offsets = [
                0L,      "F.PRE"
                groupT,  "F.A"
                groupT,  "F.B"
                postT,   "F.POST"
            ]
            let nodes = [ "F.PRE"; "F.A"; "F.B"; "F.POST" ]
            scenario $"m{30 + i}_groupMix" "F" arrows [] nodes (mkPattern offsets) 5000L ]

    // ────────── m40 ~ m49: Confounded — Bimodal lag (명백한 가짜 인과) ──────────
    // 실제 confounded 는 외부 timer 다중 발화 → bimodal 분포.
    // 사이클의 절반은 short lag (100~200), 나머지는 long lag (1500~2500) →
    // CV 매우 크고 (>0.7) 알고리즘이 일관되게 drop.
    let private confoundedModels : Scenario list =
        [ for i in 0 .. 9 ->
            let chainArrows = [
                { VLine.Src = "F.A"; VLine.Tgt = "F.B"; VLine.Kind = "Start" }
            ]
            let spurious = [
                { VLine.Src = "F.A"; VLine.Tgt = "F.C"; VLine.Kind = "Start" }
            ]
            let chainOffsets = [ 0L, "F.A"; 200L, "F.B" ]
            // i 별 두 mode 간격 다양 — 그러나 모두 bimodal (높은 CV)
            let modeShort = 100 + i * 20
            let modeLong = 1500 + i * 100
            let dynamicGen (rng: Random) =
                // 50/50 확률로 short 또는 long
                let useLong = rng.Next(0, 2) = 0
                let lag =
                    if useLong then int64 modeLong + int64 (rng.Next(-100, 101))
                    else int64 modeShort + int64 (rng.Next(-50, 51))
                [ lag, "F.C" ]
            scenario $"m{40 + i}_confounded_bimodal" "F"
                chainArrows spurious
                [ "F.A"; "F.B"; "F.C" ]
                (mkPatternDynamic chainOffsets dynamicGen) 5000L ]

    // ────────── m50 ~ m59: Spurious 노이즈 변화 ──────────
    let private spuriousModels : Scenario list =
        [ for i in 0 .. 9 ->
            // base chain + spurious arrows
            let arrows, offsets, nodes = chain "F" "N" 3 0L 200L
            let spuriousCalls = [ for k in 1 .. (i + 1) -> $"X{k}" ]
            let spuriousArrows =
                [ for sc in spuriousCalls ->
                    { VLine.Src = $"F.{sc}"; VLine.Tgt = "F.N1"; VLine.Kind = "Start" } ]
            let dynamicGen (rng: Random) =
                [ for sc in spuriousCalls ->
                    spuriousPing "F" sc rng 4900L ]
            let allNodes = nodes @ (spuriousCalls |> List.map (fun n -> $"F.{n}"))
            scenario $"m{50 + i}_spurious{i + 1}" "F"
                arrows spuriousArrows allNodes
                (mkPatternDynamic offsets dynamicGen) 5000L ]

    // ────────── m60 ~ m69: Long chain + multiple groups ──────────
    let private longChainGroupModels : Scenario list =
        [ for i in 0 .. 9 ->
            // chain N1..N5 with group pair between N3 and N3b
            let arrows, offsets, nodes = chain "F" "N" 5 0L 200L
            let n3T = 400L
            let arrows2 = arrows @ [ { VLine.Src = "F.N3"; VLine.Tgt = "F.N3b"; VLine.Kind = "Group" } ]
            let offsets2 = offsets @ [ n3T, "F.N3b" ]
            let nodes2 = nodes @ [ "F.N3b" ]
            scenario $"m{60 + i}_longChain_g{i}" "F"
                arrows2 [] nodes2 (mkPattern offsets2) 5000L ]

    // ────────── m70 ~ m79: 다중 cycle 후보 (DAG enforcement stress) ──────────
    let private cycleModels : Scenario list =
        [ for i in 0 .. 9 ->
            // truth: chain N1 → N2 → N3 → N4 → N5
            // spurious: 여러 back-edge (N3→N1, N4→N2, N5→N1 등) — 모두 drop 되어야
            let arrows, offsets, nodes = chain "F" "N" 5 0L 200L
            // i 별 spurious 개수 (1~5개) 와 위치 변화
            let nSpurious = (i % 5) + 1
            let backEdges = [
                "F.N3", "F.N1"
                "F.N4", "F.N2"
                "F.N5", "F.N1"
                "F.N5", "F.N3"
                "F.N4", "F.N1"
            ]
            let spuriousArrows =
                backEdges
                |> List.truncate nSpurious
                |> List.map (fun (s, t) ->
                    { VLine.Src = s; VLine.Tgt = t; VLine.Kind = "Start" })
            scenario $"m{70 + i}_multiBackEdge{nSpurious}" "F"
                arrows spuriousArrows nodes (mkPattern offsets) 5000L ]

    // ────────── m80 ~ m89: 엣지 — 짧은 lag, 긴 lag, 적은 cycle ──────────
    let private edgeModels : Scenario list =
        [ for i in 0 .. 9 ->
            let lag, cycleMs, name =
                match i with
                | 0 -> 10L, 3000L, "lag10"       // 매우 짧은 lag — jitter 영향 큼
                | 1 -> 20L, 3000L, "lag20"
                | 2 -> 50L, 3000L, "lag50"
                | 3 -> 100L, 3000L, "lag100"
                | 4 -> 500L, 5000L, "lag500"
                | 5 -> 1000L, 6000L, "lag1000"
                | 6 -> 1500L, 7000L, "lag1500"
                | 7 -> 2000L, 8000L, "lag2000"
                | 8 -> 2500L, 9000L, "lag2500_nearWindow"
                | _ -> 2900L, 10000L, "lag2900_borderWindow"
            let arrows, offsets, nodes = chain "F" "N" 4 0L lag
            scenario $"m{80 + i}_edge_{name}" "F"
                arrows [] nodes (mkPattern offsets) cycleMs ]

    // ────────── m90 ~ m100: 복합 — 점점 더 큰 스케일 + 노이즈 + group ──────────
    let private compositeModels : Scenario list =
        [ for i in 0 .. 10 ->
            // i=0 (2 stages) ~ i=10 (12 stages). 각 stage 마다 A→B sequential.
            // i 가 짝수면 stage 끝에 group pair 추가 (B ↔ Bp).
            let stages = 2 + i
            let withGroup = i % 2 = 0
            let mutable allArrows = []
            let mutable staticOffsets = []
            let mutable allNodes = []
            let mutable t = 0L
            for s in 1 .. stages do
                let a = $"S{s}A"
                let b = $"S{s}B"
                staticOffsets <- staticOffsets @ [ t, $"F.{a}"; t + 100L, $"F.{b}" ]
                allArrows <- allArrows @ [
                    { VLine.Src = $"F.{a}"; VLine.Tgt = $"F.{b}"; VLine.Kind = "Start" } ]
                allNodes <- allNodes @ [ $"F.{a}"; $"F.{b}" ]
                if withGroup then
                    let bp = $"S{s}Bp"
                    staticOffsets <- staticOffsets @ [ t + 100L, $"F.{bp}" ]
                    allArrows <- allArrows @ [
                        { VLine.Src = $"F.{b}"; VLine.Tgt = $"F.{bp}"; VLine.Kind = "Group" } ]
                    allNodes <- allNodes @ [ $"F.{bp}" ]
                if s < stages then
                    let nextA = $"S{s + 1}A"
                    allArrows <- allArrows @ [
                        { VLine.Src = $"F.{b}"; VLine.Tgt = $"F.{nextA}"; VLine.Kind = "Start" } ]
                t <- t + 200L
            // 큰 모델 (stages >= 8) 일 때 노이즈 추가
            let noiseCount = max 0 (stages - 7)
            let noiseCalls = [ for k in 1 .. noiseCount -> $"NOISE{k}" ]
            let allNodesPlus = allNodes @ (noiseCalls |> List.map (fun n -> $"F.{n}"))
            let spuriousArrows =
                [ for nc in noiseCalls ->
                    { VLine.Src = $"F.{nc}"; VLine.Tgt = "F.S1A"; VLine.Kind = "Start" } ]
            let dynamicGen (rng: Random) =
                [ for nc in noiseCalls -> spuriousPing "F" nc rng (t + 2000L) ]
            let cycleMs = max 5000L (t + 2500L)
            scenario $"m{90 + i}_composite_s{stages}_g{withGroup}_n{noiseCount}" "F"
                allArrows spuriousArrows allNodesPlus
                (mkPatternDynamic staticOffsets dynamicGen) cycleMs ]

    // ────────── 추가 hard 시나리오 (m70~m79, m80~m89, m90~m100 강도 보강 후) ──────────
    // 위에서 정의된 모델 외 — 회귀 stress 용 별도 카테고리는 user 가 필요 시 m101+ 으로 확장.

    /// 전체 m0 ~ m100.
    let all : Scenario list =
        chainModels @ fanOutModels @ fanInModels @ groupMixModels
        @ confoundedModels @ spuriousModels @ longChainGroupModels
        @ cycleModels @ edgeModels @ compositeModels

    /// 카테고리별 갯수 통계.
    let stats () =
        [ "chain (m0-m9)", List.length chainModels
          "fanOut (m10-m19)", List.length fanOutModels
          "fanIn (m20-m29)", List.length fanInModels
          "groupMix (m30-m39)", List.length groupMixModels
          "confounded (m40-m49)", List.length confoundedModels
          "spurious (m50-m59)", List.length spuriousModels
          "longChain (m60-m69)", List.length longChainGroupModels
          "multiBackEdge (m70-m79)", List.length cycleModels
          "edge (m80-m89)", List.length edgeModels
          "composite (m90-m100)", List.length compositeModels ]
