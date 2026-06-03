/// Phase 10 — Work-internal Call DAG diversity (≥10 nodes per scenario).
module Ds2.Reverse.Tests.Phase10Tests

open Xunit
open Ds2.Reverse.Bench
open Ds2.Reverse.Bench.Phase10Models

let private runAndEval (sc: CallDagScenario) =
    let store, _rep = runCallDag sc 42 80
    evaluate sc store

let private printR (r: DagResult) =
    printfn "  TP=%d FP=%d FN=%d  P=%.3f R=%.3f F1=%.3f"
        r.TP r.FP r.FN r.Precision r.Recall r.F1

// ── Per-pattern tests ────────────────────────────────────────────────

[<Fact>]
let ``Phase10 - DeepChain10`` () =
    let r = runAndEval (makeDeepChain10 ())
    printfn "DeepChain10:"
    printR r
    Assert.True(r.F1 >= 0.85,
        sprintf "DeepChain10 F1=%.3f below 0.85" r.F1)

[<Fact>]
let ``Phase10 - DeepChain15`` () =
    let r = runAndEval (makeDeepChain15 ())
    printfn "DeepChain15:"
    printR r
    Assert.True(r.F1 >= 0.85,
        sprintf "DeepChain15 F1=%.3f below 0.85" r.F1)

[<Fact>]
let ``Phase10 - WideFanOut`` () =
    let r = runAndEval (makeWideFanOut ())
    printfn "WideFanOut:"
    printR r
    Assert.True(r.F1 >= 0.85,
        sprintf "WideFanOut F1=%.3f below 0.85" r.F1)

[<Fact>]
let ``Phase10 - WideFanIn`` () =
    let r = runAndEval (makeWideFanIn ())
    printfn "WideFanIn:"
    printR r
    Assert.True(r.F1 >= 0.85,
        sprintf "WideFanIn F1=%.3f below 0.85" r.F1)

[<Fact>]
let ``Phase10 - Layered3`` () =
    let r = runAndEval (makeLayered3 ())
    printfn "Layered3:"
    printR r
    Assert.True(r.F1 >= 0.85,
        sprintf "Layered3 F1=%.3f below 0.85" r.F1)

[<Fact>]
let ``Phase10 - DiamondCascade`` () =
    let r = runAndEval (makeDiamondCascade ())
    printfn "DiamondCascade:"
    printR r
    Assert.True(r.F1 >= 0.85,
        sprintf "DiamondCascade F1=%.3f below 0.85" r.F1)

[<Fact>]
let ``Phase10 - Lattice3x4`` () =
    let r = runAndEval (makeLattice3x4 ())
    printfn "Lattice3x4:"
    printR r
    Assert.True(r.F1 >= 0.85,
        sprintf "Lattice3x4 F1=%.3f below 0.85" r.F1)

[<Fact>]
let ``Phase10 - TreeBinary`` () =
    let r = runAndEval (makeTreeBinary ())
    printfn "TreeBinary:"
    printR r
    Assert.True(r.F1 >= 0.85,
        sprintf "TreeBinary F1=%.3f below 0.85" r.F1)

[<Fact>]
let ``Phase10 - HubSpoke`` () =
    let r = runAndEval (makeHubSpoke ())
    printfn "HubSpoke:"
    printR r
    Assert.True(r.F1 >= 0.85,
        sprintf "HubSpoke F1=%.3f below 0.85" r.F1)

[<Fact>]
let ``Phase10 - MixedDAG`` () =
    let r = runAndEval (makeMixedDAG ())
    printfn "MixedDAG:"
    printR r
    Assert.True(r.F1 >= 0.85,
        sprintf "MixedDAG F1=%.3f below 0.85" r.F1)

// ── Aggregate ────────────────────────────────────────────────────────

[<Fact>]
let ``Phase10 - aggregate F1 ≥ 0.85`` () =
    let mutable totTP, totFP, totFN = 0, 0, 0
    let mutable rowCt = 0
    let mutable sumF1 = 0.0
    for sc in allCallDagScenarios do
        let r = runAndEval sc
        printfn "%-22s  TP=%d FP=%d FN=%d  F1=%.3f"
            sc.Name r.TP r.FP r.FN r.F1
        totTP <- totTP + r.TP
        totFP <- totFP + r.FP
        totFN <- totFN + r.FN
        sumF1 <- sumF1 + r.F1
        rowCt <- rowCt + 1
    let macroF1 = sumF1 / float rowCt
    let p =
        if totTP + totFP = 0 then 0.0
        else float totTP / float (totTP + totFP)
    let r =
        if totTP + totFN = 0 then 0.0
        else float totTP / float (totTP + totFN)
    let microF1 =
        if p + r = 0.0 then 0.0 else 2.0 * p * r / (p + r)
    printfn "──────────────────────────────────────────"
    printfn "Phase10 aggregate: micro-F1=%.3f  macro-F1=%.3f"
        microF1 macroF1
    Assert.True(microF1 >= 0.85,
        sprintf "Phase10 aggregate micro-F1=%.3f below 0.85" microF1)
    Assert.True(macroF1 >= 0.85,
        sprintf "Phase10 aggregate macro-F1=%.3f below 0.85" macroF1)

// ── Diagnostic — node / edge count 확인 ──────────────────────────────

[<Fact>]
let ``Phase10 - all scenarios have ≥10 nodes`` () =
    for sc in allCallDagScenarios do
        let n = List.length sc.AllCalls
        printfn "%-22s  nodes=%d  edges=%d" sc.Name n (List.length sc.GroundTruth)
        Assert.True(n >= 10,
            sprintf "%s has only %d nodes (<10)" sc.Name n)
