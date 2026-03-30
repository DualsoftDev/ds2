module Ds2.Store.Editor.Tests.WorkNamingTests

open System
open Xunit
open Ds2.Core
open Ds2.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

[<Fact>]
let ``AddWork sets FlowPrefix from parent Flow`` () =
    let store = createStore()
    let _, _, flow, _ = setupBasicHierarchy store
    let work2 = addWork store "Work2" flow.Id
    Assert.Equal("TestFlow", work2.FlowPrefix)
    Assert.Equal("Work2", work2.LocalName)
    Assert.Equal("TestFlow.Work2", work2.Name)

[<Fact>]
let ``AddWork rejects duplicate LocalName in same Flow`` () =
    let store = createStore()
    let _, _, flow, _ = setupBasicHierarchy store
    Assert.Throws<InvalidOperationException>(fun () ->
        store.AddWork("TestWork", flow.Id) |> ignore)

[<Fact>]
let ``AddFlow rejects duplicate name in same System`` () =
    let store = createStore()
    let _, system, _, _ = setupBasicHierarchy store
    Assert.Throws<InvalidOperationException>(fun () ->
        store.AddFlow("TestFlow", system.Id) |> ignore)

[<Fact>]
let ``RenameEntity Work changes LocalName only`` () =
    let store = createStore()
    let _, _, _, work = setupBasicHierarchy store
    store.RenameEntity(work.Id, EntityKind.Work, "Renamed")
    Assert.Equal("Renamed", work.LocalName)
    Assert.Equal("TestFlow", work.FlowPrefix)
    Assert.Equal("TestFlow.Renamed", work.Name)

[<Fact>]
let ``RenameEntity Work emits treeName as LocalName`` () =
    let store = createStore()
    let _, _, _, work = setupBasicHierarchy store
    let mutable captured = None
    use _sub = store.ObserveEvents().Subscribe(fun evt ->
        match evt with
        | EntityRenamed(id, newName, treeName) when id = work.Id ->
            captured <- Some (newName, treeName)
        | _ -> ())
    store.RenameEntity(work.Id, EntityKind.Work, "Renamed")
    let (displayName, treeName) = captured.Value
    Assert.Equal("TestFlow.Renamed", displayName) // 캔버스용: Flow.LocalName
    Assert.Equal("Renamed", treeName)              // 트리용: LocalName만

[<Fact>]
let ``RenameEntity Work with dot extracts LocalName`` () =
    let store = createStore()
    let _, _, _, work = setupBasicHierarchy store
    store.RenameEntity(work.Id, EntityKind.Work, "SomeFlow.NewLocal")
    Assert.Equal("NewLocal", work.LocalName)
    Assert.Equal("TestFlow", work.FlowPrefix) // FlowPrefix는 변경 안 됨

[<Fact>]
let ``RenameEntity Work rejects duplicate LocalName`` () =
    let store = createStore()
    let _, _, flow, _ = setupBasicHierarchy store
    addWork store "Other" flow.Id |> ignore
    let otherId = store.Works.Values |> Seq.find (fun w -> w.LocalName = "Other") |> fun w -> w.Id
    Assert.Throws<InvalidOperationException>(fun () ->
        store.RenameEntity(otherId, EntityKind.Work, "TestWork"))

[<Fact>]
let ``RenameEntity Flow cascades FlowPrefix to child Works`` () =
    let store = createStore()
    let _, _, flow, work = setupBasicHierarchy store
    let work2 = addWork store "W2" flow.Id
    store.RenameEntity(flow.Id, EntityKind.Flow, "RenamedFlow")
    Assert.Equal("RenamedFlow", flow.Name)
    Assert.Equal("RenamedFlow", work.FlowPrefix)
    Assert.Equal("RenamedFlow", work2.FlowPrefix)
    Assert.Equal("RenamedFlow.TestWork", work.Name)

[<Fact>]
let ``RenameEntity Flow Undo restores FlowPrefix`` () =
    let store = createStore()
    let _, _, flow, work = setupBasicHierarchy store
    let workId = work.Id
    store.RenameEntity(flow.Id, EntityKind.Flow, "NewFlow")
    Assert.Equal("NewFlow", store.Works.[workId].FlowPrefix)
    store.Undo()
    Assert.Equal("TestFlow", store.Works.[workId].FlowPrefix)
    Assert.Equal("TestFlow.TestWork", store.Works.[workId].Name)

[<Fact>]
let ``AddReferenceWork creates reference with same naming`` () =
    let store = createStore()
    let _, _, _, work = setupBasicHierarchy store
    let refId = store.AddReferenceWork(work.Id)
    let refWork = store.Works.[refId]
    Assert.Equal(Some work.Id, refWork.ReferenceOf)
    Assert.Equal("TestFlow", refWork.FlowPrefix)
    Assert.Equal("TestWork", refWork.LocalName)
    Assert.Equal(work.ParentId, refWork.ParentId)

[<Fact>]
let ``RemoveEntities cascades to reference Works`` () =
    let store = createStore()
    let _, _, _, work = setupBasicHierarchy store
    let refId = store.AddReferenceWork(work.Id)
    Assert.True(store.Works.ContainsKey(refId))
    store.RemoveEntities([ EntityKind.Work, work.Id ])
    Assert.False(store.Works.ContainsKey(work.Id))
    Assert.False(store.Works.ContainsKey(refId))

[<Fact>]
let ``DsQuery nextUniqueName auto-increments`` () =
    Assert.Equal("Work", DsQuery.nextUniqueName "Work" [])
    Assert.Equal("Work_1", DsQuery.nextUniqueName "Work" ["Work"])
    Assert.Equal("Work_2", DsQuery.nextUniqueName "Work" ["Work"; "Work_1"])

[<Fact>]
let ``DsQuery referenceGroupOf returns original plus references`` () =
    let store = createStore()
    let _, _, _, work = setupBasicHierarchy store
    let refId = store.AddReferenceWork(work.Id)

    let group = DsQuery.referenceGroupOf work.Id store
    Assert.Contains(work.Id, group)
    Assert.Contains(refId, group)
    Assert.Equal(2, group.Length)

    // reference에서 조회해도 같은 그룹
    let group2 = DsQuery.referenceGroupOf refId store
    Assert.Equal(2, group2.Length)

[<Fact>]
let ``DsQuery originalWorksOf excludes reference Works`` () =
    let store = createStore()
    let _, _, flow, work = setupBasicHierarchy store
    store.AddReferenceWork(work.Id) |> ignore

    let originals = DsQuery.originalWorksOf flow.Id store
    Assert.Equal(1, originals.Length)
    Assert.Equal(work.Id, originals.[0].Id)

    let all = DsQuery.worksOf flow.Id store
    Assert.Equal(2, all.Length)
