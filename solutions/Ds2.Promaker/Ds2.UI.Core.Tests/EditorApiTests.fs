module Ds2.UI.Core.Tests.EditorApiTests

open System
open Xunit
open Ds2.Core
open Ds2.UI.Core
open Ds2.UI.Core.Tests.TestHelpers

// =============================================================================
// 헬퍼
// =============================================================================

let private collectEvents (api: EditorApi) =
    let events = ResizeArray<EditorEvent>()
    api.OnEvent.Add(fun e -> events.Add(e))
    events

// =============================================================================
// Work CRUD + Undo/Redo
// =============================================================================

[<Fact>]
let ``AddWork should add to store and be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)

    Assert.True(store.Works.ContainsKey(work.Id))
    Assert.Equal("W1", store.Works.[work.Id].Name)

    api.Undo()
    Assert.False(store.Works.ContainsKey(work.Id))

    api.Redo()
    Assert.True(store.Works.ContainsKey(work.Id))
    Assert.Equal("W1", store.Works.[work.Id].Name)

[<Fact>]
let ``RemoveWork should remove from store and be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let workId = work.Id

    api.RemoveWork(workId)
    Assert.False(store.Works.ContainsKey(workId))

    api.Undo()
    Assert.True(store.Works.ContainsKey(workId))
    Assert.Equal("W1", store.Works.[workId].Name)

    api.Redo()
    Assert.False(store.Works.ContainsKey(workId))

[<Fact>]
let ``MoveWork should update position and be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let pos = Some(Xywh(100, 200, 80, 40))

    api.MoveWork(work.Id, pos)
    Assert.True(store.Works.[work.Id].Position.IsSome)

    api.Undo()
    Assert.True(store.Works.[work.Id].Position.IsNone)

    api.Redo()
    Assert.True(store.Works.[work.Id].Position.IsSome)

[<Fact>]
let ``MoveEntities should move multiple nodes in one undo step`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)

    let moved =
        api.MoveEntities([
            MoveEntityRequest("Work", w1.Id, Some(Xywh(100, 200, 120, 40)))
            MoveEntityRequest("Work", w2.Id, Some(Xywh(300, 200, 120, 40)))
        ])

    Assert.Equal(2, moved)
    Assert.True(store.Works.[w1.Id].Position.IsSome)
    Assert.True(store.Works.[w2.Id].Position.IsSome)
    Assert.Equal(100, store.Works.[w1.Id].Position.Value.X)
    Assert.Equal(200, store.Works.[w1.Id].Position.Value.Y)
    Assert.Equal(300, store.Works.[w2.Id].Position.Value.X)
    Assert.Equal(200, store.Works.[w2.Id].Position.Value.Y)

    // one undo should rollback all moved nodes from the same drag action
    api.Undo()
    Assert.True(store.Works.[w1.Id].Position.IsNone)
    Assert.True(store.Works.[w2.Id].Position.IsNone)

    api.Redo()
    Assert.True(store.Works.[w1.Id].Position.IsSome)
    Assert.True(store.Works.[w2.Id].Position.IsSome)
    Assert.Equal(100, store.Works.[w1.Id].Position.Value.X)
    Assert.Equal(200, store.Works.[w1.Id].Position.Value.Y)
    Assert.Equal(300, store.Works.[w2.Id].Position.Value.X)
    Assert.Equal(200, store.Works.[w2.Id].Position.Value.Y)

[<Fact>]
let ``MoveWork should not create command if position unchanged`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)

    api.MoveWork(work.Id, None) // same as initial — should be no-op
    // undo should take us back to before AddWork, not before MoveWork
    api.Undo() // undoes AddWork (not MoveWork, since no command was created)
    Assert.False(store.Works.ContainsKey(work.Id)) // work removed by undo

[<Fact>]
let ``RenameWork should update name and be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)

    api.RenameWork(work.Id, "W1_Renamed")
    Assert.Equal("W1_Renamed", store.Works.[work.Id].Name)

    api.Undo()
    Assert.Equal("W1", store.Works.[work.Id].Name)

    api.Redo()
    Assert.Equal("W1_Renamed", store.Works.[work.Id].Name)

// =============================================================================
// Call CRUD + Undo/Redo
// =============================================================================

[<Fact>]
let ``AddCall should add to store and be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCall("Dev", "C1", work.Id)

    Assert.True(store.Calls.ContainsKey(call.Id))

    api.Undo()
    Assert.False(store.Calls.ContainsKey(call.Id))

    api.Redo()
    Assert.True(store.Calls.ContainsKey(call.Id))

[<Fact>]
let ``RemoveCall should remove from store and be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCall("Dev", "C1", work.Id)
    let callId = call.Id

    api.RemoveCall(callId)
    Assert.False(store.Calls.ContainsKey(callId))

    api.Undo()
    Assert.True(store.Calls.ContainsKey(callId))

    api.Redo()
    Assert.False(store.Calls.ContainsKey(callId))

// =============================================================================
// Flow CRUD + Undo/Redo
// =============================================================================

[<Fact>]
let ``AddFlow should add to store and be undoable`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", system.Id)

    Assert.True(store.Flows.ContainsKey(flow.Id))

    api.Undo()
    Assert.False(store.Flows.ContainsKey(flow.Id))

    api.Redo()
    Assert.True(store.Flows.ContainsKey(flow.Id))

// =============================================================================
// System CRUD + Undo/Redo
// =============================================================================

[<Fact>]
let ``AddSystem should add to store and project list`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)

    Assert.True(store.Systems.ContainsKey(system.Id))
    Assert.True(project.ActiveSystemIds.Contains(system.Id))

    api.Undo()
    Assert.False(store.Systems.ContainsKey(system.Id))
    Assert.False(project.ActiveSystemIds.Contains(system.Id))

    api.Redo()
    Assert.True(store.Systems.ContainsKey(system.Id))
    Assert.True(project.ActiveSystemIds.Contains(system.Id))

