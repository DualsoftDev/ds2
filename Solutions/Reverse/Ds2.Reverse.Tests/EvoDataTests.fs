module Ds2.Reverse.Tests.EvoDataTests

open System
open System.IO
open System.Text.Json
open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

/// EVO arrows.json schema (richer than DEMO — includes parent_work hint).
type EvoArrowDoc = {
    arrows: EvoArrow[]
    activeFlowToSysName: System.Collections.Generic.Dictionary<string, string>
}
and EvoArrow = {
    src: string
    tgt: string
    kind: string
    scope: string
    parent_work: string                // 있을 수도 없을 수도 — JSON nullable 로
}

type EvoCaptureDoc = {
    callEvents: EvoCallEvent[]
    durationMs: int64
}
and EvoCallEvent = {
    t: int64
    name: string
    next_: string
}

let private deserialize<'T> (path: string) : 'T =
    let options =
        JsonSerializerOptions(
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip)
    JsonSerializer.Deserialize<'T>(File.ReadAllText path, options)

[<Fact>]
let ``EVO ***REDACTED*** real data — algorithm verification`` () =
    let dir = @"D:\dstest\kwangmyeongEVO"
    let arrowsPath = Path.Combine(dir, "***REDACTED***EVO_260514A.arrows.json")
    let capturePath = Path.Combine(dir, "***REDACTED***EVO_260514A.capture.json")

    Assert.True(File.Exists arrowsPath)
    Assert.True(File.Exists capturePath)

    let arrowsDoc = deserialize<EvoArrowDoc> arrowsPath
    let capDoc = deserialize<EvoCaptureDoc> capturePath

    // in-active-work arrows → candidates
    let inActive = arrowsDoc.arrows |> Array.filter (fun a -> a.scope = "in-active-work")
    let crossFlow = arrowsDoc.arrows |> Array.filter (fun a -> a.scope = "cross-flow")

    // parent_work 가 있는 arrows 에서 (flow, work, raw_call) tuples 추출 →
    // unique call name = "{flow}.{work}::{raw_call}" 으로 multi-instance 처리.
    let workCalls = System.Collections.Generic.Dictionary<string * string, System.Collections.Generic.HashSet<string>>()
    let parentWorkOf = System.Collections.Generic.Dictionary<EvoArrow, (string * string) option>()
    let addToWork (flow: string) (work: string) (call: string) =
        let key = flow, work
        let set =
            match workCalls.TryGetValue key with
            | true, s -> s
            | _ -> let s = System.Collections.Generic.HashSet() in workCalls.[key] <- s; s
        set.Add call |> ignore
    for a in inActive do
        let pwOpt =
            if String.IsNullOrEmpty a.parent_work then None
            else
                let parts = a.parent_work.Split('.')
                if parts.Length >= 2 then
                    Some(parts.[0], String.Join('.', parts.[1..]))
                else None
        parentWorkOf.[a] <- pwOpt
        match pwOpt with
        | Some (flow, work) ->
            addToWork flow work a.src
            addToWork flow work a.tgt
        | None -> ()
    // Cross-flow arrow 의 src/tgt 도 work 정의로 보강 (CARTYPE 등 in-active 에 없는 flow 인식).
    // src/tgt 형식 "{flow}.{work}" — 그 work 를 단일 call (자기자신) 으로 등록.
    for a in crossFlow do
        let parseRef (s: string) =
            match s.IndexOf '.' with
            | -1 -> None
            | i -> Some (s.Substring(0, i), s.Substring(i + 1))
        match parseRef a.src with
        | Some (flow, work) -> addToWork flow work a.src
        | None -> ()
        match parseRef a.tgt with
        | Some (flow, work) -> addToWork flow work a.tgt
        | None -> ()

    // Unique call name = "{flow}::{work}::{rawCall}" — multi-instance 구분.
    let uniqueOf (flow: string) (work: string) (raw: string) = $"{flow}::{work}::{raw}"

    // FlowCalls: 각 flow → unique call names list
    let flowCalls =
        workCalls
        |> Seq.collect (fun kv ->
            let (flow, work) = kv.Key
            kv.Value |> Seq.map (fun raw -> flow, uniqueOf flow work raw))
        |> Seq.groupBy fst
        |> Seq.map (fun (flow, items) ->
            flow,
            items |> Seq.map (fun (_, uName) -> uName, "") |> List.ofSeq |> List.distinct)
        |> Map.ofSeq

    // WorkAssignments: uniqueName → (flow, work)
    let workAssignments =
        workCalls
        |> Seq.collect (fun kv ->
            let (flow, work) = kv.Key
            kv.Value |> Seq.map (fun raw -> uniqueOf flow work raw, (flow, work)))
        |> Map.ofSeq

    // Capture events — 모든 raw call name 의 events 가 모든 unique instance 에 동일하게 적용.
    // (multi-instance 가 동일 timing 공유 가정. EVO 의 같은 alias.local 의 다른 station instance.)
    let rawEvents =
        capDoc.callEvents
        |> Array.filter (fun e -> e.next_ = "Going")
        |> Array.map (fun e -> e.t, e.name)
    // Build events for each unique call (replicate timing for each instance)
    let events =
        [ for kv in workAssignments do
            // raw = uniqueName 의 "::"  뒤 부분
            let parts = (kv.Key: string).Split([|"::"|], System.StringSplitOptions.None)
            let raw = parts.[parts.Length - 1]
            for (t, n) in rawEvents do
                if n = raw then
                    yield { T = t; Name = kv.Key } ]

    // Candidates — in-active arrows 의 src/tgt 를 그 arrow 의 parent_work 의 unique name 으로 변환
    let candidates =
        inActive
        |> Array.choose (fun a ->
            match parentWorkOf.[a] with
            | Some (flow, work) ->
                let su = uniqueOf flow work a.src
                let tu = uniqueOf flow work a.tgt
                // 둘 다 workCalls 에 있어야
                if workCalls.[(flow, work)].Contains a.src
                   && workCalls.[(flow, work)].Contains a.tgt then
                    Some { Src = su; Tgt = tu; DeclaredKind = a.kind }
                else None
            | None -> None)
        |> List.ofArray

    let crossFlowCands =
        crossFlow
        |> Array.map (fun a -> { Src = a.src; Tgt = a.tgt; DeclaredKind = a.kind })
        |> List.ofArray

    let cfg =
        CausationConfig.defaults
        |> CausationConfig.withCycleHint 60000L   // EVO 사이클 ~60s 추정

    let baseInput =
        ReverseEngine.mkInput
            "***REDACTED***EVO_v18"
            "NewSystem"
            flowCalls
            candidates
            events
            cfg
    let input =
        { baseInput with
            CrossFlowCandidates = crossFlowCands
            WorkAssignments = workAssignments }

    let store, report = ReverseEngine.run input

    printfn "════ ***REDACTED***EVO Real Data Verification ════"
    printfn "Events: %d (duration=%dms)" events.Length capDoc.durationMs
    printfn "In-active-work arrows: %d, Cross-flow: %d" inActive.Length crossFlow.Length
    printfn "Flows: %d, Works (workCalls): %d" flowCalls.Count workCalls.Count
    printfn ""
    printfn "Detection Report:"
    printfn "  Total candidates : %d" report.TotalCandidates
    printfn "  Passed Sequential: %d" report.PassedSeq
    printfn "  Passed Group     : %d" report.PassedGrp
    printfn "  Dropped (gate)   : %d" report.DroppedCausation
    printfn "  Removed cycle    : %d" report.RemovedCycle
    printfn "  Removed transitive: %d" report.RemovedTransitive
    printfn "  Group dup removed: %d" report.RemovedGroupDup
    printfn "  Final arrowCalls : %d" report.FinalArrowCount
    printfn ""
    printfn "Store: Flows=%d, Works=%d, Calls=%d, ApiDefs=%d, arrowCalls=%d, arrowWorks=%d"
        store.Flows.Count store.Works.Count store.Calls.Count
        store.ApiDefs.Count store.ArrowCalls.Count store.ArrowWorks.Count

    // 저장
    let outDir = Path.Combine(dir, "out_v18_fsharp")
    Directory.CreateDirectory outDir |> ignore
    let sdfPath = Path.Combine(outDir, "EVO_v18.sdf")
    Ds2.Serialization.JsonConverter.saveToFile sdfPath store
    printfn ""
    printfn "✓ Saved: %s" sdfPath

    // sanity
    Assert.True(store.Calls.Count >= 50,
        sprintf "expect at least 50 calls; got %d" store.Calls.Count)
    Assert.True(store.ArrowCalls.Count >= 20,
        sprintf "expect at least 20 arrowCalls; got %d" store.ArrowCalls.Count)


