module Ds2.Store.Editor.Tests.DsStoreCrudHistoryTests

open System
open Xunit
open Ds2.Core
open Ds2.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

// =============================================================================
// CRUD
// =============================================================================

module AddTests =

    [<Fact>]
    let ``AddProject creates project in store`` () =
        let store = createStore ()
        let id = store.AddProject("P1")
        Assert.True(store.Projects.ContainsKey(id))
        Assert.Equal("P1", store.Projects.[id].Name)

    [<Fact>]
    let ``AddSystem adds to active or passive list`` () =
        let store = createStore ()
        let project = addProject store "P"
        let activeId = store.AddSystem("Active", project.Id, true)
        let passiveId = store.AddSystem("Passive", project.Id, false)
        Assert.True(project.ActiveSystemIds.Contains(activeId))
        Assert.True(project.PassiveSystemIds.Contains(passiveId))

    [<Fact>]
    let ``AddFlow creates flow under system`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flowId = store.AddFlow("F", system.Id)
        let flow = store.Flows.[flowId]
        Assert.Equal(system.Id, flow.ParentId)

    [<Fact>]
    let ``AddWork creates work under flow`` () =
        let store = createStore ()
        let _, _, flow, _ = setupBasicHierarchy store
        let workId = store.AddWork("W2", flow.Id)
        Assert.Equal(flow.Id, store.Works.[workId].ParentId)


// =============================================================================
// Undo / Redo
// =============================================================================

module UndoRedoTests =

    [<Fact>]
    let ``Undo reverts AddProject`` () =
        let store = createStore ()
        store.AddProject("P1") |> ignore
        Assert.Equal(1, store.Projects.Count)
        store.Undo()
        Assert.Equal(0, store.Projects.Count)

    [<Fact>]
    let ``Redo re-applies AddProject`` () =
        let store = createStore ()
        store.AddProject("P1") |> ignore
        store.Undo()
        Assert.Equal(0, store.Projects.Count)
        store.Redo()
        Assert.Equal(1, store.Projects.Count)

    [<Fact>]
    let ``Multiple Undo steps restore previous states`` () =
        let store = createStore ()
        let p = addProject store "P"
        addSystem store "S1" p.Id true |> ignore
        addSystem store "S2" p.Id true |> ignore
        Assert.Equal(2, store.Systems.Count)
        store.Undo()
        Assert.Equal(1, store.Systems.Count)
        store.Undo()
        Assert.Equal(0, store.Systems.Count)

    [<Fact>]
    let ``UndoTo batch undoes multiple steps`` () =
        let store = createStore ()
        let p = addProject store "P"
        addSystem store "S1" p.Id true |> ignore
        addSystem store "S2" p.Id true |> ignore
        store.UndoTo(2)
        Assert.Equal(0, store.Systems.Count)


    [<Fact>]
    let ``UndoTo all then RedoTo all preserves parent-child links`` () =
        let store = createStore ()
        let p = addProject store "P"
        let s = addSystem store "S" p.Id true
        let f = addFlow store "F" s.Id
        addWork store "W" f.Id |> ignore

        // Verify initial links
        Assert.True(store.Projects.[p.Id].ActiveSystemIds.Contains(s.Id))

        // Undo ALL (4 steps)
        store.UndoTo(4)
        Assert.Equal(0, store.Projects.Count)
        Assert.Equal(0, store.Systems.Count)

        // Redo ALL (4 steps)
        store.RedoTo(4)
        Assert.Equal(1, store.Projects.Count)
        Assert.Equal(1, store.Systems.Count)
        Assert.Equal(1, store.Flows.Count)
        Assert.Equal(1, store.Works.Count)

        // Parent-child link must be preserved after full undo→redo
        let restoredProject = store.Projects.[p.Id]
        Assert.True(restoredProject.ActiveSystemIds.Contains(s.Id),
            $"Project.ActiveSystemIds should contain system {s.Id} but was [{System.String.Join(',', restoredProject.ActiveSystemIds)}]")

// =============================================================================
// Events
// =============================================================================

module EventTests =

    [<Fact>]
    let ``AddProject emits ProjectAdded event`` () =
        let store = createStore ()
        let mutable received = false
        store.ObserveEvents().Add(fun evt ->
            match evt with
            | ProjectAdded _ -> received <- true
            | _ -> ())
        store.AddProject("P") |> ignore
        Assert.True(received)

    [<Fact>]
    let ``Undo emits StoreRefreshed`` () =
        let store = createStore ()
        store.AddProject("P") |> ignore
        let mutable refreshed = false
        store.ObserveEvents().Add(fun evt ->
            match evt with
            | StoreRefreshed -> refreshed <- true
            | _ -> ())
        store.Undo()
        Assert.True(refreshed)

    [<Fact>]
    let ``HistoryChanged contains labels`` () =
        let store = createStore ()
        let mutable labels: string list = []
        store.ObserveEvents().Add(fun evt ->
            match evt with
            | HistoryChanged(undoLabels, _) -> labels <- undoLabels
            | _ -> ())
        store.AddProject("Test") |> ignore
        Assert.NotEmpty(labels)

// =============================================================================
// Remove (Cascade)
// =============================================================================

