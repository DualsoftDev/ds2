module Ds2.Store.Editor.Tests.SimulationTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Model
open Ds2.Runtime.Report

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
        store.AddCallsWithDevice(project.Id, work1.Id, [ "Dev.Api1"; "Dev.Api2" ], true, None)

        let callIds =
            Queries.callsOf work1.Id store
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

        store.AddCallsWithDevice(project.Id, work.Id, [ "Src.Api"; "Target.Api" ], true, None)

        let calls = Queries.callsOf work.Id store
        let sourceCall = calls[0]
        let targetCall = calls[1]
        let sourceApiCall = sourceCall.ApiCalls |> Seq.head

        let apiDefId = sourceApiCall.ApiDefId |> Option.defaultValue Guid.Empty
        let apiDef = store.ApiDefs[apiDefId]
        apiDef.RxGuid <- Some rxWork.Id

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
    let ``build collects token role successor and manual sink`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id
        let work3 = addWork store "Work3" flow.Id

        store.UpdateWorkTokenRole(work1.Id, TokenRole.Source)
        store.UpdateWorkTokenRole(work3.Id, TokenRole.Sink)
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work3.Id ], ArrowType.StartReset) |> ignore

        let index = SimIndex.build store 10

        Assert.Equal<Guid list>([ work1.Id ], index.TokenSourceGuids)
        Assert.Equal(TokenRole.Source, index.WorkTokenRole[work1.Id])
        Assert.Equal(TokenRole.Sink, index.WorkTokenRole[work3.Id])
        Assert.Equal<Guid list>([ work2.Id ], index.WorkTokenSuccessors[work1.Id])
        Assert.Equal<Guid list>([ work3.Id ], index.WorkTokenSuccessors[work2.Id])
        // Sink는 수동 지정: work3만 sink
        Assert.True(index.TokenSinkGuids.Contains work3.Id)
        Assert.False(index.TokenSinkGuids.Contains work1.Id)
        Assert.False(index.TokenSinkGuids.Contains work2.Id)

    [<Fact>]
    let ``build without manual sink produces empty TokenSinkGuids`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id

        store.UpdateWorkTokenRole(work1.Id, TokenRole.Source)
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10

        // Sink 수동 지정 없으면 비어있음
        Assert.True(index.TokenSinkGuids.IsEmpty)

    [<Fact>]
    let ``build canonicalizes token source guid for reference works`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store
        let refId = store.AddReferenceWork(work.Id)

        store.UpdateWorkTokenRole(refId, TokenRole.Source)

        let index = SimIndex.build store 10

        Assert.Equal<Guid list>([ work.Id ], index.TokenSourceGuids)
        Assert.Equal(TokenRole.Source, index.WorkTokenRole[work.Id])
        Assert.Equal(TokenRole.Source, index.WorkTokenRole[refId])

    [<Fact>]
    let ``build collects WorkFlowGuid and AllFlowGuids`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id

        let index = SimIndex.build store 10

        Assert.Contains(flow.Id, index.AllFlowGuids)
        Assert.Equal(flow.Id, index.WorkFlowGuid[work1.Id])
        Assert.Equal(flow.Id, index.WorkFlowGuid[work2.Id])

    [<Fact>]
    let ``build with no token roles produces empty token maps`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10

        Assert.Empty(index.TokenSourceGuids)
        Assert.True(index.WorkTokenRole.IsEmpty)

    [<Fact>]
    let ``GraphValidator finds works without reset predecessors using manual sink`` () =
        let store = createStore ()
        let _, _system, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id
        let work3 = addWork store "Work3" flow.Id

        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work3.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work3.Id; work2.Id ], ArrowType.Reset) |> ignore
        // work3를 Sink로 수동 지정
        store.UpdateWorkTokenRole(work3.Id, TokenRole.Sink)

        let index = SimIndex.build store 10
        let unreset = GraphValidator.findUnresetWorks index

        // work1: sink 아니고 reset pred 없음 → 경고 대상
        // work3: Sink 지정 → 경고 대상 아님
        // work2: reset pred 있음 → 경고 대상 아님
        let unresetGuids = unreset |> List.map (fun (g, _, _) -> g)
        Assert.Contains(work1.Id, unresetGuids)
        Assert.DoesNotContain(work2.Id, unresetGuids)
        Assert.DoesNotContain(work3.Id, unresetGuids)

    [<Fact>]
    let ``build includes reference calls in AllCallGuids with original data`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
        let originalCall = Queries.callsOf work.Id store |> List.head
        let refId = store.AddReferenceCall(originalCall.Id)

        let index = SimIndex.build store 10

        // 레퍼런스 Call이 AllCallGuids에 포함
        Assert.Contains(refId, index.AllCallGuids)
        Assert.Contains(originalCall.Id, index.AllCallGuids)
        // 레퍼런스 Call의 CallWorkGuid가 원본과 동일한 Work를 가리킴
        Assert.Equal(index.CallWorkGuid[originalCall.Id], index.CallWorkGuid[refId])
        // OR 그룹 형성 확인
        let group = SimIndex.callReferenceGroupOf index originalCall.Id
        Assert.Contains(originalCall.Id, group)
        Assert.Contains(refId, group)
        Assert.Equal(2, group.Length)