// =============================================================================
// Arrow + Undo/Redo
// =============================================================================

[<Fact>]
let ``AddArrowBetweenWorks should add and be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    let arrow = api.AddArrowBetweenWorks(flow.Id, w1.Id, w2.Id, ArrowType.Start)

    Assert.True(store.ArrowWorks.ContainsKey(arrow.Id))

    api.Undo()
    Assert.False(store.ArrowWorks.ContainsKey(arrow.Id))

    api.Redo()
    Assert.True(store.ArrowWorks.ContainsKey(arrow.Id))

[<Fact>]
let ``RemoveArrows should delete multiple arrows in one undo step`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    let w3 = api.AddWork("W3", flow.Id)
    let a1 = api.AddArrowBetweenWorks(flow.Id, w1.Id, w2.Id, ArrowType.Start)
    let a2 = api.AddArrowBetweenWorks(flow.Id, w2.Id, w3.Id, ArrowType.Start)

    let removed = api.RemoveArrows([ a1.Id; a2.Id ])
    Assert.Equal(2, removed)
    Assert.Equal(0, store.ArrowWorks.Count)

    api.Undo()
    Assert.Equal(2, store.ArrowWorks.Count)
    Assert.True(store.ArrowWorks.ContainsKey(a1.Id))
    Assert.True(store.ArrowWorks.ContainsKey(a2.Id))

    api.Redo()
    Assert.Equal(0, store.ArrowWorks.Count)

[<Fact>]
let ``ReconnectArrow should reconnect work arrow and be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    let w3 = api.AddWork("W3", flow.Id)
    let arrow = api.AddArrowBetweenWorks(flow.Id, w1.Id, w2.Id, ArrowType.Start)

    let changed = api.ReconnectArrow(arrow.Id, true, w3.Id)
    Assert.True(changed)
    Assert.Equal(w3.Id, store.ArrowWorks.[arrow.Id].SourceId)
    Assert.Equal(w2.Id, store.ArrowWorks.[arrow.Id].TargetId)

    api.Undo()
    Assert.Equal(w1.Id, store.ArrowWorks.[arrow.Id].SourceId)
    Assert.Equal(w2.Id, store.ArrowWorks.[arrow.Id].TargetId)

    api.Redo()
    Assert.Equal(w3.Id, store.ArrowWorks.[arrow.Id].SourceId)
    Assert.Equal(w2.Id, store.ArrowWorks.[arrow.Id].TargetId)

[<Fact>]
let ``ReconnectArrow should reconnect call arrow and be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let c1 = api.AddCall("Dev", "C1", work.Id)
    let c2 = api.AddCall("Dev", "C2", work.Id)
    let c3 = api.AddCall("Dev", "C3", work.Id)
    let arrow = api.AddArrowBetweenCalls(flow.Id, c1.Id, c2.Id, ArrowType.Start)

    let changed = api.ReconnectArrow(arrow.Id, false, c3.Id)
    Assert.True(changed)
    Assert.Equal(c1.Id, store.ArrowCalls.[arrow.Id].SourceId)
    Assert.Equal(c3.Id, store.ArrowCalls.[arrow.Id].TargetId)

    api.Undo()
    Assert.Equal(c1.Id, store.ArrowCalls.[arrow.Id].SourceId)
    Assert.Equal(c2.Id, store.ArrowCalls.[arrow.Id].TargetId)

    api.Redo()
    Assert.Equal(c1.Id, store.ArrowCalls.[arrow.Id].SourceId)
    Assert.Equal(c3.Id, store.ArrowCalls.[arrow.Id].TargetId)

[<Fact>]
let ``ConnectSelectionInOrder should be undone as single action`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    let w3 = api.AddWork("W3", flow.Id)

    let created = api.ConnectSelectionInOrder([ w1.Id; w2.Id; w3.Id ], ArrowType.Start)
    Assert.Equal(2, created)
    Assert.Equal(2, store.ArrowWorks.Count)

    // one undo should rollback all arrows created by one connect action
    api.Undo()
    Assert.Equal(0, store.ArrowWorks.Count)
    Assert.True(store.Works.ContainsKey(w1.Id))
    Assert.True(store.Works.ContainsKey(w2.Id))
    Assert.True(store.Works.ContainsKey(w3.Id))

    api.Redo()
    Assert.Equal(2, store.ArrowWorks.Count)

// =============================================================================
// 캐스케이드 삭제 + Undo 복원
// =============================================================================

[<Fact>]
let ``RemoveWork should cascade delete calls and arrows`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    let c1 = api.AddCall("Dev", "C1", w1.Id)
    let arrow = api.AddArrowBetweenWorks(flow.Id, w1.Id, w2.Id, ArrowType.Start)

    let workCount = store.Works.Count
    let callCount = store.Calls.Count
    let arrowCount = store.ArrowWorks.Count

    api.RemoveWork(w1.Id)
    Assert.False(store.Works.ContainsKey(w1.Id))
    Assert.False(store.Calls.ContainsKey(c1.Id))
    Assert.False(store.ArrowWorks.ContainsKey(arrow.Id))

    // Undo should restore everything
    api.Undo()
    Assert.Equal(workCount, store.Works.Count)
    Assert.Equal(callCount, store.Calls.Count)
    Assert.Equal(arrowCount, store.ArrowWorks.Count)
    Assert.True(store.Works.ContainsKey(w1.Id))
    Assert.True(store.Calls.ContainsKey(c1.Id))
    Assert.True(store.ArrowWorks.ContainsKey(arrow.Id))

    // Redo should delete again
    api.Redo()
    Assert.False(store.Works.ContainsKey(w1.Id))
    Assert.False(store.Calls.ContainsKey(c1.Id))
    Assert.False(store.ArrowWorks.ContainsKey(arrow.Id))

[<Fact>]
let ``RemoveSystem should cascade and be fully restorable`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", system.Id)
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    let _c1 = api.AddCall("Dev", "C1", w1.Id)
    let _arrow = api.AddArrowBetweenWorks(flow.Id, w1.Id, w2.Id, ArrowType.Start)

    let systemCount = store.Systems.Count
    let flowCount = store.Flows.Count
    let workCount = store.Works.Count
    let callCount = store.Calls.Count
    let arrowCount = store.ArrowWorks.Count

    api.RemoveSystem(system.Id)
    Assert.Equal(0, store.Flows.Count)
    Assert.Equal(0, store.Works.Count)
    Assert.Equal(0, store.Calls.Count)
    Assert.Equal(0, store.ArrowWorks.Count)

    api.Undo()
    Assert.Equal(systemCount, store.Systems.Count)
    Assert.Equal(flowCount, store.Flows.Count)
    Assert.Equal(workCount, store.Works.Count)
    Assert.Equal(callCount, store.Calls.Count)
    Assert.Equal(arrowCount, store.ArrowWorks.Count)
    Assert.True(project.ActiveSystemIds.Contains(system.Id))

