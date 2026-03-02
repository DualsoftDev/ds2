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

let private latestHistoryChanged (events: ResizeArray<EditorEvent>) =
    events
    |> Seq.choose (function HistoryChanged(undoLabels, redoLabels) -> Some(undoLabels, redoLabels) | _ -> None)
    |> Seq.tryLast

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

    api.RemoveEntities(seq { EntityTypeNames.Work, workId })
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

    api.MoveEntities([ MoveEntityRequest(EntityTypeNames.Work, work.Id, pos) ]) |> ignore
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

    api.MoveEntities([ MoveEntityRequest(EntityTypeNames.Work, work.Id, None) ]) |> ignore
    // undo should take us back to before AddWork, not before MoveWork
    api.Undo() // undoes AddWork (not MoveWork, since no command was created)
    Assert.False(store.Works.ContainsKey(work.Id)) // work removed by undo

[<Fact>]
let ``RenameEntity Work should update name and be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)

    api.RenameEntity(work.Id, EntityTypeNames.Work, "W1_Renamed")
    Assert.Equal("W1_Renamed", store.Works.[work.Id].Name)

    api.Undo()
    Assert.Equal("W1", store.Works.[work.Id].Name)

    api.Redo()
    Assert.Equal("W1_Renamed", store.Works.[work.Id].Name)

// =============================================================================
// Call CRUD + Undo/Redo
// =============================================================================

[<Fact>]
let ``AddCallWithLinkedApiDefs should add to store and be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCallWithLinkedApiDefs work.Id "Dev" "C1" [||]

    Assert.True(store.Calls.ContainsKey(call.Id))

    api.Undo()
    Assert.False(store.Calls.ContainsKey(call.Id))

    api.Redo()
    Assert.True(store.Calls.ContainsKey(call.Id))

[<Fact>]
let ``RemoveCall should remove from store and be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCallWithLinkedApiDefs work.Id "Dev" "C1" [||]
    let callId = call.Id

    api.RemoveEntities(seq { EntityTypeNames.Call, callId })
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
let ``ConnectSelectionInOrder adds a single work arrow and is undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    api.ConnectSelectionInOrder([w1.Id; w2.Id], ArrowType.Start) |> ignore
    let arrow = store.ArrowWorks.Values |> Seq.find (fun a -> a.SourceId = w1.Id && a.TargetId = w2.Id)

    Assert.True(store.ArrowWorks.ContainsKey(arrow.Id))

    api.Undo()
    Assert.False(store.ArrowWorks.ContainsKey(arrow.Id))

    api.Redo()
    Assert.True(store.ArrowWorks.ContainsKey(arrow.Id))

[<Fact>]
let ``ConnectSelectionInOrder connects cross-flow works in same system and produces path`` () =
    let store, api, _, system, flow1 = setupProjectSystemFlow()
    let flow2 = api.AddFlow("F2", system.Id)
    let w1 = api.AddWork("W1", flow1.Id)
    let w2 = api.AddWork("W2", flow2.Id)

    let created = api.ConnectSelectionInOrder([ w1.Id; w2.Id ], ArrowType.Start)
    Assert.Equal(1, created)

    let arrow = store.ArrowWorks.Values |> Seq.find (fun a -> a.SourceId = w1.Id && a.TargetId = w2.Id)
    Assert.Equal(flow1.Id, arrow.ParentId)

    let paths = api.GetFlowArrowPaths(flow1.Id)
    Assert.True(paths.ContainsKey(arrow.Id))

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
    api.ConnectSelectionInOrder([w1.Id; w2.Id], ArrowType.Start) |> ignore
    api.ConnectSelectionInOrder([w2.Id; w3.Id], ArrowType.Start) |> ignore
    let a1 = store.ArrowWorks.Values |> Seq.find (fun a -> a.SourceId = w1.Id && a.TargetId = w2.Id)
    let a2 = store.ArrowWorks.Values |> Seq.find (fun a -> a.SourceId = w2.Id && a.TargetId = w3.Id)

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
    api.ConnectSelectionInOrder([w1.Id; w2.Id], ArrowType.Start) |> ignore
    let arrow = store.ArrowWorks.Values |> Seq.find (fun a -> a.SourceId = w1.Id && a.TargetId = w2.Id)

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
    let c1 = api.AddCallWithLinkedApiDefs work.Id "Dev" "C1" [||]
    let c2 = api.AddCallWithLinkedApiDefs work.Id "Dev" "C2" [||]
    let c3 = api.AddCallWithLinkedApiDefs work.Id "Dev" "C3" [||]
    api.ConnectSelectionInOrder([c1.Id; c2.Id], ArrowType.Start) |> ignore
    let arrow = store.ArrowCalls.Values |> Seq.find (fun a -> a.SourceId = c1.Id && a.TargetId = c2.Id)

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
    let c1 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||]
    api.ConnectSelectionInOrder([w1.Id; w2.Id], ArrowType.Start) |> ignore
    let arrow = store.ArrowWorks.Values |> Seq.find (fun a -> a.SourceId = w1.Id && a.TargetId = w2.Id)

    let workCount = store.Works.Count
    let callCount = store.Calls.Count
    let arrowCount = store.ArrowWorks.Count

    api.RemoveEntities(seq { EntityTypeNames.Work, w1.Id })
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
    let _c1 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||]
    api.ConnectSelectionInOrder([w1.Id; w2.Id], ArrowType.Start) |> ignore

    let systemCount = store.Systems.Count
    let flowCount = store.Flows.Count
    let workCount = store.Works.Count
    let callCount = store.Calls.Count
    let arrowCount = store.ArrowWorks.Count

    api.RemoveEntities(seq { EntityTypeNames.System, system.Id })
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
    let _c1 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||]

    let flowCount = store.Flows.Count
    let workCount = store.Works.Count
    let callCount = store.Calls.Count

    api.RemoveEntities(seq { EntityTypeNames.Flow, flow.Id })
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
let ``AddWork should emit WorkAdded and HistoryChanged with label`` () =
    let _, api, _, _, flow = setupProjectSystemFlow()
    let events = collectEvents api

    api.AddWork("W1", flow.Id) |> ignore

    Assert.True(events |> Seq.exists (function WorkAdded _ -> true | _ -> false))
    let histOpt = events |> Seq.tryPick (function HistoryChanged(u, _) -> Some u | _ -> None)
    Assert.True(histOpt.IsSome)
    let undoLabels = histOpt.Value
    // undo 스택 최신 항목(앞)이 방금 추가한 Work 레이블
    Assert.True(undoLabels.Length > 0)
    Assert.Contains("W1", undoLabels[0])

