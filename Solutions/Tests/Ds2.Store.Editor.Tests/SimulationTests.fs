module Ds2.Store.Editor.Tests.SimulationTests

open System
open System.Threading
open Xunit
open Ds2.Core
open Ds2.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers
open Ds2.Runtime.Sim.Engine
open Ds2.Runtime.Sim.Engine.Core
open Ds2.Runtime.Sim.Report

module ReportServiceTests =

    [<Fact>]
    let ``fromStateChanges groups entries and computes metadata`` () =
        let startTime = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        let endTime = startTime.AddSeconds(10)

        let records = [
            {
                NodeId = "work-1"
                NodeName = "Work1"
                NodeType = "Work"
                SystemId = "SystemA"
                State = "R"
                Timestamp = startTime
            }
            {
                NodeId = "call-1"
                NodeName = "Call1"
                NodeType = "Call"
                SystemId = "SystemA"
                State = "R"
                Timestamp = startTime.AddSeconds(1)
            }
            {
                NodeId = "work-1"
                NodeName = "Work1"
                NodeType = "Work"
                SystemId = "SystemA"
                State = "G"
                Timestamp = startTime.AddSeconds(2)
            }
        ]

        let report = ReportService.fromStateChanges startTime endTime records

        Assert.Equal(2, report.Entries.Length)
        Assert.Equal(1, report.Metadata.WorkCount)
        Assert.Equal(1, report.Metadata.CallCount)

        let workEntry = report.Entries |> List.find (fun entry -> entry.Id = "work-1")
        Assert.Equal(2, workEntry.Segments.Length)
        Assert.Equal(startTime.AddSeconds(2), workEntry.Segments[0].EndTime |> Option.defaultValue DateTime.MinValue)
        Assert.Equal(endTime, workEntry.Segments[1].EndTime |> Option.defaultValue DateTime.MinValue)