[<Fact>]
let ``RemoveFlow should cascade and be fully restorable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let _c1 = api.AddCall("Dev", "C1", w1.Id)

    let flowCount = store.Flows.Count
    let workCount = store.Works.Count
    let callCount = store.Calls.Count

    api.RemoveFlow(flow.Id)
    Assert.False(store.Flows.ContainsKey(flow.Id))
    Assert.Equal(0, store.Works.Count)
    Assert.Equal(0, store.Calls.Count)

    api.Undo()
    Assert.Equal(flowCount, store.Flows.Count)
    Assert.Equal(workCount, store.Works.Count)
    Assert.Equal(callCount, store.Calls.Count)

// =============================================================================
// 이벤트 발행 검증
// =============================================================================

[<Fact>]
let ``AddWork should emit WorkAdded and UndoRedoChanged events`` () =
    let _, api, _, _, flow = setupProjectSystemFlow()
    let events = collectEvents api

    api.AddWork("W1", flow.Id) |> ignore

    Assert.True(events |> Seq.exists (function WorkAdded _ -> true | _ -> false))
    Assert.True(events |> Seq.exists (function UndoRedoChanged _ -> true | _ -> false))

[<Fact>]
let ``Composite delete should emit StoreRefreshed`` () =
    let _, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let _c1 = api.AddCall("Dev", "C1", w1.Id)

    let events = collectEvents api
    api.RemoveWork(w1.Id) // cascade -> Composite -> StoreRefreshed

    Assert.True(events |> Seq.exists (function StoreRefreshed -> true | _ -> false))

[<Fact>]
let ``Undo of Composite should emit StoreRefreshed`` () =
    let _, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let _c1 = api.AddCall("Dev", "C1", w1.Id)
    api.RemoveWork(w1.Id)

    let events = collectEvents api
    api.Undo()

    Assert.True(events |> Seq.exists (function StoreRefreshed -> true | _ -> false))

[<Fact>]
let ``Validation rollback should emit StoreRefreshed and UndoRedoChanged`` () =
    let store, api = createApi()
    api.AddProject("Dup") |> ignore

    let events = collectEvents api
    events.Clear()

    let _ =
        Assert.Throws<InvalidOperationException>(fun () ->
            api.AddProject("Dup") |> ignore)

    Assert.Single(store.ProjectsReadOnly) |> ignore
    Assert.True(events |> Seq.exists (function StoreRefreshed -> true | _ -> false))
    Assert.True(events |> Seq.exists (function UndoRedoChanged _ -> true | _ -> false))

// =============================================================================
// Undo/Redo 상태
// =============================================================================

[<Fact>]
let ``CanUndo and CanRedo should reflect state`` () =
    let _, api, _, _, flow = setupProjectSystemFlow()

    Assert.True(api.CanUndo) // AddProject, AddSystem, AddFlow in stack
    api.AddWork("W1", flow.Id) |> ignore
    Assert.True(api.CanUndo)
    Assert.False(api.CanRedo)

    api.Undo()
    Assert.True(api.CanUndo)
    Assert.True(api.CanRedo)

// =============================================================================
// Project CRUD + Undo/Redo
// =============================================================================

[<Fact>]
let ``AddProject should add to store and be undoable`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")

    Assert.True(store.Projects.ContainsKey(project.Id))

    api.Undo()
    Assert.False(store.Projects.ContainsKey(project.Id))

    api.Redo()
    Assert.True(store.Projects.ContainsKey(project.Id))

// =============================================================================
// HW Component + Undo/Redo
// =============================================================================

[<Fact>]
let ``AddButton should add to store and be undoable`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let button = api.AddButton("B1", system.Id)

    Assert.True(store.HwButtons.ContainsKey(button.Id))

    api.Undo()
    Assert.False(store.HwButtons.ContainsKey(button.Id))

    api.Redo()
    Assert.True(store.HwButtons.ContainsKey(button.Id))

// =============================================================================
// ApiDef + Undo/Redo
// =============================================================================