[<Fact>]
let ``Composite delete should emit StoreRefreshed`` () =
    let _, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let _c1 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||]

    let events = collectEvents api
    api.RemoveEntities(seq { EntityTypeNames.Work, w1.Id }) // cascade -> Composite -> StoreRefreshed

    Assert.True(events |> Seq.exists (function StoreRefreshed -> true | _ -> false))

[<Fact>]
let ``Undo of Composite should emit StoreRefreshed`` () =
    let _, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let _c1 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||]
    api.RemoveEntities(seq { EntityTypeNames.Work, w1.Id })

    let events = collectEvents api
    api.Undo()

    Assert.True(events |> Seq.exists (function StoreRefreshed -> true | _ -> false))

[<Fact>]
let ``CommandExecutor undo should invert nested Composite in reverse order`` () =
    let store = DsStore.empty()
    let project = Ds2.Core.Project("P1")

    let cmd =
        Composite(
            "Outer",
            [
                AddProject project
                Composite(
                    "Inner",
                    [
                        RenameEntity(project.Id, EntityTypeNames.Project, "P1", "P2")
                    ])
            ])

    CommandExecutor.execute cmd store |> ignore
    Assert.True(store.Projects.ContainsKey(project.Id))
    Assert.Equal("P2", store.Projects.[project.Id].Name)

    CommandExecutor.undo cmd store |> ignore
    Assert.False(store.Projects.ContainsKey(project.Id))

[<Fact>]
let ``Validation rollback should emit StoreRefreshed and HistoryChanged`` () =
    let store, api = createApi()
    api.AddProject("Dup") |> ignore

    let events = collectEvents api
    events.Clear()

    let _ =
        Assert.Throws<InvalidOperationException>(fun () ->
            api.AddProject("Dup") |> ignore)

    Assert.Single(store.ProjectsReadOnly) |> ignore
    Assert.True(events |> Seq.exists (function StoreRefreshed -> true | _ -> false))
    Assert.True(events |> Seq.exists (function HistoryChanged _ -> true | _ -> false))

// =============================================================================
// Undo/Redo 상태
// =============================================================================

[<Fact>]
let ``HistoryChanged should reflect undo/redo availability`` () =
    let _, api, _, _, flow = setupProjectSystemFlow()
    let events = collectEvents api

    api.AddWork("W1", flow.Id) |> ignore
    match latestHistoryChanged events with
    | Some (undoLabels, redoLabels) ->
        Assert.True(undoLabels.Length > 0)
        Assert.Empty(redoLabels)
    | None -> failwith "Expected HistoryChanged after AddWork"

    api.Undo()
    match latestHistoryChanged events with
    | Some (undoLabels, redoLabels) ->
        Assert.True(undoLabels.Length > 0)
        Assert.True(redoLabels.Length > 0)
    | None -> failwith "Expected HistoryChanged after Undo"

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
    let call = api.AddCallWithLinkedApiDefs work.Id "Dev" "C1" [||]

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
    Assert.Equal("10", row.ValueSpecText)        // Int64Value(Single 10L) → "10"
    Assert.Equal(5, row.OutputSpecTypeIndex)     // Int64Value = 인덱스 5
    Assert.Equal(0, row.InputSpecTypeIndex)      // UndefinedValue = 인덱스 0

    // Float64 정수 값(10.0)은 format 시 소수점을 보장해야 LoadFromText 역파싱에서 정수 오인식 방지
    // OutputSpecTypeIndex=11(Float64)이 정확히 전달되어야 UI에서 float32로 오인식하지 않음
    let created2 = api.AddApiCallFromPanel(call.Id, apiDef.Id, "AC2", "F.0", "", "10.0", "")
    Assert.True(created2.IsSome)
    let rows2 = api.GetCallApiCallsForPanel(call.Id)
    let floatRow = rows2 |> List.find (fun r -> r.OutputAddress = "F.0")
    Assert.Equal("10.0", floatRow.ValueSpecText)    // Float64Value(Single 10.0) → "10.0" (소수점 보장)
    Assert.Equal(11, floatRow.OutputSpecTypeIndex)  // Float64Value = 인덱스 11 (float32=10과 구분)

[<Fact>]
let ``UpdateApiCallFromPanel should be one undo step with full rollback`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let activeSystem = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", activeSystem.Id)
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCallWithLinkedApiDefs work.Id "Dev" "C1" [||]
    let deviceSystem = api.AddSystem("D1", project.Id, false)
    let apiDef = api.AddApiDef("ADV", deviceSystem.Id)

    let created = api.AddApiCallFromPanel(call.Id, apiDef.Id, "AC1", "OUT.0", "IN.0", "10", "")
    Assert.True(created.IsSome)
    let apiCallId = created.Value

    let changed = api.UpdateApiCallFromPanel(call.Id, apiCallId, apiDef.Id, "AC2", "OUT.1", "IN.1", 1, "true", 0, "")
    Assert.True(changed)

    let updatedApiCall =
        store.Calls.[call.Id].ApiCalls
        |> Seq.find (fun ac -> ac.Id = apiCallId)
    Assert.Equal("AC2", updatedApiCall.Name)
    Assert.Equal("OUT.1", updatedApiCall.OutTag.Value.Address)
    Assert.Equal("IN.1", updatedApiCall.InTag.Value.Address)
    match updatedApiCall.OutputSpec with
    | BoolValue(Single true) -> ()
    | _ -> failwith "Expected BoolValue(Single true)"

    // Composite update must rollback in one undo call.
    api.Undo()
    let rolledBackApiCall =
        store.Calls.[call.Id].ApiCalls
        |> Seq.find (fun ac -> ac.Id = apiCallId)
    Assert.Equal("AC1", rolledBackApiCall.Name)
    Assert.Equal("OUT.0", rolledBackApiCall.OutTag.Value.Address)
    Assert.Equal("IN.0", rolledBackApiCall.InTag.Value.Address)
    match rolledBackApiCall.OutputSpec with
    | Int64Value(Single 10L) -> ()
    | _ -> failwith "Expected Int64Value(Single 10L)"

    api.Redo()
    let redoneApiCall =
        store.Calls.[call.Id].ApiCalls
        |> Seq.find (fun ac -> ac.Id = apiCallId)
    Assert.Equal("AC2", redoneApiCall.Name)
    match redoneApiCall.OutputSpec with
    | BoolValue(Single true) -> ()
    | _ -> failwith "Expected BoolValue(Single true)"

// =============================================================================
// RemoveCall with ArrowBetweenCalls cascade
// =============================================================================

[<Fact>]
let ``RemoveCall should cascade delete ArrowBetweenCalls`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let c1 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||]
    let c2 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C2" [||]
    api.ConnectSelectionInOrder([c1.Id; c2.Id], ArrowType.Start) |> ignore
    let arrow = store.ArrowCalls.Values |> Seq.find (fun a -> a.SourceId = c1.Id && a.TargetId = c2.Id)

    api.RemoveEntities(seq { EntityTypeNames.Call, c1.Id })
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

    api.RemoveEntities(seq { EntityTypeNames.System, system.Id })
    Assert.False(store.ApiDefs.ContainsKey(apiDef.Id))

    api.Undo()
    Assert.Equal(apiDefCount, store.ApiDefs.Count)
    Assert.True(store.ApiDefs.ContainsKey(apiDef.Id))

