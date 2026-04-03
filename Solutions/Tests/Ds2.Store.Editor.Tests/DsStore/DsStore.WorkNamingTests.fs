module Ds2.Store.Editor.Tests.WorkNamingTests

open System
open Xunit
open Ds2.Core
open Ds2.Store
open Ds2.Store.DsQuery
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
let ``isCallNameUniqueInWork detects duplicate Call name`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    Assert.False(Queries.isCallNameUniqueInWork work.Id "Dev.Api" None store)

[<Fact>]
let ``isCallNameUniqueInWork allows different Call name`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    Assert.True(Queries.isCallNameUniqueInWork work.Id "Dev.Api2" None store)

[<Fact>]
let ``AddCallsWithDevice allows duplicate Call name`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let calls = Queries.originalCallsOf work.Id store |> List.filter (fun c -> c.Name = "Dev.Api")
    Assert.Equal(2, calls.Length)

[<Fact>]
let ``isCallNameUniqueInWork with excludeId ignores self`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let call = Queries.originalCallsOf work.Id store |> List.head
    Assert.False(Queries.isCallNameUniqueInWork work.Id "Dev.Api" None store)
    Assert.True(Queries.isCallNameUniqueInWork work.Id "Dev.Api" (Some call.Id) store)

[<Fact>]
let ``RenameEntity Call rejects duplicate name in same Work`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev2.Api" ], true, None)
    let call2 = Queries.originalCallsOf work.Id store |> List.find (fun c -> c.DevicesAlias = "Dev2")
    Assert.Throws<InvalidOperationException>(fun () ->
        store.RenameEntity(call2.Id, EntityKind.Call, "Dev"))

[<Fact>]
let ``AddReferenceCall creates reference with same naming`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.callsOf work.Id store |> List.head
    let refId = store.AddReferenceCall(originalCall.Id)
    let refCall = store.Calls.[refId]
    Assert.Equal(Some originalCall.Id, refCall.ReferenceOf)
    Assert.Equal("Dev", refCall.DevicesAlias)
    Assert.Equal("Api", refCall.ApiName)
    Assert.Equal(work.Id, refCall.ParentId)

[<Fact>]
let ``Reference Call blocks property mutations`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.callsOf work.Id store |> List.head
    let refId = store.AddReferenceCall(originalCall.Id)
    // Timeout 수정 차단
    Assert.Throws<InvalidOperationException>(fun () ->
        store.UpdateCallTimeoutMs(refId, Some 1000))
    // 조건 추가 차단
    Assert.Throws<InvalidOperationException>(fun () ->
        store.AddCallCondition(refId, CallConditionType.AutoAux))
    // Rename 차단
    Assert.Throws<InvalidOperationException>(fun () ->
        store.RenameEntity(refId, EntityKind.Call, "NewName"))
    // 원본은 정상 편집 가능
    store.UpdateCallTimeoutMs(originalCall.Id, Some 1000)

[<Fact>]
let ``RemoveEntities cascades to reference Calls`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.callsOf work.Id store |> List.head
    let refId = store.AddReferenceCall(originalCall.Id)
    store.RemoveEntities([ (EntityKind.Call, originalCall.Id) ])
    Assert.False(store.Calls.ContainsKey(refId))

[<Fact>]
let ``originalCallsOf excludes reference Calls`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.callsOf work.Id store |> List.head
    store.AddReferenceCall(originalCall.Id) |> ignore
    let originals = Queries.originalCallsOf work.Id store
    Assert.Equal(1, originals.Length)
    let all = Queries.callsOf work.Id store
    Assert.Equal(2, all.Length)

[<Fact>]
let ``callReferenceGroupOf returns original plus references`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.callsOf work.Id store |> List.head
    let refId = store.AddReferenceCall(originalCall.Id)
    let group = Queries.callReferenceGroupOf originalCall.Id store
    Assert.Contains(originalCall.Id, group)
    Assert.Contains(refId, group)
    Assert.Equal(2, group.Length)

[<Fact>]
let ``isCallNameUniqueInWork ignores reference Calls`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.callsOf work.Id store |> List.head
    store.AddReferenceCall(originalCall.Id) |> ignore
    // 참조 Call이 있어도 원본 1개만 있으므로 같은 이름은 중복
    Assert.False(Queries.isCallNameUniqueInWork work.Id "Dev.Api" None store)
    // 다른 이름은 허용
    Assert.True(Queries.isCallNameUniqueInWork work.Id "Dev.Api2" None store)

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
let ``AddReferenceWork from reference resolves to original`` () =
    let store = createStore()
    let _, _, _, work = setupBasicHierarchy store
    let refId = store.AddReferenceWork(work.Id)
    // Ref에서 또 Ref를 만들면 원본을 직접 가리켜야 함
    let refOfRefId = store.AddReferenceWork(refId)
    let refOfRef = store.Works.[refOfRefId]
    Assert.Equal(Some work.Id, refOfRef.ReferenceOf)

[<Fact>]
let ``AddReferenceCall from reference resolves to original`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.callsOf work.Id store |> List.head
    let refId = store.AddReferenceCall(originalCall.Id)
    // Ref에서 또 Ref를 만들면 원본을 직접 가리켜야 함
    let refOfRefId = store.AddReferenceCall(refId)
    let refOfRef = store.Calls.[refOfRefId]
    Assert.Equal(Some originalCall.Id, refOfRef.ReferenceOf)

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
    Assert.Equal("Work", Queries.nextUniqueName "Work" [])
    Assert.Equal("Work_1", Queries.nextUniqueName "Work" ["Work"])
    Assert.Equal("Work_2", Queries.nextUniqueName "Work" ["Work"; "Work_1"])

[<Fact>]
let ``DsQuery referenceGroupOf returns original plus references`` () =
    let store = createStore()
    let _, _, _, work = setupBasicHierarchy store
    let refId = store.AddReferenceWork(work.Id)

    let group = Queries.referenceGroupOf work.Id store
    Assert.Contains(work.Id, group)
    Assert.Contains(refId, group)
    Assert.Equal(2, group.Length)

    // reference에서 조회해도 같은 그룹
    let group2 = Queries.referenceGroupOf refId store
    Assert.Equal(2, group2.Length)

[<Fact>]
let ``DsQuery originalWorksOf excludes reference Works`` () =
    let store = createStore()
    let _, _, flow, work = setupBasicHierarchy store
    store.AddReferenceWork(work.Id) |> ignore

    let originals = Queries.originalWorksOf flow.Id store
    Assert.Equal(1, originals.Length)
    Assert.Equal(work.Id, originals.[0].Id)

    let all = Queries.worksOf flow.Id store
    Assert.Equal(2, all.Length)

[<Fact>]
let ``parseWorkNameParts splits prefix and localName`` () =
    let struct(prefix, local) = TokenRoleOps.parseWorkNameParts "Flow1.Work1"
    Assert.Equal("Flow1.", prefix)
    Assert.Equal("Work1", local)

[<Fact>]
let ``parseWorkNameParts with no dot returns empty prefix`` () =
    let struct(prefix, local) = TokenRoleOps.parseWorkNameParts "JustName"
    Assert.Equal("", prefix)
    Assert.Equal("JustName", local)
