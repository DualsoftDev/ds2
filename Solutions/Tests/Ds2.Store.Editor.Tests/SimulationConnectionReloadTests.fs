module Ds2.Store.Editor.Tests.SimulationConnectionReloadTests

open System
open System.Threading
open Xunit
open Ds2.Core
open Ds2.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers
open Ds2.Runtime.Sim.Engine
open Ds2.Runtime.Sim.Engine.Core
open Ds2.Runtime.Sim.Model

let private waitUntil timeoutMs predicate =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
    let mutable matched = predicate ()
    while not matched && DateTime.UtcNow < deadline do
        Thread.Sleep(10)
        matched <- predicate ()
    matched

[<Fact>]
let ``CanAdvanceStep includes source priming before initial seed`` () =
    let store = createStore ()
    let _, _, _, work = setupBasicHierarchy store

    store.UpdateWorkTokenRole(work.Id, TokenRole.Source)

    let index = SimIndex.build store 10
    let engine = new EventDrivenEngine(index)

    try
        engine.Start()
        engine.SetAllFlowStates(FlowTag.Pause)

        Assert.False(engine.HasStartableWork)
        Assert.False(engine.HasActiveDuration)
        Assert.True(engine.CanAdvanceStep(Guid.Empty, true))
        Assert.True(engine.StepWithSourcePriming(Guid.Empty, true))
    finally
        engine.Stop()

[<Fact>]
let ``paused connection reload applies new work arrow to subsequent step progression`` () =
    let store = createStore ()
    let _, _, flow, work1 = setupBasicHierarchy store
    let work2 = addWork store "Work2" flow.Id

    store.UpdateWorkTokenRole(work1.Id, TokenRole.Source)
    store.UpdateWorkPeriodMs(work1.Id, Some 1)
    store.UpdateWorkPeriodMs(work2.Id, Some 1)

    let index = SimIndex.build store 10
    let engine = new EventDrivenEngine(index)

    try
        engine.Start()
        engine.SetAllFlowStates(FlowTag.Pause)

        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.StartReset) |> ignore
        engine.ReloadConnections()

        Assert.Equal<Guid list>([ work1.Id ], engine.Index.WorkStartPreds[work2.Id])
        Assert.Equal<Guid list>([ work2.Id ], engine.Index.WorkTokenSuccessors[work1.Id])

        let mutable work2Going = false
        let mutable guard = 0

        while not work2Going && guard < 8 do
            guard <- guard + 1
            engine.StepWithSourcePriming(Guid.Empty, true) |> ignore
            work2Going <- waitUntil 250 (fun () -> engine.GetWorkState(work2.Id) = Some Status4.Going)

        Assert.True(work2Going, "paused connection reload should affect the next STEP progression")
    finally
        engine.Stop()

[<Fact>]
let ``deleting arrow during simulation reverts orphaned Going works to Ready`` () =
    let store = createStore ()
    let _, system, flow, work1 = setupBasicHierarchy store
    let work2 = addWork store "Work2" flow.Id

    store.UpdateWorkTokenRole(work1.Id, TokenRole.Source)
    store.UpdateWorkPeriodMs(work1.Id, Some 1)
    store.UpdateWorkPeriodMs(work2.Id, Some 500)

    store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.StartReset) |> ignore

    let index = SimIndex.build store 10
    let engine = new EventDrivenEngine(index)

    try
        engine.Start()
        engine.SetAllFlowStates(FlowTag.Pause)

        // Source priming + step으로 Work1 시작 → Finish → Work2 Going
        let mutable guard = 0
        while engine.GetWorkState(work2.Id) <> Some Status4.Going && guard < 16 do
            guard <- guard + 1
            engine.StepWithSourcePriming(Guid.Empty, true) |> ignore
            waitUntil 300 (fun () -> engine.GetWorkState(work2.Id) = Some Status4.Going) |> ignore

        Assert.Equal(Some Status4.Going, engine.GetWorkState(work2.Id))

        // 화살표 삭제 → ReloadConnections
        let arrowId = (DsQuery.arrowWorksOf system.Id store).Head.Id
        store.RemoveArrows([ arrowId ]) |> ignore
        engine.ReloadConnections()

        // Work2는 선행 노드가 없으므로 Ready로 되돌아가야 함
        Assert.Equal(Some Status4.Ready, engine.GetWorkState(work2.Id))
        // 토큰도 회수되어야 함
        Assert.Equal(None, engine.GetWorkToken(work2.Id))
    finally
        engine.Stop()