module StepModeTests =

    [<Fact>]
    let ``STEP mode: repeated step after pause at work start eventually reaches successor work`` () =
        let store = createStore ()
        let project, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id

        store.UpdateWorkTokenRole(work1.Id, TokenRole.Source)
        store.UpdateWorkPeriodMs(work1.Id, Some 2000)
        store.UpdateWorkPeriodMs(work2.Id, Some 2000)
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.StartReset) |> ignore
        store.AddCallsWithDevice(project.Id, work1.Id, [ "Dev.Api1"; "Dev.Api2" ], true, None)

        let index = SimIndex.build store 10
        let engine = new EventDrivenEngine(index, RuntimeMode.Simulation)

        let dumpState () =
            let w1 = engine.GetWorkState(work1.Id)
            let w2 = engine.GetWorkState(work2.Id)
            let w1Token = engine.GetWorkToken(work1.Id)
            let w2Token = engine.GetWorkToken(work2.Id)
            let calls =
                SimIndex.findOrEmpty work1.Id index.WorkCallGuids
                |> List.map (fun callGuid -> engine.GetCallState(callGuid))
            $"W1={w1}, W2={w2}, W1tok={w1Token}, W2tok={w2Token}, Calls={calls}, HSW={engine.HasStartableWork}, HAD={engine.HasActiveDuration}"

        try
            engine.Start()

            let token = engine.NextToken()
            engine.SeedToken(work1.Id, token)

            Assert.True(
                waitUntil 2000 (fun () -> engine.GetWorkState(work1.Id) = Some Status4.Going),
                $"Work1 should start before pause. {dumpState ()}")

            engine.SetAllFlowStates(FlowTag.Pause)

            let mutable work2Going = false
            let mutable step = 0
            let mutable lastState = dumpState ()

            while not work2Going && step < 16 do
                step <- step + 1
                let progressed = engine.Step()
                lastState <- $"Step {step}: progressed={progressed}, {dumpState ()}"
                work2Going <- engine.GetWorkState(work2.Id) = Some Status4.Going

            Assert.True(work2Going, $"Work2 should become Going after repeated STEP. Last={lastState}")
        finally
            engine.Stop()

    [<Fact>]
    let ``STEP mode: terminal finish leaves no further step work`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store

        store.UpdateWorkTokenRole(work.Id, TokenRole.Source)
        store.UpdateWorkPeriodMs(work.Id, Some 1)

        let index = SimIndex.build store 10
        let engine = new EventDrivenEngine(index, RuntimeMode.Simulation)

        try
            engine.Start()
            engine.SetAllFlowStates(FlowTag.Pause)

            let token = engine.NextToken()
            engine.SeedToken(work.Id, token)

            let mutable guard = 0
            while (engine.HasStartableWork || engine.HasActiveDuration) && guard < 8 do
                guard <- guard + 1
                engine.Step() |> ignore

            Assert.Equal(Some Status4.Finish, engine.GetWorkState(work.Id))
            Assert.False(engine.HasStartableWork, "terminal finish should not leave any startable work")
            Assert.False(engine.HasActiveDuration, "terminal finish should not leave any active duration")
            Assert.False(engine.Step(), "terminal finish should not advance any further")
        finally
            engine.Stop()

    [<Fact>]
    let ``pause resume and restart reuse a clean engine thread lifecycle`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store

        store.UpdateWorkTokenRole(work.Id, TokenRole.Source)

        let index = SimIndex.build store 10
        let engine = new EventDrivenEngine(index, RuntimeMode.Simulation)

        try
            engine.Start()
            Assert.Equal(SimulationStatus.Running, engine.Status)

            engine.Pause()
            Assert.Equal(SimulationStatus.Paused, engine.Status)

            engine.Resume()
            Assert.Equal(SimulationStatus.Running, engine.Status)

            engine.Stop()
            Assert.Equal(SimulationStatus.Stopped, engine.Status)

            engine.Start()
            Assert.Equal(SimulationStatus.Running, engine.Status)
        finally
            engine.Stop()

