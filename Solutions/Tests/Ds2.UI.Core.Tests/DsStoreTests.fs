module Ds2.UI.Core.Tests.DsStoreTests

open System
open Xunit
open Ds2.Core
open Ds2.UI.Core
open Ds2.UI.Core.Tests.TestHelpers

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
        store.OnEvent.Add(fun evt ->
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
        store.OnEvent.Add(fun evt ->
            match evt with
            | StoreRefreshed -> refreshed <- true
            | _ -> ())
        store.Undo()
        Assert.True(refreshed)

    [<Fact>]
    let ``HistoryChanged contains labels`` () =
        let store = createStore ()
        let mutable labels: string list = []
        store.OnEvent.Add(fun evt ->
            match evt with
            | HistoryChanged(undoLabels, _) -> labels <- undoLabels
            | _ -> ())
        store.AddProject("Test") |> ignore
        Assert.NotEmpty(labels)

// =============================================================================
// Remove (Cascade)
// =============================================================================

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
        store.AddCallsWithDevice(Guid.Empty, work.Id, ["Dev.Api"], false)
        Assert.True(store.Calls.Count > 0)
        store.RemoveEntities([ (EntityKind.Work, work.Id) ])
        Assert.Equal(0, store.Calls.Count)

    [<Fact>]
    let ``RemoveEntities keeps nested condition api call reference alive`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Src.Api"; "Target.Api" ], true)

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
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true)
        let call = store.Calls |> Seq.head |> (fun kv -> kv.Value)
        Assert.Equal("Dev", call.DevicesAlias)
        Assert.Equal("Api", call.ApiName)

        // UI는 전체 이름("NewDev.Api")을 전달 — RenameEntity가 alias만 추출
        store.RenameEntity(call.Id, EntityKind.Call, "NewDev.Api")
        Assert.Equal("NewDev", call.DevicesAlias)
        Assert.Equal("Api", call.ApiName)  // ApiName 불변
        Assert.Equal("NewDev.Api", call.Name)

    [<Fact>]
    let ``RenameEntity for Call with same alias is no-op`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true)
        let call = store.Calls |> Seq.head |> (fun kv -> kv.Value)

        store.RenameEntity(call.Id, EntityKind.Call, "Dev.Api")  // same alias → no-op
        // Undo should undo AddCallsWithDevice, not a rename (rename was no-op)
        store.Undo()
        Assert.Empty(store.Calls)

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
    let ``ConnectSelectionInOrder creates ArrowBetweenCalls with parentId = workId`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1"; "Dev.Api2" ], true)
        let callIds = DsQuery.callsOf work.Id store |> List.map (fun c -> c.Id)
        let count = store.ConnectSelectionInOrder(callIds, ArrowType.ResetReset)
        Assert.Equal(1, count)
        Assert.Equal(1, store.ArrowCalls.Count)
        let arrow = store.ArrowCalls.Values |> Seq.head
        Assert.Equal(work.Id, arrow.ParentId)

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

module PasteTests =

    [<Fact>]
    let ``PasteEntities copies flow and returns new flow id`` () =
        let store = createStore ()
        let _, system, flow, _ = setupBasicHierarchy store
        let pastedIds = store.PasteEntities(EntityKind.Flow, [ flow.Id ], EntityKind.System, system.Id)
        Assert.Equal(1, pastedIds.Length)
        Assert.NotEqual(flow.Id, pastedIds.Head)
        Assert.Equal(2, DsQuery.flowsOf system.Id store |> List.length)

    [<Fact>]
    let ``PasteEntities copies works and returns new work ids`` () =
        let store = createStore ()
        let _, _, flow, work = setupBasicHierarchy store
        let pastedIds = store.PasteEntities(EntityKind.Work, [ work.Id ], EntityKind.Flow, flow.Id)
        Assert.Equal(1, pastedIds.Length)
        Assert.NotEqual(work.Id, pastedIds.Head)
        Assert.Equal(2, DsQuery.worksOf flow.Id store |> List.length)

    [<Fact>]
    let ``PasteEntities copies call with multiple device ApiCalls across flows`` () =
        let store = createStore ()
        let project, system, flow, work = setupBasicHierarchy store
        // ApiCall 복제 모드: 1 Call + 3 ApiCalls pointing to 3 different Device Systems
        let callId = store.AddCallWithMultipleDevicesResolved(
                        EntityKind.Work, work.Id, work.Id,
                        "Conv", "ADV", [ "Conv_1"; "Conv_2"; "Conv_3" ])
        let originalCall = store.Calls.[callId]
        Assert.Equal(3, originalCall.ApiCalls.Count)
        // 다른 Flow 생성
        let flow2Id = store.AddFlow("Flow2", system.Id)
        let work2Id = store.AddWork("Work2", flow2Id)
        // Call을 다른 Flow의 Work로 복사
        let pastedIds = store.PasteEntities(EntityKind.Call, [ callId ], EntityKind.Work, work2Id)
        Assert.Equal(1, pastedIds.Length)
        let pastedCall = store.Calls.[pastedIds.Head]
        // 복사된 Call에 3개 ApiCall이 있어야 함
        Assert.Equal(3, pastedCall.ApiCalls.Count)
        // 각 ApiCall이 서로 다른 ApiDef를 가리켜야 함 (원본과 다른 ID)
        let pastedApiDefIds =
            pastedCall.ApiCalls
            |> Seq.choose (fun ac -> ac.ApiDefId)
            |> Seq.distinct |> Seq.toList
        Assert.Equal(3, pastedApiDefIds.Length)
        // 새 Device System이 Flow2 기준으로 생성되어야 함
        let passiveSystems = DsQuery.passiveSystemsOf project.Id store
        // 원본 3 + 복사본 3 = 6 Device Systems
        Assert.True(passiveSystems.Length >= 6)

    [<Fact>]
    let ``ValidateCopySelection returns Ok for single copyable entity`` () =
        let store = createStore ()
        let _, _, flow, _ = setupBasicHierarchy store
        let keys = [| SelectionKey(flow.Id, EntityKind.Flow) |]
        let result = store.ValidateCopySelection(keys)
        Assert.True(result.IsOk)

    [<Fact>]
    let ``ValidateCopySelection returns NothingToCopy for non-copyable type`` () =
        let store = createStore ()
        let project, _, _, _ = setupBasicHierarchy store
        let keys = [| SelectionKey(project.Id, EntityKind.Project) |]
        let result = store.ValidateCopySelection(keys)
        Assert.True(result.IsNothingToCopy)

    [<Fact>]
    let ``ValidateCopySelection returns MixedTypes for different entity types`` () =
        let store = createStore ()
        let _, _, flow, work = setupBasicHierarchy store
        let keys = [| SelectionKey(flow.Id, EntityKind.Flow); SelectionKey(work.Id, EntityKind.Work) |]
        let result = store.ValidateCopySelection(keys)
        Assert.True(result.IsMixedTypes)

    [<Fact>]
    let ``ValidateCopySelection returns MixedParents for works in different flows`` () =
        let store = createStore ()
        let _, system, _, work1 = setupBasicHierarchy store
        let flow2 = addFlow store "Flow2" system.Id
        let work2 = addWork store "Work2" flow2.Id
        let keys = [| SelectionKey(work1.Id, EntityKind.Work); SelectionKey(work2.Id, EntityKind.Work) |]
        let result = store.ValidateCopySelection(keys)
        Assert.True(result.IsMixedParents)

    [<Fact>]
    let ``ValidateCopySelection returns Ok for same-parent works`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id
        let keys = [| SelectionKey(work1.Id, EntityKind.Work); SelectionKey(work2.Id, EntityKind.Work) |]
        let result = store.ValidateCopySelection(keys)
        Assert.True(result.IsOk)
        match result with
        | CopyValidationResult.Ok items -> Assert.Equal(2, items.Length)
        | _ -> Assert.Fail("Expected Ok")

