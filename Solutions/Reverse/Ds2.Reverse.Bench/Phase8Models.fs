/// Phase 8 — Cross-Flow detection + Logic-Hybrid + Confidence Calibration.
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

module Phase8Models =

    type CrossFlowScenario = {
        Name: string
        /// Intra-flow GT arrows ("F1.A" "F1.B" 형식 — single dot)
        IntraGroundTruth: (string * string) list
        /// Cross-flow GT arrows ({flow}.{work}) — work-level
        CrossFlowGroundTruth: (string * string) list
        AllCalls: string list
        FlowCalls: Map<string, (string * string) list>
        WorkAssignments: Map<string, string * string>
        Pattern: Random -> Simulator.CyclePattern
        CycleMs: int64
    }

    let runCrossFlow (sc: CrossFlowScenario) (seed: int) (nCycles: int) =
        let events = Simulator.simulate seed sc.CycleMs nCycles sc.Pattern
        let cfg = CausationConfig.withCycleHint sc.CycleMs CausationConfig.defaults
        let intraCands =
            sc.IntraGroundTruth |> List.map (fun (s, t) ->
                { Src = s; Tgt = t; DeclaredKind = "trigger" })
        let crossCands =
            sc.CrossFlowGroundTruth |> List.map (fun (s, t) ->
                { Src = s; Tgt = t; DeclaredKind = "trigger" })
        let baseInp =
            ReverseEngine.mkInput "Phase8" "Main"
                sc.FlowCalls intraCands events cfg
        let inp =
            { baseInp with
                CrossFlowCandidates = crossCands
                WorkAssignments = sc.WorkAssignments }
        ReverseEngine.run inp

    type CrossFlowResult = {
        IntraTP: int; IntraFP: int; IntraFN: int
        CrossTP: int; CrossFP: int; CrossFN: int
        IntraF1: float
        CrossF1: float
    }

    let private f1 tp fp fn =
        if tp + fp + fn = 0 then 1.0
        else
            let p = if tp + fp = 0 then 0.0 else float tp / float (tp + fp)
            let r = if tp + fn = 0 then 0.0 else float tp / float (tp + fn)
            if p + r = 0.0 then 0.0 else 2.0 * p * r / (p + r)

    let evaluate (sc: CrossFlowScenario) (store: Ds2.Core.Store.DsStore)
        : CrossFlowResult =
        let intraTruth = sc.IntraGroundTruth |> Set.ofList
        let crossTruth = sc.CrossFlowGroundTruth |> Set.ofList
        let detectedIntra =
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
        let workName (workId: Guid) =
            match store.Works.TryGetValue workId with
            | true, w -> sprintf "%s.%s" w.FlowPrefix w.LocalName
            | _ -> "?"
        let detectedCross =
            store.ArrowWorks.Values
            |> Seq.map (fun a -> workName a.SourceId, workName a.TargetId)
            |> Set.ofSeq
        let intraTP = Set.intersect intraTruth detectedIntra |> Set.count
        let intraFP = detectedIntra - intraTruth |> Set.count
        let intraFN = intraTruth - detectedIntra |> Set.count
        let crossTP = Set.intersect crossTruth detectedCross |> Set.count
        let crossFP = detectedCross - crossTruth |> Set.count
        let crossFN = crossTruth - detectedCross |> Set.count
        {
            IntraTP = intraTP; IntraFP = intraFP; IntraFN = intraFN
            CrossTP = crossTP; CrossFP = crossFP; CrossFN = crossFN
            IntraF1 = f1 intraTP intraFP intraFN
            CrossF1 = f1 crossTP crossFP crossFN
        }

    // ── Helpers (single-dot call names) ──────────────────────────────

    /// Stage 'A'/'B' per work. Call name "F1.A1" (where 1 is stage idx in flow).
    let private callName (flowIdx: int) (workIdx: int) (suffix: string) =
        sprintf "F%d.%s%d" flowIdx suffix workIdx

    // ── Scenario constructors ────────────────────────────────────────

    /// 2-flow chain. 각 flow 안 2개 work, 각 work 안 A→B intra arrow.
    /// Cross-flow: F1.W2 → F2.W1.
    let makeTwoFlowChain () : CrossFlowScenario =
        let cycleMs = 4000L
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L,    "F1.A1"; 200L,  "F1.B1"
                400L,  "F1.A2"; 600L,  "F1.B2"
                1200L, "F2.A1"; 1400L, "F2.B1"
                1600L, "F2.A2"; 1800L, "F2.B2"
              ]; Jitter = 15L }
        let intraGT = [
            "F1.A1", "F1.B1"; "F1.A2", "F1.B2"
            "F2.A1", "F2.B1"; "F2.A2", "F2.B2"
        ]
        let crossGT = [ "F1.W2", "F2.W1" ]
        let allCalls = [
            "F1.A1"; "F1.B1"; "F1.A2"; "F1.B2"
            "F2.A1"; "F2.B1"; "F2.A2"; "F2.B2"
        ]
        let workAssign = Map.ofList [
            "F1.A1", ("F1", "W1"); "F1.B1", ("F1", "W1")
            "F1.A2", ("F1", "W2"); "F1.B2", ("F1", "W2")
            "F2.A1", ("F2", "W1"); "F2.B1", ("F2", "W1")
            "F2.A2", ("F2", "W2"); "F2.B2", ("F2", "W2")
        ]
        {
            Name = "f8_twoFlowChain"
            IntraGroundTruth = intraGT
            CrossFlowGroundTruth = crossGT
            AllCalls = allCalls
            FlowCalls = Scenario.flowCallsAuto allCalls
            WorkAssignments = workAssign
            Pattern = pattern
            CycleMs = cycleMs
        }

    /// 3-flow chain — F1 → F2 → F3 sequential.
    let makeThreeFlowChain () : CrossFlowScenario =
        let cycleMs = 6000L
        let pattern (_: Random) : Simulator.CyclePattern =
            let offsets = ResizeArray<int64 * string>()
            let mutable baseT = 0L
            for f in 1 .. 3 do
                offsets.Add(baseT, callName f 1 "A")
                offsets.Add(baseT + 200L, callName f 1 "B")
                offsets.Add(baseT + 400L, callName f 2 "A")
                offsets.Add(baseT + 600L, callName f 2 "B")
                baseT <- baseT + 1200L
            { Offsets = offsets |> List.ofSeq; Jitter = 15L }
        let intraGT = [
            for f in 1 .. 3 do
                yield callName f 1 "A", callName f 1 "B"
                yield callName f 2 "A", callName f 2 "B"
        ]
        let crossGT = [
            "F1.W2", "F2.W1"
            "F2.W2", "F3.W1"
        ]
        let allCalls = [
            for f in 1 .. 3 do
                yield callName f 1 "A"; yield callName f 1 "B"
                yield callName f 2 "A"; yield callName f 2 "B"
        ]
        let workAssign =
            Map.ofList [
                for f in 1 .. 3 do
                    yield callName f 1 "A", (sprintf "F%d" f, "W1")
                    yield callName f 1 "B", (sprintf "F%d" f, "W1")
                    yield callName f 2 "A", (sprintf "F%d" f, "W2")
                    yield callName f 2 "B", (sprintf "F%d" f, "W2")
            ]
        {
            Name = "f8_threeFlowChain"
            IntraGroundTruth = intraGT
            CrossFlowGroundTruth = crossGT
            AllCalls = allCalls
            FlowCalls = Scenario.flowCallsAuto allCalls
            WorkAssignments = workAssign
            Pattern = pattern
            CycleMs = cycleMs
        }

    /// Cross-flow fan-out — F1.W1 → F2.W1, F3.W1, F4.W1.
    let makeCrossFlowFanOut () : CrossFlowScenario =
        let cycleMs = 5000L
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L,    "F1.A1"; 200L,  "F1.B1"
                600L,  "F2.A1"; 800L,  "F2.B1"
                600L,  "F3.A1"; 800L,  "F3.B1"
                600L,  "F4.A1"; 800L,  "F4.B1"
              ]; Jitter = 15L }
        let intraGT = [
            for f in 1 .. 4 -> callName f 1 "A", callName f 1 "B"
        ]
        let crossGT = [
            "F1.W1", "F2.W1"
            "F1.W1", "F3.W1"
            "F1.W1", "F4.W1"
        ]
        let allCalls = [
            for f in 1 .. 4 do
                yield callName f 1 "A"; yield callName f 1 "B"
        ]
        let workAssign =
            Map.ofList [
                for f in 1 .. 4 do
                    yield callName f 1 "A", (sprintf "F%d" f, "W1")
                    yield callName f 1 "B", (sprintf "F%d" f, "W1")
            ]
        {
            Name = "f8_crossFlowFanOut"
            IntraGroundTruth = intraGT
            CrossFlowGroundTruth = crossGT
            AllCalls = allCalls
            FlowCalls = Scenario.flowCallsAuto allCalls
            WorkAssignments = workAssign
            Pattern = pattern
            CycleMs = cycleMs
        }

    let makeCrossFlowFanIn () : CrossFlowScenario =
        let cycleMs = 5000L
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L,    "F1.A1"; 200L,  "F1.B1"
                0L,    "F2.A1"; 200L,  "F2.B1"
                0L,    "F3.A1"; 200L,  "F3.B1"
                600L,  "F4.A1"; 800L,  "F4.B1"
              ]; Jitter = 15L }
        let intraGT = [
            for f in 1 .. 4 -> callName f 1 "A", callName f 1 "B"
        ]
        let crossGT = [
            "F1.W1", "F4.W1"
            "F2.W1", "F4.W1"
            "F3.W1", "F4.W1"
        ]
        let allCalls = [
            for f in 1 .. 4 do
                yield callName f 1 "A"; yield callName f 1 "B"
        ]
        let workAssign =
            Map.ofList [
                for f in 1 .. 4 do
                    yield callName f 1 "A", (sprintf "F%d" f, "W1")
                    yield callName f 1 "B", (sprintf "F%d" f, "W1")
            ]
        {
            Name = "f8_crossFlowFanIn"
            IntraGroundTruth = intraGT
            CrossFlowGroundTruth = crossGT
            AllCalls = allCalls
            FlowCalls = Scenario.flowCallsAuto allCalls
            WorkAssignments = workAssign
            Pattern = pattern
            CycleMs = cycleMs
        }

    let makeCrossFlowWithSpurious () : CrossFlowScenario =
        let cycleMs = 4000L
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L,    "F1.A1"; 200L,  "F1.B1"
                600L,  "F2.A1"; 800L,  "F2.B1"
                2000L, "F3.A1"; 2200L, "F3.B1"   // F3 무관 timing
              ]; Jitter = 15L }
        let intraGT = [
            for f in 1 .. 3 -> callName f 1 "A", callName f 1 "B"
        ]
        let crossGT = [ "F1.W1", "F2.W1" ]
        let allCalls = [
            for f in 1 .. 3 do
                yield callName f 1 "A"; yield callName f 1 "B"
        ]
        let workAssign =
            Map.ofList [
                for f in 1 .. 3 do
                    yield callName f 1 "A", (sprintf "F%d" f, "W1")
                    yield callName f 1 "B", (sprintf "F%d" f, "W1")
            ]
        {
            Name = "f8_crossFlowSpurious"
            IntraGroundTruth = intraGT
            CrossFlowGroundTruth = crossGT
            AllCalls = allCalls
            FlowCalls = Scenario.flowCallsAuto allCalls
            WorkAssignments = workAssign
            Pattern = pattern
            CycleMs = cycleMs
        }

    let allCrossFlowScenarios : CrossFlowScenario list = [
        makeTwoFlowChain ()
        makeThreeFlowChain ()
        makeCrossFlowFanOut ()
        makeCrossFlowFanIn ()
        makeCrossFlowWithSpurious ()
    ]

    // ── Phase 8B — Logic-Hybrid ──────────────────────────────────────

    /// Logic-Hybrid scenario: weak capture + logic rungs 제공.
    type LogicHybridScenario = {
        Name: string
        IntraGroundTruth: (string * string) list
        AllCalls: string list
        FlowCalls: Map<string, (string * string) list>
        WorkAssignments: Map<string, string * string>
        Pattern: Random -> Simulator.CyclePattern
        CycleMs: int64
        /// PLC rungs — A → B 인과 hint
        LogicRungs: LogicRung list
    }

    let runLogicHybrid (sc: LogicHybridScenario) (useLogic: bool) (seed: int) (nCycles: int) =
        let events = Simulator.simulate seed sc.CycleMs nCycles sc.Pattern
        let cfg = CausationConfig.withCycleHint sc.CycleMs CausationConfig.defaults
        let intraCands =
            sc.IntraGroundTruth |> List.map (fun (s, t) ->
                { Src = s; Tgt = t; DeclaredKind = "trigger" })
        let baseInp =
            ReverseEngine.mkInput "Phase8B" "Main"
                sc.FlowCalls intraCands events cfg
        let inp =
            { baseInp with
                LogicRungs = if useLogic then Some sc.LogicRungs else None
                WorkAssignments = sc.WorkAssignments }
        ReverseEngine.run inp

    /// Conditional with logic hint — A→B 가 60% cycles 만 (suff=0.6, 미달).
    /// Logic rung 으로 A→B 인과 명시 → logic-hybrid 인정.
    let makeWeakConditionalWithLogic () : LogicHybridScenario =
        let cycleMs = 2000L
        let pattern (rng: Random) : Simulator.CyclePattern =
            let hasB = rng.Next(0, 100) < 60
            let offs =
                [ 0L, "F.A" ]
                @ (if hasB then [ 200L, "F.B" ] else [])
            { Offsets = offs; Jitter = 10L }
        let intraGT = [ "F.A", "F.B" ]
        let allCalls = [ "F.A"; "F.B" ]
        let workAssign = Map.ofList [
            "F.A", ("F", "W1"); "F.B", ("F", "W1")
        ]
        // Logic: B = AND[A]  (single var). 변수명 = call name.
        let rungs = [
            { Output = "F.B"; Expr = LVar "F.A" }
        ]
        {
            Name = "f8b_weakConditional"
            IntraGroundTruth = intraGT
            AllCalls = allCalls
            FlowCalls = Scenario.flowCallsAuto allCalls
            WorkAssignments = workAssign
            Pattern = pattern
            CycleMs = cycleMs
            LogicRungs = rungs
        }

    /// Borderline suff — A→B 가 70% cycles, logic 으로 회복.
    let makeBorderlineSuff () : LogicHybridScenario =
        let cycleMs = 2000L
        let pattern (rng: Random) : Simulator.CyclePattern =
            let hasB = rng.Next(0, 100) < 70
            let offs =
                [ 0L, "F.A" ]
                @ (if hasB then [ 200L, "F.B" ] else [])
            { Offsets = offs; Jitter = 10L }
        let intraGT = [ "F.A", "F.B" ]
        let allCalls = [ "F.A"; "F.B" ]
        let workAssign = Map.ofList [
            "F.A", ("F", "W1"); "F.B", ("F", "W1")
        ]
        let rungs = [
            { Output = "F.B"; Expr = LVar "F.A" }
        ]
        {
            Name = "f8b_borderlineSuff"
            IntraGroundTruth = intraGT
            AllCalls = allCalls
            FlowCalls = Scenario.flowCallsAuto allCalls
            WorkAssignments = workAssign
            Pattern = pattern
            CycleMs = cycleMs
            LogicRungs = rungs
        }

    /// OR-gate logic — B = A OR C. logic strength 0.5 (weaker).
    /// capture 강해야 통과 (logic 보조 not enough).
    let makeOrGateLogic () : LogicHybridScenario =
        let cycleMs = 2000L
        let pattern (_: Random) : Simulator.CyclePattern =
            { Offsets = [
                0L, "F.A"; 200L, "F.B"
                500L, "F.C"
              ]; Jitter = 10L }
        let intraGT = [ "F.A", "F.B" ]
        let allCalls = [ "F.A"; "F.B"; "F.C" ]
        let workAssign = Map.ofList [
            "F.A", ("F", "W1"); "F.B", ("F", "W1"); "F.C", ("F", "W1")
        ]
        let rungs = [
            { Output = "F.B"; Expr = LOr [ LVar "F.A"; LVar "F.C" ] }
        ]
        {
            Name = "f8b_orGateLogic"
            IntraGroundTruth = intraGT
            AllCalls = allCalls
            FlowCalls = Scenario.flowCallsAuto allCalls
            WorkAssignments = workAssign
            Pattern = pattern
            CycleMs = cycleMs
            LogicRungs = rungs
        }

    /// Strong logic + weak capture (mainly logic-driven detection).
    let makeStrongLogicWeakCapture () : LogicHybridScenario =
        let cycleMs = 3000L
        let pattern (rng: Random) : Simulator.CyclePattern =
            let hasB = rng.Next(0, 100) < 55   // very weak suff
            let base_ = [ 0L, "F.A" ]
            let extra = if hasB then [ 200L, "F.B" ] else []
            { Offsets = base_ @ extra; Jitter = 10L }
        let intraGT = [ "F.A", "F.B" ]
        let allCalls = [ "F.A"; "F.B" ]
        let workAssign = Map.ofList [
            "F.A", ("F", "W1"); "F.B", ("F", "W1")
        ]
        // Strong logic: B = AND[A, A2, A3] (all reduce to A in recursion).
        let rungs = [
            { Output = "F.B"; Expr = LAnd [ LVar "F.A"; LVar "F.A2"; LVar "F.A3" ] }
            { Output = "F.A2"; Expr = LVar "F.A" }
            { Output = "F.A3"; Expr = LVar "F.A" }
        ]
        {
            Name = "f8b_strongLogic"
            IntraGroundTruth = intraGT
            AllCalls = allCalls
            FlowCalls = Scenario.flowCallsAuto allCalls
            WorkAssignments = workAssign
            Pattern = pattern
            CycleMs = cycleMs
            LogicRungs = rungs
        }

    let allLogicHybridScenarios : LogicHybridScenario list = [
        makeWeakConditionalWithLogic ()
        makeBorderlineSuff ()
        makeOrGateLogic ()
        makeStrongLogicWeakCapture ()
    ]

    let evaluateLogicHybrid (sc: LogicHybridScenario) (store: Ds2.Core.Store.DsStore) =
        let truth = sc.IntraGroundTruth |> Set.ofList
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
        tp, fp, fn