[<Fact>]
let ``AddApiDef should add to store and be undoable`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let apiDef = api.AddApiDef("AD1", system.Id)

    Assert.True(store.ApiDefs.ContainsKey(apiDef.Id))

    api.Undo()
    Assert.False(store.ApiDefs.ContainsKey(apiDef.Id))

    api.Redo()
    Assert.True(store.ApiDefs.ContainsKey(apiDef.Id))

// =============================================================================
// UpdateWorkProperties + Undo/Redo
// =============================================================================

[<Fact>]
let ``UpdateWorkProperties should be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)

    let newProps = WorkProperties()
    newProps.Motion <- Some "Linear"
    newProps.NumRepeat <- 5

    api.UpdateWorkProperties(work.Id, newProps)
    Assert.Equal(Some "Linear", store.Works.[work.Id].Properties.Motion)
    Assert.Equal(5, store.Works.[work.Id].Properties.NumRepeat)

    api.Undo()
    Assert.Equal(None, store.Works.[work.Id].Properties.Motion)
    Assert.Equal(0, store.Works.[work.Id].Properties.NumRepeat)

    api.Redo()
    Assert.Equal(Some "Linear", store.Works.[work.Id].Properties.Motion)

[<Fact>]
let ``TryUpdateWorkDuration should be undoable and redoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)

    let updated = api.TryUpdateWorkDuration(work.Id, "00:00:05")
    Assert.True(updated)
    Assert.Equal(Some(TimeSpan.FromSeconds(5.0)), store.Works.[work.Id].Properties.Duration)

    api.Undo()
    Assert.Equal(None, store.Works.[work.Id].Properties.Duration)

    api.Redo()
    Assert.Equal(Some(TimeSpan.FromSeconds(5.0)), store.Works.[work.Id].Properties.Duration)

[<Fact>]
let ``Call property panel data should include Device ApiDef addresses and ValueSpec`` () =
    let _, api = createApi()
    let project = api.AddProject("P1")
    let activeSystem = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", activeSystem.Id)
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCall("Dev", "C1", work.Id)

    let deviceSystem = api.AddSystem("D1", project.Id, false)
    let apiDef = api.AddApiDef("ADV", deviceSystem.Id)

    let created = api.AddApiCallFromPanel(call.Id, apiDef.Id, "AC1", "OUT.0", "IN.0", "10", "")
    Assert.True(created.IsSome)

    let rows = api.GetCallApiCallsForPanel(call.Id)
    Assert.Single(rows) |> ignore
    let row = rows.Head
    Assert.Equal(apiDef.Id, row.ApiDefId)
    Assert.True(row.HasApiDef)
    Assert.Equal("D1.ADV", row.ApiDefDisplayName)
    Assert.Equal("OUT.0", row.OutputAddress)
    Assert.Equal("IN.0", row.InputAddress)
    Assert.Equal("10", row.ValueSpecText)

[<Fact>]
let ``UpdateApiCallFromPanel should be one undo step with full rollback`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let activeSystem = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", activeSystem.Id)
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCall("Dev", "C1", work.Id)
    let deviceSystem = api.AddSystem("D1", project.Id, false)
    let apiDef = api.AddApiDef("ADV", deviceSystem.Id)

    let created = api.AddApiCallFromPanel(call.Id, apiDef.Id, "AC1", "OUT.0", "IN.0", "10", "")
    Assert.True(created.IsSome)
    let apiCallId = created.Value

    let changed = api.UpdateApiCallFromPanel(call.Id, apiCallId, apiDef.Id, "AC2", "OUT.1", "IN.1", "true", "")
    Assert.True(changed)

    let (updatedApiCall, updatedValueSpec) =
        store.Calls.[call.Id].ApiCalls
        |> Seq.find (fun (ac, _) -> ac.Id = apiCallId)
    Assert.Equal("AC2", updatedApiCall.Name)
    Assert.Equal("OUT.1", updatedApiCall.OutTag.Value.Address)
    Assert.Equal("IN.1", updatedApiCall.InTag.Value.Address)
    match updatedValueSpec with
    | BoolValue(Single true) -> ()
    | _ -> failwith "Expected BoolValue(Single true)"

    // Composite update must rollback in one undo call.
    api.Undo()
    let (rolledBackApiCall, rolledBackValueSpec) =
        store.Calls.[call.Id].ApiCalls
        |> Seq.find (fun (ac, _) -> ac.Id = apiCallId)
    Assert.Equal("AC1", rolledBackApiCall.Name)
    Assert.Equal("OUT.0", rolledBackApiCall.OutTag.Value.Address)
    Assert.Equal("IN.0", rolledBackApiCall.InTag.Value.Address)
    match rolledBackValueSpec with
    | IntValue(Single 10) -> ()
    | _ -> failwith "Expected IntValue(Single 10)"

    api.Redo()
    let (redoneApiCall, redoneValueSpec) =
        store.Calls.[call.Id].ApiCalls
        |> Seq.find (fun (ac, _) -> ac.Id = apiCallId)
    Assert.Equal("AC2", redoneApiCall.Name)
    match redoneValueSpec with
    | BoolValue(Single true) -> ()
    | _ -> failwith "Expected BoolValue(Single true)"

// =============================================================================
// RemoveCall with ArrowBetweenCalls cascade
// =============================================================================

[<Fact>]
let ``RemoveCall should cascade delete ArrowBetweenCalls`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let c1 = api.AddCall("Dev", "C1", w1.Id)
    let c2 = api.AddCall("Dev", "C2", w1.Id)
    let arrow = api.AddArrowBetweenCalls(flow.Id, c1.Id, c2.Id, ArrowType.Start)

    api.RemoveCall(c1.Id)
    Assert.False(store.Calls.ContainsKey(c1.Id))
    Assert.False(store.ArrowCalls.ContainsKey(arrow.Id))

    api.Undo()
    Assert.True(store.Calls.ContainsKey(c1.Id))
    Assert.True(store.ArrowCalls.ContainsKey(arrow.Id))

// =============================================================================
// RemoveSystem should cascade ApiDef
// =============================================================================

[<Fact>]
let ``RemoveSystem should cascade delete ApiDefs`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let _flow = api.AddFlow("F1", system.Id)
    let apiDef = api.AddApiDef("AD1", system.Id)

    let apiDefCount = store.ApiDefs.Count

    api.RemoveSystem(system.Id)
    Assert.False(store.ApiDefs.ContainsKey(apiDef.Id))

    api.Undo()
    Assert.Equal(apiDefCount, store.ApiDefs.Count)
    Assert.True(store.ApiDefs.ContainsKey(apiDef.Id))