// =============================================================================
// Move
// =============================================================================

module MoveTests =

    [<Fact>]
    let ``MoveEntities updates work position by id lookup`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store
        let request = MoveEntityRequest(work.Id, Xywh(100, 200, 50, 30))
        let moved = store.MoveEntities([ request ])
        Assert.Equal(1, moved)
        Assert.True(store.Works.[work.Id].Position.IsSome)

    [<Fact>]
    let ``MoveEntities updates call position by id lookup`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1" ], true)
        let call = DsQuery.callsOf work.Id store |> List.head
        let request = MoveEntityRequest(call.Id, Xywh(50, 60, 120, 40))
        let moved = store.MoveEntities([ request ])
        Assert.Equal(1, moved)
        Assert.True(store.Calls.[call.Id].Position.IsSome)

// =============================================================================
// Panel (도메인 타입 직접 사용)
// =============================================================================

module PanelTests =


    [<Fact>]
    let ``AddApiDefWithProperties creates apiDef with all properties set`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id false
        let id = store.AddApiDefWithProperties("Api1", system.Id, true, None, None, 200, Some "test desc")
        let apiDef = store.ApiDefs.[id]
        Assert.Equal("Api1", apiDef.Name)
        Assert.Equal(system.Id, apiDef.ParentId)
        Assert.True(apiDef.Properties.IsPush)
        Assert.Equal(200, apiDef.Properties.Period)
        Assert.Equal(Some "test desc", apiDef.Properties.Description)

    [<Fact>]
    let ``TryGetCallApiCallForPanel returns item when api call exists`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true)

        let call = store.Calls.Values |> Seq.head
        let apiCall = call.ApiCalls |> Seq.head

        let row = store.TryGetCallApiCallForPanel(call.Id, apiCall.Id)
        Assert.True(row.IsSome)
        Assert.Equal(apiCall.Id, row.Value.ApiCallId)

    [<Fact>]
    let ``TryGetCallApiCallForPanel returns None for unknown api call id`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true)

        let call = store.Calls.Values |> Seq.head
        let row = store.TryGetCallApiCallForPanel(call.Id, Guid.NewGuid())
        Assert.True(row.IsNone)

    [<Fact>]
    let ``AddApiCallFromPanel uses ApiDef name when panel no longer supplies api call name`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id false
        let flow = addFlow store "F" system.Id
        let work = addWork store "W" flow.Id
        store.AddCallsWithDevice(project.Id, work.Id, [ "Seed.Api" ], true)
        let callId = store.Calls.Values |> Seq.head |> fun call -> call.Id
        let apiDef = addApiDef store "DeviceApi" system.Id

        let createdId =
            store.AddApiCallFromPanel(
                callId,
                apiDef.Id,
                "", "out-addr",
                "", "in-addr",
                0, "",
                0, "")

        let created = store.ApiCalls.[createdId]
        Assert.Equal("DeviceApi", created.Name)
        Assert.Equal(Some apiDef.Id, created.ApiDefId)

    [<Fact>]
    let ``UpdateApiDef changes name and properties atomically`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id false
        let apiDef = addApiDef store "Api1" system.Id
        store.UpdateApiDef(apiDef.Id, "ApiRenamed", true, None, None, 300, Some "new desc")
        let updated = store.ApiDefs.[apiDef.Id]
        Assert.Equal("ApiRenamed", updated.Name)
        Assert.True(updated.Properties.IsPush)
        Assert.Equal(300, updated.Properties.Period)
        Assert.Equal(Some "new desc", updated.Properties.Description)

    [<Fact>]
    let ``UpdateApiDef is single undo step`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id false
        let apiDef = addApiDef store "OldName" system.Id
        store.UpdateApiDef(apiDef.Id, "NewName", true, None, None, 100, None)
        store.Undo()
        let reverted = store.ApiDefs.[apiDef.Id]
        Assert.Equal("OldName", reverted.Name)
        Assert.False(reverted.Properties.IsPush)
        Assert.Equal(0, reverted.Properties.Period)

    [<Fact>]
    let ``UpdateConditionApiCallOutputSpec updates selected condition api call`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true)

        let call = store.Calls.Values |> Seq.head
        let sourceApiCallId = call.ApiCalls |> Seq.head |> fun ac -> ac.Id

        store.AddCallCondition(call.Id, CallConditionType.ComAux)
        let condId = store.Calls.[call.Id].CallConditions |> Seq.head |> fun cc -> cc.Id

        let added = store.AddApiCallsToConditionBatch(call.Id, condId, [ sourceApiCallId ])
        Assert.Equal(1, added)

        let conditionApiCallId =
            store.Calls.[call.Id].CallConditions
            |> Seq.head
            |> fun cc -> cc.Conditions |> Seq.head |> fun ac -> ac.Id

        let changed = store.UpdateConditionApiCallOutputSpec(call.Id, condId, conditionApiCallId, 4, "123")
        Assert.True(changed)

        let updatedSpec =
            store.Calls.[call.Id].CallConditions
            |> Seq.head
            |> fun cc -> cc.Conditions |> Seq.find (fun ac -> ac.Id = conditionApiCallId) |> fun ac -> ac.OutputSpec

        match updatedSpec with
        | Int32Value (Single v) -> Assert.Equal(123, v)
        | _ -> Assert.Fail("OutputSpec should be Int32Value(Single 123)")

// =============================================================================
// File I/O
// =============================================================================

module FileIOTests =

    [<Fact>]
    let ``SaveToFile and LoadFromFile round-trip`` () =
        let store = createStore ()
        let project = addProject store "IOProject"
        addSystem store "IOSystem" project.Id true |> ignore
        let path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}.json")
        try
            store.SaveToFile(path)
            let store2 = createStore ()
            store2.LoadFromFile(path)
            Assert.Equal(1, store2.Projects.Count)
            Assert.Equal(1, store2.Systems.Count)
        finally
            if System.IO.File.Exists(path) then System.IO.File.Delete(path)

// =============================================================================
// Query (위임 래퍼)
// =============================================================================

module QueryTests =

    [<Fact>]
    let ``BuildTrees returns project tree`` () =
        let store = createStore ()
        let _ = setupBasicHierarchy store
        let activeTrees, _ = store.BuildTrees()
        Assert.NotEmpty(activeTrees)

    [<Fact>]
    let ``TryOpenTabForEntity returns tab info for system`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        let result = store.TryOpenTabForEntity(EntityKind.System, system.Id)
        Assert.True(result.IsSome)
        Assert.Equal(TabKind.System, result.Value.Kind)

    [<Fact>]
    let ``GetCallConditionTypes includes nested child condition types`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true)

        let call = DsQuery.callsOf work.Id store |> List.head
        store.AddCallCondition(call.Id, CallConditionType.ComAux)
        let parentCondition = call.CallConditions |> Seq.head
        store.AddChildCondition(call.Id, parentCondition.Id, false)
        let childCondition = parentCondition.Children |> Seq.head
        childCondition.Type <- Some CallConditionType.SkipUnmatch

        let result = store.GetCallConditionTypes(call.Id)

        Assert.Equal<CallConditionType list>([ CallConditionType.ComAux; CallConditionType.SkipUnmatch ], result)

    [<Fact>]
    let ``FindCallsByApiCallId finds nested child condition reference`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Src.Api"; "Target.Api" ], true)

        let sourceCall = DsQuery.callsOf work.Id store |> List.find (fun c -> c.Name = "Src.Api")
        let targetCall = DsQuery.callsOf work.Id store |> List.find (fun c -> c.Name = "Target.Api")
        let sourceApiCallId = sourceCall.ApiCalls |> Seq.head |> fun ac -> ac.Id

        store.AddCallCondition(targetCall.Id, CallConditionType.ComAux)
        let parentCondition = targetCall.CallConditions |> Seq.head
        store.AddChildCondition(targetCall.Id, parentCondition.Id, false)
        let childCondition = parentCondition.Children |> Seq.head
        store.AddApiCallsToConditionBatch(targetCall.Id, childCondition.Id, [ sourceApiCallId ]) |> ignore
        store.RemoveEntities([ (EntityKind.Call, sourceCall.Id) ])

        let result = store.FindCallsByApiCallId(sourceApiCallId)
        let struct (callId, callName) = Assert.Single(result)
        Assert.Equal(targetCall.Id, callId)
        Assert.Equal(targetCall.Name, callName)

