module Ds2.Store.Editor.Tests.DsStoreEntityMutationTests

open System
open Xunit
open Ds2.Core
open Ds2.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

module RemoveTests =

    [<Fact>]
    let ``RemoveEntities deletes project and cascades`` () =
        let store = createStore ()
        let project, _, _, _ = setupBasicHierarchy store
        store.RemoveEntities([ (EntityKind.Project, project.Id) ])
        Assert.Equal(0, store.Projects.Count)
        Assert.Equal(0, store.Systems.Count)
        Assert.Equal(0, store.Flows.Count)
        Assert.Equal(0, store.Works.Count)

    [<Fact>]
    let ``RemoveEntities with work removes descendant calls`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(Guid.Empty, work.Id, ["Dev.Api"], false, None)
        Assert.True(store.Calls.Count > 0)
        store.RemoveEntities([ (EntityKind.Work, work.Id) ])
        Assert.Equal(0, store.Calls.Count)

    [<Fact>]
    let ``RemoveEntities keeps nested condition api call reference alive`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Src.Api"; "Target.Api" ], true, None)

        let sourceCall = DsQuery.callsOf work.Id store |> List.find (fun c -> c.Name = "Src.Api")
        let targetCall = DsQuery.callsOf work.Id store |> List.find (fun c -> c.Name = "Target.Api")
        let sourceApiCallId = sourceCall.ApiCalls |> Seq.head |> fun ac -> ac.Id

        store.AddCallCondition(targetCall.Id, CallConditionType.ComAux)
        let parentCondition = targetCall.CallConditions |> Seq.head
        store.AddChildCondition(targetCall.Id, parentCondition.Id, false)
        let childCondition = parentCondition.Children |> Seq.head

        let added = store.AddApiCallsToConditionBatch(targetCall.Id, childCondition.Id, [ sourceApiCallId ])
        Assert.Equal(1, added)

        store.RemoveEntities([ (EntityKind.Call, sourceCall.Id) ])

        Assert.True(store.ApiCalls.ContainsKey(sourceApiCallId))
        let nestedApiCallId =
            childCondition.Conditions
            |> Seq.head
            |> fun apiCall -> apiCall.Id
        Assert.Equal(sourceApiCallId, nestedApiCallId)

    [<Fact>]
    let ``Undo restores deleted entities`` () =
        let store = createStore ()
        let project, _, _, _ = setupBasicHierarchy store
        let projectCount = store.Projects.Count
        store.RemoveEntities([ (EntityKind.Project, project.Id) ])
        Assert.Equal(0, store.Projects.Count)
        store.Undo()
        Assert.Equal(projectCount, store.Projects.Count)

// =============================================================================
// Rename
// =============================================================================