// =============================================================================
// ApiCall API + Undo/Redo
// =============================================================================

[<Fact>]
let ``AddApiCallToCall should add and be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCall("Dev", "C1", work.Id)
    let apiCall = ApiCall("AC1")
    let valueSpec = UndefinedValue

    api.AddApiCallToCall(call.Id, apiCall, valueSpec)
    Assert.True(store.ApiCalls.ContainsKey(apiCall.Id))
    Assert.Equal(1, call.ApiCalls.Count)

    api.Undo()
    Assert.False(store.ApiCalls.ContainsKey(apiCall.Id))
    Assert.Equal(0, call.ApiCalls.Count)

    api.Redo()
    Assert.True(store.ApiCalls.ContainsKey(apiCall.Id))
    Assert.Equal(1, call.ApiCalls.Count)

[<Fact>]
let ``RemoveApiCallFromCall should remove and be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCall("Dev", "C1", work.Id)
    let apiCall = ApiCall("AC1")
    let valueSpec = UndefinedValue

    api.AddApiCallToCall(call.Id, apiCall, valueSpec)
    api.RemoveApiCallFromCall(call.Id, apiCall.Id)
    Assert.False(store.ApiCalls.ContainsKey(apiCall.Id))
    Assert.Equal(0, call.ApiCalls.Count)

    api.Undo()
    Assert.True(store.ApiCalls.ContainsKey(apiCall.Id))
    Assert.Equal(1, call.ApiCalls.Count)

// =============================================================================
// RenameEntity — Project/HW 타입
// =============================================================================

[<Fact>]
let ``RenameEntity should work for Project and HW types`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let button = api.AddButton("B1", system.Id)

    api.RenameEntity(project.Id, "Project", "P1_Renamed")
    Assert.Equal("P1_Renamed", store.Projects.[project.Id].Name)

    api.RenameEntity(button.Id, "Button", "B1_Renamed")
    Assert.Equal("B1_Renamed", store.HwButtons.[button.Id].Name)

    api.Undo() // undo button rename
    Assert.Equal("B1", store.HwButtons.[button.Id].Name)

    api.Undo() // undo project rename
    Assert.Equal("P1", store.Projects.[project.Id].Name)

// =============================================================================
// RemoveProject cascade + Undo
// =============================================================================

[<Fact>]
let ``RemoveProject should cascade and be fully restorable`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", system.Id)
    let w1 = api.AddWork("W1", flow.Id)
    let _c1 = api.AddCall("Dev", "C1", w1.Id)
    let _apiDef = api.AddApiDef("AD1", system.Id)
    let _button = api.AddButton("B1", system.Id)

    let projectCount = store.Projects.Count
    let systemCount  = store.Systems.Count
    let flowCount    = store.Flows.Count
    let workCount    = store.Works.Count
    let callCount    = store.Calls.Count
    let apiDefCount  = store.ApiDefs.Count
    let buttonCount  = store.HwButtons.Count

    api.RemoveProject(project.Id)
    Assert.Equal(0, store.Projects.Count)
    Assert.Equal(0, store.Systems.Count)
    Assert.Equal(0, store.Flows.Count)
    Assert.Equal(0, store.Works.Count)
    Assert.Equal(0, store.Calls.Count)
    Assert.Equal(0, store.ApiDefs.Count)
    Assert.Equal(0, store.HwButtons.Count)

    api.Undo()
    Assert.Equal(projectCount, store.Projects.Count)
    Assert.Equal(systemCount,  store.Systems.Count)
    Assert.Equal(flowCount,    store.Flows.Count)
    Assert.Equal(workCount,    store.Works.Count)
    Assert.Equal(callCount,    store.Calls.Count)
    Assert.Equal(apiDefCount,  store.ApiDefs.Count)
    Assert.Equal(buttonCount,  store.HwButtons.Count)

// =============================================================================
// SaveToFile / LoadFromFile
// =============================================================================

[<Fact>]
let ``SaveToFile and LoadFromFile should round-trip store`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let _flow = api.AddFlow("F1", system.Id)

    let path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), sprintf "%O.json" (System.Guid.NewGuid()))
    try
        api.SaveToFile(path)

        // Load into fresh api
        let store2 = DsStore.empty()
        let api2 = EditorApi(store2)
        api2.LoadFromFile(path)

        Assert.Equal(store.Projects.Count, store2.Projects.Count)
        Assert.Equal(store.Systems.Count,  store2.Systems.Count)
        Assert.Equal(store.Flows.Count,    store2.Flows.Count)
        Assert.True(store2.Projects.ContainsKey(project.Id))

        // Undo stack should be cleared after load
        Assert.False(api2.CanUndo)
    finally
        if System.IO.File.Exists(path) then System.IO.File.Delete(path)

// =============================================================================
// maxUndoSize 제한
// =============================================================================

[<Fact>]
let ``UndoRedoManager should trim stack when exceeding maxSize`` () =
    let store = DsStore.empty()
    let api = EditorApi(store, maxUndoSize = 3)
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", system.Id)

    // 3 commands above, now add a 4th — oldest should be trimmed
    let _w1 = api.AddWork("W1", flow.Id)

    // We can undo 3 times (maxSize=3), not 4
    api.Undo() // undo AddWork
    api.Undo() // undo AddFlow
    api.Undo() // undo AddSystem
    Assert.False(api.CanUndo) // AddProject was trimmed

// =============================================================================
// Copy / Paste
// =============================================================================

[<Fact>]
let ``PasteEntity should duplicate Work with Calls ApiCalls and internal call arrows`` () =
    let store, api, _, system, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let c1 = api.AddCall("Dev", "C1", w1.Id)
    let c2 = api.AddCall("Dev", "C2", w1.Id)
    api.AddArrowBetweenCalls(flow.Id, c1.Id, c2.Id, ArrowType.Start) |> ignore

    let apiDef = api.AddApiDef("AD1", system.Id)
    let ac = ApiCall("AC1")
    ac.ApiDefId <- Some apiDef.Id
    api.AddApiCallToCall(c1.Id, ac, UndefinedValue)

    let pastedOpt = api.PasteEntity("Work", w1.Id, "Flow", flow.Id)
    Assert.True(pastedOpt.IsSome)

    let pastedWorkId = pastedOpt.Value
    Assert.True(store.Works.ContainsKey(pastedWorkId))

    let pastedCalls = DsQuery.callsOf pastedWorkId store
    Assert.Equal(2, pastedCalls.Length)
    Assert.True(pastedCalls |> List.exists (fun c -> c.ApiCalls.Count = 1))

    // original 1 + pasted 1
    Assert.Equal(2, store.ArrowCalls.Count)

[<Fact>]
let ``PasteEntity should preserve original names`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let c1 = api.AddCall("Dev", "C1", w1.Id)

    let pastedWorkOpt = api.PasteEntity("Work", w1.Id, "Flow", flow.Id)
    Assert.True(pastedWorkOpt.IsSome)

    let pastedWork = store.Works.[pastedWorkOpt.Value]
    Assert.Equal("W1", pastedWork.Name)

    let pastedCallOpt = api.PasteEntity("Call", c1.Id, "Work", w1.Id)
    Assert.True(pastedCallOpt.IsSome)

    let pastedCall = store.Calls.[pastedCallOpt.Value]
    Assert.Equal("Dev.C1", pastedCall.Name)

