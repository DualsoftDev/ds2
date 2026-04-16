module Ds2.Store.Editor.Tests.DsStoreReorderTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

module WorkOrderTests =

    [<Fact>]
    let ``AddWork syncs WorkIds`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        Assert.Contains(work1.Id, flow.WorkIds)
        Assert.Contains(work2.Id, flow.WorkIds)
        Assert.Equal(2, flow.WorkIds.Count)

    [<Fact>]
    let ``AddReferenceWork syncs WorkIds`` () =
        let store = createStore ()
        let _, _, flow, work = setupBasicHierarchy store
        let refId = store.AddReferenceWork(work.Id)
        Assert.Contains(refId, flow.WorkIds)
        Assert.Equal(2, flow.WorkIds.Count)

    [<Fact>]
    let ``RemoveWork removes from WorkIds`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.RemoveEntities([ EntityKind.Work, work2.Id ])
        Assert.DoesNotContain(work2.Id, flow.WorkIds)
        Assert.Contains(work1.Id, flow.WorkIds)

    [<Fact>]
    let ``orderedWorksOf returns fallback when WorkIds empty`` () =
        let store = createStore ()
        let _, _, flow, work = setupBasicHierarchy store
        flow.WorkIds.Clear()
        let result = Queries.orderedWorksOf flow.Id store
        Assert.Equal(1, result.Length)
        Assert.Equal(work.Id, result[0].Id)

    [<Fact>]
    let ``orderedWorksOf returns sorted by WorkIds`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        flow.WorkIds.Clear()
        flow.WorkIds.Add(work2.Id)
        flow.WorkIds.Add(work1.Id)
        let result = Queries.orderedOriginalWorksOf flow.Id store
        Assert.Equal(work2.Id, result[0].Id)
        Assert.Equal(work1.Id, result[1].Id)

    [<Fact>]
    let ``MoveWorkInFlow moves down`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.MoveWorkInFlow(flow.Id, work1.Id, +1)
        Assert.Equal(work2.Id, flow.WorkIds[0])
        Assert.Equal(work1.Id, flow.WorkIds[1])

    [<Fact>]
    let ``MoveWorkInFlow does not exceed bounds`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.MoveWorkInFlow(flow.Id, work2.Id, +1)
        Assert.Equal(work1.Id, flow.WorkIds[0])
        Assert.Equal(work2.Id, flow.WorkIds[1])

    [<Fact>]
    let ``MoveWorkToPosition insertBefore=false inserts after target`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id
        store.MoveWorkToPosition(flow.Id, work1.Id, work3.Id, false)
        Assert.Equal(work2.Id, flow.WorkIds[0])
        Assert.Equal(work3.Id, flow.WorkIds[1])
        Assert.Equal(work1.Id, flow.WorkIds[2])

    [<Fact>]
    let ``MoveWorkToPosition insertBefore=true inserts before target`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id
        store.MoveWorkToPosition(flow.Id, work1.Id, work3.Id, true)
        Assert.Equal(work2.Id, flow.WorkIds[0])
        Assert.Equal(work1.Id, flow.WorkIds[1])
        Assert.Equal(work3.Id, flow.WorkIds[2])

    [<Fact>]
    let ``MoveWorkToPosition moves up with insertBefore=true`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id
        store.MoveWorkToPosition(flow.Id, work3.Id, work1.Id, true)
        Assert.Equal(work3.Id, flow.WorkIds[0])
        Assert.Equal(work1.Id, flow.WorkIds[1])
        Assert.Equal(work2.Id, flow.WorkIds[2])

    [<Fact>]
    let ``MoveWorkInFlow with empty WorkIds initializes within transaction`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        // WorkIds를 비워서 레거시 데이터 시뮬레이션
        flow.WorkIds.Clear()
        Assert.Equal(0, flow.WorkIds.Count)
        store.MoveWorkInFlow(flow.Id, work1.Id, +1)
        // 초기화 + 이동이 적용됨
        Assert.Equal(2, store.Flows[flow.Id].WorkIds.Count)
        Assert.Equal(work2.Id, store.Flows[flow.Id].WorkIds[0])
        Assert.Equal(work1.Id, store.Flows[flow.Id].WorkIds[1])
        // Undo → 초기화도 함께 되돌려져야 함
        store.Undo()
        Assert.Equal(0, store.Flows[flow.Id].WorkIds.Count)

module WorkOrderUndoTests =

    [<Fact>]
    let ``AddWork undo restores WorkIds`` () =
        let store = createStore ()
        let _, _, flow, _ = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        Assert.Equal(2, store.Flows[flow.Id].WorkIds.Count)
        store.Undo()
        Assert.Equal(1, store.Flows[flow.Id].WorkIds.Count)
        Assert.DoesNotContain(work2.Id, store.Flows[flow.Id].WorkIds)
        store.Redo()
        Assert.Equal(2, store.Flows[flow.Id].WorkIds.Count)

    [<Fact>]
    let ``RemoveWork undo restores WorkIds`` () =
        let store = createStore ()
        let _, _, flow, _ = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.RemoveEntities([ EntityKind.Work, work2.Id ])
        Assert.Equal(1, store.Flows[flow.Id].WorkIds.Count)
        store.Undo()
        Assert.Equal(2, store.Flows[flow.Id].WorkIds.Count)
        Assert.Contains(work2.Id, store.Flows[flow.Id].WorkIds)

    [<Fact>]
    let ``MoveWorkInFlow undo restores order`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.MoveWorkInFlow(flow.Id, work1.Id, +1)
        Assert.Equal(work2.Id, store.Flows[flow.Id].WorkIds[0])
        store.Undo()
        Assert.Equal(work1.Id, store.Flows[flow.Id].WorkIds[0])
        Assert.Equal(work2.Id, store.Flows[flow.Id].WorkIds[1])

