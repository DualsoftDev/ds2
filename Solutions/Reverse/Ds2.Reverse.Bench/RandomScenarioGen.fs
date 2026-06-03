/// 무작위 시나리오 사양 생성. 알고리즘과 독립.
/// 사용처: InfiniteTestRunner / FuzzTests / property tests 확장.
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

/// Topology 종류 — 시나리오의 그래프 구조.
type TopologyKind =
    | Chain          // N1 → N2 → ... → Nn
    | Tree           // 분기 (depth 2-3)
    | DAG            // 임의 DAG (no cycle)
    | Star           // S → T1, T2, ..., Tn (fan-out)
    | Bipartite      // n × m 양분 (모든 S → 모든 T)

/// Lag 분포 종류.
type LagKind =
    | ConstantLag of int64
    | LinearDrift of baseMs: int64 * stepMs: int64
    | Bimodal of lag1: int64 * lag2: int64
    | RandomLag of minMs: int64 * maxMs: int64
    | CyclicDrift of meanMs: int64 * ampMs: int64 * period: int

/// 무작위 시나리오 사양 (이름은 spec_<seed>).
type ScenarioSpec = {
    Seed: int
    NCalls: int                  // 2 ~ 20
    NCycles: int                 // 20 ~ 200
    CycleMs: int64               // 500 ~ 10000
    Topology: TopologyKind
    LagPattern: LagKind
    JitterMs: int                // 5 ~ 100
    SpuriousCount: int           // 0 ~ 5
}