[<Fact>]
let ``PasteEntities should preserve arrows between selected works and calls`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    api.MoveWork(w1.Id, Some(Xywh(100, 100, 120, 40)))
    api.MoveWork(w2.Id, Some(Xywh(300, 100, 120, 40)))
    api.AddArrowBetweenWorks(flow.Id, w1.Id, w2.Id, ArrowType.Start) |> ignore

    let c1 = api.AddCall("Dev", "C1", w1.Id)
    let c2 = api.AddCall("Dev", "C2", w2.Id)
    api.MoveCall(c1.Id, Some(Xywh(120, 180, 120, 40)))
    api.MoveCall(c2.Id, Some(Xywh(320, 180, 120, 40)))
    api.AddArrowBetweenCalls(flow.Id, c1.Id, c2.Id, ArrowType.Start) |> ignore

    let workIdsBefore =
        store.Works.Keys
        |> Seq.map id
        |> Set.ofSeq

    let pastedCount = api.PasteEntities("Work", [ w1.Id; w2.Id ], "Flow", flow.Id)
    Assert.Equal(2, pastedCount)

    let pastedWorks =
        store.Works.Values
        |> Seq.filter (fun w -> not (workIdsBefore.Contains w.Id))
        |> Seq.toList

    Assert.Equal(2, pastedWorks.Length)

    let pastedW1Opt =
        pastedWorks
        |> List.tryFind (fun w ->
            match w.Position with
            | Some p -> p.X = 130 && p.Y = 130
            | None -> false)

    let pastedW2Opt =
        pastedWorks
        |> List.tryFind (fun w ->
            match w.Position with
            | Some p -> p.X = 330 && p.Y = 130
            | None -> false)

    Assert.True(pastedW1Opt.IsSome)
    Assert.True(pastedW2Opt.IsSome)

    let pastedW1 = pastedW1Opt.Value
    let pastedW2 = pastedW2Opt.Value

    let pastedC1Opt =
        DsQuery.callsOf pastedW1.Id store
        |> List.tryFind (fun c ->
            match c.Position with
            | Some p -> p.X = 150 && p.Y = 210
            | None -> false)

    let pastedC2Opt =
        DsQuery.callsOf pastedW2.Id store
        |> List.tryFind (fun c ->
            match c.Position with
            | Some p -> p.X = 350 && p.Y = 210
            | None -> false)

    Assert.True(pastedC1Opt.IsSome)
    Assert.True(pastedC2Opt.IsSome)

    let pastedC1 = pastedC1Opt.Value
    let pastedC2 = pastedC2Opt.Value

    let hasPastedWorkArrow =
        store.ArrowWorks.Values
        |> Seq.exists (fun a ->
            a.ParentId = flow.Id
            && a.SourceId = pastedW1.Id
            && a.TargetId = pastedW2.Id)

    let hasPastedCallArrow =
        store.ArrowCalls.Values
        |> Seq.exists (fun a ->
            a.ParentId = flow.Id
            && a.SourceId = pastedC1.Id
            && a.TargetId = pastedC2.Id)

    Assert.True(hasPastedWorkArrow)
    Assert.True(hasPastedCallArrow)

[<Fact>]
let ``PasteEntity should duplicate Flow with Works Calls and arrows`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let sourceFlow = api.AddFlow("F1", system.Id)
    let w1 = api.AddWork("W1", sourceFlow.Id)
    let w2 = api.AddWork("W2", sourceFlow.Id)
    let c1 = api.AddCall("Dev", "C1", w1.Id)
    let c2 = api.AddCall("Dev", "C2", w1.Id)
    api.AddArrowBetweenWorks(sourceFlow.Id, w1.Id, w2.Id, ArrowType.Start) |> ignore
    api.AddArrowBetweenCalls(sourceFlow.Id, c1.Id, c2.Id, ArrowType.Start) |> ignore

    let pastedOpt = api.PasteEntity("Flow", sourceFlow.Id, "System", system.Id)
    Assert.True(pastedOpt.IsSome)

    let pastedFlowId = pastedOpt.Value
    Assert.True(store.Flows.ContainsKey(pastedFlowId))

    let pastedWorks = DsQuery.worksOf pastedFlowId store
    Assert.Equal(2, pastedWorks.Length)
    let pastedCalls = pastedWorks |> List.collect (fun w -> DsQuery.callsOf w.Id store)
    Assert.Equal(2, pastedCalls.Length)

    // original 1 + pasted 1
    Assert.Equal(2, store.ArrowWorks.Count)
    Assert.Equal(2, store.ArrowCalls.Count)

