/// Phase 10 — Work-internal Call DAG diversity (≥10 nodes per scenario).
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

module Phase10Models =

    /// 한 work 안 Call DAG 시나리오. Single flow `F`, single work `F.W1`.
    type CallDagScenario = {
        Name: string
        /// 직접 (transitively reduced) intra GT edges. Call name 은 "F.<node>" 형식.
        GroundTruth: (string * string) list
        /// 모든 call full name (예: "F.A1"). FlowCalls / WorkAssignments 자동 빌드.
        AllCalls: string list
        Pattern: Random -> Simulator.CyclePattern
        CycleMs: int64
    }

    /// FlowCalls — single flow "F".
    let private flowCallsOf (calls: string list) : Map<string, (string * string) list> =
        Scenario.flowCallsAuto calls

    /// WorkAssignments — 모든 call 을 동일 work `F.W1`.
    let private workAssignOf (calls: string list) : Map<string, string * string> =
        calls |> List.map (fun c -> c, ("F", "W1")) |> Map.ofList

    /// Run — single-work DAG.
    let runCallDag (sc: CallDagScenario) (seed: int) (nCycles: int) =
        let events = Simulator.simulate seed sc.CycleMs nCycles sc.Pattern
        let cfg = CausationConfig.withCycleHint sc.CycleMs CausationConfig.defaults
        let cands =
            sc.GroundTruth |> List.map (fun (s, t) ->
                { Src = s; Tgt = t; DeclaredKind = "trigger" })
        let baseInp =
            ReverseEngine.mkInput "Phase10" "Main"
                (flowCallsOf sc.AllCalls) cands events cfg
        let inp = { baseInp with WorkAssignments = workAssignOf sc.AllCalls }
        ReverseEngine.run inp

    type DagResult = {
        TP: int; FP: int; FN: int
        Precision: float; Recall: float; F1: float
    }

    let private f1 tp fp fn =
        if tp + fp + fn = 0 then 1.0, 1.0, 1.0
        else
            let p = if tp + fp = 0 then 0.0 else float tp / float (tp + fp)
            let r = if tp + fn = 0 then 0.0 else float tp / float (tp + fn)
            let f = if p + r = 0.0 then 0.0 else 2.0 * p * r / (p + r)
            p, r, f

    let evaluate (sc: CallDagScenario) (store: Ds2.Core.Store.DsStore) : DagResult =
        let truth = sc.GroundTruth |> Set.ofList
        let detected =
            store.ArrowCalls.Values
            |> Seq.map (fun a ->
                let sName =
                    match store.Calls.TryGetValue a.SourceId with
                    | true, c -> c.Name | _ -> "?"
                let tName =
                    match store.Calls.TryGetValue a.TargetId with
                    | true, c -> c.Name | _ -> "?"
                sName, tName)
            |> Set.ofSeq
        let tp = Set.intersect truth detected |> Set.count
        let fp = detected - truth |> Set.count
        let fn = truth - detected |> Set.count
        let p, r, f = f1 tp fp fn
        { TP = tp; FP = fp; FN = fn; Precision = p; Recall = r; F1 = f }

    // ── Helpers ─────────────────────────────────────────────────────────

    let private full node : string = $"F.{node}"

    /// 노드 list + edge list → AllCalls + GroundTruth.
    let private build (nodes: string list) (edges: (string * string) list) =
        let calls = nodes |> List.map full
        let gt = edges |> List.map (fun (s, t) -> full s, full t)
        calls, gt

    /// Layered fire schedule — 각 노드의 발화 시각 (ms) 부여, jitter 동일.
    let private mkPattern (offsets: (int64 * string) list) (jitter: int64)
        : Random -> Simulator.CyclePattern =
        fun (_: Random) -> { Offsets = offsets; Jitter = jitter }

    // ══════════════════════════════════════════════════════════════════
    // 1. DeepChain10 — 10 노드 chain A1→A2→…→A10
    // ══════════════════════════════════════════════════════════════════
    let makeDeepChain10 () : CallDagScenario =
        let nodes = [ for i in 1 .. 10 -> sprintf "A%d" i ]
        let edges = [ for i in 1 .. 9 -> sprintf "A%d" i, sprintf "A%d" (i + 1) ]
        let calls, gt = build nodes edges
        let offsets =
            [ for i in 0 .. 9 -> int64 i * 150L, full (sprintf "A%d" (i + 1)) ]
        {
            Name = "f10_deepChain10"
            GroundTruth = gt
            AllCalls = calls
            Pattern = mkPattern offsets 10L
            CycleMs = 3000L
        }

    // ══════════════════════════════════════════════════════════════════
    // 2. DeepChain15 — 15 노드 chain
    // ══════════════════════════════════════════════════════════════════
    let makeDeepChain15 () : CallDagScenario =
        let nodes = [ for i in 1 .. 15 -> sprintf "B%d" i ]
        let edges = [ for i in 1 .. 14 -> sprintf "B%d" i, sprintf "B%d" (i + 1) ]
        let calls, gt = build nodes edges
        let offsets =
            [ for i in 0 .. 14 -> int64 i * 120L, full (sprintf "B%d" (i + 1)) ]
        {
            Name = "f10_deepChain15"
            GroundTruth = gt
            AllCalls = calls
            Pattern = mkPattern offsets 10L
            CycleMs = 4000L
        }

    // ══════════════════════════════════════════════════════════════════
    // 3. WideFanOut — 1 source → 9 parallel targets (10 nodes, 9 edges)
    // ══════════════════════════════════════════════════════════════════
    let makeWideFanOut () : CallDagScenario =
        let nodes = "S" :: [ for i in 1 .. 9 -> sprintf "T%d" i ]
        let edges = [ for i in 1 .. 9 -> "S", sprintf "T%d" i ]
        let calls, gt = build nodes edges
        let offsets =
            (0L, full "S") ::
            [ for i in 1 .. 9 -> 200L, full (sprintf "T%d" i) ]
        {
            Name = "f10_wideFanOut"
            GroundTruth = gt
            AllCalls = calls
            Pattern = mkPattern offsets 10L
            CycleMs = 2000L
        }

    // ══════════════════════════════════════════════════════════════════
    // 4. WideFanIn — 9 parallel sources → 1 sink (10 nodes, 9 edges)
    // ══════════════════════════════════════════════════════════════════
    let makeWideFanIn () : CallDagScenario =
        let nodes = [ for i in 1 .. 9 -> sprintf "P%d" i ] @ [ "K" ]
        let edges = [ for i in 1 .. 9 -> sprintf "P%d" i, "K" ]
        let calls, gt = build nodes edges
        let offsets =
            [ for i in 1 .. 9 -> 0L, full (sprintf "P%d" i) ]
            @ [ 200L, full "K" ]
        {
            Name = "f10_wideFanIn"
            GroundTruth = gt
            AllCalls = calls
            Pattern = mkPattern offsets 10L
            CycleMs = 2000L
        }

    // ══════════════════════════════════════════════════════════════════
    // 5. Layered3 — 3 layers (3-4-4 = 11 nodes, 10 edges)
    //   L1: X1, X2, X3
    //   L2: Y1, Y2, Y3, Y4
    //   L3: Z1, Z2, Z3, Z4
    //   X1→Y1, X1→Y2;  X2→Y2, X2→Y3;  X3→Y3, X3→Y4
    //   Y1→Z1; Y2→Z2; Y3→Z3; Y4→Z4
    // ══════════════════════════════════════════════════════════════════
    let makeLayered3 () : CallDagScenario =
        let nodes =
            [ "X1"; "X2"; "X3"
              "Y1"; "Y2"; "Y3"; "Y4"
              "Z1"; "Z2"; "Z3"; "Z4" ]
        let edges = [
            "X1", "Y1"; "X1", "Y2"
            "X2", "Y2"; "X2", "Y3"
            "X3", "Y3"; "X3", "Y4"
            "Y1", "Z1"; "Y2", "Z2"; "Y3", "Z3"; "Y4", "Z4"
        ]
        let calls, gt = build nodes edges
        let offsets = [
            // L1 @ 0
            0L, full "X1"; 0L, full "X2"; 0L, full "X3"
            // L2 @ 200
            200L, full "Y1"; 200L, full "Y2"; 200L, full "Y3"; 200L, full "Y4"
            // L3 @ 400
            400L, full "Z1"; 400L, full "Z2"; 400L, full "Z3"; 400L, full "Z4"
        ]
        {
            Name = "f10_layered3"
            GroundTruth = gt
            AllCalls = calls
            Pattern = mkPattern offsets 10L
            CycleMs = 2500L
        }

    // ══════════════════════════════════════════════════════════════════
    // 6. DiamondCascade — 10 노드, 12 edges, 다중 diamond
    //   A1 → {A2, A3}; {A2, A3} → A4; A4 → {A5, A6}; {A5, A6} → A7;
    //   A7 → {A8, A9}; {A8, A9} → A10
    // ══════════════════════════════════════════════════════════════════
    let makeDiamondCascade () : CallDagScenario =
        let nodes = [ for i in 1 .. 10 -> sprintf "D%d" i ]
        let edges = [
            "D1", "D2"; "D1", "D3"
            "D2", "D4"; "D3", "D4"
            "D4", "D5"; "D4", "D6"
            "D5", "D7"; "D6", "D7"
            "D7", "D8"; "D7", "D9"
            "D8", "D10"; "D9", "D10"
        ]
        let calls, gt = build nodes edges
        let offsets = [
            0L,   full "D1"
            150L, full "D2"; 150L, full "D3"
            300L, full "D4"
            450L, full "D5"; 450L, full "D6"
            600L, full "D7"
            750L, full "D8"; 750L, full "D9"
            900L, full "D10"
        ]
        {
            Name = "f10_diamondCascade"
            GroundTruth = gt
            AllCalls = calls
            Pattern = mkPattern offsets 12L
            CycleMs = 3000L
        }

    // ══════════════════════════════════════════════════════════════════
    // 7. Lattice3x4 — 3×4 grid (12 nodes), each → right + down
    //   G(r,c) for r=1..3, c=1..4. Edges G(r,c)→G(r,c+1) and G(r,c)→G(r+1,c).
    //   Total edges: 3*3 (right) + 2*4 (down) = 17.
    // ══════════════════════════════════════════════════════════════════
    let makeLattice3x4 () : CallDagScenario =
        let name r c = sprintf "G%d%d" r c
        let nodes = [ for r in 1 .. 3 do for c in 1 .. 4 -> name r c ]
        let edges = [
            // right
            for r in 1 .. 3 do
                for c in 1 .. 3 do
                    yield name r c, name r (c + 1)
            // down
            for r in 1 .. 2 do
                for c in 1 .. 4 do
                    yield name r c, name (r + 1) c
        ]
        let calls, gt = build nodes edges
        // 발화 시각: Manhattan distance × 120ms
        let offsets =
            [ for r in 1 .. 3 do
                for c in 1 .. 4 ->
                    let d = (r - 1) + (c - 1)
                    int64 d * 120L, full (name r c) ]
        {
            Name = "f10_lattice3x4"
            GroundTruth = gt
            AllCalls = calls
            Pattern = mkPattern offsets 10L
            CycleMs = 3000L
        }

    // ══════════════════════════════════════════════════════════════════
    // 8. TreeBinary — 10 노드 binary tree
    //   R → L1, L2; L1 → L3, L4; L2 → L5, L6; L3 → L7, L8; L4 → L9
    //   Edges: 9
    // ══════════════════════════════════════════════════════════════════
    let makeTreeBinary () : CallDagScenario =
        let nodes = [ "R"; "L1"; "L2"; "L3"; "L4"; "L5"; "L6"; "L7"; "L8"; "L9" ]
        let edges = [
            "R", "L1"; "R", "L2"
            "L1", "L3"; "L1", "L4"
            "L2", "L5"; "L2", "L6"
            "L3", "L7"; "L3", "L8"
            "L4", "L9"
        ]
        let calls, gt = build nodes edges
        let offsets = [
            0L,   full "R"
            200L, full "L1"; 200L, full "L2"
            400L, full "L3"; 400L, full "L4"; 400L, full "L5"; 400L, full "L6"
            600L, full "L7"; 600L, full "L8"; 600L, full "L9"
        ]
        {
            Name = "f10_treeBinary"
            GroundTruth = gt
            AllCalls = calls
            Pattern = mkPattern offsets 10L
            CycleMs = 2500L
        }

    // ══════════════════════════════════════════════════════════════════
    // 9. HubSpoke — Hub + 3 sub-hubs + 6 leaves (10 nodes)
    //   H → S1, S2, S3; S1 → L11, L12; S2 → L21, L22; S3 → L31, L32
    //   Edges: 9
    // ══════════════════════════════════════════════════════════════════
    let makeHubSpoke () : CallDagScenario =
        let nodes = [ "H"; "S1"; "S2"; "S3"; "L11"; "L12"; "L21"; "L22"; "L31"; "L32" ]
        let edges = [
            "H", "S1"; "H", "S2"; "H", "S3"
            "S1", "L11"; "S1", "L12"
            "S2", "L21"; "S2", "L22"
            "S3", "L31"; "S3", "L32"
        ]
        let calls, gt = build nodes edges
        let offsets = [
            0L,   full "H"
            200L, full "S1"; 200L, full "S2"; 200L, full "S3"
            400L, full "L11"; 400L, full "L12"
            400L, full "L21"; 400L, full "L22"
            400L, full "L31"; 400L, full "L32"
        ]
        {
            Name = "f10_hubSpoke"
            GroundTruth = gt
            AllCalls = calls
            Pattern = mkPattern offsets 10L
            CycleMs = 2500L
        }

    // ══════════════════════════════════════════════════════════════════
    // 10. MixedDAG — chain + fan-out + fan-in 혼합 (12 nodes, 14 edges)
    //   M1 → M2 → M3
    //   M3 → {M4, M5, M6}     (fan-out)
    //   {M4, M5, M6} → M7      (fan-in)
    //   M7 → M8 → M9
    //   M9 → {M10, M11}        (fan-out)
    //   {M10, M11} → M12       (fan-in)
    // ══════════════════════════════════════════════════════════════════
    let makeMixedDAG () : CallDagScenario =
        let nodes = [ for i in 1 .. 12 -> sprintf "M%d" i ]
        let edges = [
            "M1", "M2"; "M2", "M3"
            "M3", "M4"; "M3", "M5"; "M3", "M6"
            "M4", "M7"; "M5", "M7"; "M6", "M7"
            "M7", "M8"; "M8", "M9"
            "M9", "M10"; "M9", "M11"
            "M10", "M12"; "M11", "M12"
        ]
        let calls, gt = build nodes edges
        let offsets = [
            0L,   full "M1"
            150L, full "M2"
            300L, full "M3"
            450L, full "M4"; 450L, full "M5"; 450L, full "M6"
            600L, full "M7"
            750L, full "M8"
            900L, full "M9"
            1050L, full "M10"; 1050L, full "M11"
            1200L, full "M12"
        ]
        {
            Name = "f10_mixedDAG"
            GroundTruth = gt
            AllCalls = calls
            Pattern = mkPattern offsets 12L
            CycleMs = 3500L
        }

    /// 모든 Phase 10 시나리오.
    let allCallDagScenarios : CallDagScenario list = [
        makeDeepChain10 ()
        makeDeepChain15 ()
        makeWideFanOut ()
        makeWideFanIn ()
        makeLayered3 ()
        makeDiamondCascade ()
        makeLattice3x4 ()
        makeTreeBinary ()
        makeHubSpoke ()
        makeMixedDAG ()
    ]
