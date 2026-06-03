module Ds2.Reverse.Tests.RealDataTests

open System
open System.IO
open System.Text.Json
open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

/// DEMO arrows JSON 모델
type ArrowDocJson = {
    arrows: ArrowJson[]
}
and ArrowJson = {
    src: string
    tgt: string
    kind: string
    scope: string
}

type EventsDocJson = {
    events: EventJson[]
    flowCalls: System.Collections.Generic.Dictionary<string, string[]>
}
and EventJson = {
    t: int64
    name: string
}

let private deserialize<'T> (path: string) : 'T =
    let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
    JsonSerializer.Deserialize<'T>(File.ReadAllText path, options)

[<Fact>]
let ``DEMO real data — algorithm runs and produces valid output`` () =
    let demoDir = @"D:\dstest\demoKit"
    let arrowsPath = Path.Combine(demoDir, "DEMO.arrows_minimal.json")
    let eventsPath = Path.Combine(demoDir, "DEMO.events.json")

    Assert.True(File.Exists arrowsPath, sprintf "missing: %s" arrowsPath)
    Assert.True(File.Exists eventsPath, sprintf "missing: %s" eventsPath)

    let arrowsDoc = deserialize<ArrowDocJson> arrowsPath
    let eventsDoc = deserialize<EventsDocJson> eventsPath

    // in-active-work arrows → candidates
    let candidates =
        arrowsDoc.arrows
        |> Array.filter (fun a -> a.scope = "in-active-work")
        |> Array.map (fun a -> { Src = a.src; Tgt = a.tgt; DeclaredKind = a.kind })
        |> List.ofArray

    // cross-flow arrows → arrowWorks candidates
    let crossFlow =
        arrowsDoc.arrows
        |> Array.filter (fun a -> a.scope = "cross-flow")
        |> Array.map (fun a -> { Src = a.src; Tgt = a.tgt; DeclaredKind = a.kind })
        |> List.ofArray

    let events =
        eventsDoc.events
        |> Array.map (fun e -> { T = e.t; Name = e.name })
        |> List.ofArray

    let flowCalls =
        eventsDoc.flowCalls
        |> Seq.map (fun kv -> kv.Key, kv.Value |> Array.map (fun n -> n, "") |> List.ofArray)
        |> Map.ofSeq

    // cycle 추정 — 한 cycle period ≈ events duration / unique events 수
    // 단순화: cycle hint 5000ms (capture 길이 244초 / 약 50 cycle)
    let cfg =
        CausationConfig.defaults
        |> CausationConfig.withCycleHint 5500L

    let baseInput =
        ReverseEngine.mkInput
            "DEMO_v18_alg"
            "Main"
            flowCalls
            candidates
            events
            cfg
    let input = { baseInput with CrossFlowCandidates = crossFlow }

    let store, report = ReverseEngine.run input

    printfn "═════ DEMO Real Data Verification ═════"
    printfn "Events: %d, Flows: %d, In-active-work candidates: %d, Cross-flow candidates: %d"
        events.Length flowCalls.Count candidates.Length crossFlow.Length
    printfn ""
    printfn "Detection Report:"
    printfn "  Total candidates : %d" report.TotalCandidates
    printfn "  Passed Sequential: %d" report.PassedSeq
    printfn "  Passed Group     : %d" report.PassedGrp
    printfn "  Dropped (causation gate): %d" report.DroppedCausation
    printfn "  Removed cycle    : %d" report.RemovedCycle
    printfn "  Removed transitive: %d" report.RemovedTransitive
    printfn "  Removed group dup: %d" report.RemovedGroupDup
    printfn "  Final arrowCalls : %d" report.FinalArrowCount
    printfn ""
    printfn "Store summary: Flows=%d, Works=%d, Calls=%d, ApiDefs=%d, arrowCalls=%d"
        store.Flows.Count store.Works.Count store.Calls.Count
        store.ApiDefs.Count store.ArrowCalls.Count
    printfn ""
    printfn "Dropped detail (인과 검증 실패):"
    for (s, t, sco, reason) in report.DroppedDetail |> Seq.truncate 10 do
        printfn "  %s → %s  reason=%s suff=%.2f necc=%.2f lag=%.0f cv=%.2f"
            s t reason sco.Sufficiency sco.Necessity sco.LagMean sco.LagCv

    // arrowCalls 표시
    let callName id =
        match store.Calls.TryGetValue(id: Guid) with
        | true, c -> c.Name
        | _ -> "?"
    printfn ""
    printfn "Final arrowCalls (work 별):"
    let byWork =
        store.ArrowCalls.Values
        |> Seq.groupBy (fun a -> a.ParentId)
        |> Seq.toList
    for (workId, arrs) in byWork do
        let workName =
            match store.Works.TryGetValue workId with
            | true, w -> w.Name
            | _ -> "?"
        let arrows = arrs |> List.ofSeq
        printfn "  Work [%s] — %d arrows" workName arrows.Length
        for a in arrows do
            let kind = if a.ArrowType = Ds2.Core.ArrowType.Group then "Group" else "Start"
            printfn "    %s → %s  [%s]"
                (callName a.SourceId) (callName a.TargetId) kind

    // 저장 — Promaker UI 검증 가능
    let outDir = Path.Combine(demoDir, "out_v18_fsharp")
    Directory.CreateDirectory outDir |> ignore
    let sdfPath = Path.Combine(outDir, "DEMO_v18.sdf")
    Ds2.Serialization.JsonConverter.saveToFile sdfPath store
    printfn ""
    printfn "✓ Saved: %s" sdfPath

    // 기본 sanity:
    Assert.True(store.Calls.Count >= 10, "expect at least 10 calls")
    Assert.True(store.ArrowCalls.Count >= 5,
        sprintf "expect at least 5 arrowCalls; got %d" store.ArrowCalls.Count)