[<Fact>]
let ``deleting arrow preserves Source works that are Going`` () =
    let store = createStore ()
    let _, system, flow, work1 = setupBasicHierarchy store
    let work2 = addWork store "Work2" flow.Id

    store.UpdateWorkTokenRole(work1.Id, TokenRole.Source)
    store.UpdateWorkPeriodMs(work1.Id, Some 500)
    store.UpdateWorkPeriodMs(work2.Id, Some 500)

    store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.StartReset) |> ignore

    let index = SimIndex.build store 10
    let engine = new EventDrivenEngine(index)

    try
        engine.Start()
        engine.SetAllFlowStates(FlowTag.Pause)

        let mutable guard = 0
        while engine.GetWorkState(work1.Id) <> Some Status4.Going && guard < 8 do
            guard <- guard + 1
            engine.StepWithSourcePriming(Guid.Empty, true) |> ignore
            waitUntil 300 (fun () -> engine.GetWorkState(work1.Id) = Some Status4.Going) |> ignore

        Assert.Equal(Some Status4.Going, engine.GetWorkState(work1.Id))

        // 화살표 삭제 → ReloadConnections
        let arrowId = (DsQuery.arrowWorksOf system.Id store).Head.Id
        store.RemoveArrows([ arrowId ]) |> ignore
        engine.ReloadConnections()

        // Source인 Work1은 선행 노드가 없어도 유지
        Assert.Equal(Some Status4.Going, engine.GetWorkState(work1.Id))
    finally
        engine.Stop()

[<Fact>]
let ``running connection reload lets blocked token flow to a newly connected successor`` () =
    let store = createStore ()
    let _, _, flow, work1 = setupBasicHierarchy store
    let work2 = addWork store "Work2" flow.Id

    store.UpdateWorkTokenRole(work1.Id, TokenRole.Source)
    store.UpdateWorkPeriodMs(work1.Id, Some 1)
    store.UpdateWorkPeriodMs(work2.Id, Some 1)

    let index = SimIndex.build store 10
    let engine = new EventDrivenEngine(index)

    try
        engine.Start()

        let token = engine.NextToken()
        engine.SeedToken(work1.Id, token)

        Assert.True(
            waitUntil 1000 (fun () -> engine.GetWorkState(work1.Id) = Some Status4.Finish),
            "source work should finish and hold a blocked token before the new arrow is added")
        Assert.Equal(Some token, engine.GetWorkToken(work1.Id))
        Assert.Equal(None, engine.GetWorkToken(work2.Id))

        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.StartReset) |> ignore
        engine.ReloadConnections()

        Assert.True(
            waitUntil 1000 (fun () -> engine.GetWorkToken(work2.Id) = Some token),
            "running connection reload should let a previously blocked token shift to the new successor")
        Assert.True(
            waitUntil 1000 (fun () -> engine.GetWorkState(work2.Id) = Some Status4.Going || engine.GetWorkState(work2.Id) = Some Status4.Finish),
            "successor should begin progressing after the live connection reload")
    finally
        engine.Stop()