// =============================================================================
// ApiCall API + Undo/Redo
// =============================================================================

[<Fact>]
let ``RemoveApiCallFromCall should remove and be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCallWithLinkedApiDefs work.Id "Dev" "C1" [||]
    let apiCall = ApiCall("AC1")
    store.ApiCalls.[apiCall.Id] <- apiCall
    store.Calls.[call.Id].ApiCalls.Add(apiCall)

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
    let button = HwButton("B1", system.Id)
    store.HwButtons.[button.Id] <- button

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
    api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||] |> ignore
    let _apiDef = api.AddApiDef("AD1", system.Id)
    let button = HwButton("B1", system.Id)
    store.HwButtons.[button.Id] <- button

    let projectCount = store.Projects.Count
    let systemCount  = store.Systems.Count
    let flowCount    = store.Flows.Count
    let workCount    = store.Works.Count
    let callCount    = store.Calls.Count
    let apiDefCount  = store.ApiDefs.Count
    let buttonCount  = store.HwButtons.Count

    api.RemoveEntities(seq { EntityTypeNames.Project, project.Id })
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
        let events2 = collectEvents api2
        api2.LoadFromFile(path)

        Assert.Equal(store.Projects.Count, store2.Projects.Count)
        Assert.Equal(store.Systems.Count,  store2.Systems.Count)
        Assert.Equal(store.Flows.Count,    store2.Flows.Count)
        Assert.True(store2.Projects.ContainsKey(project.Id))

        // Undo stack should be cleared after load
        Assert.True(events2 |> Seq.exists (function HistoryChanged([], []) -> true | _ -> false))
    finally
        if System.IO.File.Exists(path) then System.IO.File.Delete(path)

// =============================================================================
// ReplaceStore
// =============================================================================

[<Fact>]
let ``ReplaceStore should replace all collections, clear undo stack, and emit StoreRefreshed`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let _flow = api.AddFlow("F1", system.Id)

    // 새 store에 프로젝트 추가 — Project(...) 직접 생성 시 EntityKind.Project DU 케이스와 이름 충돌
    let newStore = DsStore.empty()
    let newApi2 = EditorApi(newStore)
    let newProject = newApi2.AddProject("P2")

    let events = collectEvents api
    api.ReplaceStore(newStore)

    Assert.Equal(1, store.Projects.Count)
    Assert.True(store.Projects.ContainsKey(newProject.Id))
    Assert.False(store.Projects.ContainsKey(project.Id))
    Assert.True(events |> Seq.exists (function StoreRefreshed -> true | _ -> false))
    Assert.True(events |> Seq.exists (function HistoryChanged([], []) -> true | _ -> false))

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
    Assert.True(store.Projects.ContainsKey(project.Id)) // AddProject was trimmed
    Assert.Equal(0, store.Systems.Count)
    Assert.Equal(0, store.Flows.Count)
    Assert.Equal(0, store.Works.Count)

    // one more undo should be a no-op
    api.Undo()
    Assert.True(store.Projects.ContainsKey(project.Id))
    Assert.Equal(0, store.Systems.Count)

// =============================================================================
// UndoTo / RedoTo 점프
// =============================================================================

[<Fact>]
let ``UndoTo should jump multiple steps and emit single HistoryChanged`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let _ = api.AddWork("W1", flow.Id)
    let _ = api.AddWork("W2", flow.Id)
    let _ = api.AddWork("W3", flow.Id)

    let events = collectEvents api
    api.UndoTo(2)  // W3, W2 undo → W1만 남아야 함

    Assert.Equal(1, store.Works.Count)
    // suppress로 HistoryChanged는 1번만 발행
    let histChanges =
        events |> Seq.filter (function HistoryChanged _ -> true | _ -> false) |> Seq.toList
    Assert.Single(histChanges) |> ignore
    let _, redoLabels =
        match histChanges[0] with
        | HistoryChanged(u, r) -> u, r
        | _ -> failwith "unexpected"
    // undo 순서: W3→W2, 따라서 redo 스택은 W2(다음 redo 대상)가 앞
    Assert.Equal(2, List.length redoLabels)
    Assert.Contains("W2", redoLabels[0])
    Assert.Contains("W3", redoLabels[1])