// =============================================================================
// Device (AddCallsWithDevice)
// =============================================================================

module DeviceTests =

    [<Fact>]
    let ``AddCallsWithDevice creates device system with ResetReset arrows`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1"; "Dev.Api2" ], true)
        let passiveSystems = DsQuery.passiveSystemsOf project.Id store
        Assert.True(passiveSystems.Length > 0)
        Assert.Equal(2, store.Calls.Count)
        // Device System 내부 Work 간 ResetReset 화살표가 자동 생성되어야 함
        let devSystem = passiveSystems |> List.head
        let arrows = DsQuery.arrowWorksOf devSystem.Id store
        let resetArrows = arrows |> List.filter (fun a -> a.ArrowType = ArrowType.ResetReset)
        Assert.Equal(1, resetArrows.Length)

    [<Fact>]
    let ``AddCallsWithDevice on existing device system still creates ResetReset arrows`` () =
        let store = createStore ()
        let project, _, flow, work = setupBasicHierarchy store
        // 첫 번째 호출: 시스템 생성 + Api1 Work 생성
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1" ], true)
        let passiveSystems = DsQuery.passiveSystemsOf project.Id store
        Assert.Equal(1, passiveSystems.Length)
        let devSystem = passiveSystems |> List.head
        // 이 시점에 Work 1개 → pairwise 없으므로 ResetReset 0개
        let arrowsBefore = DsQuery.arrowWorksOf devSystem.Id store |> List.filter (fun a -> a.ArrowType = ArrowType.ResetReset)
        Assert.Equal(0, arrowsBefore.Length)
        // 두 번째 호출: 같은 Flow의 다른 Work → 같은 Device System에 Api2 추가
        let work2Id = store.AddWork("Work2", flow.Id)
        store.AddCallsWithDevice(project.Id, work2Id, [ "Dev.Api2" ], true)
        // 기존 시스템에 Work 2개 → ResetReset 1개 생성되어야 함
        let arrowsAfter = DsQuery.arrowWorksOf devSystem.Id store |> List.filter (fun a -> a.ArrowType = ArrowType.ResetReset)
        Assert.Equal(1, arrowsAfter.Length)

    [<Fact>]
    let ``AddCallsWithDevice with createDeviceSystem validates project id`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store
        let ex =
            Assert.Throws<InvalidOperationException>(fun () ->
                store.AddCallsWithDevice(Guid.Empty, work.Id, [ "Dev.Api" ], true))
        Assert.Contains("Project not found", ex.Message)
        Assert.Empty(store.Calls)