module FlowOrderUndoTests =

    [<Fact>]
    let ``AddFlow undo restores FlowIds`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow = addFlow store "F1" system.Id
        Assert.Equal(1, store.Systems[system.Id].FlowIds.Count)
        store.Undo()
        Assert.Equal(0, store.Systems[system.Id].FlowIds.Count)
        store.Redo()
        Assert.Equal(1, store.Systems[system.Id].FlowIds.Count)

module FlowOrderTests =

    [<Fact>]
    let ``AddFlow syncs FlowIds`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow1 = addFlow store "F1" system.Id
        let flow2 = addFlow store "F2" system.Id
        Assert.Contains(flow1.Id, system.FlowIds)
        Assert.Contains(flow2.Id, system.FlowIds)
        Assert.Equal(2, system.FlowIds.Count)

    [<Fact>]
    let ``RemoveFlow removes from FlowIds`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow1 = addFlow store "F1" system.Id
        let flow2 = addFlow store "F2" system.Id
        store.RemoveEntities([ EntityKind.Flow, flow2.Id ])
        Assert.DoesNotContain(flow2.Id, system.FlowIds)
        Assert.Contains(flow1.Id, system.FlowIds)

    [<Fact>]
    let ``MoveFlowInSystem moves up`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow1 = addFlow store "F1" system.Id
        let flow2 = addFlow store "F2" system.Id
        store.MoveFlowInSystem(system.Id, flow2.Id, -1)
        Assert.Equal(flow2.Id, system.FlowIds[0])
        Assert.Equal(flow1.Id, system.FlowIds[1])

    [<Fact>]
    let ``MoveFlowToPosition insertBefore=false inserts after target`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow1 = addFlow store "F1" system.Id
        let flow2 = addFlow store "F2" system.Id
        let flow3 = addFlow store "F3" system.Id
        store.MoveFlowToPosition(system.Id, flow1.Id, flow3.Id, false)
        Assert.Equal(flow2.Id, system.FlowIds[0])
        Assert.Equal(flow3.Id, system.FlowIds[1])
        Assert.Equal(flow1.Id, system.FlowIds[2])

    [<Fact>]
    let ``MoveFlowToPosition insertBefore=true inserts before target`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow1 = addFlow store "F1" system.Id
        let flow2 = addFlow store "F2" system.Id
        let flow3 = addFlow store "F3" system.Id
        store.MoveFlowToPosition(system.Id, flow1.Id, flow3.Id, true)
        Assert.Equal(flow2.Id, system.FlowIds[0])
        Assert.Equal(flow1.Id, system.FlowIds[1])
        Assert.Equal(flow3.Id, system.FlowIds[2])

    [<Fact>]
    let ``MoveFlowInSystem with empty FlowIds initializes within transaction`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow1 = addFlow store "F1" system.Id
        let flow2 = addFlow store "F2" system.Id
        // FlowIds를 비워서 레거시 데이터 시뮬레이션
        system.FlowIds.Clear()
        Assert.Equal(0, system.FlowIds.Count)
        store.MoveFlowInSystem(system.Id, flow1.Id, +1)
        Assert.Equal(2, store.Systems[system.Id].FlowIds.Count)
        Assert.Equal(flow2.Id, store.Systems[system.Id].FlowIds[0])
        Assert.Equal(flow1.Id, store.Systems[system.Id].FlowIds[1])
        // Undo → 초기화도 함께 되돌려져야 함
        store.Undo()
        Assert.Equal(0, store.Systems[system.Id].FlowIds.Count)

    [<Fact>]
    let ``orderedFlowsOf returns sorted by FlowIds`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow1 = addFlow store "F1" system.Id
        let flow2 = addFlow store "F2" system.Id
        system.FlowIds.Clear()
        system.FlowIds.Add(flow2.Id)
        system.FlowIds.Add(flow1.Id)
        let result = Queries.orderedFlowsOf system.Id store
        Assert.Equal(flow2.Id, result[0].Id)
        Assert.Equal(flow1.Id, result[1].Id)