[<Fact>]
let ``removing work group arrows after predecessor start prevents token propagation to former group members`` () =
    let store = createStore ()
    let _, system, flow, work1 = setupBasicHierarchy store
    let work2 = addWork store "Work2" flow.Id
    let work2_1 = addWork store "Work2_1" flow.Id
    let work2_2 = addWork store "Work2_2" flow.Id

    store.UpdateWorkTokenRole(work1.Id, TokenRole.Source)
    store.UpdateWorkPeriodMs(work1.Id, Some 500)
    store.UpdateWorkPeriodMs(work2.Id, Some 1)
    store.UpdateWorkPeriodMs(work2_1.Id, Some 1)
    store.UpdateWorkPeriodMs(work2_2.Id, Some 1)

    store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.StartReset) |> ignore
    store.ConnectSelectionInOrder([ work2.Id; work2_1.Id; work2_2.Id ], ArrowType.Group) |> ignore

    let index = SimIndex.build store 10
    let engine = new EventDrivenEngine(index)

    try
        engine.Start()

        let token = engine.NextToken()
        engine.SeedToken(work1.Id, token)

        Assert.True(
            waitUntil 1000 (fun () -> engine.GetWorkState(work1.Id) = Some Status4.Going),
            "source work should enter Going before group links are removed")

        let groupArrowIds =
            DsQuery.arrowWorksOf system.Id store
            |> List.filter (fun arrow -> arrow.ArrowType = ArrowType.Group)
            |> List.map (fun arrow -> arrow.Id)
        store.RemoveArrows(groupArrowIds) |> ignore
        engine.ReloadConnections()

        Assert.Equal<Guid list>([ work2.Id ], engine.Index.WorkTokenSuccessors[work1.Id])
        Assert.True(
            waitUntil 2500 (fun () -> engine.GetWorkToken(work2.Id) = Some token),
            $"central successor should receive token after group reload; w1={engine.GetWorkState(work1.Id)}, w2={engine.GetWorkState(work2.Id)}, w2_1={engine.GetWorkState(work2_1.Id)}, w2_2={engine.GetWorkState(work2_2.Id)}, t2={engine.GetWorkToken(work2.Id)}, t21={engine.GetWorkToken(work2_1.Id)}, t22={engine.GetWorkToken(work2_2.Id)}")

        Assert.Equal(None, engine.GetWorkToken(work2_1.Id))
        Assert.Equal(None, engine.GetWorkToken(work2_2.Id))
        Assert.True(
            engine.GetWorkState(work2.Id) = Some Status4.Going
            || engine.GetWorkState(work2.Id) = Some Status4.Finish)
        Assert.Equal(Some Status4.Ready, engine.GetWorkState(work2_1.Id))
        Assert.Equal(Some Status4.Ready, engine.GetWorkState(work2_2.Id))
    finally
        engine.Stop()

[<Fact>]
let ``removing work group arrows after token shift retracts orphaned former group members`` () =
    let store = createStore ()
    let _, system, flow, work1 = setupBasicHierarchy store
    let work2 = addWork store "Work2" flow.Id
    let work2_1 = addWork store "Work2_1" flow.Id
    let work2_2 = addWork store "Work2_2" flow.Id

    store.UpdateWorkTokenRole(work1.Id, TokenRole.Source)
    store.UpdateWorkPeriodMs(work1.Id, Some 1)
    store.UpdateWorkPeriodMs(work2.Id, Some 500)
    store.UpdateWorkPeriodMs(work2_1.Id, Some 500)
    store.UpdateWorkPeriodMs(work2_2.Id, Some 500)

    store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.StartReset) |> ignore
    store.ConnectSelectionInOrder([ work2.Id; work2_1.Id; work2_2.Id ], ArrowType.Group) |> ignore

    let index = SimIndex.build store 10
    let engine = new EventDrivenEngine(index)
    let dumpState () =
        $"w1={engine.GetWorkState(work1.Id)}, w2={engine.GetWorkState(work2.Id)}, w21={engine.GetWorkState(work2_1.Id)}, w22={engine.GetWorkState(work2_2.Id)}, t1={engine.GetWorkToken(work1.Id)}, t2={engine.GetWorkToken(work2.Id)}, t21={engine.GetWorkToken(work2_1.Id)}, t22={engine.GetWorkToken(work2_2.Id)}"

    try
        engine.Start()

        let token = engine.NextToken()
        engine.SeedToken(work1.Id, token)

        Assert.True(
            waitUntil 1500 (fun () ->
                engine.GetWorkToken(work2.Id) = Some token
                && engine.GetWorkToken(work2_1.Id) = Some token
                && engine.GetWorkToken(work2_2.Id) = Some token),
            $"old group topology should have propagated token to all grouped work2 nodes before deletion. {dumpState ()}")

        let groupArrowIds =
            DsQuery.arrowWorksOf system.Id store
            |> List.filter (fun arrow -> arrow.ArrowType = ArrowType.Group)
            |> List.map (fun arrow -> arrow.Id)
        store.RemoveArrows(groupArrowIds) |> ignore
        engine.ReloadConnections()

        Assert.Equal<Guid list>([ work2.Id ], engine.Index.WorkTokenSuccessors[work1.Id])
        Assert.Equal(Some Status4.Going, engine.GetWorkState(work2.Id))
        Assert.Equal(Some token, engine.GetWorkToken(work2.Id))
        Assert.Equal(Some Status4.Ready, engine.GetWorkState(work2_1.Id))
        Assert.Equal(Some Status4.Ready, engine.GetWorkState(work2_2.Id))
        Assert.Equal(None, engine.GetWorkToken(work2_1.Id))
        Assert.Equal(None, engine.GetWorkToken(work2_2.Id))
    finally
        engine.Stop()