[<Fact>]
let ``RedoTo should jump multiple steps and emit single HistoryChanged`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let _ = api.AddWork("W1", flow.Id)
    let _ = api.AddWork("W2", flow.Id)
    let _ = api.AddWork("W3", flow.Id)
    api.UndoTo(3)  // W1~W3 모두 undo

    let events = collectEvents api
    api.RedoTo(2)  // W1, W2 redo

    Assert.Equal(2, store.Works.Count)
    let histChanges =
        events |> Seq.filter (function HistoryChanged _ -> true | _ -> false) |> Seq.toList
    Assert.Single(histChanges) |> ignore
    let _, redoLabels =
        match histChanges[0] with
        | HistoryChanged(u, r) -> u, r
        | _ -> failwith "unexpected"
    // W3가 redo 스택에 남아 있어야 함
    Assert.Equal(1, List.length redoLabels)
    Assert.Contains("W3", redoLabels[0])

// =============================================================================
// Copy / Paste
// =============================================================================

[<Fact>]
let ``PasteEntities should duplicate Work with Calls ApiCalls and internal call arrows`` () =
    let store, api, _, system, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let c1 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||]
    let c2 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C2" [||]
    api.ConnectSelectionInOrder([c1.Id; c2.Id], ArrowType.Start) |> ignore

    let apiDef = api.AddApiDef("AD1", system.Id)
    let ac = ApiCall("AC1")
    ac.ApiDefId <- Some apiDef.Id
    store.ApiCalls.[ac.Id] <- ac
    store.Calls.[c1.Id].ApiCalls.Add(ac)

    let worksBefore = store.Works.Keys |> Set.ofSeq
    api.PasteEntities("Work", [| w1.Id |], "Flow", flow.Id) |> ignore
    let pastedWorkId = store.Works.Keys |> Seq.find (fun id -> not (worksBefore.Contains id))

    Assert.True(store.Works.ContainsKey(pastedWorkId))

    let pastedCalls = DsQuery.callsOf pastedWorkId store
    Assert.Equal(2, pastedCalls.Length)
    Assert.True(pastedCalls |> List.exists (fun c -> c.ApiCalls.Count = 1))

    // original 1 + pasted 1
    Assert.Equal(2, store.ArrowCalls.Count)

[<Fact>]
let ``PasteEntities should preserve original names`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let c1 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||]

    let worksBefore = store.Works.Keys |> Set.ofSeq
    api.PasteEntities("Work", [| w1.Id |], "Flow", flow.Id) |> ignore
    let pastedWork = store.Works.Values |> Seq.find (fun w -> not (worksBefore.Contains w.Id))
    Assert.Equal("W1", pastedWork.Name)

    let callsBefore = store.Calls.Keys |> Set.ofSeq
    api.PasteEntities("Call", [| c1.Id |], "Work", w1.Id) |> ignore
    let pastedCall = store.Calls.Values |> Seq.find (fun c -> not (callsBefore.Contains c.Id))
    Assert.Equal("Dev.C1", pastedCall.Name)

[<Fact>]
let ``PasteEntities should preserve arrows between selected works and calls`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    api.MoveEntities([ MoveEntityRequest(EntityTypeNames.Work, w1.Id, Some(Xywh(100, 100, 120, 40))) ]) |> ignore
    api.MoveEntities([ MoveEntityRequest(EntityTypeNames.Work, w2.Id, Some(Xywh(300, 100, 120, 40))) ]) |> ignore
    api.ConnectSelectionInOrder([w1.Id; w2.Id], ArrowType.Start) |> ignore

    let c1 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||]
    let c2 = api.AddCallWithLinkedApiDefs w2.Id "Dev" "C2" [||]
    api.MoveEntities([ MoveEntityRequest(EntityTypeNames.Call, c1.Id, Some(Xywh(120, 180, 120, 40))) ]) |> ignore
    api.MoveEntities([ MoveEntityRequest(EntityTypeNames.Call, c2.Id, Some(Xywh(320, 180, 120, 40))) ]) |> ignore
    api.ConnectSelectionInOrder([c1.Id; c2.Id], ArrowType.Start) |> ignore

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
let ``PasteEntities should duplicate Flow with Works Calls and arrows`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let sourceFlow = api.AddFlow("F1", system.Id)
    let w1 = api.AddWork("W1", sourceFlow.Id)
    let w2 = api.AddWork("W2", sourceFlow.Id)
    let c1 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||]
    let c2 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C2" [||]
    api.ConnectSelectionInOrder([w1.Id; w2.Id], ArrowType.Start) |> ignore
    api.ConnectSelectionInOrder([c1.Id; c2.Id], ArrowType.Start) |> ignore

    let flowsBefore = store.Flows.Keys |> Set.ofSeq
    api.PasteEntities("Flow", [| sourceFlow.Id |], "System", system.Id) |> ignore
    let pastedFlowId = store.Flows.Keys |> Seq.find (fun id -> not (flowsBefore.Contains id))

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
    // System 이름은 {flowName}_{devAlias} 형식
    Assert.Equal("F1_Dev1", passiveSystem.Name)
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
    // System 이름은 {flowName}_{devAlias} 형식
    Assert.Equal("F1_Dev1", passiveSystem.Name)
    let advDefFirst = DsQuery.apiDefsOf passiveSystem.Id store |> List.exactlyOne

    // 두 번째 배치 — 같은 flow이므로 기존 "F1_Dev1" 시스템 재사용
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
    // System 이름은 {flowName}_{devAlias} 형식
    Assert.Equal("F1_Dev1", passiveSystem.Name)
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
    // System 이름은 {flowName}_{devAlias} 형식
    Assert.Equal("F1_Dev1", passiveSystem.Name)
    let passiveFlow = DsQuery.flowsOf passiveSystem.Id store |> List.exactlyOne

    let arrows =
        store.ArrowWorks.Values
        |> Seq.filter (fun a -> a.ParentId = passiveFlow.Id)
        |> Seq.toList

    Assert.Equal(0, arrows.Length)

// =============================================================================
// PasteEntities DifferentFlow — Device System 복제/재사용
// =============================================================================

