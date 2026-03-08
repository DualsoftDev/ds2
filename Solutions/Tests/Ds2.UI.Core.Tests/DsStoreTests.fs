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
        store.RemoveEntities([ (EntityTypeNames.Project, project.Id) ])
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
        store.RemoveEntities([ (EntityTypeNames.Work, work.Id) ])
        Assert.Equal(0, store.Calls.Count)

    [<Fact>]
    let ``Undo restores deleted entities`` () =
        let store = createStore ()
        let project, _, _, _ = setupBasicHierarchy store
        let projectCount = store.Projects.Count
        store.RemoveEntities([ (EntityTypeNames.Project, project.Id) ])
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
        store.RenameEntity(project.Id, EntityTypeNames.Project, "NewName")
        Assert.Equal("NewName", store.Projects.[project.Id].Name)

    [<Fact>]
    let ``RenameEntity with same name does not create extra undo step`` () =
        let store = createStore ()
        let project = addProject store "Same"
        store.RenameEntity(project.Id, EntityTypeNames.Project, "Same")
        store.Undo() // should undo AddProject directly (rename no-op must not push)
        Assert.False(store.Projects.ContainsKey(project.Id))

// =============================================================================
// Arrows
// =============================================================================

module ArrowTests =

    [<Fact>]
    let ``ConnectSelectionInOrderUi creates arrows between works`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let count = store.ConnectSelectionInOrderUi([ work1.Id; work2.Id ], UiArrowType.ResetReset)
        Assert.Equal(1, count)
        Assert.Equal(1, store.ArrowWorks.Count)

    [<Fact>]
    let ``RemoveArrows deletes arrows`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.ConnectSelectionInOrderUi([ work1.Id; work2.Id ], UiArrowType.ResetReset) |> ignore
        let arrowId = store.ArrowWorks.Values |> Seq.head |> fun a -> a.Id
        let removed = store.RemoveArrows([ arrowId ])
        Assert.Equal(1, removed)
        Assert.Equal(0, store.ArrowWorks.Count)

// =============================================================================
// Paste
// =============================================================================

module PasteTests =

    [<Fact>]
    let ``PasteEntities copies flow to same system`` () =
        let store = createStore ()
        let _, system, flow, _ = setupBasicHierarchy store
        let count = store.PasteEntities(EntityTypeNames.Flow, [ flow.Id ], EntityTypeNames.System, system.Id)
        Assert.Equal(1, count)
        Assert.Equal(2, DsQuery.flowsOf system.Id store |> List.length)

    [<Fact>]
    let ``PasteEntities copies works to same flow`` () =
        let store = createStore ()
        let _, _, flow, work = setupBasicHierarchy store
        let count = store.PasteEntities(EntityTypeNames.Work, [ work.Id ], EntityTypeNames.Flow, flow.Id)
        Assert.Equal(1, count)
        Assert.Equal(2, DsQuery.worksOf flow.Id store |> List.length)

    [<Fact>]
    let ``ValidateCopySelection returns Ok for single copyable entity`` () =
        let store = createStore ()
        let _, _, flow, _ = setupBasicHierarchy store
        let keys = [| SelectionKey(flow.Id, EntityTypeNames.Flow) |]
        let result = store.ValidateCopySelection(keys)
        Assert.True(result.IsOk)

    [<Fact>]
    let ``ValidateCopySelection returns NothingToCopy for non-copyable type`` () =
        let store = createStore ()
        let project, _, _, _ = setupBasicHierarchy store
        let keys = [| SelectionKey(project.Id, EntityTypeNames.Project) |]
        let result = store.ValidateCopySelection(keys)
        Assert.True(result.IsNothingToCopy)

    [<Fact>]
    let ``ValidateCopySelection returns MixedTypes for different entity types`` () =
        let store = createStore ()
        let _, _, flow, work = setupBasicHierarchy store
        let keys = [| SelectionKey(flow.Id, EntityTypeNames.Flow); SelectionKey(work.Id, EntityTypeNames.Work) |]
        let result = store.ValidateCopySelection(keys)
        Assert.True(result.IsMixedTypes)

    [<Fact>]
    let ``ValidateCopySelection returns MixedParents for works in different flows`` () =
        let store = createStore ()
        let _, system, _, work1 = setupBasicHierarchy store
        let flow2 = addFlow store "Flow2" system.Id
        let work2 = addWork store "Work2" flow2.Id
        let keys = [| SelectionKey(work1.Id, EntityTypeNames.Work); SelectionKey(work2.Id, EntityTypeNames.Work) |]
        let result = store.ValidateCopySelection(keys)
        Assert.True(result.IsMixedParents)

    [<Fact>]
    let ``ValidateCopySelection returns Ok for same-parent works`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id
        let keys = [| SelectionKey(work1.Id, EntityTypeNames.Work); SelectionKey(work2.Id, EntityTypeNames.Work) |]
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
    let ``MoveEntitiesUi updates work position`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store
        let request = UiMoveEntityRequest(EntityTypeNames.Work, work.Id, true, 100, 200, 50, 30)
        let moved = store.MoveEntitiesUi([ request ])
        Assert.Equal(1, moved)
        Assert.True(store.Works.[work.Id].Position.IsSome)

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
    let ``TabExists returns true for valid tab`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        Assert.True(store.TabExists(TabKind.System, system.Id))

    [<Fact>]
    let ``TryOpenTabForEntity returns tab info for system`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        let result = store.TryOpenTabForEntity(EntityTypeNames.System, system.Id)
        Assert.True(result.IsSome)
        Assert.Equal(TabKind.System, result.Value.Kind)

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