[<Fact>]
let ``removing group arrows while predecessor calls are still running preserves in-flight progression`` () =
    let store = createStore ()
    let project, system, flow, work1 = setupBasicHierarchy store
    let work2 = addWork store "Work2" flow.Id
    let work2_1 = addWork store "Work2_1" flow.Id
    let work2_2 = addWork store "Work2_2" flow.Id

    store.UpdateWorkTokenRole(work1.Id, TokenRole.Source)
    store.UpdateWorkPeriodMs(work1.Id, Some 500)
    store.UpdateWorkPeriodMs(work2.Id, Some 1)
    store.UpdateWorkPeriodMs(work2_1.Id, Some 1)
    store.UpdateWorkPeriodMs(work2_2.Id, Some 1)
    store.AddCallsWithDevice(project.Id, work1.Id, [ "Dev.Api1"; "Dev.Api2"; "Dev.Api3" ], true, None)

    store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.StartReset) |> ignore
    store.ConnectSelectionInOrder([ work2.Id; work2_1.Id; work2_2.Id ], ArrowType.Group) |> ignore

    let index = SimIndex.build store 10
    let work1CallGuids = SimIndex.findOrEmpty work1.Id index.WorkCallGuids
    let engine = new EventDrivenEngine(index)
    let dumpState () =
        let callStates = work1CallGuids |> List.map (fun callGuid -> engine.GetCallState(callGuid))
        $"w1={engine.GetWorkState(work1.Id)}, w2={engine.GetWorkState(work2.Id)}, w21={engine.GetWorkState(work2_1.Id)}, w22={engine.GetWorkState(work2_2.Id)}, t1={engine.GetWorkToken(work1.Id)}, t2={engine.GetWorkToken(work2.Id)}, t21={engine.GetWorkToken(work2_1.Id)}, t22={engine.GetWorkToken(work2_2.Id)}, calls={callStates}"

    try
        engine.Start()

        let token = engine.NextToken()
        engine.SeedToken(work1.Id, token)

        Assert.True(
            waitUntil 1500 (fun () ->
                engine.GetWorkState(work1.Id) = Some Status4.Going),
            $"predecessor work should be running before group links are removed. {dumpState ()}")

        let groupArrowIds =
            DsQuery.arrowWorksOf system.Id store
            |> List.filter (fun arrow -> arrow.ArrowType = ArrowType.Group)
            |> List.map (fun arrow -> arrow.Id)
        store.RemoveArrows(groupArrowIds) |> ignore
        engine.ReloadConnections()

        Assert.Equal<Guid list>([ work2.Id ], engine.Index.WorkTokenSuccessors[work1.Id])
        Assert.True(
            waitUntil 4000 (fun () -> engine.GetWorkToken(work2.Id) = Some token),
            $"central successor should still receive token after predecessor progression continues past the reload. {dumpState ()}")
        Assert.True(
            waitUntil 1500 (fun () ->
                engine.GetWorkState(work2.Id) = Some Status4.Going
                || engine.GetWorkState(work2.Id) = Some Status4.Finish),
            $"central successor should keep progressing after the reload. {dumpState ()}")
        Assert.True(
            waitUntil 1500 (fun () -> engine.GetWorkState(work1.Id) <> Some Status4.Going),
            $"predecessor should not remain stuck in Going after the reload. {dumpState ()}")
        Assert.Equal(Some Status4.Ready, engine.GetWorkState(work2_1.Id))
        Assert.Equal(Some Status4.Ready, engine.GetWorkState(work2_2.Id))
        Assert.Equal(None, engine.GetWorkToken(work2_1.Id))
        Assert.Equal(None, engine.GetWorkToken(work2_2.Id))
    finally
        engine.Stop()

