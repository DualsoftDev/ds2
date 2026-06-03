module Ds2.Reverse.Tests.EndToEndTests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``VLINE — perfect causation detection (P/R/F1 = 1.000)`` () =
    let events = Simulator.simulate 20260515 5000L 60 Simulator.vlinePattern
    let input =
        ReverseEngine.mkInput
            "VLINE_test" "Main"
            VLine.flowCalls VLine.candidateArrows
            events CausationConfig.defaults

    let store, report = ReverseEngine.run input
    let metrics = Evaluation.evaluate VLine.groundTruth store

    printfn "=== VLINE Detection Report ==="
    printfn "  Candidates: %d, Passed Seq: %d, Passed Grp: %d, Dropped: %d"
        report.TotalCandidates report.PassedSeq report.PassedGrp report.DroppedCausation
    printfn "  Removed cycle: %d, transitive: %d, grp dup: %d"
        report.RemovedCycle report.RemovedTransitive report.RemovedGroupDup
    printfn "  Final arrowCalls: %d" report.FinalArrowCount
    printfn "Sequential — P=%.3f R=%.3f F1=%.3f (TP=%d FP=%d FN=%d)"
        metrics.Sequential.Precision metrics.Sequential.Recall metrics.Sequential.F1
        metrics.Sequential.TP metrics.Sequential.FP metrics.Sequential.FN
    printfn "Group      — P=%.3f R=%.3f F1=%.3f (TP=%d FP=%d FN=%d)"
        metrics.Group.Precision metrics.Group.Recall metrics.Group.F1
        metrics.Group.TP metrics.Group.FP metrics.Group.FN
    printfn "Overall    — P=%.3f R=%.3f F1=%.3f"
        metrics.Overall.Precision metrics.Overall.Recall metrics.Overall.F1
    if metrics.SeqFP |> List.isEmpty |> not then printfn "Seq FP: %A" metrics.SeqFP
    if metrics.SeqFN |> List.isEmpty |> not then printfn "Seq FN: %A" metrics.SeqFN

    Assert.Equal(1.0, metrics.Sequential.Precision)
    Assert.Equal(1.0, metrics.Sequential.Recall)
    Assert.Equal(1.0, metrics.Group.Precision)
    Assert.Equal(1.0, metrics.Group.Recall)
    Assert.Equal(1.0, metrics.Overall.F1)

[<Fact>]
let ``VLINE — spurious arrows all dropped`` () =
    let events = Simulator.simulate 20260515 5000L 60 Simulator.vlinePattern
    let input =
        ReverseEngine.mkInput
            "VLINE_test" "Main"
            VLine.flowCalls VLine.candidateArrows
            events CausationConfig.defaults
    let _, report = ReverseEngine.run input

    let droppedOrTransitive =
        report.DroppedCausation + report.RemovedTransitive
    Assert.True(droppedOrTransitive >= 5,
        sprintf "expected >=5 dropped/transitive; got dropped=%d transitive=%d"
            report.DroppedCausation report.RemovedTransitive)

[<Fact>]
let ``Round-trip — model can be serialized and deserialized`` () =
    let events = Simulator.simulate 20260515 5000L 60 Simulator.vlinePattern
    let input =
        ReverseEngine.mkInput
            "VLINE_test" "Main"
            VLine.flowCalls VLine.candidateArrows
            events CausationConfig.defaults
    let store, _ = ReverseEngine.run input

    let json = Ds2.Serialization.JsonConverter.serialize<Ds2.Core.Store.DsStore> store
    Assert.False(System.String.IsNullOrEmpty json)
    let restored : Ds2.Core.Store.DsStore = Ds2.Serialization.JsonConverter.deserialize json
    Assert.NotNull restored
    Assert.Equal(store.Calls.Count, restored.Calls.Count)
    Assert.Equal(store.ArrowCalls.Count, restored.ArrowCalls.Count)
