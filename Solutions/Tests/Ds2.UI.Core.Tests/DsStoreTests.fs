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

        store.AddCallCondition(targetCall.Id, CallConditionType.Common)
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

        store.AddCallCondition(call.Id, CallConditionType.Common)
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
        store.AddCallCondition(call.Id, CallConditionType.Common)
        let parentCondition = call.CallConditions |> Seq.head
        store.AddChildCondition(call.Id, parentCondition.Id, false)
        let childCondition = parentCondition.Children |> Seq.head
        childCondition.Type <- Some CallConditionType.Active

        let result = store.GetCallConditionTypes(call.Id)

        Assert.Equal<CallConditionType list>([ CallConditionType.Common; CallConditionType.Active ], result)

    [<Fact>]
    let ``FindCallsByApiCallId finds nested child condition reference`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Src.Api"; "Target.Api" ], true)

        let sourceCall = DsQuery.callsOf work.Id store |> List.find (fun c -> c.Name = "Src.Api")
        let targetCall = DsQuery.callsOf work.Id store |> List.find (fun c -> c.Name = "Target.Api")
        let sourceApiCallId = sourceCall.ApiCalls |> Seq.head |> fun ac -> ac.Id

        store.AddCallCondition(targetCall.Id, CallConditionType.Common)
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
    let ``AddCallsWithDevice creates device system`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1"; "Dev.Api2" ], true)
        let passiveSystems = DsQuery.passiveSystemsOf project.Id store
        Assert.True(passiveSystems.Length > 0)
        Assert.Equal(2, store.Calls.Count)

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