[<Fact>]
let ``json-like running reload with zero-duration predecessor and four device pairs keeps central progression`` () =
    let store = createStore ()
    let project, system, flow, work1 = setupBasicHierarchy store
    let work2 = addWork store "Work2" flow.Id
    let work2_1 = addWork store "Work2_1" flow.Id
    let work2_2 = addWork store "Work2_2" flow.Id

    store.UpdateWorkTokenRole(work1.Id, TokenRole.Source)
    store.UpdateWorkPeriodMs(work2.Id, Some 2000)
    store.UpdateWorkPeriodMs(work2_1.Id, Some 2000)
    store.UpdateWorkPeriodMs(work2_2.Id, Some 2000)
    store.AddCallsWithDevice(project.Id, work1.Id, [ "Dev_1.ADV"; "Dev_2.ADV"; "Dev_3.ADV"; "Dev_4.ADV" ], true, None)

    store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.StartReset) |> ignore
    store.ConnectSelectionInOrder([ work2.Id; work2_1.Id; work2_2.Id ], ArrowType.Group) |> ignore

    let index = SimIndex.build store 10
    let engine = new EventDrivenEngine(index)
    let dumpState () =
        let work1Calls = SimIndex.findOrEmpty work1.Id index.WorkCallGuids
        let callStates = work1Calls |> List.map (fun callGuid -> engine.GetCallState(callGuid))
        $"w1={engine.GetWorkState(work1.Id)}, w2={engine.GetWorkState(work2.Id)}, w21={engine.GetWorkState(work2_1.Id)}, w22={engine.GetWorkState(work2_2.Id)}, t1={engine.GetWorkToken(work1.Id)}, t2={engine.GetWorkToken(work2.Id)}, t21={engine.GetWorkToken(work2_1.Id)}, t22={engine.GetWorkToken(work2_2.Id)}, calls={callStates}"

    try
        engine.ApplyInitialStates()
        engine.Start()

        let token = engine.NextToken()
        engine.SeedToken(work1.Id, token)
        engine.ForceWorkState(work1.Id, Status4.Going)

        Assert.True(
            waitUntil 1500 (fun () -> engine.GetWorkState(work1.Id) = Some Status4.Going),
            $"predecessor should be Going before group links are removed. {dumpState ()}")

        let groupArrowIds =
            DsQuery.arrowWorksOf system.Id store
            |> List.filter (fun arrow -> arrow.ArrowType = ArrowType.Group)
            |> List.map (fun arrow -> arrow.Id)
        store.RemoveArrows(groupArrowIds) |> ignore
        engine.ReloadConnections()

        Assert.True(
            waitUntil 4000 (fun () ->
                engine.GetWorkState(work1.Id) <> Some Status4.Going
                && engine.GetWorkToken(work2.Id) = Some token),
            $"central successor should receive token after predecessor and device calls finish. {dumpState ()}")
        Assert.Equal(Some Status4.Ready, engine.GetWorkState(work2_1.Id))
        Assert.Equal(Some Status4.Ready, engine.GetWorkState(work2_2.Id))
        Assert.Equal(None, engine.GetWorkToken(work2_1.Id))
        Assert.Equal(None, engine.GetWorkToken(work2_2.Id))
        Assert.True(
            waitUntil 3000 (fun () -> engine.GetWorkState(work2.Id) = Some Status4.Finish),
            $"central successor should keep progressing after token shift. {dumpState ()}")
    finally
        engine.Stop()