[<Fact>]
let ``PasteEntities Work to different Flow creates device system with target flow name prefix`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let activeSystem = api.AddSystem("S1", project.Id, true)
    let f1 = api.AddFlow("F1", activeSystem.Id)
    let f2 = api.AddFlow("F2", activeSystem.Id)
    let work = api.AddWork("W1", f1.Id)

    api.AddCallsWithDevice project.Id work.Id [ "Dev1.ADV" ] true

    // F1_Dev1 passive system 확인
    let f1Passive = DsQuery.passiveSystemsOf project.Id store |> List.exactlyOne
    Assert.Equal("F1_Dev1", f1Passive.Name)
    let f1ApiDef = DsQuery.apiDefsOf f1Passive.Id store |> List.exactlyOne

    // W1을 F2로 붙여넣기 (DifferentFlow)
    let worksBefore = store.Works.Keys |> Set.ofSeq
    api.PasteEntities("Work", [| work.Id |], "Flow", f2.Id) |> ignore
    let pastedWork = store.Works.Values |> Seq.find (fun w -> not (worksBefore.Contains w.Id))

    // F2_Dev1 passive system이 새로 생성되어야 함
    let passiveSystems = DsQuery.passiveSystemsOf project.Id store
    Assert.Equal(2, passiveSystems.Length)
    let f2Passive = passiveSystems |> List.find (fun s -> s.Name = "F2_Dev1")

    // 복제된 ApiDef가 F2_Dev1에 있어야 함
    let f2ApiDefs = DsQuery.apiDefsOf f2Passive.Id store
    Assert.Equal(1, f2ApiDefs.Length)
    Assert.Equal("ADV", f2ApiDefs.[0].Name)

    // 붙여넣어진 Call의 ApiCall이 F2_Dev1의 ApiDef를 가리켜야 함
    let pastedCalls = DsQuery.callsOf pastedWork.Id store
    Assert.Equal(1, pastedCalls.Length)
    let pastedApiCalls = pastedCalls.[0].ApiCalls
    Assert.Equal(1, pastedApiCalls.Count)
    Assert.Equal(Some f2ApiDefs.[0].Id, pastedApiCalls.[0].ApiDefId)
    // 원본 F1_Dev1의 ApiDef를 가리키지 않아야 함
    Assert.NotEqual(Some f1ApiDef.Id, pastedApiCalls.[0].ApiDefId)

[<Fact>]
let ``PasteEntities Work to different Flow reuses existing device system`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let activeSystem = api.AddSystem("S1", project.Id, true)
    let f1 = api.AddFlow("F1", activeSystem.Id)
    let f2 = api.AddFlow("F2", activeSystem.Id)
    let work = api.AddWork("W1", f1.Id)

    api.AddCallsWithDevice project.Id work.Id [ "Dev1.ADV" ] true

    // F2_Dev1 passive system 미리 수동 생성 (ADV ApiDef 포함)
    let existingF2System = api.AddSystem("F2_Dev1", project.Id, false)
    let existingF2ApiDef = api.AddApiDef("ADV", existingF2System.Id)

    // passiveSystems: F1_Dev1 + F2_Dev1
    Assert.Equal(2, DsQuery.passiveSystemsOf project.Id store |> List.length)

    // W1을 F2로 붙여넣기
    api.PasteEntities("Work", [| work.Id |], "Flow", f2.Id) |> ignore

    // 새 passive system이 생성되지 않아야 함 (재사용)
    Assert.Equal(2, DsQuery.passiveSystemsOf project.Id store |> List.length)

    // 붙여넣어진 Call의 ApiCall이 기존 F2_Dev1의 ApiDef를 가리켜야 함
    let worksBefore = [ work.Id ] |> Set.ofList
    let pastedWork = store.Works.Values |> Seq.find (fun w -> not (worksBefore.Contains w.Id) && w.ParentId = f2.Id)
    let pastedCalls = DsQuery.callsOf pastedWork.Id store
    Assert.Equal(1, pastedCalls.Length)
    let pastedApiCalls = pastedCalls.[0].ApiCalls
    Assert.Equal(1, pastedApiCalls.Count)
    Assert.Equal(Some existingF2ApiDef.Id, pastedApiCalls.[0].ApiDefId)

// =============================================================================
// PasteEntities Flow — Device System 복제 (pasteFlowToSystem 경로)
// 버그: pastedFlow는 exec 후에도 store snapshot에 없어서 makeDeviceFlowCtx = None → Device System 미생성
// 수정: makeDeviceFlowCtxDirect로 targetSystemId + pastedFlow.Name 직접 구성
// =============================================================================

[<Fact>]
let ``PasteEntities Flow to system in different project creates new device system with mapped ApiDefs`` () =
    let store, api = createApi()
    let p1 = api.AddProject("P1")
    let p2 = api.AddProject("P2")
    let s1 = api.AddSystem("S1", p1.Id, true)
    let s2 = api.AddSystem("S2", p2.Id, true)
    let f1 = api.AddFlow("F1", s1.Id)
    let work = api.AddWork("W1", f1.Id)

    api.AddCallsWithDevice p1.Id work.Id [ "Dev1.ADV" ] true

    // P1에 F1_Dev1 passive system 확인
    let p1Passive = DsQuery.passiveSystemsOf p1.Id store |> List.exactlyOne
    Assert.Equal("F1_Dev1", p1Passive.Name)
    let p1ApiDef = DsQuery.apiDefsOf p1Passive.Id store |> List.exactlyOne

    // F1(Flow)을 S2(P2 소속)에 붙여넣기 — pasteFlowToSystem 경로
    api.PasteEntities("Flow", [| f1.Id |], "System", s2.Id) |> ignore

    // P2에 새 F1_Dev1 passive system이 생성돼야 함
    let p2Passive = DsQuery.passiveSystemsOf p2.Id store |> List.exactlyOne
    Assert.Equal("F1_Dev1", p2Passive.Name)

    // P2의 ApiDef는 P1과 다른 ID여야 함
    let p2ApiDef = DsQuery.apiDefsOf p2Passive.Id store |> List.exactlyOne
    Assert.Equal("ADV", p2ApiDef.Name)
    Assert.NotEqual(p1ApiDef.Id, p2ApiDef.Id)

    // 붙여넣어진 Flow의 Work → Call → ApiCall이 P2의 ApiDef를 가리켜야 함
    let pastedFlow = DsQuery.flowsOf s2.Id store |> List.exactlyOne
    let pastedWork = DsQuery.worksOf pastedFlow.Id store |> List.exactlyOne
    let pastedCalls = DsQuery.callsOf pastedWork.Id store
    Assert.Equal(1, pastedCalls.Length)
    let pastedApiCalls = pastedCalls.[0].ApiCalls
    Assert.Equal(1, pastedApiCalls.Count)
    Assert.Equal(Some p2ApiDef.Id, pastedApiCalls.[0].ApiDefId)
    // P1의 ApiDef를 가리키면 안 됨 (수정 전 버그: copyApiCalls가 ApiDefId를 그대로 복사)
    Assert.NotEqual(Some p1ApiDef.Id, pastedApiCalls.[0].ApiDefId)

// =============================================================================
// Paste Undo — BatchExec 보장 (Paste 1회 = Undo 1회)
// =============================================================================