module EventDrivenEngineTokenTests =

    [<Fact>]
    let ``reference work shares token and runtime state with original work`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store
        let refId = store.AddReferenceWork(work.Id)

        store.UpdateWorkTokenRole(work.Id, TokenRole.Source)
        store.UpdateWorkPeriodMs(work.Id, Some 2000)

        let index = SimIndex.build store 10
        let engine = new EventDrivenEngine(index, RuntimeMode.Simulation)

        try
            let token = engine.NextToken()
            engine.SeedToken(work.Id, token)

            Assert.Equal(Some token, engine.GetWorkToken(work.Id))
            Assert.Equal(Some token, engine.GetWorkToken(refId))

            engine.Start()
            engine.ForceWorkState(work.Id, Status4.Going)
            Assert.True(
                waitUntil 1000 (fun () ->
                    engine.GetWorkState(work.Id) = Some Status4.Going
                    && engine.GetWorkState(refId) = Some Status4.Going),
                "reference group should share Going state")

            engine.ForceWorkState(work.Id, Status4.Finish)
            Assert.True(
                waitUntil 1000 (fun () ->
                    engine.GetWorkState(work.Id) = Some Status4.Finish
                    && engine.GetWorkState(refId) = Some Status4.Finish),
                "reference group should share Finish state")

            Assert.Equal(engine.GetWorkToken(work.Id), engine.GetWorkToken(refId))
        finally
            engine.Stop()


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
        let engine = new EventDrivenEngine(index, RuntimeMode.Simulation)

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
        let engine = new EventDrivenEngine(index, RuntimeMode.Simulation)

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
    let ``reverse Call order with SkipIfCompleted completes normally`` () =
        let store = createStore ()
        let project = addProject store "P"
        let activeSys = addSystem store "Active" project.Id true
        let flow = addFlow store "F" activeSys.Id
        let work = addWork store "W" flow.Id
        work.Duration <- Some (System.TimeSpan.FromMilliseconds 100.)
        store.UpdateWorkTokenRole(work.Id, TokenRole.Source)

        // Device System: ADV, RET (RET에 IsFinished=true)
        let deviceSys = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DF" deviceSys.Id
        let advWork = addWork store "ADV" deviceFlow.Id
        advWork.Duration <- Some (System.TimeSpan.FromMilliseconds 100.)
        let retWork = addWork store "RET" deviceFlow.Id
        retWork.Duration <- Some (System.TimeSpan.FromMilliseconds 100.)
        let retProps = SimulationWorkProperties()
        retProps.IsFinished <- true
        retWork.SetSimulationProperties(retProps)

        let advApiDef = addApiDef store "ADV" deviceSys.Id
        advApiDef.TxGuid <- Some advWork.Id
        advApiDef.RxGuid <- Some advWork.Id
        let retApiDef = addApiDef store "RET" deviceSys.Id
        retApiDef.TxGuid <- Some retWork.Id
        retApiDef.RxGuid <- Some retWork.Id

        // Call 2개: Device.RET, Device.ADV (역순 화살표)
        let retCallId = store.AddCallWithLinkedApiDefs(work.Id, "Device", "RET", [retApiDef.Id])
        let advCallId = store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [advApiDef.Id])
        store.ConnectSelectionInOrder([retCallId; advCallId], ArrowType.Start) |> ignore

        // SkipIfCompleted로 설정 → IsFinished인 RET도 바로 Complete
        let retCallProps = SimulationCallProperties()
        retCallProps.CallType <- CallType.SkipIfCompleted
        store.Calls.[retCallId].SetSimulationProperties(retCallProps)
        let advCallProps = SimulationCallProperties()
        advCallProps.CallType <- CallType.SkipIfCompleted
        store.Calls.[advCallId].SetSimulationProperties(advCallProps)

        let index = SimIndex.build store 10
        use engine = new EventDrivenEngine(index, RuntimeMode.Simulation)
        let sim = engine :> ISimulationEngine

        sim.SpeedMultiplier <- 100.0
        sim.ApplyInitialStates()

        let token = sim.NextToken()
        sim.SeedToken(work.Id, token)

        sim.Start()

        // Work가 Going을 거쳐 Finish/Homing/Ready까지 가는지 확인
        let completed = waitUntil 5000 (fun () ->
            sim.GetWorkState(work.Id)
            |> Option.exists (fun s -> s = Status4.Finish || s = Status4.Homing || s = Status4.Ready))

        sim.Stop()

        let workState = sim.GetWorkState(work.Id)
        let retCallState = sim.GetCallState(retCallId)
        let advCallState = sim.GetCallState(advCallId)
        printfn $"  Work={workState}, RET Call={retCallState}, ADV Call={advCallState}"

        Assert.True(completed, $"SkipIfCompleted should prevent stuck Going. State={workState}")

module AutoHomingOriginTests =

    /// Device 2개(Func1, Func2) 상호 리셋, Call 순서 Func1→Func2 → Func2=On(Finish), Func1=Off
    [<Fact>]
    let ``simple ADV RET pattern: descendant RxWork is On`` () =
        let store = createStore ()
        let project = addProject store "P"
        let activeSys = addSystem store "Active" project.Id true
        let flow = addFlow store "F" activeSys.Id
        let work = addWork store "W" flow.Id

        // Device: ADV, RET (상호 리셋)
        let deviceSys = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DF" deviceSys.Id
        let advWork = addWork store "ADV" deviceFlow.Id
        let retWork = addWork store "RET" deviceFlow.Id
        let advApiDef = addApiDef store "ADV" deviceSys.Id
        advApiDef.TxGuid <- Some advWork.Id
        advApiDef.RxGuid <- Some advWork.Id
        let retApiDef = addApiDef store "RET" deviceSys.Id
        retApiDef.TxGuid <- Some retWork.Id
        retApiDef.RxGuid <- Some retWork.Id

        // Active Work의 Call: ADV → RET (Start 화살표)
        let advCallId = store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [advApiDef.Id])
        let retCallId = store.AddCallWithLinkedApiDefs(work.Id, "Device", "RET", [retApiDef.Id])
        store.ConnectSelectionInOrder([advCallId; retCallId], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10
        let targets = SimIndex.computeAutoHomingTargets index

        // ADV = ancestor(Off), RET = descendant(On/Finish)
        Assert.Contains(retWork.Id, targets)
        Assert.DoesNotContain(advWork.Id, targets)

    /// Call 순서 RET→ADV (역순): ADV=descendant(On), RET=ancestor(Off)
    [<Fact>]
    let ``reverse order RET then ADV: ADV RxWork is On`` () =
        let store = createStore ()
        let project = addProject store "P"
        let activeSys = addSystem store "Active" project.Id true
        let flow = addFlow store "F" activeSys.Id
        let work = addWork store "W" flow.Id

        let deviceSys = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DF" deviceSys.Id
        let advWork = addWork store "ADV" deviceFlow.Id
        let retWork = addWork store "RET" deviceFlow.Id
        let advApiDef = addApiDef store "ADV" deviceSys.Id
        advApiDef.TxGuid <- Some advWork.Id; advApiDef.RxGuid <- Some advWork.Id
        let retApiDef = addApiDef store "RET" deviceSys.Id
        retApiDef.TxGuid <- Some retWork.Id; retApiDef.RxGuid <- Some retWork.Id

        // Call 순서: RET → ADV
        let retCallId = store.AddCallWithLinkedApiDefs(work.Id, "Device", "RET", [retApiDef.Id])
        let advCallId = store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [advApiDef.Id])
        store.ConnectSelectionInOrder([retCallId; advCallId], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10
        let targets = SimIndex.computeAutoHomingTargets index

        Assert.Contains(advWork.Id, targets)
        Assert.DoesNotContain(retWork.Id, targets)

    /// SkipIfCompleted Call도 원위치 계산에는 참여 (실행 시만 스킵)
    [<Fact>]
    let ``SkipIfCompleted call still participates in auto homing calculation`` () =
        let store = createStore ()
        let project = addProject store "P"
        let activeSys = addSystem store "Active" project.Id true
        let flow = addFlow store "F" activeSys.Id
        let work = addWork store "W" flow.Id

        let deviceSys = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DF" deviceSys.Id
        let advWork = addWork store "ADV" deviceFlow.Id
        let retWork = addWork store "RET" deviceFlow.Id
        let advApiDef = addApiDef store "ADV" deviceSys.Id
        advApiDef.TxGuid <- Some advWork.Id; advApiDef.RxGuid <- Some advWork.Id
        let retApiDef = addApiDef store "RET" deviceSys.Id
        retApiDef.TxGuid <- Some retWork.Id; retApiDef.RxGuid <- Some retWork.Id

        // Call 순서: RET(SkipIfCompleted) → ADV
        let retCallId = store.AddCallWithLinkedApiDefs(work.Id, "Device", "RET", [retApiDef.Id])
        let advCallId = store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [advApiDef.Id])
        store.ConnectSelectionInOrder([retCallId; advCallId], ArrowType.Start) |> ignore
        let retCallProps = SimulationCallProperties()
        retCallProps.CallType <- CallType.SkipIfCompleted
        store.Calls.[retCallId].SetSimulationProperties(retCallProps)

        let index = SimIndex.build store 10
        let targets = SimIndex.computeAutoHomingTargets index

        // SkipIfCompleted여도 투표에 참여 → ADV(descendant)=On 대상
        Assert.Contains(advWork.Id, targets)

    /// 병렬 브랜치에서 같은 Device Call → NotCare (투표 안 함)
    [<Fact>]
    let ``parallel branches same device results in no auto homing target`` () =
        let store = createStore ()
        let project = addProject store "P"
        let activeSys = addSystem store "Active" project.Id true
        let flow = addFlow store "F" activeSys.Id
        let work = addWork store "W" flow.Id

        let deviceSys = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DF" deviceSys.Id
        let advWork = addWork store "ADV" deviceFlow.Id
        let retWork = addWork store "RET" deviceFlow.Id
        let advApiDef = addApiDef store "ADV" deviceSys.Id
        advApiDef.TxGuid <- Some advWork.Id; advApiDef.RxGuid <- Some advWork.Id
        let retApiDef = addApiDef store "RET" deviceSys.Id
        retApiDef.TxGuid <- Some retWork.Id; retApiDef.RxGuid <- Some retWork.Id

        // ADV와 RET가 병렬 (Start 화살표 없음)
        let _advCallId = store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [advApiDef.Id])
        let _retCallId = store.AddCallWithLinkedApiDefs(work.Id, "Device", "RET", [retApiDef.Id])
        // 연결 안 함 → 병렬

        let index = SimIndex.build store 10
        let targets = SimIndex.computeAutoHomingTargets index

        // 병렬이라 ancestorOf = None → 투표 안 함 → 대상 없음
        Assert.Empty(targets)

    [<Fact>]
    let ``conflicting on off votes exclude device work from auto homing plan`` () =
        let store = createStore ()
        let project = addProject store "P"
        let activeSys = addSystem store "Active" project.Id true
        let flow = addFlow store "F" activeSys.Id
        let work = addWork store "W" flow.Id

        let deviceSys = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DF" deviceSys.Id
        let setWork = addWork store "SET" deviceFlow.Id

        let setApiDef1 = addApiDef store "SET_1" deviceSys.Id
        setApiDef1.TxGuid <- Some setWork.Id
        setApiDef1.RxGuid <- Some setWork.Id

        let setApiDef2 = addApiDef store "SET_2" deviceSys.Id
        setApiDef2.TxGuid <- Some setWork.Id
        setApiDef2.RxGuid <- Some setWork.Id

        let setCall1 = store.AddCallWithLinkedApiDefs(work.Id, "Device", "SET_1", [ setApiDef1.Id ])
        let setCall2 = store.AddCallWithLinkedApiDefs(work.Id, "Device", "SET_2", [ setApiDef2.Id ])
        store.ConnectSelectionInOrder([ setCall1; setCall2 ], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10
        let finishTargets, readyTargets = SimIndex.computeAutoHomingPlan index

        Assert.DoesNotContain(setWork.Id, finishTargets)
        Assert.DoesNotContain(setWork.Id, readyTargets)

    [<Fact>]
    let ``StartWithHomingPhase skips homing when only pending target has conflicting votes`` () =
        let store = createStore ()
        let project = addProject store "P"
        let activeSys = addSystem store "Active" project.Id true
        let flow = addFlow store "F" activeSys.Id
        let work = addWork store "W" flow.Id

        let finishedDeviceSys = addSystem store "FinishedDevice" project.Id false
        let finishedDeviceFlow = addFlow store "FDF" finishedDeviceSys.Id
        let retWork = addWork store "RET" finishedDeviceFlow.Id

        let retSimProps = SimulationWorkProperties()
        retSimProps.IsFinished <- true
        retWork.SetSimulationProperties(retSimProps)

        let retApiDef = addApiDef store "RET" finishedDeviceSys.Id
        retApiDef.TxGuid <- Some retWork.Id
        retApiDef.RxGuid <- Some retWork.Id

        let conflictDeviceSys = addSystem store "ConflictDevice" project.Id false
        let conflictDeviceFlow = addFlow store "CDF" conflictDeviceSys.Id
        let setWork = addWork store "SET" conflictDeviceFlow.Id

        let setApiDef1 = addApiDef store "SET_1" conflictDeviceSys.Id
        setApiDef1.TxGuid <- Some setWork.Id
        setApiDef1.RxGuid <- Some setWork.Id

        let setApiDef2 = addApiDef store "SET_2" conflictDeviceSys.Id
        setApiDef2.TxGuid <- Some setWork.Id
        setApiDef2.RxGuid <- Some setWork.Id

        let retCall = store.AddCallWithLinkedApiDefs(work.Id, "FinishedDevice", "RET", [ retApiDef.Id ])
        let setCall1 = store.AddCallWithLinkedApiDefs(work.Id, "ConflictDevice", "SET_1", [ setApiDef1.Id ])
        let setCall2 = store.AddCallWithLinkedApiDefs(work.Id, "ConflictDevice", "SET_2", [ setApiDef2.Id ])
        store.ConnectSelectionInOrder([ setCall1; setCall2 ], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10
        use engine = new EventDrivenEngine(index, RuntimeMode.Simulation)
        let sim = engine :> ISimulationEngine

        let hasHoming = sim.StartWithHomingPhase()

        Assert.False(hasHoming)
        Assert.False(sim.IsHomingPhase)
        Assert.Equal(Some Status4.Finish, sim.GetWorkState(retWork.Id))
        Assert.Equal(Some Status4.Ready, sim.GetWorkState(setWork.Id))
        Assert.Equal(Some Status4.Ready, sim.GetCallState(retCall))
        Assert.Equal(Some Status4.Ready, sim.GetCallState(setCall1))
        Assert.Equal(Some Status4.Ready, sim.GetCallState(setCall2))

        sim.Stop()

module CallTimeoutTests =

    [<Fact>]
    let ``call timeout emits event and keeps call Going`` () =
        let store = createStore ()
        let _, _, flow, work = setupBasicHierarchy store
        work.Duration <- Some (TimeSpan.FromMilliseconds 5000.)
        store.UpdateWorkTokenRole(work.Id, TokenRole.Source)

        // Device: 아주 긴 duration → timeout이 먼저 발동
        let project = store.Projects.Values |> Seq.head
        let deviceSys = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DF" deviceSys.Id
        let devWork = addWork store "ADV" deviceFlow.Id
        devWork.Duration <- Some (TimeSpan.FromMilliseconds 99999.)
        let apiDef = addApiDef store "ADV" deviceSys.Id
        apiDef.TxGuid <- Some devWork.Id
        apiDef.RxGuid <- Some devWork.Id

        let callId = store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [apiDef.Id])

        // Timeout 200ms
        let callProps = SimulationCallProperties()
        callProps.Timeout <- Some (TimeSpan.FromMilliseconds 200.)
        store.Calls.[callId].SetSimulationProperties(callProps)

        let index = SimIndex.build store 10
        use engine = new EventDrivenEngine(index, RuntimeMode.Simulation)
        let sim = engine :> ISimulationEngine

        let mutable timeoutFired = false
        let mutable timeoutCallGuid = Guid.Empty
        sim.CallTimeout.AddHandler(fun _ args ->
            timeoutFired <- true
            timeoutCallGuid <- args.CallGuid)

        sim.SpeedMultiplier <- 100.0
        sim.Start()

        let token = sim.NextToken()
        sim.SeedToken(work.Id, token)
        sim.ForceWorkState(work.Id, Status4.Going)

        // Timeout 이벤트 발생 대기
        let fired = waitUntil 5000 (fun () -> timeoutFired)

        // Call은 Going 유지 (강제 Finish 아님)
        let callState = sim.GetCallState(callId)
        sim.Stop()

        Assert.True(fired, "CallTimeout event should fire")
        Assert.Equal(callId, timeoutCallGuid)
        Assert.Equal(Some Status4.Going, callState)

module ResetTriggerClearTests =

    [<Fact>]
    let ``reset triggers cleared when work returns to Ready allowing second cycle`` () =
        // W1 → W2(Reset): W1 Going → W2 Homing→Ready
        // ForceWorkState로 강제 전이하며 reset trigger 클리어 확인
        let store = createStore ()
        let _, _, flow, w1 = setupBasicHierarchy store
        let w2 = addWork store "W2" flow.Id
        w1.Duration <- Some (TimeSpan.FromMilliseconds 5000.) // 충분히 길게
        w2.Duration <- Some (TimeSpan.FromMilliseconds 5000.)

        // W1→W2 Reset: W1 Going 시 Finish인 W2를 Homing
        store.ConnectSelectionInOrder([ w1.Id; w2.Id ], ArrowType.Reset) |> ignore

        let index = SimIndex.build store 10
        use engine = new EventDrivenEngine(index, RuntimeMode.Simulation)
        let sim = engine :> ISimulationEngine

        sim.SpeedMultiplier <- 1.0
        sim.Start()

        // 1사이클: W2를 Finish로 → W1을 Going → W2가 Homing→Ready
        sim.ForceWorkState(w2.Id, Status4.Going)
        Assert.True(waitUntil 1000 (fun () -> sim.GetWorkState(w2.Id) = Some Status4.Going))
        sim.ForceWorkState(w2.Id, Status4.Finish)
        Assert.True(waitUntil 1000 (fun () -> sim.GetWorkState(w2.Id) = Some Status4.Finish))

        sim.ForceWorkState(w1.Id, Status4.Going)
        // W1 Going → W2(Finish) → Homing → Ready
        Assert.True(waitUntil 2000 (fun () -> sim.GetWorkState(w2.Id) = Some Status4.Ready),
            $"1st cycle: W2 should reset to Ready. State={sim.GetWorkState(w2.Id)}")

        // W1을 Finish→Ready로 돌린 후 2사이클
        sim.ForceWorkState(w1.Id, Status4.Finish)
        System.Threading.Thread.Sleep(100)

        // W2를 다시 Finish로
        sim.ForceWorkState(w2.Id, Status4.Going)
        Assert.True(waitUntil 1000 (fun () -> sim.GetWorkState(w2.Id) = Some Status4.Going))
        sim.ForceWorkState(w2.Id, Status4.Finish)
        Assert.True(waitUntil 1000 (fun () -> sim.GetWorkState(w2.Id) = Some Status4.Finish))

        // 2사이클: W1을 다시 Going
        sim.ForceWorkState(w1.Id, Status4.Going)
        // Reset trigger가 클리어됐다면 W2가 다시 Homing→Ready
        let secondReset = waitUntil 2000 (fun () -> sim.GetWorkState(w2.Id) = Some Status4.Ready)

        sim.Stop()

        Assert.True(secondReset, $"2nd cycle: W2 should reset to Ready again. State={sim.GetWorkState(w2.Id)}. Reset triggers must be cleared.")

    /// Device 2개(Func1, Func2) 상호 리셋, Call 순서 Func1→Func2 → Func2=On(Finish), Func1=Off
    [<Fact>]
    let ``different device systems computed independently`` () =
        let store = createStore ()
        let project = addProject store "P"
        let activeSys = addSystem store "Active" project.Id true
        let flow = addFlow store "F" activeSys.Id
        let work1 = addWork store "W1" flow.Id
        let work2 = addWork store "W2" flow.Id

        // Device 1: ADV→RET 순서
        let dev1Sys = addSystem store "Dev1" project.Id false
        let dev1Flow = addFlow store "D1F" dev1Sys.Id
        let dev1Adv = addWork store "ADV" dev1Flow.Id
        let dev1Ret = addWork store "RET" dev1Flow.Id
        let dev1AdvDef = addApiDef store "ADV" dev1Sys.Id
        dev1AdvDef.TxGuid <- Some dev1Adv.Id; dev1AdvDef.RxGuid <- Some dev1Adv.Id
        let dev1RetDef = addApiDef store "RET" dev1Sys.Id
        dev1RetDef.TxGuid <- Some dev1Ret.Id; dev1RetDef.RxGuid <- Some dev1Ret.Id

        // Device 2: RET→ADV 순서 (역순)
        let dev2Sys = addSystem store "Dev2" project.Id false
        let dev2Flow = addFlow store "D2F" dev2Sys.Id
        let dev2Adv = addWork store "ADV" dev2Flow.Id
        let dev2Ret = addWork store "RET" dev2Flow.Id
        let dev2AdvDef = addApiDef store "ADV" dev2Sys.Id
        dev2AdvDef.TxGuid <- Some dev2Adv.Id; dev2AdvDef.RxGuid <- Some dev2Adv.Id
        let dev2RetDef = addApiDef store "RET" dev2Sys.Id
        dev2RetDef.TxGuid <- Some dev2Ret.Id; dev2RetDef.RxGuid <- Some dev2Ret.Id

        // Work1: Dev1.ADV → Dev1.RET
        let w1AdvCall = store.AddCallWithLinkedApiDefs(work1.Id, "Dev1", "ADV", [dev1AdvDef.Id])
        let w1RetCall = store.AddCallWithLinkedApiDefs(work1.Id, "Dev1", "RET", [dev1RetDef.Id])
        store.ConnectSelectionInOrder([w1AdvCall; w1RetCall], ArrowType.Start) |> ignore

        // Work2: Dev2.RET → Dev2.ADV
        let w2RetCall = store.AddCallWithLinkedApiDefs(work2.Id, "Dev2", "RET", [dev2RetDef.Id])
        let w2AdvCall = store.AddCallWithLinkedApiDefs(work2.Id, "Dev2", "ADV", [dev2AdvDef.Id])
        store.ConnectSelectionInOrder([w2RetCall; w2AdvCall], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10
        let targets = SimIndex.computeAutoHomingTargets index

        // Dev1: ADV→RET → RET=On
        Assert.Contains(dev1Ret.Id, targets)
        Assert.DoesNotContain(dev1Adv.Id, targets)
        // Dev2: RET→ADV → ADV=On
        Assert.Contains(dev2Adv.Id, targets)
        Assert.DoesNotContain(dev2Ret.Id, targets)

module HomingPhaseExecutionTests =

    [<Fact>]
    let ``Tester json: two devices with bidirectional StartReset homing completes`` () =
        let store = createStore ()
        let project = addProject store "P"
        let activeSys = addSystem store "Active" project.Id true

        // Active: 2 Flows, each with 1 Work
        let flow1 = addFlow store "NewFlow" activeSys.Id
        let w1 = addWork store "NewWork" flow1.Id
        let flow2 = addFlow store "NewFlow1" activeSys.Id
        let w2 = addWork store "NewWork" flow2.Id

        // Device 1: ADV + RET (ResetReset)
        let dev1 = addSystem store "Tester1" project.Id false
        let dev1Flow = addFlow store "TF" dev1.Id
        let dev1Adv = addWork store "ADV" dev1Flow.Id
        dev1Adv.Duration <- Some (TimeSpan.FromMilliseconds 500.)
        let dev1Ret = addWork store "RET" dev1Flow.Id
        dev1Ret.Duration <- Some (TimeSpan.FromMilliseconds 500.)
        let dev1RetProps = SimulationWorkProperties()
        dev1RetProps.IsFinished <- true
        dev1Ret.SetSimulationProperties(dev1RetProps)
        let dev1AdvDef = addApiDef store "ADV" dev1.Id
        dev1AdvDef.TxGuid <- Some dev1Adv.Id; dev1AdvDef.RxGuid <- Some dev1Adv.Id
        let dev1RetDef = addApiDef store "RET" dev1.Id
        dev1RetDef.TxGuid <- Some dev1Ret.Id; dev1RetDef.RxGuid <- Some dev1Ret.Id
        // ADV ↔ RET ResetReset
        store.ConnectSelectionInOrder([dev1Adv.Id; dev1Ret.Id], ArrowType.ResetReset) |> ignore

        // Device 2: ADV + RET (ResetReset)
        let dev2 = addSystem store "Tester2" project.Id false
        let dev2Flow = addFlow store "TF" dev2.Id
        let dev2Adv = addWork store "ADV" dev2Flow.Id
        dev2Adv.Duration <- Some (TimeSpan.FromMilliseconds 500.)
        let dev2Ret = addWork store "RET" dev2Flow.Id
        dev2Ret.Duration <- Some (TimeSpan.FromMilliseconds 500.)
        let dev2RetProps = SimulationWorkProperties()
        dev2RetProps.IsFinished <- true
        dev2Ret.SetSimulationProperties(dev2RetProps)
        let dev2AdvDef = addApiDef store "ADV" dev2.Id
        dev2AdvDef.TxGuid <- Some dev2Adv.Id; dev2AdvDef.RxGuid <- Some dev2Adv.Id
        let dev2RetDef = addApiDef store "RET" dev2.Id
        dev2RetDef.TxGuid <- Some dev2Ret.Id; dev2RetDef.RxGuid <- Some dev2Ret.Id
        store.ConnectSelectionInOrder([dev2Adv.Id; dev2Ret.Id], ArrowType.ResetReset) |> ignore

        // W1 Calls: Tester1.RET → Tester1.ADV (Start) — ADV는 SkipIfCompleted
        let w1RetCall = store.AddCallWithLinkedApiDefs(w1.Id, "Tester1", "RET", [dev1RetDef.Id])
        let w1AdvCall = store.AddCallWithLinkedApiDefs(w1.Id, "Tester1", "ADV", [dev1AdvDef.Id])
        store.ConnectSelectionInOrder([w1RetCall; w1AdvCall], ArrowType.Start) |> ignore
        let advCallProps = SimulationCallProperties()
        advCallProps.CallType <- CallType.SkipIfCompleted
        store.Calls.[w1AdvCall].SetSimulationProperties(advCallProps)

        // W2 Calls: Tester2.ADV → Tester2.RET (Start)
        let w2AdvCall = store.AddCallWithLinkedApiDefs(w2.Id, "Tester2", "ADV", [dev2AdvDef.Id])
        let w2RetCall = store.AddCallWithLinkedApiDefs(w2.Id, "Tester2", "RET", [dev2RetDef.Id])
        store.ConnectSelectionInOrder([w2AdvCall; w2RetCall], ArrowType.Start) |> ignore

        // Active arrows: W1 ↔ W2 (StartReset 양방향)
        store.ConnectSelectionInOrder([w1.Id; w2.Id], ArrowType.StartReset) |> ignore
        store.ConnectSelectionInOrder([w2.Id; w1.Id], ArrowType.StartReset) |> ignore

        let index = SimIndex.build store 10
        use engine = new EventDrivenEngine(index, RuntimeMode.Simulation)
        let sim = engine :> ISimulationEngine

        let log = System.Collections.Generic.List<string>()
        sim.WorkStateChanged.AddHandler(fun _ e ->
            log.Add($"Work {e.WorkName}: {e.PreviousState}->{e.NewState}"))
        sim.CallStateChanged.AddHandler(fun _ e ->
            log.Add($"Call {e.CallName}: {e.PreviousState}->{e.NewState}"))

        let mutable homingCompleted = false
        sim.HomingPhaseCompleted.AddHandler(fun _ _ -> homingCompleted <- true)

        sim.SpeedMultiplier <- 100.0
        let hasTargets = sim.StartWithHomingPhase()

        let completed = waitUntil 10000 (fun () -> homingCompleted || not sim.IsHomingPhase)

        sim.Stop()

        let logStr = String.Join("\n  ", log)

        if hasTargets then
            Assert.True(completed, $"Homing should complete within timeout.\nLog:\n  {logStr}")

        // Device1: ADV1 Going→Finish (homing), RET1 Finish→Homing→Ready (ResetReset에 의해)
        // Device2: RET2 Finish 유지 (IsFinished, homing 불필요), ADV2 Ready 유지
        Assert.Equal(Some Status4.Finish, sim.GetWorkState(dev2Ret.Id))

        printfn $"hasTargets={hasTargets}, homingCompleted={homingCompleted}\nLog:\n  {logStr}"

    [<Fact>]
    let ``StartWithHomingPhase triggers Device Work via Call and completes`` () =
        let store = createStore ()
        let project = addProject store "P"
        let activeSys = addSystem store "Active" project.Id true
        let flow = addFlow store "F" activeSys.Id
        let work = addWork store "W" flow.Id

        let deviceSys = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DF" deviceSys.Id
        let advWork = addWork store "ADV" deviceFlow.Id
        advWork.Duration <- Some (TimeSpan.FromMilliseconds 50.)
        let retWork = addWork store "RET" deviceFlow.Id
        retWork.Duration <- Some (TimeSpan.FromMilliseconds 50.)
        // RET에 IsFinished → ApplyInitialStates로 즉시 Finish
        let retSimProps = SimulationWorkProperties()
        retSimProps.IsFinished <- true
        retWork.SetSimulationProperties(retSimProps)

        let advApiDef = addApiDef store "ADV" deviceSys.Id
        advApiDef.TxGuid <- Some advWork.Id; advApiDef.RxGuid <- Some advWork.Id
        let retApiDef = addApiDef store "RET" deviceSys.Id
        retApiDef.TxGuid <- Some retWork.Id; retApiDef.RxGuid <- Some retWork.Id
        // Device 내부: ADV ↔ RET ResetReset
        store.ConnectSelectionInOrder([advWork.Id; retWork.Id], ArrowType.ResetReset) |> ignore

        // Call: RET → ADV → computeAutoHomingTargets → ADV=descendant=On(needsHoming)
        let retCallId = store.AddCallWithLinkedApiDefs(work.Id, "Device", "RET", [retApiDef.Id])
        let advCallId = store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [advApiDef.Id])
        store.ConnectSelectionInOrder([retCallId; advCallId], ArrowType.Start) |> ignore

        let index = SimIndex.build store 10
        use engine = new EventDrivenEngine(index, RuntimeMode.Simulation)
        let sim = engine :> ISimulationEngine

        let log = System.Collections.Generic.List<string>()
        sim.WorkStateChanged.AddHandler(fun _ e ->
            log.Add($"{e.WorkName}: {e.PreviousState}->{e.NewState}"))

        let mutable homingCompleted = false
        sim.HomingPhaseCompleted.AddHandler(fun _ _ -> homingCompleted <- true)

        sim.SpeedMultiplier <- 100.0
        let hasTargets = sim.StartWithHomingPhase()

        Assert.True(hasTargets, "Should have homing targets (ADV needs Going)")

        // advWork(ADV)가 Finish에 도달해야 homing 완료
        let completed = waitUntil 5000 (fun () -> homingCompleted)

        sim.Stop()

        let logStr = String.Join("\n  ", log)
        Assert.True(completed, $"Homing should complete. Log:\n  {logStr}")