/// EVO with autoTune comparison — 같은 데이터를 autoTune ON 으로 다시 실행하고 결과 비교.
[<Fact>]
let ``EVO autoTune comparison — autoTune ON vs OFF`` () =
    let dir = @"D:\dstest\kwangmyeongEVO"
    let arrowsPath = Path.Combine(dir, "***REDACTED***EVO_260514A.arrows.json")
    let capturePath = Path.Combine(dir, "***REDACTED***EVO_260514A.capture.json")

    // 데이터 없으면 skip
    if not (File.Exists arrowsPath && File.Exists capturePath) then
        Assert.True true   // skip silently
    else

    let arrowsDoc = deserialize<EvoArrowDoc> arrowsPath
    let capDoc = deserialize<EvoCaptureDoc> capturePath
    let inActive = arrowsDoc.arrows |> Array.filter (fun a -> a.scope = "in-active-work")
    let crossFlow = arrowsDoc.arrows |> Array.filter (fun a -> a.scope = "cross-flow")

    // 간소화된 빌드: 첫 번째 fact 와 동일하지만 압축
    let workCalls = System.Collections.Generic.Dictionary<string * string, System.Collections.Generic.HashSet<string>>()
    let parentWorkOf = System.Collections.Generic.Dictionary<EvoArrow, (string * string) option>()
    let addToWork (flow: string) (work: string) (call: string) =
        let key = flow, work
        let set =
            match workCalls.TryGetValue key with
            | true, s -> s
            | _ -> let s = System.Collections.Generic.HashSet() in workCalls.[key] <- s; s
        set.Add call |> ignore
    for a in inActive do
        let pwOpt =
            if String.IsNullOrEmpty a.parent_work then None
            else
                let parts = a.parent_work.Split('.')
                if parts.Length >= 2 then Some(parts.[0], String.Join('.', parts.[1..]))
                else None
        parentWorkOf.[a] <- pwOpt
        match pwOpt with
        | Some (flow, work) -> addToWork flow work a.src; addToWork flow work a.tgt
        | None -> ()
    for a in crossFlow do
        let parseRef (s: string) =
            match s.IndexOf '.' with
            | -1 -> None
            | i -> Some (s.Substring(0, i), s.Substring(i + 1))
        match parseRef a.src with
        | Some (flow, work) -> addToWork flow work a.src
        | None -> ()
        match parseRef a.tgt with
        | Some (flow, work) -> addToWork flow work a.tgt
        | None -> ()

    let uniqueOf (flow: string) (work: string) (raw: string) = $"{flow}::{work}::{raw}"
    let flowCalls =
        workCalls
        |> Seq.collect (fun kv ->
            let (flow, work) = kv.Key
            kv.Value |> Seq.map (fun raw -> flow, uniqueOf flow work raw))
        |> Seq.groupBy fst
        |> Seq.map (fun (flow, items) ->
            flow,
            items |> Seq.map (fun (_, uName) -> uName, "") |> List.ofSeq |> List.distinct)
        |> Map.ofSeq
    let workAssignments =
        workCalls
        |> Seq.collect (fun kv ->
            let (flow, work) = kv.Key
            kv.Value |> Seq.map (fun raw -> uniqueOf flow work raw, (flow, work)))
        |> Map.ofSeq
    let rawEvents =
        capDoc.callEvents
        |> Array.filter (fun e -> e.next_ = "Going")
        |> Array.map (fun e -> e.t, e.name)
    let events =
        [ for kv in workAssignments do
            let parts = (kv.Key: string).Split([|"::"|], System.StringSplitOptions.None)
            let raw = parts.[parts.Length - 1]
            for (t, n) in rawEvents do
                if n = raw then yield { T = t; Name = kv.Key } ]
    let candidates =
        inActive
        |> Array.choose (fun a ->
            match parentWorkOf.[a] with
            | Some (flow, work) ->
                let su = uniqueOf flow work a.src
                let tu = uniqueOf flow work a.tgt
                if workCalls.[(flow, work)].Contains a.src
                   && workCalls.[(flow, work)].Contains a.tgt then
                    Some { Src = su; Tgt = tu; DeclaredKind = a.kind }
                else None
            | None -> None)
        |> List.ofArray
    let crossFlowCands =
        crossFlow
        |> Array.map (fun a -> { Src = a.src; Tgt = a.tgt; DeclaredKind = a.kind })
        |> List.ofArray
    let cfg = CausationConfig.withCycleHint 60000L CausationConfig.defaults

    let mkInputBase () =
        let baseInput =
            ReverseEngine.mkInput "EVO_compare" "NewSystem" flowCalls candidates events cfg
        { baseInput with
            CrossFlowCandidates = crossFlowCands
            WorkAssignments = workAssignments }

    let inputOff = { mkInputBase () with AutoTuneThreshold = false }
    let inputOn = { mkInputBase () with AutoTuneThreshold = true }

    let storeOff, reportOff = ReverseEngine.run inputOff
    let storeOn, reportOn = ReverseEngine.run inputOn

    printfn "════ EVO autoTune comparison ════"
    printfn "Noise level (estimated): %.3f" reportOn.NoiseLevel
    printfn "ArrowCalls — OFF: %d, ON: %d" storeOff.ArrowCalls.Count storeOn.ArrowCalls.Count
    printfn "Passed seq — OFF: %d, ON: %d" reportOff.PassedSeq reportOn.PassedSeq
    printfn "Anomalous cycles — OFF: %d, ON: %d"
        reportOff.AnomalousCycles.Count reportOn.AnomalousCycles.Count

    // 회귀: autoTune ON 이 OFF 대비 detection 결과가 합리적 범위 (50% ~ 400%) 안에 있어야.
    // EVO 는 매우 noisy (cycle 길이가 길어 noise level=1.0 추정) → autoTune ON 이 더 관대.
    let offCount = storeOff.ArrowCalls.Count
    let onCount = storeOn.ArrowCalls.Count
    Assert.True(onCount >= offCount / 2 && onCount <= offCount * 4,
        sprintf "autoTune ON arrowCalls=%d, OFF=%d — 비율 비정상 (예상 50~400%%)"
            onCount offCount)
    // autoTune 은 더 많은 arrow 를 emit 함을 확인 (noisy data 에서 더 관대)
    Assert.True(onCount >= offCount,
        sprintf "noisy EVO 에서 autoTune ON 이 OFF 보다 많거나 같아야: ON=%d OFF=%d" onCount offCount)