[<Fact>]
let ``PasteEntities Work with Calls should be exactly 1 undo step`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("OrigWork", flow.Id)
    api.AddCallWithLinkedApiDefs work.Id "Dev" "A" [||] |> ignore
    api.AddCallWithLinkedApiDefs work.Id "Dev" "B" [||] |> ignore

    // Paste: Work + 2 Calls → BatchExec Composite → Undo 1회
    api.PasteEntities("Work", [| work.Id |], "Flow", flow.Id) |> ignore
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
let ``PasteEntities Call should be exactly 1 undo step`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCallWithLinkedApiDefs work.Id "Dev" "API" [||]

    api.PasteEntities("Call", [| call.Id |], "Work", work.Id) |> ignore
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
    let call = api.AddCallWithLinkedApiDefs work.Id "Dev" "C1" [||]

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
    let call = api.AddCallWithLinkedApiDefs work.Id "Dev" "C1" [||]

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
    let call = api.AddCallWithLinkedApiDefs work.Id "Dev" "C1" [||]

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
    Assert.True(apiCalls |> Seq.exists (fun ac -> ac.ApiDefId = Some apiDef1.Id))
    Assert.True(apiCalls |> Seq.exists (fun ac -> ac.ApiDefId = Some apiDef2.Id))

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

// =============================================================================
// Regression (CRITIC Q8~Q12)
// =============================================================================

[<Fact>]
let ``Undo failure should restore undo stack and store snapshot`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let events = collectEvents api

    // force undo(AddWork) failure by corrupting current state
    store.Works.Remove(work.Id) |> ignore

    let _ =
        Assert.Throws<InvalidOperationException>(fun () ->
            api.Undo())

    match latestHistoryChanged events with
    | Some (undoLabels, redoLabels) ->
        Assert.True(undoLabels.Length > 0)
        Assert.Empty(redoLabels)
    | None -> failwith "Expected HistoryChanged after failed Undo"
    Assert.False(store.Works.ContainsKey(work.Id))

[<Fact>]
let ``Redo failure should restore redo stack and store snapshot`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let events = collectEvents api
    api.Undo()
    match latestHistoryChanged events with
    | Some (_, redoLabels) -> Assert.True(redoLabels.Length > 0)
    | None -> failwith "Expected HistoryChanged after Undo"

    // force redo(AddWork) failure by pre-inserting same ID
    store.Works.[work.Id] <- work

    let _ =
        Assert.Throws<InvalidOperationException>(fun () ->
            api.Redo())

    match latestHistoryChanged events with
    | Some (undoLabels, redoLabels) ->
        Assert.True(undoLabels.Length > 0)
        Assert.True(redoLabels.Length > 0)
    | None -> failwith "Expected HistoryChanged after failed Redo"
    Assert.True(store.Works.ContainsKey(work.Id))

[<Fact>]
let ``RemoveWork should keep shared ApiCall referenced by surviving Call`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    let c1 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||]
    let c2 = api.AddCallWithLinkedApiDefs w2.Id "Dev" "C2" [||]
    let shared = ApiCall("Shared")

    store.ApiCalls.[shared.Id] <- shared
    store.Calls.[c1.Id].ApiCalls.Add(shared)
    store.Calls.[c2.Id].ApiCalls.Add(shared)

    api.RemoveEntities(seq { EntityTypeNames.Work, w1.Id })

    Assert.True(store.Calls.ContainsKey(c2.Id))
    Assert.True(store.ApiCalls.ContainsKey(shared.Id))
    Assert.True(store.Calls.[c2.Id].ApiCalls |> Seq.exists (fun ac -> ac.Id = shared.Id))

[<Fact>]
let ``RemoveCall should keep ApiCall referenced only via CallCondition Conditions`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let c1 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||]
    let c2 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C2" [||]

    // c1의 CallCondition.Conditions에만 ApiCall 등록 (c1.ApiCalls에는 없음)
    let condAc = ApiCall("CondApiCall")
    store.ApiCalls.[condAc.Id] <- condAc
    let cc = CallCondition()
    cc.Conditions.Add(condAc)
    store.Calls.[c1.Id].CallConditions.Add(cc)

    // c2 삭제 → removeOrphanApiCalls 실행
    api.RemoveEntities(seq { EntityTypeNames.Call, c2.Id })

    // condAc는 c1.CallConditions에서 참조 중이므로 orphan 제거 대상이 아님
    Assert.True(store.ApiCalls.ContainsKey(condAc.Id))
    Assert.True(store.Calls.[c1.Id].CallConditions.[0].Conditions |> Seq.exists (fun ac -> ac.Id = condAc.Id))

// =============================================================================
// CallCondition CRUD + Undo
// =============================================================================

[<Fact>]
let ``AddCallCondition should add condition and be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCallWithLinkedApiDefs work.Id "Dev" "C1" [||]

    let ok = api.AddCallCondition(call.Id, CallConditionType.Auto)
    Assert.True(ok)
    Assert.Equal(1, store.Calls.[call.Id].CallConditions.Count)
    Assert.Equal(Some CallConditionType.Auto, store.Calls.[call.Id].CallConditions.[0].Type)

    api.Undo()
    Assert.Equal(0, store.Calls.[call.Id].CallConditions.Count)

    api.Redo()
    Assert.Equal(1, store.Calls.[call.Id].CallConditions.Count)