// =============================================================================
// AddCallsWithDevice — 자동 생성 ApiDef 기본 속성
// =============================================================================

[<Fact>]
let ``AddCallsWithDevice should set ApiDef IsPush=true and TxGuid to same-name Work`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let activeSystem = api.AddSystem("Active", project.Id, true)
    let flow = api.AddFlow("F1", activeSystem.Id)
    let work = api.AddWork("W1", flow.Id)

    api.AddCallsWithDevice project.Id work.Id [ "Dev1.ADV"; "Dev1.RET" ] true

    let passiveSystem = DsQuery.passiveSystemsOf project.Id store |> List.exactlyOne
    let apiDefs = DsQuery.apiDefsOf passiveSystem.Id store

    Assert.Equal(2, apiDefs.Length)

    let advDef = apiDefs |> List.find (fun d -> d.Name = "ADV")
    Assert.True(advDef.Properties.IsPush)
    Assert.True(advDef.Properties.TxGuid.IsSome)

    let txWork = store.Works.[advDef.Properties.TxGuid.Value]
    Assert.Equal("ADV", txWork.Name)

    let retDef = apiDefs |> List.find (fun d -> d.Name = "RET")
    Assert.True(retDef.Properties.IsPush)
    Assert.True(retDef.Properties.TxGuid.IsSome)
    Assert.Equal("RET", store.Works.[retDef.Properties.TxGuid.Value].Name)

[<Fact>]
let ``AddCallsWithDevice existing system should not overwrite existing ApiDef properties`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let activeSystem = api.AddSystem("Active", project.Id, true)
    let flow = api.AddFlow("F1", activeSystem.Id)
    let work = api.AddWork("W1", flow.Id)

    // 첫 번째 배치로 자동 생성
    api.AddCallsWithDevice project.Id work.Id [ "Dev1.ADV" ] true

    let passiveSystem = DsQuery.passiveSystemsOf project.Id store |> List.exactlyOne
    let advDefFirst = DsQuery.apiDefsOf passiveSystem.Id store |> List.exactlyOne

    // 두 번째 배치 — 기존 시스템에 같은 ApiDef 재사용
    let work2 = api.AddWork("W2", flow.Id)
    api.AddCallsWithDevice project.Id work2.Id [ "Dev1.ADV" ] true

    // ApiDef가 중복 생성되지 않아야 함
    Assert.Equal(1, DsQuery.apiDefsOf passiveSystem.Id store |> List.length)
    // ID 동일 (기존 것 재사용)
    Assert.Equal(advDefFirst.Id, (DsQuery.apiDefsOf passiveSystem.Id store |> List.exactlyOne).Id)

[<Fact>]
let ``AddCallsWithDevice should create ResetReset arrows between consecutive Works in new system flow`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let activeSystem = api.AddSystem("Active", project.Id, true)
    let flow = api.AddFlow("F1", activeSystem.Id)
    let work = api.AddWork("W1", flow.Id)

    api.AddCallsWithDevice project.Id work.Id [ "Dev1.ADV"; "Dev1.RET"; "Dev1.HOME" ] true

    let passiveSystem = DsQuery.passiveSystemsOf project.Id store |> List.exactlyOne
    let passiveFlow = DsQuery.flowsOf passiveSystem.Id store |> List.exactlyOne
    let passiveWorks = DsQuery.worksOf passiveFlow.Id store

    Assert.Equal(3, passiveWorks.Length)

    // 3개 Work → 2개 ResetReset 화살표
    let arrows =
        store.ArrowWorks.Values
        |> Seq.filter (fun a -> a.ParentId = passiveFlow.Id)
        |> Seq.toList

    Assert.Equal(2, arrows.Length)
    Assert.True(arrows |> List.forall (fun a -> a.ArrowType = ArrowType.ResetReset))

    // 연결 체인 검증: ADV→RET→HOME 순서
    let advWork  = passiveWorks |> List.find (fun w -> w.Name = "ADV")
    let retWork  = passiveWorks |> List.find (fun w -> w.Name = "RET")
    let homeWork = passiveWorks |> List.find (fun w -> w.Name = "HOME")

    Assert.True(arrows |> List.exists (fun a -> a.SourceId = advWork.Id  && a.TargetId = retWork.Id))
    Assert.True(arrows |> List.exists (fun a -> a.SourceId = retWork.Id  && a.TargetId = homeWork.Id))

[<Fact>]
let ``AddCallsWithDevice with single Work should create no arrows`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let activeSystem = api.AddSystem("Active", project.Id, true)
    let flow = api.AddFlow("F1", activeSystem.Id)
    let work = api.AddWork("W1", flow.Id)

    api.AddCallsWithDevice project.Id work.Id [ "Dev1.ADV" ] true

    let passiveSystem = DsQuery.passiveSystemsOf project.Id store |> List.exactlyOne
    let passiveFlow = DsQuery.flowsOf passiveSystem.Id store |> List.exactlyOne

    let arrows =
        store.ArrowWorks.Values
        |> Seq.filter (fun a -> a.ParentId = passiveFlow.Id)
        |> Seq.toList

    Assert.Equal(0, arrows.Length)

// =============================================================================
// Paste Undo — BatchExec 보장 (Paste 1회 = Undo 1회)
// =============================================================================