module SimIndexTests =

    [<Fact>]
    let ``build collects work and call predecessor maps`` () =
        let store = createStore ()
        let project, system, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id

        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work1.Id ], ArrowType.Reset) |> ignore
        store.AddCallsWithDevice(project.Id, work1.Id, [ "Dev.Api1"; "Dev.Api2" ], true)

        let callIds =
            DsQuery.callsOf work1.Id store
            |> List.map (fun call -> call.Id)

        store.ConnectSelectionInOrder(callIds, ArrowType.Start) |> ignore

        let index = SimIndex.build store 10

        Assert.Contains(system.Name, index.ActiveSystemNames)
        Assert.Equal<Guid list>(callIds, index.WorkCallGuids[work1.Id])
        Assert.Equal<Guid list>([ work1.Id ], index.WorkStartPreds[work2.Id])
        Assert.Equal<Guid list>([ work2.Id ], index.WorkResetPreds[work1.Id])
        Assert.Equal<Guid list>([ callIds[0] ], index.CallStartPreds[callIds[1]])

    [<Fact>]
    let ``build collects condition specs by call condition type`` () =
        let store = createStore ()
        let project, _, flow, work = setupBasicHierarchy store
        let rxWork = addWork store "RxWork" flow.Id

        store.AddCallsWithDevice(project.Id, work.Id, [ "Src.Api"; "Target.Api" ], true)

        let calls = DsQuery.callsOf work.Id store
        let sourceCall = calls[0]
        let targetCall = calls[1]
        let sourceApiCall = sourceCall.ApiCalls |> Seq.head

        let apiDefId = sourceApiCall.ApiDefId |> Option.defaultValue Guid.Empty
        let apiDef = store.ApiDefs[apiDefId]
        apiDef.Properties.RxGuid <- Some rxWork.Id

        store.AddCallCondition(targetCall.Id, CallConditionType.ComAux)
        let conditionId =
            store.Calls[targetCall.Id].CallConditions
            |> Seq.head
            |> fun condition -> condition.Id

        store.AddApiCallsToConditionBatch(targetCall.Id, conditionId, [ sourceApiCall.Id ]) |> ignore

        let index = SimIndex.build store 10
        let specs = index.CallComAuxConditions[targetCall.Id]

        Assert.Single(specs) |> ignore
        Assert.Equal(rxWork.Id, specs[0].RxWorkGuid)
        Assert.Equal(Some sourceApiCall.Id, specs[0].ApiCallGuid)

    [<Fact>]
    let ``build collects token role successor and sink maps`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id
        let work3 = addWork store "Work3" flow.Id

        store.UpdateWorkTokenRole(work1.Id, TokenRole.Source)
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work3.Id ], ArrowType.StartReset) |> ignore
        store.ConnectSelectionInOrder([ work3.Id; work1.Id ], ArrowType.Reset) |> ignore

        let index = SimIndex.build store 10

        // Source 워크 수집
        Assert.Equal<Guid list>([ work1.Id ], index.TokenSourceGuids)
        // TokenRole 매핑
        Assert.Equal(TokenRole.Source, index.WorkTokenRole[work1.Id])
        // wStartPreds 역전 방식 successor (Group 확장 포함)
        Assert.Equal<Guid list>([ work2.Id ], index.WorkTokenSuccessors[work1.Id])
        Assert.Equal<Guid list>([ work3.Id ], index.WorkTokenSuccessors[work2.Id])
        // Reset 화살표는 successor 아님
        Assert.False(index.WorkTokenSuccessors.ContainsKey work3.Id)
        // Sink 감지: work3는 successor 없음 → sink
        Assert.True(index.TokenSinkGuids.Contains work3.Id)
        // work1, work2는 successor 있음 → sink 아님
        Assert.False(index.TokenSinkGuids.Contains work1.Id)
        Assert.False(index.TokenSinkGuids.Contains work2.Id)

    [<Fact>]
    let ``build detects cycle sink in cyclic token graph`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id
        let work3 = addWork store "Work3" flow.Id

        store.UpdateWorkTokenRole(work1.Id, TokenRole.Source)
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work3.Id ], ArrowType.Start) |> ignore
        // 순환: work3 → work1
        store.ConnectSelectionInOrder([ work3.Id; work1.Id ], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10

        // work3→work1 back edge → work3(source)가 cycle sink
        Assert.True(index.TokenSinkGuids.Contains work3.Id)
        Assert.False(index.TokenSinkGuids.Contains work1.Id)
        Assert.False(index.TokenSinkGuids.Contains work2.Id)

    [<Fact>]
    let ``build detects cycle sink with Start arrow back to mid-chain`` () =
        // A→B(SR), B→C(SR), C→D(SR), D→B(Start) — D가 cycle sink이어야 함
        let store = createStore ()
        let _, _, flow, workA = setupBasicHierarchy store
        let workB = addWork store "WorkB" flow.Id
        let workC = addWork store "WorkC" flow.Id
        let workD = addWork store "WorkD" flow.Id

        store.UpdateWorkTokenRole(workA.Id, TokenRole.Source)
        store.ConnectSelectionInOrder([ workA.Id; workB.Id ], ArrowType.StartReset) |> ignore
        store.ConnectSelectionInOrder([ workB.Id; workC.Id ], ArrowType.StartReset) |> ignore
        store.ConnectSelectionInOrder([ workC.Id; workD.Id ], ArrowType.StartReset) |> ignore
        store.ConnectSelectionInOrder([ workD.Id; workB.Id ], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10

        // D→B back edge → D가 cycle sink
        Assert.True(index.TokenSinkGuids.Contains workD.Id, "D should be cycle sink")
        Assert.False(index.TokenSinkGuids.Contains workA.Id)
        Assert.False(index.TokenSinkGuids.Contains workB.Id)
        Assert.False(index.TokenSinkGuids.Contains workC.Id)
        // successor 확인: D→B가 tokenSuccMap에 있어야 함
        Assert.Contains(workB.Id, index.WorkTokenSuccessors[workD.Id])

    [<Fact>]
    let ``build with no token roles produces empty token maps`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10

        Assert.Empty(index.TokenSourceGuids)
        Assert.True(index.WorkTokenRole.IsEmpty)

module EventDrivenEngineTokenTests =

    let private waitUntil timeoutMs predicate =
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
        let mutable matched = predicate ()
        while not matched && DateTime.UtcNow < deadline do
            Thread.Sleep(10)
            matched <- predicate ()
        matched

    [<Fact>]
    let ``blocked token waits for successor ready before shifting`` () =
        // w1→w2(Start), w2→w3(Start), guard→w3(Start)
        // guard가 Ready이면 w3 canStartWork false → 자동 Going 안 됨
        let store = createStore ()
        let _, _, flow, w1 = setupBasicHierarchy store
        let w2 = addWork store "W2" flow.Id
        let w3 = addWork store "W3" flow.Id
        let guard = addWork store "Guard" flow.Id

        store.ConnectSelectionInOrder([ w1.Id; w2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ w2.Id; w3.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ guard.Id; w3.Id ], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10
        let engine = new EventDrivenEngine(index)

        try
            engine.Start()

            let token = engine.NextToken()
            engine.SeedToken(w2.Id, token)
            engine.ForceWorkState(w2.Id, Status4.Finish)

            // w3=Ready이지만 guard=Ready(Finish 아님) → w3 자동 Going 안 됨
            // w3=Ready + 토큰 없음 → canReceiveToken true → shift
            Assert.True(
                waitUntil 1000 (fun () ->
                    engine.GetWorkToken(w2.Id).IsNone
                    && engine.GetWorkToken(w3.Id) = Some token),
                "token should shift to Ready successor")

            // shift 후 w3는 여전히 Ready (guard 조건 미충족)
            Assert.Equal(Some Status4.Ready, engine.GetWorkState(w3.Id))
        finally
            engine.Stop()

    [<Fact>]
    let ``blocked token stays when successor is not ready`` () =
        let store = createStore ()
        let _, _, flow, w1 = setupBasicHierarchy store
        let w2 = addWork store "W2" flow.Id
        let w3 = addWork store "W3" flow.Id

        store.ConnectSelectionInOrder([ w1.Id; w2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ w2.Id; w3.Id ], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10
        let engine = new EventDrivenEngine(index)

        try
            engine.Start()

            // w3를 Finish로 → canReceiveToken false
            engine.ForceWorkState(w3.Id, Status4.Finish)
            Assert.True(
                waitUntil 1000 (fun () -> engine.GetWorkState(w3.Id) = Some Status4.Finish),
                "w3 should be Finish")

            let token = engine.NextToken()
            engine.SeedToken(w2.Id, token)
            engine.ForceWorkState(w2.Id, Status4.Finish)

            // w3=Finish → canReceiveToken false → blocked
            Assert.True(
                waitUntil 1000 (fun () ->
                    engine.GetWorkState(w2.Id) = Some Status4.Finish
                    && engine.GetWorkToken(w2.Id) = Some token),
                "token should stay blocked on source while successor is not Ready")
        finally
            engine.Stop()

    [<Fact>]
    let ``GraphValidator finds works without reset predecessors`` () =
        let store = createStore ()
        let _, _system, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id
        let work3 = addWork store "Work3" flow.Id

        // work1→work2 (Start), work2→work3 (Start)
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work3.Id ], ArrowType.Start) |> ignore
        // work3→work2 (Reset) — work2에만 reset 연결
        store.ConnectSelectionInOrder([ work3.Id; work2.Id ], ArrowType.Reset) |> ignore

        let index = SimIndex.build store 10
        let unreset = GraphValidator.findUnresetWorks index

        // work1은 sink(successor 없음)이 아니고 reset pred도 없음 → 경고 대상
        // work3는 sink (successor 없음) → 경고 대상 아님
        // work2는 reset pred 있음 → 경고 대상 아님
        let unresetGuids = unreset |> List.map (fun (g, _, _) -> g)
        Assert.Contains(work1.Id, unresetGuids)
        Assert.DoesNotContain(work2.Id, unresetGuids)
        Assert.DoesNotContain(work3.Id, unresetGuids)

    [<Fact>]
    let ``GraphValidator finds deadlock candidates in cyclic token path`` () =
        // A→B(SR), B→C(SR), C→D(SR), D→B(Start)
        // B.startPreds = [A, D], D는 B의 successor 체인 → 데드락 후보
        let store = createStore ()
        let _, _, flow, workA = setupBasicHierarchy store
        let workB = addWork store "WorkB" flow.Id
        let workC = addWork store "WorkC" flow.Id
        let workD = addWork store "WorkD" flow.Id

        store.UpdateWorkTokenRole(workA.Id, TokenRole.Source)
        store.ConnectSelectionInOrder([ workA.Id; workB.Id ], ArrowType.StartReset) |> ignore
        store.ConnectSelectionInOrder([ workB.Id; workC.Id ], ArrowType.StartReset) |> ignore
        store.ConnectSelectionInOrder([ workC.Id; workD.Id ], ArrowType.StartReset) |> ignore
        store.ConnectSelectionInOrder([ workD.Id; workB.Id ], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10
        let deadlocks = GraphValidator.findDeadlockCandidates index
        let deadlockGuids = deadlocks |> List.map (fun (g, _, _) -> g)

        // B.startPreds=[A,D], D는 B의 successor → 데드락
        Assert.Contains(workB.Id, deadlockGuids)
        // A, C, D는 해당 없음
        Assert.DoesNotContain(workA.Id, deadlockGuids)
        Assert.DoesNotContain(workC.Id, deadlockGuids)
        Assert.DoesNotContain(workD.Id, deadlockGuids)

    [<Fact>]
    let ``GraphValidator finds missing source candidates`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id

        // work1→work2 (Start) — work1은 startPreds 없음, Source도 아님 → 후보
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10
        let missing = GraphValidator.findMissingSources index
        let missingGuids = missing |> List.map (fun (g, _, _) -> g)

        Assert.Contains(work1.Id, missingGuids)
        // work2는 startPreds 있음 → 후보 아님
        Assert.DoesNotContain(work2.Id, missingGuids)

    [<Fact>]
    let ``GraphValidator excludes actual sources from missing sources`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id

        store.UpdateWorkTokenRole(work1.Id, TokenRole.Source)
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10
        let missing = GraphValidator.findMissingSources index
        let missingGuids = missing |> List.map (fun (g, _, _) -> g)

        // work1은 Source로 지정됨 → 후보 아님
        Assert.DoesNotContain(work1.Id, missingGuids)

    [<Fact>]
    let ``findSourceCandidates includes deadlock resolution candidates`` () =
        // A(Source)→B(SR)→C(SR)→D(SR)→B(Start)
        // B.startPreds=[A,D], D는 B의 정방향 descendant → D를 Source로 지정해야 데드락 해소
        let store = createStore ()
        let _, _, flow, workA = setupBasicHierarchy store
        let workB = addWork store "WorkB" flow.Id
        let workC = addWork store "WorkC" flow.Id
        let workD = addWork store "WorkD" flow.Id

        store.UpdateWorkTokenRole(workA.Id, TokenRole.Source)
        store.ConnectSelectionInOrder([ workA.Id; workB.Id ], ArrowType.StartReset) |> ignore
        store.ConnectSelectionInOrder([ workB.Id; workC.Id ], ArrowType.StartReset) |> ignore
        store.ConnectSelectionInOrder([ workC.Id; workD.Id ], ArrowType.StartReset) |> ignore
        store.ConnectSelectionInOrder([ workD.Id; workB.Id ], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10
        let candidates = GraphValidator.findSourceCandidates index
        let candidateGuids = candidates |> List.map (fun (g, _, _) -> g)

        // D: B의 정방향 descendant이면서 B의 startPred → Source 후보
        Assert.Contains(workD.Id, candidateGuids)
        // A는 이미 Source → 후보 아님
        Assert.DoesNotContain(workA.Id, candidateGuids)

    [<Fact>]
    let ``source based sink detects predecessor of Source as sink`` () =
        // A(Source)→B(SR)→C(SR)→D(Source)→B(Start)
        // C→D에서 D가 Source → C가 sink
        // D는 cycle sink이지만 Source이므로 sink 제외
        let store = createStore ()
        let _, _, flow, workA = setupBasicHierarchy store
        let workB = addWork store "WorkB" flow.Id
        let workC = addWork store "WorkC" flow.Id
        let workD = addWork store "WorkD" flow.Id

        store.UpdateWorkTokenRole(workA.Id, TokenRole.Source)
        store.UpdateWorkTokenRole(workD.Id, TokenRole.Source)
        store.ConnectSelectionInOrder([ workA.Id; workB.Id ], ArrowType.StartReset) |> ignore
        store.ConnectSelectionInOrder([ workB.Id; workC.Id ], ArrowType.StartReset) |> ignore
        store.ConnectSelectionInOrder([ workC.Id; workD.Id ], ArrowType.StartReset) |> ignore
        store.ConnectSelectionInOrder([ workD.Id; workB.Id ], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10

        // C: successor D가 Source → source-based sink
        Assert.True(index.TokenSinkGuids.Contains workC.Id, "C should be sink (predecessor of Source D)")
        // D: cycle sink이지만 Source → sink 제외
        Assert.False(index.TokenSinkGuids.Contains workD.Id, "D should not be sink (is Source)")
        // A: Source, sink 아님
        Assert.False(index.TokenSinkGuids.Contains workA.Id)
        // B: 중간 노드, sink 아님
        Assert.False(index.TokenSinkGuids.Contains workB.Id)