module RandomScenarioGen =

    /// 한 seed 의 random scenario 사양 생성.
    let random (rng: Random) : ScenarioSpec =
        let seed = rng.Next(1, 1_000_000_000)
        let nCalls = rng.Next(2, 21)
        let nCycles = rng.Next(20, 201)
        let cycleMs = int64 (rng.Next(500, 10001))
        let topo =
            match rng.Next(0, 5) with
            | 0 -> Chain
            | 1 -> Tree
            | 2 -> DAG
            | 3 -> Star
            | _ -> Bipartite
        let lag =
            match rng.Next(0, 5) with
            | 0 -> ConstantLag (int64 (rng.Next(50, 800)))
            | 1 -> LinearDrift (int64 (rng.Next(100, 500)), int64 (rng.Next(1, 10)))
            | 2 ->
                let l1 = int64 (rng.Next(100, 400))
                let l2 = l1 + int64 (rng.Next(200, 600))
                Bimodal(l1, l2)
            | 3 -> RandomLag (int64 (rng.Next(50, 300)), int64 (rng.Next(400, 1000)))
            | _ ->
                let mean = int64 (rng.Next(200, 600))
                let amp = int64 (rng.Next(50, 200))
                let period = rng.Next(4, 16)
                CyclicDrift(mean, amp, period)
        let jitter = rng.Next(5, 101)
        let spurious = rng.Next(0, 6)
        {
            Seed = seed
            NCalls = nCalls
            NCycles = nCycles
            CycleMs = cycleMs
            Topology = topo
            LagPattern = lag
            JitterMs = jitter
            SpuriousCount = spurious
        }

    /// Topology 별 (arrows, ordered call indices for simulation) 생성.
    /// 결과: (arrow list, simulation order [(srcIdx, tgtIdx)])
    let private buildTopology (spec: ScenarioSpec) (callNames: string[])
        : VLine.GroundTruthArrow list * (int * int) list =
        let n = callNames.Length
        let rng = Random(spec.Seed * 7 + 3)
        let mkArrow s t : VLine.GroundTruthArrow =
            { Src = callNames.[s]; Tgt = callNames.[t]; Kind = "Start" }
        match spec.Topology with
        | Chain ->
            let arrows = [ for i in 0 .. n - 2 -> mkArrow i (i + 1) ]
            let order = [ for i in 0 .. n - 2 -> i, i + 1 ]
            arrows, order
        | Tree ->
            // root 0, 각 node 는 random parent (이전 index 중)
            let parents = [|
                for i in 1 .. n - 1 -> rng.Next(0, i)
            |]
            let arrows = [
                for i in 1 .. n - 1 -> mkArrow parents.[i - 1] i
            ]
            let order = [
                for i in 1 .. n - 1 -> parents.[i - 1], i
            ]
            arrows, order
        | DAG ->
            // i<j 사이 30% 확률로 edge
            let edges =
                [ for i in 0 .. n - 1 do
                    for j in i + 1 .. n - 1 do
                        if rng.NextDouble() < 0.3 then yield i, j ]
            let edges' =
                if List.isEmpty edges && n >= 2 then [ 0, 1 ]
                else edges
            let arrows = edges' |> List.map (fun (s, t) -> mkArrow s t)
            arrows, edges'
        | Star ->
            // 0 = source, 1..n-1 = targets
            let arrows = [ for i in 1 .. n - 1 -> mkArrow 0 i ]
            let order = [ for i in 1 .. n - 1 -> 0, i ]
            arrows, order
        | Bipartite ->
            // 절반 source, 절반 target
            let half = n / 2
            let arrows = [
                for s in 0 .. half - 1 do
                    for t in half .. n - 1 -> mkArrow s t
            ]
            let order = [
                for s in 0 .. half - 1 do
                    for t in half .. n - 1 -> s, t
            ]
            arrows, order

    /// Lag 계산 — cycle 인덱스 + rng → lag 값.
    let private computeLag (spec: ScenarioSpec) (cycleIdx: int) (rng: Random) : int64 =
        match spec.LagPattern with
        | ConstantLag l -> l
        | LinearDrift(b, step) -> b + int64 cycleIdx * step
        | Bimodal(l1, l2) -> if rng.Next(0, 2) = 0 then l1 else l2
        | RandomLag(mn, mx) -> int64 (rng.Next(int mn, int mx + 1))
        | CyclicDrift(mean, amp, period) ->
            mean + int64 (float amp * cos (2.0 * System.Math.PI * float cycleIdx / float period))

    /// ScenarioSpec → Scenario (Ds2.Reverse.Bench 형식).
    let toScenario (spec: ScenarioSpec) : Scenario =
        let flow = "F"
        let n = max 2 (min 20 spec.NCalls)
        let callNames = [| for i in 0 .. n - 1 -> sprintf "%s.N%d" flow i |]
        let arrows, order = buildTopology spec callNames
        let allCalls = callNames |> Array.toList |> List.distinct

        // simulation pattern: cycle-aware
        let patternCA (cycleIdx: int) (rng: Random) : Simulator.CyclePattern =
            // 각 node 의 발화 시각 = topology order 따라 lag 누적
            // 가장 간단: callNames.[i] 가 t = i * lag (cumulative) 에 발화
            let lag = computeLag spec cycleIdx rng
            let offsets =
                [ for i in 0 .. n - 1 -> int64 i * lag, callNames.[i] ]
            // spurious calls (의도 noise — algorithm 이 drop 해야)
            let spuriousOffsets =
                [ for s in 1 .. spec.SpuriousCount ->
                    int64 (rng.Next(0, int spec.CycleMs)),
                    sprintf "%s.SP%d" flow s ]
            { Offsets = offsets @ spuriousOffsets
              Jitter = int64 spec.JitterMs }
        let allCallsWithSpurious =
            allCalls @ [
                for s in 1 .. spec.SpuriousCount -> sprintf "%s.SP%d" flow s
            ]
        {
            Name = sprintf "rand_seed%d_n%d_%A" spec.Seed n spec.Topology
            Flow = flow
            GroundTruth = arrows
            Spurious = []
            AllCalls = allCallsWithSpurious
            Pattern = (fun rng -> patternCA 0 rng)
            PatternCycleAware = Some patternCA
            CycleMs = spec.CycleMs
        }

    /// 사람이 읽을 수 있는 설명.
    let describe (spec: ScenarioSpec) : string =
        sprintf "seed=%d n=%d topo=%A lag=%A cycle=%dms jitter=%d sp=%d"
            spec.Seed spec.NCalls spec.Topology spec.LagPattern
            spec.CycleMs spec.JitterMs spec.SpuriousCount