[<Fact>]
let ``UpdateCallConditionSettings IsOR toggle should be undoable`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCallWithLinkedApiDefs work.Id "Dev" "C1" [||]

    api.AddCallCondition(call.Id, CallConditionType.Common) |> ignore
    let condId = api.GetCallConditionsForPanel(call.Id).[0].ConditionId

    let ok = api.UpdateCallConditionSettings(call.Id, condId, true, false)
    Assert.True(ok)
    Assert.True(store.Calls.[call.Id].CallConditions.[0].IsOR)
    Assert.False(store.Calls.[call.Id].CallConditions.[0].IsRising)

    api.Undo()
    Assert.False(store.Calls.[call.Id].CallConditions.[0].IsOR)

[<Fact>]
let ``GetAllApiCallsForPanel returns ApiCalls across all Calls`` () =
    let _, api = createApi()
    let project = api.AddProject("P1")
    let activeSystem = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", activeSystem.Id)
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    let call1 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||]
    let call2 = api.AddCallWithLinkedApiDefs w2.Id "Dev" "C2" [||]
    let deviceSystem = api.AddSystem("D1", project.Id, false)
    let apiDef = api.AddApiDef("ADV", deviceSystem.Id)
    let id1 = (api.AddApiCallFromPanel(call1.Id, apiDef.Id, "AC1", "", "", "", "")).Value
    let id2 = (api.AddApiCallFromPanel(call2.Id, apiDef.Id, "AC2", "", "", "", "")).Value

    let all = api.GetAllApiCallsForPanel()
    let ids = all |> List.map (fun x -> x.ApiCallId)
    Assert.Equal(2, all.Length)
    Assert.Contains(id1, ids)
    Assert.Contains(id2, ids)

[<Fact>]
let ``AddApiCallsToConditionBatch adds multiple ApiCalls as single Undo unit`` () =
    let _, api = createApi()
    let project = api.AddProject("P1")
    let activeSystem = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", activeSystem.Id)
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    let srcCall  = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||]
    let condCall = api.AddCallWithLinkedApiDefs w2.Id "Dev" "C2" [||]
    let deviceSystem = api.AddSystem("D1", project.Id, false)
    let apiDef = api.AddApiDef("ADV", deviceSystem.Id)
    let srcId1 = (api.AddApiCallFromPanel(srcCall.Id, apiDef.Id, "AC1", "", "", "", "")).Value
    let srcId2 = (api.AddApiCallFromPanel(srcCall.Id, apiDef.Id, "AC2", "", "", "", "")).Value

    api.AddCallCondition(condCall.Id, CallConditionType.Active) |> ignore
    let condId = api.GetCallConditionsForPanel(condCall.Id).[0].ConditionId

    let added = api.AddApiCallsToConditionBatch(condCall.Id, condId, [| srcId1; srcId2 |])
    Assert.Equal(2, added)
    Assert.Equal(2, api.GetCallConditionsForPanel(condCall.Id).[0].Items.Length)

    api.Undo()  // 단일 Undo로 2개 모두 제거
    Assert.Equal(0, api.GetCallConditionsForPanel(condCall.Id).[0].Items.Length)

// =============================================================================
// RemoveEntities — 다중 선택 삭제 회귀 테스트
// =============================================================================

[<Fact>]
let ``RemoveEntities with two connected Works should not crash`` () =
    // W1 → W2 화살표가 있는 두 Work를 동시에 선택 삭제할 때 ArrowWork 중복 제거 검증
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    api.ConnectSelectionInOrder([w1.Id; w2.Id], ArrowType.Start) |> ignore

    let selections = seq { (EntityTypeNames.Work, w1.Id); (EntityTypeNames.Work, w2.Id) }
    api.RemoveEntities(selections)

    Assert.False(store.Works.ContainsKey(w1.Id))
    Assert.False(store.Works.ContainsKey(w2.Id))
    Assert.Empty(store.ArrowWorks)

[<Fact>]
let ``RemoveEntities with two connected Calls should not crash`` () =
    // C1 → C2 화살표가 있는 두 Call을 동시에 선택 삭제할 때 ArrowCall 중복 제거 검증
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let c1 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||]
    let c2 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C2" [||]
    api.ConnectSelectionInOrder([c1.Id; c2.Id], ArrowType.Start) |> ignore

    let selections = seq { (EntityTypeNames.Call, c1.Id); (EntityTypeNames.Call, c2.Id) }
    api.RemoveEntities(selections)

    Assert.False(store.Calls.ContainsKey(c1.Id))
    Assert.False(store.Calls.ContainsKey(c2.Id))
    Assert.Empty(store.ArrowCalls)

[<Fact>]
let ``RemoveEntities Work with its own Call selected should not double-remove Call arrows`` () =
    // Work와 그 아래 Call이 함께 선택될 때 Call은 Work cascade에 맡기고 중복 삭제 없어야 함
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let c1 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C1" [||]
    let c2 = api.AddCallWithLinkedApiDefs w1.Id "Dev" "C2" [||]
    api.ConnectSelectionInOrder([c1.Id; c2.Id], ArrowType.Start) |> ignore

    let selections = seq { (EntityTypeNames.Work, w1.Id); (EntityTypeNames.Call, c1.Id) }
    api.RemoveEntities(selections)

    Assert.False(store.Works.ContainsKey(w1.Id))
    Assert.False(store.Calls.ContainsKey(c1.Id))
    Assert.False(store.Calls.ContainsKey(c2.Id))
    Assert.Empty(store.ArrowCalls)

// =============================================================================
// ValueSpec 13케이스 — OutputSpecTypeIndex + tryParseAs roundtrip
// =============================================================================

[<Fact>]
let ``GetCallApiCallsForPanel should return correct OutputSpecTypeIndex for all 13 ValueSpec types`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", system.Id)
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCallWithLinkedApiDefs work.Id "Dev" "C1" [||]

    // 각 타입을 직접 store에 삽입
    let addAc (spec: ValueSpec) =
        let ac = ApiCall($"AC-{System.Guid.NewGuid()}")
        ac.OutputSpec <- spec
        store.ApiCalls.[ac.Id] <- ac
        store.Calls.[call.Id].ApiCalls.Add(ac)
        ac.Id

    let cases: (ValueSpec * int) list = [
        UndefinedValue,                           0
        BoolValue    (Single true),               1
        Int8Value    (Single 100y),               2
        Int16Value   (Single 1000s),              3
        Int32Value   (Single 100000),             4
        Int64Value   (Single 10000000000L),       5
        UInt8Value   (Single 200uy),              6
        UInt16Value  (Single 60000us),            7
        UInt32Value  (Single 4000000000u),        8
        UInt64Value  (Single 18000000000000000UL),9
        Float32Value (Single 3.14f),              10
        Float64Value (Single 3.14159265358979),   11
        StringValue  (Single "hello"),            12
    ]

    let acIds = cases |> List.map (fun (spec, _) -> addAc spec)

    let rows = api.GetCallApiCallsForPanel(call.Id)
    for (acId, (_, expectedIdx)) in List.zip acIds cases do
        let row = rows |> List.find (fun r -> r.ApiCallId = acId)
        Assert.Equal(expectedIdx, row.OutputSpecTypeIndex)