[<Fact>]
let ``PasteEntity Work with Calls should be exactly 1 undo step`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("OrigWork", flow.Id)
    api.AddCall("Dev", "A", work.Id) |> ignore
    api.AddCall("Dev", "B", work.Id) |> ignore

    // Paste: Work + 2 Calls → BatchExec Composite → Undo 1회
    api.PasteEntity("Work", work.Id, "Flow", flow.Id) |> ignore
    Assert.Equal(2, DsQuery.worksOf flow.Id store |> List.length)

    api.Undo()  // 1회 Undo → 붙여넣기 결과(Work + 2 Calls) 전부 제거
    let worksAfterUndo = DsQuery.worksOf flow.Id store
    Assert.Equal(1, worksAfterUndo.Length)
    Assert.Equal(work.Id, worksAfterUndo.[0].Id)

[<Fact>]
let ``PasteEntities Works batch should be exactly 1 undo step`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)

    let count = api.PasteEntities("Work", [| w1.Id; w2.Id |], "Flow", flow.Id)
    Assert.Equal(2, count)
    Assert.Equal(4, DsQuery.worksOf flow.Id store |> List.length)

    api.Undo()  // 1회 Undo → 2개 Work 동시 제거
    Assert.Equal(2, DsQuery.worksOf flow.Id store |> List.length)

[<Fact>]
let ``PasteEntity Call should be exactly 1 undo step`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCall("Dev", "API", work.Id)

    api.PasteEntity("Call", call.Id, "Work", work.Id) |> ignore
    Assert.Equal(2, DsQuery.callsOf work.Id store |> List.length)

    api.Undo()  // 1회 Undo → 붙여넣기 결과 제거
    Assert.Equal(1, DsQuery.callsOf work.Id store |> List.length)
    Assert.Equal(call.Id, (DsQuery.callsOf work.Id store).[0].Id)

// =============================================================================
// Call Timeout — TryUpdateCallTimeout Undo/Redo
// =============================================================================

[<Fact>]
let ``TryUpdateCallTimeout should set timeout and support undo redo`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", system.Id)
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCall("Dev", "C1", work.Id)

    Assert.Equal("", api.GetCallTimeoutText(call.Id))

    let ok = api.TryUpdateCallTimeout(call.Id, "500")
    Assert.True(ok)
    Assert.Equal("500", api.GetCallTimeoutText(call.Id))
    Assert.Equal(Some(System.TimeSpan.FromMilliseconds(500.0)), store.Calls.[call.Id].Properties.Timeout)

    api.Undo()
    Assert.Equal("", api.GetCallTimeoutText(call.Id))
    Assert.Equal(None, store.Calls.[call.Id].Properties.Timeout)

    api.Redo()
    Assert.Equal("500", api.GetCallTimeoutText(call.Id))
    Assert.Equal(Some(System.TimeSpan.FromMilliseconds(500.0)), store.Calls.[call.Id].Properties.Timeout)

[<Fact>]
let ``TryUpdateCallTimeout with empty string should clear timeout`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", system.Id)
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCall("Dev", "C1", work.Id)

    api.TryUpdateCallTimeout(call.Id, "1000") |> ignore
    Assert.Equal("1000", api.GetCallTimeoutText(call.Id))

    let ok = api.TryUpdateCallTimeout(call.Id, "")
    Assert.True(ok)
    Assert.Equal("", api.GetCallTimeoutText(call.Id))
    Assert.Equal(None, store.Calls.[call.Id].Properties.Timeout)

[<Fact>]
let ``TryUpdateCallTimeout with invalid text should return false`` () =
    let _, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", system.Id)
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCall("Dev", "C1", work.Id)

    let ok = api.TryUpdateCallTimeout(call.Id, "not-a-number")
    Assert.False(ok)

// =============================================================================
// AddCallWithLinkedApiDefs — 단일 Undo 스텝 보장
// =============================================================================

[<Fact>]
let ``AddCallWithLinkedApiDefs should create one Call with N ApiCalls in one undo step`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let activeSystem = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", activeSystem.Id)
    let work = api.AddWork("W1", flow.Id)
    let deviceSystem = api.AddSystem("D1", project.Id, false)
    let apiDef1 = api.AddApiDef("ADV", deviceSystem.Id)
    let apiDef2 = api.AddApiDef("RET", deviceSystem.Id)

    let call = api.AddCallWithLinkedApiDefs work.Id "Cylinder" "move" [| apiDef1.Id; apiDef2.Id |]
    Assert.Equal("Cylinder.move", call.Name)

    let calls = DsQuery.callsOf work.Id store
    Assert.Equal(1, calls.Length)
    Assert.Equal(call.Id, calls.[0].Id)

    let apiCalls = store.Calls.[call.Id].ApiCalls
    Assert.Equal(2, apiCalls.Count)
    Assert.True(apiCalls |> Seq.exists (fun (ac, _) -> ac.ApiDefId = Some apiDef1.Id))
    Assert.True(apiCalls |> Seq.exists (fun (ac, _) -> ac.ApiDefId = Some apiDef2.Id))

    // 전체가 단 1회의 Undo로 되돌려짐
    api.Undo()
    Assert.Equal(0, DsQuery.callsOf work.Id store |> List.length)

    api.Redo()
    Assert.Equal(1, DsQuery.callsOf work.Id store |> List.length)
    Assert.Equal(2, store.Calls.[call.Id].ApiCalls.Count)

[<Fact>]
let ``AddCallWithLinkedApiDefs with no apiDefIds should still create Call`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", system.Id)
    let work = api.AddWork("W1", flow.Id)

    let call = api.AddCallWithLinkedApiDefs work.Id "Dev" "op" [||]
    Assert.Equal("Dev.op", call.Name)

    let calls = DsQuery.callsOf work.Id store
    Assert.Equal(1, calls.Length)
    Assert.Equal(0, store.Calls.[call.Id].ApiCalls.Count)

