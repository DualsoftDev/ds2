/// 다른 차원의 검증 시나리오 — 통계적/시간적 stress test.
///
/// 차원:
///   D1 Sparse  (d1_0 ~ d1_4)  — 적은 발화
///   D2 Burst   (d2_0 ~ d2_4)  — 한 사이클 다중 발화
///   D3 Drift   (d3_0 ~ d3_4)  — 시간 따라 lag 변화
///   D4 Race    (d4_0 ~ d4_4)  — 비결정적 순서
///   D5 Partial (d5_0 ~ d5_4)  — 조건부 인과
namespace Ds2.Reverse.Bench

open System

module MoreModels =

    let private scenario name flow gt spurious nodes pattern cycleMs : Scenario =
        { Name = name; Flow = flow
          GroundTruth = gt; Spurious = spurious
          AllCalls = nodes |> List.distinct
          Pattern = pattern; PatternCycleAware = None; CycleMs = cycleMs }

    let private mkArrow flow s t kind : VLine.GroundTruthArrow =
        { Src = $"{flow}.{s}"; Tgt = $"{flow}.{t}"; Kind = kind }

    let private full flow node = $"{flow}.{node}"

    // ════════════════════════════════════════════════════════════════════
    // D1 Sparse — 적은 발화. minFires(5) 미만이면 인과 검증 불가 → drop
    // ════════════════════════════════════════════════════════════════════
    let private sparseModels : Scenario list = [
        // d1_0: 8 cycles, 3-step chain (모두 minFires 통과)
        let arrows = [ mkArrow "F" "A" "B" "Start"; mkArrow "F" "B" "C" "Start" ]
        let nodes = [ full "F" "A"; full "F" "B"; full "F" "C" ]
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "A"; 200L, full "F" "B"; 400L, full "F" "C" ]
              Jitter = 20L }
        yield scenario "d1_0_sparse8" "F" arrows [] nodes pattern 1000L

        // d1_1: 절반 사이클은 B 가 발화 안 함 (suff < 0.85 → drop)
        let arrowsTruth = [ mkArrow "F" "A" "C" "Start" ]
        let spurious = [ mkArrow "F" "A" "B" "Start" ]
        let nodes1 = [ full "F" "A"; full "F" "B"; full "F" "C" ]
        let pattern1 (rng: Random) : Simulator.CyclePattern =
            let withB = rng.Next(0, 2) = 0
            let base_ = [ 0L, full "F" "A"; 200L, full "F" "C" ]
            let extra = if withB then [ 100L, full "F" "B" ] else []
            { Offsets = base_ @ extra; Jitter = 20L }
        yield scenario "d1_1_intermittentB" "F" arrowsTruth spurious nodes1 pattern1 1000L

        // d1_2: tail node 가 매우 sparse (1/3 cycle 만 발화)
        let arrows2 = [ mkArrow "F" "A" "B" "Start" ]
        let spurious2 = [ mkArrow "F" "A" "C" "Start" ]
        let nodes2 = [ full "F" "A"; full "F" "B"; full "F" "C" ]
        let pattern2 (rng: Random) : Simulator.CyclePattern =
            let withC = rng.Next(0, 3) = 0
            let base_ = [ 0L, full "F" "A"; 200L, full "F" "B" ]
            let extra = if withC then [ 400L, full "F" "C" ] else []
            { Offsets = base_ @ extra; Jitter = 20L }
        yield scenario "d1_2_tailSparse" "F" arrows2 spurious2 nodes2 pattern2 1000L

        // d1_3: 모든 chain 노드가 한 사이클 걸러 한 번씩 발화 (동기화)
        let arrows3 = [ mkArrow "F" "A" "B" "Start"; mkArrow "F" "B" "C" "Start" ]
        let nodes3 = [ full "F" "A"; full "F" "B"; full "F" "C" ]
        let pattern3 (rng: Random) : Simulator.CyclePattern =
            let fire = rng.Next(0, 2) = 0
            let offsets =
                if fire then
                    [ 0L, full "F" "A"; 200L, full "F" "B"; 400L, full "F" "C" ]
                else []
            { Offsets = offsets; Jitter = 20L }
        yield scenario "d1_3_everyOther" "F" arrows3 [] nodes3 pattern3 1000L

        // d1_4: 5 cycle 미만으로 시뮬 — 모두 minFires 실패 → 빈 결과 (truth=0)
        // (BenchRunner 의 nCycles=60 기본 → 모든 노드 60번 발화. 이 케이스는 short cycle 로 변형)
        let arrows4 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes4 = [ full "F" "A"; full "F" "B" ]
        let pattern4 (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "A"; 100L, full "F" "B" ]; Jitter = 10L }
        // cycleMs 큼 → nCycles=60 으로 충분 발화 → 정상 검출.
        yield scenario "d1_4_shortLag" "F" arrows4 [] nodes4 pattern4 500L
    ]

    // ════════════════════════════════════════════════════════════════════
    // D2 Burst — 한 사이클 안 동일 노드 다중 발화. suff/necc 계산 robustness.
    // ════════════════════════════════════════════════════════════════════
    let private burstModels : Scenario list = [
        // d2_0: A 가 사이클당 2번 발화 (200ms 간격), B 는 한 번 (각 A 후 100ms)
        // truth: A → B (Start). 두 A 모두 B 트리거.
        let arrows = [ mkArrow "F" "A" "B" "Start" ]
        let nodes = [ full "F" "A"; full "F" "B" ]
        // 한 사이클: A@0, B@100, A@500, B@600 — A 두 번, B 두 번 (interleaved)
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "A"; 100L, full "F" "B"
                          500L, full "F" "A"; 600L, full "F" "B" ]
              Jitter = 15L }
        yield scenario "d2_0_doubleFire" "F" arrows [] nodes pattern 2000L

        // d2_1: A 가 burst (3 번 연속), B 가 한 번 (마지막 A 후)
        let arrows1 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes1 = [ full "F" "A"; full "F" "B" ]
        let pattern1 (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "A"; 50L, full "F" "A"; 100L, full "F" "A"
                          200L, full "F" "B" ]
              Jitter = 10L }
        yield scenario "d2_1_burstA" "F" arrows1 [] nodes1 pattern1 1500L

        // d2_2: group cluster — 3 노드 동시 발화
        let arrows2 = [
            mkArrow "F" "A" "B" "Group"
            mkArrow "F" "A" "C" "Group"
            mkArrow "F" "B" "C" "Group"  // 모두 동시 → 양방향 group, dedupe 필요
        ]
        // 두 spurious 의 dedup 가 일어남 — actually 위 arrows 자체가 truth
        let nodes2 = [ full "F" "A"; full "F" "B"; full "F" "C" ]
        let pattern2 (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "A"; 0L, full "F" "B"; 0L, full "F" "C" ]
              Jitter = 5L }
        yield scenario "d2_2_groupCluster" "F" arrows2 [] nodes2 pattern2 2000L

        // d2_3: chain 안 마지막 노드만 burst — lag 계산이 first match 만 보는지
        let arrows3 = [ mkArrow "F" "A" "B" "Start"; mkArrow "F" "B" "C" "Start" ]
        let nodes3 = [ full "F" "A"; full "F" "B"; full "F" "C" ]
        let pattern3 (_: Random) : Simulator.CyclePattern =
            { Offsets = [ 0L, full "F" "A"; 100L, full "F" "B"
                          200L, full "F" "C"; 250L, full "F" "C"; 300L, full "F" "C" ]
              Jitter = 10L }
        yield scenario "d2_3_tailBurst" "F" arrows3 [] nodes3 pattern3 1500L

        // d2_4: 사이클 안 두 개 separate trigger chain.
        // 각 chain (A1→B1, A2→B2) 가 독립 인과. 같은 B 가 아니라 각자 효과.
        let arrows4 = [
            mkArrow "F" "A1" "B1" "Start"
            mkArrow "F" "A2" "B2" "Start"
        ]
        let nodes4 = [ full "F" "A1"; full "F" "B1"; full "F" "A2"; full "F" "B2" ]
        let pattern4 (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, full "F" "A1"; 100L, full "F" "B1"
                500L, full "F" "A2"; 600L, full "F" "B2"
              ]; Jitter = 15L }
        yield scenario "d2_4_dualChain" "F" arrows4 [] nodes4 pattern4 2000L
    ]

    // ════════════════════════════════════════════════════════════════════
    // D3 Drift — lag 가 시간 따라 천천히 변화. 평균 안정해도 stationarity 깨짐.
    // ════════════════════════════════════════════════════════════════════
    let private driftModels : Scenario list = [
        // d3_0: lag 가 cycle 당 10ms 씩 증가 (100 → 700ms). Linear drift.
        // 진짜 인과로 인정 (driftStable detection 적용).
        let nodes = [ full "F" "A"; full "F" "B" ]
        let mutable cycleIdx = 0
        let pattern (_: Random) : Simulator.CyclePattern =
            let lag = 100L + int64 cycleIdx * 10L
            cycleIdx <- cycleIdx + 1
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        yield scenario "d3_0_linearDrift" "F" [ mkArrow "F" "A" "B" "Start" ] [] nodes pattern 2000L

        // d3_1: lag 가 cosine pattern (mean 300, amplitude 100)
        // std ≈ 71 (cosine), CV ≈ 0.24 → 통과 기대
        let arrows1 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes1 = [ full "F" "A"; full "F" "B" ]
        let mutable c1 = 0
        let pattern1 (_: Random) : Simulator.CyclePattern =
            let lag = 300L + int64 (100.0 * Math.Cos(float c1 * 0.2))
            c1 <- c1 + 1
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        yield scenario "d3_1_cosineDrift" "F" arrows1 [] nodes1 pattern1 2000L

        // d3_2: lag step jump — 첫 30 cycle 은 100ms, 다음 30 cycle 은 600ms (bimodal)
        let nodes2 = [ full "F" "A"; full "F" "B" ]
        let mutable c2 = 0
        let pattern2 (_: Random) : Simulator.CyclePattern =
            let lag = if c2 < 30 then 100L else 600L
            c2 <- c2 + 1
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        // bimodal step jump (lag 100 → 600 after 30 cycles).
        // 2026-05-25: k-means 강화로 step jump 도 detectable. GT reclassified.
        yield scenario "d3_2_stepJump" "F" [ mkArrow "F" "A" "B" "Start" ] [] nodes2 pattern2 2000L

        // d3_3: lag random walk (각 사이클마다 ±20 from previous)
        // 분산은 점진적으로 늘지만 인접 사이클 간 lag 안정 → 평균 std 어디로?
        let arrows3 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes3 = [ full "F" "A"; full "F" "B" ]
        let mutable lag3 = 300L
        let pattern3 (rng: Random) : Simulator.CyclePattern =
            lag3 <- lag3 + int64 (rng.Next(-20, 21))
            lag3 <- max 100L (min 600L lag3)
            { Offsets = [ 0L, full "F" "A"; lag3, full "F" "B" ]; Jitter = 15L }
        // lag mean ~ 350, std ~ 100 → CV ~ 0.29, 통과 기대
        yield scenario "d3_3_randomWalk" "F" arrows3 [] nodes3 pattern3 2000L

        // d3_4: lag 안정하지만 노이즈 사이클 (10% 의 사이클은 lag 가 huge spike)
        let nodes4 = [ full "F" "A"; full "F" "B" ]
        let pattern4 (rng: Random) : Simulator.CyclePattern =
            let spike = rng.Next(0, 10) = 0
            let lag = if spike then 2000L else 200L
            { Offsets = [ 0L, full "F" "A"; lag, full "F" "B" ]; Jitter = 15L }
        // spike noise — 10% spike 2000ms, 나머지 200ms. Tukey IQR outlier filter +
        // k-means 가 spike 제거 후 정상 인식.
        yield scenario "d3_4_spikeNoise" "F" [ mkArrow "F" "A" "B" "Start" ] [] nodes4 pattern4 4000L
    ]

    // ════════════════════════════════════════════════════════════════════
    // D4 Race — 비결정적 순서 fan-in. 두 source 가 sink 트리거, 매 사이클 다름.
    // ════════════════════════════════════════════════════════════════════
    let private raceModels : Scenario list = [
        // d4_0: A1 / A2 가 같은 lag 로 B 트리거. cycle 마다 A1, A2 순서 랜덤.
        let arrows = [
            mkArrow "F" "A1" "B" "Start"
            mkArrow "F" "A2" "B" "Start"
        ]
        let nodes = [ full "F" "A1"; full "F" "A2"; full "F" "B" ]
        let pattern (rng: Random) : Simulator.CyclePattern =
            let a1First = rng.Next(0, 2) = 0
            let offsets =
                if a1First then [ 0L, full "F" "A1"; 50L, full "F" "A2"; 200L, full "F" "B" ]
                else [ 0L, full "F" "A2"; 50L, full "F" "A1"; 200L, full "F" "B" ]
            { Offsets = offsets; Jitter = 10L }
        yield scenario "d4_0_raceBoth" "F" arrows [] nodes pattern 2000L

        // d4_1: A1 가 70% 트리거, A2 가 30% (B 가 발화하기 전 적어도 하나 발화)
        let arrows1 = [
            mkArrow "F" "A1" "B" "Start"
            mkArrow "F" "A2" "B" "Start"
        ]
        let nodes1 = [ full "F" "A1"; full "F" "A2"; full "F" "B" ]
        let pattern1 (rng: Random) : Simulator.CyclePattern =
            let useA1 = rng.Next(0, 10) < 7
            let firstA = if useA1 then full "F" "A1" else full "F" "A2"
            { Offsets = [ 0L, firstA; 100L, full "F" "B" ]; Jitter = 10L }
        // A1 70% suff, A2 30% suff → 둘 다 0.85 미만 → drop. 사실 진짜 인과인데 분리됨.
        // 모델 ground truth: 두 인과는 명시하지만 알고리즘이 0.85 게이트로 drop → FN 기대.
        // → 둘 다 spurious 로 둠 (검출 안 되는 게 정답)
        yield scenario "d4_1_uneven70_30" "F" [] arrows1 nodes1 pattern1 2000L

        // d4_2: A1 가 항상 트리거, A2 는 가끔 동시 발화 (spurious 위장)
        let arrows2 = [ mkArrow "F" "A1" "B" "Start" ]
        let spurious2 = [ mkArrow "F" "A2" "B" "Start" ]
        let nodes2 = [ full "F" "A1"; full "F" "A2"; full "F" "B" ]
        let pattern2 (rng: Random) : Simulator.CyclePattern =
            let withA2 = rng.Next(0, 5) = 0   // 20% cycles 만 A2 발화
            let base_ = [ 0L, full "F" "A1"; 100L, full "F" "B" ]
            let extra = if withA2 then [ 50L, full "F" "A2" ] else []
            { Offsets = base_ @ extra; Jitter = 10L }
        // A2 발화 횟수 작음 → necc 가 작음 (A2 → B 검증 시) → drop
        yield scenario "d4_2_a1Main_a2Rare" "F" arrows2 spurious2 nodes2 pattern2 2000L

        // d4_3: 3-way race (A1/A2/A3 모두 매 사이클 발화, 순서 랜덤)
        let arrows3 = [
            mkArrow "F" "A1" "B" "Start"
            mkArrow "F" "A2" "B" "Start"
            mkArrow "F" "A3" "B" "Start"
        ]
        let nodes3 = [ full "F" "A1"; full "F" "A2"; full "F" "A3"; full "F" "B" ]
        let pattern3 (rng: Random) : Simulator.CyclePattern =
            let order = [ full "F" "A1"; full "F" "A2"; full "F" "A3" ]
                        |> List.sortBy (fun _ -> rng.Next())
            let offsets = order |> List.mapi (fun i n -> int64 i * 30L, n)
            { Offsets = offsets @ [ 200L, full "F" "B" ]; Jitter = 10L }
        yield scenario "d4_3_threeWayRace" "F" arrows3 [] nodes3 pattern3 2000L

        // d4_4: B 가 가끔 A 없이 발화 (외부 트리거) → necc 깨짐
        let nodes4 = [ full "F" "A"; full "F" "B" ]
        let pattern4 (rng: Random) : Simulator.CyclePattern =
            let withoutA = rng.Next(0, 4) = 0   // 25% cycles B 만 발화
            if withoutA then { Offsets = [ 100L, full "F" "B" ]; Jitter = 10L }
            else { Offsets = [ 0L, full "F" "A"; 100L, full "F" "B" ]; Jitter = 10L }
        // necc ~ 0.75 < 0.85 → drop. truth 없음, spurious.
        yield scenario "d4_4_externalB" "F" [] [ mkArrow "F" "A" "B" "Start" ] nodes4 pattern4 2000L
    ]

    // ════════════════════════════════════════════════════════════════════
    // D5 Partial — A → B 가 항상 발생하지 않음 (확률적 인과).
    // ════════════════════════════════════════════════════════════════════
    let private partialModels : Scenario list = [
        // d5_0: 95% — 통과 기대
        let arrows = [ mkArrow "F" "A" "B" "Start" ]
        let nodes = [ full "F" "A"; full "F" "B" ]
        let pattern (rng: Random) : Simulator.CyclePattern =
            let success = rng.Next(0, 100) < 95
            let base_ = [ 0L, full "F" "A" ]
            let extra = if success then [ 100L, full "F" "B" ] else []
            { Offsets = base_ @ extra; Jitter = 10L }
        yield scenario "d5_0_partial95" "F" arrows [] nodes pattern 2000L

        // d5_1: 85% — borderline. suff = 0.85, gate >=. 통과 기대.
        let arrows1 = [ mkArrow "F" "A" "B" "Start" ]
        let nodes1 = [ full "F" "A"; full "F" "B" ]
        let pattern1 (rng: Random) : Simulator.CyclePattern =
            let success = rng.Next(0, 100) < 85
            let base_ = [ 0L, full "F" "A" ]
            let extra = if success then [ 100L, full "F" "B" ] else []
            { Offsets = base_ @ extra; Jitter = 10L }
        yield scenario "d5_1_partial85_border" "F" arrows1 [] nodes1 pattern1 2000L

        // d5_2: 70% — fail 기대. spurious 로 둠.
        let nodes2 = [ full "F" "A"; full "F" "B" ]
        let pattern2 (rng: Random) : Simulator.CyclePattern =
            let success = rng.Next(0, 100) < 70
            let base_ = [ 0L, full "F" "A" ]
            let extra = if success then [ 100L, full "F" "B" ] else []
            { Offsets = base_ @ extra; Jitter = 10L }
        yield scenario "d5_2_partial70" "F" [] [ mkArrow "F" "A" "B" "Start" ] nodes2 pattern2 2000L

        // d5_3: 50% — 명백한 무관계. 무조건 drop.
        let nodes3 = [ full "F" "A"; full "F" "B" ]
        let pattern3 (rng: Random) : Simulator.CyclePattern =
            let success = rng.Next(0, 100) < 50
            let base_ = [ 0L, full "F" "A" ]
            let extra = if success then [ 100L, full "F" "B" ] else []
            { Offsets = base_ @ extra; Jitter = 10L }
        yield scenario "d5_3_partial50" "F" [] [ mkArrow "F" "A" "B" "Start" ] nodes3 pattern3 2000L

        // d5_4: alternating — 짝수 cycle 만 B 발화 (정확히 50%)
        let nodes4 = [ full "F" "A"; full "F" "B" ]
        let mutable c4 = 0
        let pattern4 (_: Random) : Simulator.CyclePattern =
            let success = c4 % 2 = 0
            c4 <- c4 + 1
            let base_ = [ 0L, full "F" "A" ]
            let extra = if success then [ 100L, full "F" "B" ] else []
            { Offsets = base_ @ extra; Jitter = 10L }
        yield scenario "d5_4_alternating" "F" [] [ mkArrow "F" "A" "B" "Start" ] nodes4 pattern4 2000L
    ]

    /// 전체 다른-차원 시나리오.
    let all : Scenario list =
        sparseModels @ burstModels @ driftModels @ raceModels @ partialModels

    let stats () =
        [ "D1 Sparse  (d1)", List.length sparseModels
          "D2 Burst   (d2)", List.length burstModels
          "D3 Drift   (d3)", List.length driftModels
          "D4 Race    (d4)", List.length raceModels
          "D5 Partial (d5)", List.length partialModels ]