[<Fact>]
let ``UpdateApiCallFromPanel should preserve ValueSpec type through tryParseAs roundtrip`` () =
    // 버그 재현: Float64Value(Single 10.0)을 format 후 다시 update하면 Float32로 오인식되면 안 됨
    let store, api = createApi()
    let project = api.AddProject("P1")
    let activeSystem = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", activeSystem.Id)
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCallWithLinkedApiDefs work.Id "Dev" "C1" [||]
    let deviceSystem = api.AddSystem("D1", project.Id, false)
    let apiDef = api.AddApiDef("ADV", deviceSystem.Id)

    let roundtrip (spec: ValueSpec) =
        // store에 직접 삽입
        let ac = ApiCall($"AC")
        ac.OutputSpec <- spec
        store.ApiCalls.[ac.Id] <- ac
        store.Calls.[call.Id].ApiCalls.Add(ac)
        // format 텍스트 + typeIndex 가져오기 (다이얼로그 경로와 동일 조건)
        let row = api.GetCallApiCallsForPanel(call.Id) |> List.find (fun r -> r.ApiCallId = ac.Id)
        // typeIndex를 명시하여 UpdateApiCallFromPanel 호출 → specFromTypeIndex 기반 hint 사용
        api.UpdateApiCallFromPanel(call.Id, ac.Id, apiDef.Id, "AC", "", "", row.OutputSpecTypeIndex, row.ValueSpecText, 0, "") |> ignore
        // 결과 확인
        store.Calls.[call.Id].ApiCalls |> Seq.find (fun a -> a.Id = ac.Id)

    // Float32 vs Float64 구분 (핵심 버그 케이스)
    let afterFloat32 = roundtrip (Float32Value (Single 3.14f))
    match afterFloat32.OutputSpec with
    | Float32Value _ -> ()
    | other -> Assert.Fail($"Float32Value roundtrip failed: got {other}")

    let afterFloat64 = roundtrip (Float64Value (Single 3.14159265358979))
    match afterFloat64.OutputSpec with
    | Float64Value _ -> ()
    | other -> Assert.Fail($"Float64Value roundtrip failed: got {other}")

    // 정수 계열 — 힌트 타입 보존
    let afterInt8 = roundtrip (Int8Value (Single 100y))
    match afterInt8.OutputSpec with
    | Int8Value _ -> ()
    | other -> Assert.Fail($"Int8Value roundtrip failed: got {other}")

    let afterUInt32 = roundtrip (UInt32Value (Single 4000000000u))
    match afterUInt32.OutputSpec with
    | UInt32Value _ -> ()
    | other -> Assert.Fail($"UInt32Value roundtrip failed: got {other}")

    let afterBool = roundtrip (BoolValue (Single false))
    match afterBool.OutputSpec with
    | BoolValue (Single false) -> ()
    | other -> Assert.Fail($"BoolValue roundtrip failed: got {other}")

    let afterString = roundtrip (StringValue (Single "hello"))
    match afterString.OutputSpec with
    | StringValue (Single "hello") -> ()
    | other -> Assert.Fail($"StringValue roundtrip failed: got {other}")

    // Multiple — 쉼표 구분 텍스트가 Multiple로 역파싱되어야 함
    let afterInt32Multi = roundtrip (Int32Value (Multiple [1; 2; 3]))
    match afterInt32Multi.OutputSpec with
    | Int32Value (Multiple [1; 2; 3]) -> ()
    | other -> Assert.Fail($"Int32Value Multiple roundtrip failed: got {other}")

    let afterInt64Multi = roundtrip (Int64Value (Multiple [10L; 20L; 30L]))
    match afterInt64Multi.OutputSpec with
    | Int64Value (Multiple [10L; 20L; 30L]) -> ()
    | other -> Assert.Fail($"Int64Value Multiple roundtrip failed: got {other}")

    let afterFloat64Multi = roundtrip (Float64Value (Multiple [1.5; 2.5; 3.5]))
    match afterFloat64Multi.OutputSpec with
    | Float64Value (Multiple [1.5; 2.5; 3.5]) -> ()
    | other -> Assert.Fail($"Float64Value Multiple roundtrip failed: got {other}")

    let afterStringMulti = roundtrip (StringValue (Multiple ["a"; "b"; "c"]))
    match afterStringMulti.OutputSpec with
    | StringValue (Multiple ["a"; "b"; "c"]) -> ()
    | other -> Assert.Fail($"StringValue Multiple roundtrip failed: got {other}")

    // 타입 변경 케이스 — 기존 spec과 다른 typeIndex로 저장 시 올바른 타입으로 변경되어야 함
    // (버그 재현: 기존 hint 기반이면 StringValue → 계속 StringValue로 저장됨)
    let acTypeChange = ApiCall("ATC")
    acTypeChange.OutputSpec <- StringValue (Single "old")
    store.ApiCalls.[acTypeChange.Id] <- acTypeChange
    store.Calls.[call.Id].ApiCalls.Add(acTypeChange)
    // typeIndex=5 (Int64), text="42" → Int64Value(Single 42L) 로 저장되어야 함
    api.UpdateApiCallFromPanel(call.Id, acTypeChange.Id, apiDef.Id, "ATC", "", "", 5, "42", 0, "") |> ignore
    let afterTypeChange = store.Calls.[call.Id].ApiCalls |> Seq.find (fun a -> a.Id = acTypeChange.Id)
    match afterTypeChange.OutputSpec with
    | Int64Value (Single 42L) -> ()
    | other -> Assert.Fail($"Type change StringValue→Int64 failed: got {other}")

    // 타입 변경: Int64 → Float64
    let acF64 = ApiCall("AF64")
    acF64.OutputSpec <- Int64Value (Single 0L)
    store.ApiCalls.[acF64.Id] <- acF64
    store.Calls.[call.Id].ApiCalls.Add(acF64)
    // typeIndex=11 (Float64), text="3.14"
    api.UpdateApiCallFromPanel(call.Id, acF64.Id, apiDef.Id, "AF64", "", "", 11, "3.14", 0, "") |> ignore
    let afterF64Change = store.Calls.[call.Id].ApiCalls |> Seq.find (fun a -> a.Id = acF64.Id)
    match afterF64Change.OutputSpec with
    | Float64Value (Single _) -> ()
    | other -> Assert.Fail($"Type change Int64→Float64 failed: got {other}")