// =============================================================================
// Device (AddCallWithMultipleDevices — ApiCall 복제)
// =============================================================================

module ApiCallReplicationTests =

    [<Fact>]
    let ``AddCallWithMultipleDevices creates single call with multiple ApiCalls`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let callId = store.AddCallWithMultipleDevicesResolved(
                        EntityKind.Work, work.Id, work.Id,
                        "Conv", "ADV", [ "Conv_1"; "Conv_2"; "Conv_3" ])
        // 1개 Call만 생성
        Assert.Equal(1, store.Calls.Count)
        let call = store.Calls.[callId]
        Assert.Equal("Conv", call.DevicesAlias)
        Assert.Equal("ADV", call.ApiName)
        // 3개 ApiCall 생성
        Assert.Equal(3, call.ApiCalls.Count)
        // 3개 Device System 생성
        let passiveSystems = DsQuery.passiveSystemsOf project.Id store
        Assert.Equal(3, passiveSystems.Length)

    [<Fact>]
    let ``AddCallWithMultipleDevices creates ResetReset arrows in each device system`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        // ADV;RET → 각 Device System에 Work 2개 → ResetReset 1개
        let _callId = store.AddCallWithMultipleDevicesResolved(
                        EntityKind.Work, work.Id, work.Id,
                        "Conv", "ADV", [ "Conv_1" ])
        let passiveSystems = DsQuery.passiveSystemsOf project.Id store
        Assert.Equal(1, passiveSystems.Length)
        let devSystem = passiveSystems |> List.head
        // ADV Work 1개 → pairwise 없으므로 ResetReset 0개
        let arrows = DsQuery.arrowWorksOf devSystem.Id store |> List.filter (fun a -> a.ArrowType = ArrowType.ResetReset)
        Assert.Equal(0, arrows.Length)

    [<Fact>]
    let ``AddCallWithMultipleDevices with single alias behaves like count=1`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let callId = store.AddCallWithMultipleDevicesResolved(
                        EntityKind.Work, work.Id, work.Id,
                        "Conv", "ADV", [ "Conv" ])
        Assert.Equal(1, store.Calls.Count)
        let call = store.Calls.[callId]
        Assert.Equal(1, call.ApiCalls.Count)
        let passiveSystems = DsQuery.passiveSystemsOf project.Id store
        Assert.Equal(1, passiveSystems.Length)

// =============================================================================
// Panel — typed period / timeout (int ms)
// =============================================================================

module PanelTimingTests =

    [<Fact>]
    let ``UpdateWorkPeriodMs sets and gets period as int`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store
        store.UpdateWorkPeriodMs(work.Id, Some 500)
        let result = store.GetWorkPeriodMs(work.Id)
        Assert.True(result.IsSome)
        Assert.Equal(500, result.Value)

    [<Fact>]
    let ``UpdateWorkPeriodMs with None clears period`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store
        store.UpdateWorkPeriodMs(work.Id, Some 1000)
        store.UpdateWorkPeriodMs(work.Id, None)
        let result = store.GetWorkPeriodMs(work.Id)
        Assert.True(result.IsNone)

    [<Fact>]
    let ``UpdateCallTimeoutMs sets and gets timeout as int`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true)
        let callId = store.Calls |> Seq.head |> fun kv -> kv.Key
        store.UpdateCallTimeoutMs(callId, Some 3000)
        let result = store.GetCallTimeoutMs(callId)
        Assert.True(result.IsSome)
        Assert.Equal(3000, result.Value)

    [<Fact>]
    let ``UpdateCallTimeoutMs with None clears timeout`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true)
        let callId = store.Calls |> Seq.head |> fun kv -> kv.Key
        store.UpdateCallTimeoutMs(callId, Some 5000)
        store.UpdateCallTimeoutMs(callId, None)
        let result = store.GetCallTimeoutMs(callId)
        Assert.True(result.IsNone)

// =============================================================================
// DsQuery 직접 테스트
// =============================================================================

module DsQueryTests =

    [<Fact>]
    let ``allProjects returns all projects`` () =
        let store = createStore ()
        let _ = store.AddProject("P1")
        let _ = store.AddProject("P2")
        let projects = DsQuery.allProjects store
        Assert.Equal(2, projects.Length)

    [<Fact>]
    let ``flowsOf returns flows under system`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        let _ = store.AddFlow("F2", system.Id)
        let flows = DsQuery.flowsOf system.Id store
        Assert.Equal(2, flows.Length)

    [<Fact>]
    let ``worksOf returns works under flow`` () =
        let store = createStore ()
        let _, _, flow, _ = setupBasicHierarchy store
        let _ = store.AddWork("W2", flow.Id)
        let works = DsQuery.worksOf flow.Id store
        Assert.Equal(2, works.Length)

    [<Fact>]
    let ``callsOf returns calls under work`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.A"; "Dev.B" ], true)
        let calls = DsQuery.callsOf work.Id store
        Assert.Equal(2, calls.Length)

    [<Fact>]
    let ``trySystemIdOfWork resolves Work → Flow → System`` () =
        let store = createStore ()
        let _, system, _, work = setupBasicHierarchy store
        let result = DsQuery.trySystemIdOfWork work.Id store
        Assert.Equal(Some system.Id, result)

    [<Fact>]
    let ``tryGetName resolves entity names`` () =
        let store = createStore ()
        let _, system, flow, work = setupBasicHierarchy store
        Assert.Equal(Some "TestSystem", DsQuery.tryGetName store EntityKind.System system.Id)
        Assert.Equal(Some "TestFlow", DsQuery.tryGetName store EntityKind.Flow flow.Id)
        Assert.Equal(Some "TestWork", DsQuery.tryGetName store EntityKind.Work work.Id)
        Assert.Equal(None, DsQuery.tryGetName store EntityKind.Work (Guid.NewGuid()))

    [<Fact>]
    let ``buttonsOf returns HwButtons under system`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        let btn = HwButton("Btn1", system.Id)
        store.HwButtons.[btn.Id] <- btn
        let buttons = DsQuery.buttonsOf system.Id store
        Assert.Equal(1, buttons.Length)
        Assert.Equal("Btn1", buttons.[0].Name)

    [<Fact>]
    let ``lampsOf returns HwLamps under system`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        let lamp = HwLamp("Lamp1", system.Id)
        store.HwLamps.[lamp.Id] <- lamp
        let lamps = DsQuery.lampsOf system.Id store
        Assert.Equal(1, lamps.Length)
        Assert.Equal("Lamp1", lamps.[0].Name)

    [<Fact>]
    let ``conditionsOf returns HwConditions under system`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        let cond = HwCondition("Cond1", system.Id)
        store.HwConditions.[cond.Id] <- cond
        let conditions = DsQuery.conditionsOf system.Id store
        Assert.Equal(1, conditions.Length)

    [<Fact>]
    let ``actionsOf returns HwActions under system`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        let action = HwAction("Act1", system.Id)
        store.HwActions.[action.Id] <- action
        let actions = DsQuery.actionsOf system.Id store
        Assert.Equal(1, actions.Length)

    [<Fact>]
    let ``arrowWorksOf returns arrows under system`` () =
        let store = createStore ()
        let _, system, flow, work = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.ConnectSelectionInOrder([| work.Id; work2.Id |], ArrowType.Start) |> ignore
        let arrows = DsQuery.arrowWorksOf system.Id store
        Assert.True(arrows.Length >= 1)

    [<Fact>]
    let ``tryFindConditionRec finds nested condition`` () =
        let child = CallCondition()
        let parent = CallCondition()
        parent.Children.Add(child)
        let result = DsQuery.tryFindConditionRec [parent] child.Id
        Assert.True(result.IsSome)
        Assert.Equal(child.Id, result.Value.Id)

// =============================================================================
// Batch 편집
// =============================================================================

module BatchTests =

    [<Fact>]
    let ``GetAllWorkDurationRows returns works with period`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow = addFlow store "Flow1" system.Id
        let work = addWork store "Work1" flow.Id
        store.UpdateWorkPeriodMs(work.Id, Nullable<int>(5000))

        let rows = store.GetAllWorkDurationRows()
        Assert.Equal(1, rows.Length)
        Assert.Equal(work.Id, rows.[0].WorkId)
        Assert.Equal("Flow1", rows.[0].FlowName)
        Assert.Equal("Work1", rows.[0].WorkName)
        Assert.Equal(5000, rows.[0].PeriodMs)

    [<Fact>]
    let ``UpdateWorkDurationsBatch changes work periods and supports undo`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow = addFlow store "F" system.Id
        let work1 = addWork store "W1" flow.Id
        let work2 = addWork store "W2" flow.Id

        store.UpdateWorkDurationsBatch([ struct(work1.Id, 3000); struct(work2.Id, 7000) ])

        // 변경 확인
        let p1 = work1.Properties.Period
        Assert.True(p1.IsSome)
        Assert.Equal(3000.0, p1.Value.TotalMilliseconds)
        let p2 = work2.Properties.Period
        Assert.True(p2.IsSome)
        Assert.Equal(7000.0, p2.Value.TotalMilliseconds)

        // Undo 1회로 전체 롤백
        store.Undo()
        Assert.True(store.Works.[work1.Id].Properties.Period.IsNone)
        Assert.True(store.Works.[work2.Id].Properties.Period.IsNone)

    [<Fact>]
    let ``GetAllApiCallIORows returns apiCalls with IO tags`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id false
        let activeSystem = addSystem store "A" project.Id true
        let flow = addFlow store "Flow1" activeSystem.Id
        let work = addWork store "Work1" flow.Id
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true)
        let call = store.Calls.Values |> Seq.head
        let apiDef = addApiDef store "Api1" system.Id
        let apiCallId = store.AddApiCallFromPanel(call.Id, apiDef.Id, "", "outAddr", "", "inAddr", 0, "", 0, "")

        let rows = store.GetAllApiCallIORows()
        Assert.True(rows.Length >= 1)
        let row = rows |> List.find (fun r -> r.ApiCallId = apiCallId)
        Assert.Equal("Flow1", row.FlowName)
        Assert.Equal("outAddr", row.OutAddress)
        Assert.Equal("inAddr", row.InAddress)

    [<Fact>]
    let ``UpdateApiCallIOTagsBatch changes IO tags and supports undo`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id false
        let activeSystem = addSystem store "A" project.Id true
        let flow = addFlow store "F" activeSystem.Id
        let work = addWork store "W" flow.Id
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true)
        let call = store.Calls.Values |> Seq.head
        let apiDef = addApiDef store "Api1" system.Id
        let apiCallId = store.AddApiCallFromPanel(call.Id, apiDef.Id, "", "", "", "", 0, "", 0, "")

        store.UpdateApiCallIOTagsBatch([ struct(apiCallId, "newIn", "inSym", "newOut", "outSym") ])

        let apiCall = store.ApiCalls.[apiCallId]
        Assert.True(apiCall.InTag.IsSome)
        Assert.Equal("newIn", apiCall.InTag.Value.Address)
        Assert.Equal("inSym", apiCall.InTag.Value.Name)
        Assert.True(apiCall.OutTag.IsSome)
        Assert.Equal("newOut", apiCall.OutTag.Value.Address)
        Assert.Equal("outSym", apiCall.OutTag.Value.Name)

        // Undo 1회로 전체 롤백
        store.Undo()
        let reverted = store.ApiCalls.[apiCallId]
        Assert.True(reverted.InTag.IsNone)
        Assert.True(reverted.OutTag.IsNone)

    [<Fact>]
    let ``UpdateApiCallIOTagsBatch followed by SaveToFile works`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id false
        let activeSystem = addSystem store "A" project.Id true
        let flow = addFlow store "F" activeSystem.Id
        let work = addWork store "W" flow.Id
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true)
        let call = store.Calls.Values |> Seq.head
        let apiDef = addApiDef store "Api1" system.Id
        let apiCallId = store.AddApiCallFromPanel(call.Id, apiDef.Id, "", "", "", "", 0, "", 0, "")

        store.UpdateApiCallIOTagsBatch([ struct(apiCallId, "192.168.0.1", "InSensor", "192.168.0.2", "OutActuator") ])

        let tmpPath = System.IO.Path.GetTempFileName()
        try
            store.SaveToFile(tmpPath)
            let loaded = DsStore()
            loaded.LoadFromFile(tmpPath)

            // store.ApiCalls 딕셔너리 경로
            let loadedApiCall = loaded.ApiCalls.[apiCallId]
            Assert.True(loadedApiCall.InTag.IsSome)
            Assert.Equal("192.168.0.1", loadedApiCall.InTag.Value.Address)
            Assert.Equal("InSensor", loadedApiCall.InTag.Value.Name)
            Assert.True(loadedApiCall.OutTag.IsSome)
            Assert.Equal("192.168.0.2", loadedApiCall.OutTag.Value.Address)
            Assert.Equal("OutActuator", loadedApiCall.OutTag.Value.Name)

            // call.ApiCalls 내부 리스트 경로 (UI가 읽는 경로 — RewireApiCallReferences 필요)
            let loadedCall = loaded.Calls.Values |> Seq.head
            let callApiCall = loadedCall.ApiCalls |> Seq.find (fun ac -> ac.Id = apiCallId)
            Assert.True(callApiCall.InTag.IsSome, "call.ApiCalls 내부의 InTag이 비어있음 — RewireApiCallReferences 누락")
            Assert.Equal("192.168.0.1", callApiCall.InTag.Value.Address)

            // GetAllApiCallIORows 경로 (UI 다이얼로그와 동일)
            let ioRows = loaded.GetAllApiCallIORows()
            let row = ioRows |> List.find (fun r -> r.ApiCallId = apiCallId)
            Assert.Equal("192.168.0.1", row.InAddress)
            Assert.Equal("InSensor", row.InSymbol)
            Assert.Equal("192.168.0.2", row.OutAddress)
            Assert.Equal("OutActuator", row.OutSymbol)
        finally
            System.IO.File.Delete(tmpPath)

    [<Fact>]
    let ``UpdateWorkDurationsBatch followed by SaveToFile works`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow = addFlow store "F" system.Id
        let work = addWork store "W" flow.Id

        store.UpdateWorkDurationsBatch([ struct(work.Id, 2000) ])

        let tmpPath = System.IO.Path.GetTempFileName()
        try
            store.SaveToFile(tmpPath)
            let loaded = DsStore()
            loaded.LoadFromFile(tmpPath)
            let loadedWork = loaded.Works.Values |> Seq.head
            Assert.True(loadedWork.Properties.Period.IsSome)
            Assert.Equal(2000.0, loadedWork.Properties.Period.Value.TotalMilliseconds)
        finally
            System.IO.File.Delete(tmpPath)