module RenameTests =

    [<Fact>]
    let ``RenameEntity changes project name`` () =
        let store = createStore ()
        let project = addProject store "OldName"
        store.RenameEntity(project.Id, EntityKind.Project, "NewName")
        Assert.Equal("NewName", store.Projects.[project.Id].Name)

    [<Fact>]
    let ``RenameEntity with same name does not create extra undo step`` () =
        let store = createStore ()
        let project = addProject store "Same"
        store.RenameEntity(project.Id, EntityKind.Project, "Same")
        store.Undo() // should undo AddProject directly (rename no-op must not push)
        Assert.False(store.Projects.ContainsKey(project.Id))

    [<Fact>]
    let ``RenameEntity for Call changes only DevicesAlias`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
        let call = store.Calls |> Seq.head |> (fun kv -> kv.Value)
        Assert.Equal("Dev", call.DevicesAlias)
        Assert.Equal("Api", call.ApiName)

        // UI는 전체 이름("NewDev.Api")을 전달 — RenameEntity가 alias만 추출
        store.RenameEntity(call.Id, EntityKind.Call, "NewDev.Api")
        Assert.Equal("NewDev", call.DevicesAlias)
        Assert.Equal("Api", call.ApiName)  // ApiName 불변
        Assert.Equal("NewDev.Api", call.Name)

// =============================================================================
// Arrows
// =============================================================================

module ArrowTests =

    [<Fact>]
    let ``ConnectSelectionInOrder creates ArrowBetweenWorks with parentId = systemId`` () =
        let store = createStore ()
        let _, system, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let count = store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.ResetReset)
        Assert.Equal(1, count)
        Assert.Equal(1, store.ArrowWorks.Count)
        let arrow = store.ArrowWorks.Values |> Seq.head
        Assert.Equal(system.Id, arrow.ParentId)

    [<Fact>]
    let ``ConnectSelectionInOrder creates ArrowBetweenCalls with parentId = workId for allowed call arrow type`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1"; "Dev.Api2" ], true, None)
        let callIds = DsQuery.callsOf work.Id store |> List.map (fun c -> c.Id)
        let count = store.ConnectSelectionInOrder(callIds, ArrowType.Start)
        Assert.Equal(1, count)
        Assert.Equal(1, store.ArrowCalls.Count)
        let arrow = store.ArrowCalls.Values |> Seq.head
        Assert.Equal(work.Id, arrow.ParentId)
        Assert.Equal(ArrowType.Start, arrow.ArrowType)

    [<Fact>]
    let ``ConnectSelectionInOrder blocks disallowed call arrow types`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1"; "Dev.Api2" ], true, None)
        let callIds = DsQuery.callsOf work.Id store |> List.map (fun c -> c.Id)

        Assert.Equal(0, store.ConnectSelectionInOrder(callIds, ArrowType.Reset))
        Assert.Equal(0, store.ConnectSelectionInOrder(callIds, ArrowType.StartReset))
        Assert.Equal(0, store.ConnectSelectionInOrder(callIds, ArrowType.ResetReset))
        Assert.Equal(0, store.ArrowCalls.Count)

    [<Fact>]
    let ``UpdateArrowType blocks disallowed call arrow types`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1"; "Dev.Api2" ], true, None)
        let callIds = DsQuery.callsOf work.Id store |> List.map (fun c -> c.Id)
        store.ConnectSelectionInOrder(callIds, ArrowType.Start) |> ignore

        let arrowId = store.ArrowCalls.Values |> Seq.head |> fun a -> a.Id

        Assert.False(store.UpdateArrowType(arrowId, ArrowType.Reset))
        Assert.False(store.UpdateArrowType(arrowId, ArrowType.StartReset))
        Assert.False(store.UpdateArrowType(arrowId, ArrowType.ResetReset))
        Assert.Equal(ArrowType.Start, store.ArrowCalls.[arrowId].ArrowType)

    [<Fact>]
    let ``ConnectSelectionInOrder creates cross-flow ArrowBetweenWorks in same system`` () =
        let store = createStore ()
        let _, system, _, work1 = setupBasicHierarchy store
        let flow2 = addFlow store "Flow2" system.Id
        let work2 = addWork store "W2" flow2.Id
        let count = store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.ResetReset)
        Assert.Equal(1, count)
        let arrow = store.ArrowWorks.Values |> Seq.head
        Assert.Equal(system.Id, arrow.ParentId)

    [<Fact>]
    let ``ConnectSelectionInOrder allows different ArrowType between same nodes`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let count1 = store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start)
        Assert.Equal(1, count1)
        // 같은 소스/타겟이지만 다른 타입 → 허용
        let count2 = store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Reset)
        Assert.Equal(1, count2)
        Assert.Equal(2, store.ArrowWorks.Count)

    [<Fact>]
    let ``ConnectSelectionInOrder blocks same ArrowType between same nodes`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let count1 = store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start)
        Assert.Equal(1, count1)
        // 동일 소스/타겟/타입 → 중복 차단
        let count2 = store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start)
        Assert.Equal(0, count2)
        Assert.Equal(1, store.ArrowWorks.Count)

    [<Fact>]
    let ``ReconnectArrow allows different ArrowType between same nodes`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id

        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work1.Id; work3.Id ], ArrowType.Reset) |> ignore

        let arrowToReconnect =
            store.ArrowWorks.Values
            |> Seq.find (fun arrow -> arrow.SourceId = work1.Id && arrow.TargetId = work3.Id && arrow.ArrowType = ArrowType.Reset)

        let changed = store.ReconnectArrow(arrowToReconnect.Id, false, work2.Id)

        Assert.True(changed)
        Assert.Equal(2, store.ArrowWorks.Count)
        Assert.Contains(store.ArrowWorks.Values, fun arrow -> arrow.SourceId = work1.Id && arrow.TargetId = work2.Id && arrow.ArrowType = ArrowType.Start)
        Assert.Contains(store.ArrowWorks.Values, fun arrow -> arrow.SourceId = work1.Id && arrow.TargetId = work2.Id && arrow.ArrowType = ArrowType.Reset)

    [<Fact>]
    let ``ReconnectArrow blocks same ArrowType between same nodes`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id

        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work1.Id; work3.Id ], ArrowType.Start) |> ignore

        let arrowToReconnect =
            store.ArrowWorks.Values
            |> Seq.find (fun arrow -> arrow.SourceId = work1.Id && arrow.TargetId = work3.Id && arrow.ArrowType = ArrowType.Start)

        let changed = store.ReconnectArrow(arrowToReconnect.Id, false, work2.Id)

        Assert.False(changed)
        Assert.Contains(store.ArrowWorks.Values, fun arrow -> arrow.Id = arrowToReconnect.Id && arrow.TargetId = work3.Id)

    [<Fact>]
    let ``RemoveArrows deletes arrows`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.ResetReset) |> ignore
        let arrowId = store.ArrowWorks.Values |> Seq.head |> fun a -> a.Id
        let removed = store.RemoveArrows([ arrowId ])
        Assert.Equal(1, removed)
        Assert.Equal(0, store.ArrowWorks.Count)

// =============================================================================
// Paste
// =============================================================================

