module Ds2.Store.Editor.Tests.UndoCapacityTests

open System
open Xunit
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

// undo 이력이 건수만 제한되던 시절, 대량 삭제 1건이 엔티티 복사본 수천 개(변경 1회당 old·new 2벌)를
// 붙든 채 뒤이어 99건이 더 쌓일 때까지 풀리지 않았다 — 현장 PC 가 커밋 메모리 94% 에서 OOM 을 내던 경로.
// 아래는 그 용량 상한의 회귀 방어.

let private noop () = ()
let private dummyRecord label : UndoRecord = { Undo = noop; Redo = noop; Description = label }

let private tx label bytes =
    { Label = label
      Records = [dummyRecord label]
      AffectedEntityIds = []
      ApproxSnapshotBytes = bytes
      LightEventOnUndo = None }

let private labels (mgr: UndoRedoManager) = String.Join(",", mgr.UndoLabels)

[<Fact>]
let ``Snapshot budget drops the oldest transactions first`` () =
    let mgr = UndoRedoManager(100, 1000L)

    for i in 1 .. 5 do
        mgr.Push(tx (sprintf "op%d" i) 300L)

    // 300 x 5 = 1500 > 1000 → 예산에 맞을 때까지 꼬리부터 버려 3건(900) 만 남는다.
    Assert.Equal("op5,op4,op3", labels mgr)
    Assert.Equal(900L, mgr.ApproxSnapshotBytes)

[<Fact>]
let ``A single transaction larger than the whole budget stays undoable`` () =
    let mgr = UndoRedoManager(100, 1000L)

    mgr.Push(tx "small" 100L)
    mgr.Push(tx "huge" 50_000L)

    // 방금 한 대량 작업은 그 자체로 예산을 넘겨도 되돌릴 수 있어야 한다.
    Assert.Equal("huge", labels mgr)
    Assert.Equal(50_000L, mgr.ApproxSnapshotBytes)

[<Fact>]
let ``Count limit still applies when the budget is not reached`` () =
    let mgr = UndoRedoManager(3, Int64.MaxValue)

    for i in 1 .. 5 do
        mgr.Push(tx (sprintf "op%d" i) 0L)

    Assert.Equal("op5,op4,op3", labels mgr)

[<Fact>]
let ``Undo and redo move the recorded size with the transaction`` () =
    let mgr = UndoRedoManager(100, Int64.MaxValue)
    mgr.Push(tx "op" 700L)

    match mgr.PopUndo() with
    | Some t -> mgr.PushRedo(t)
    | None -> failwith "expected a transaction on the undo stack"

    // undo 스택에서 redo 스택으로 옮겨졌을 뿐 메모리는 그대로 붙들고 있다.
    Assert.Equal(700L, mgr.ApproxSnapshotBytes)

    mgr.Clear()
    Assert.Equal(0L, mgr.ApproxSnapshotBytes)

[<Fact>]
let ``MergeTop carries the merged sizes`` () =
    let mgr = UndoRedoManager(100, Int64.MaxValue)
    mgr.Push(tx "a" 100L)
    mgr.Push(tx "b" 250L)

    mgr.MergeTop(2, "merged")

    Assert.Equal("merged", labels mgr)
    Assert.Equal(350L, mgr.ApproxSnapshotBytes)

[<Fact>]
let ``Real edits record a non-zero snapshot size`` () =
    let store = createStore ()
    let projectId = store.AddProject("P")
    store.AddSystem("S", projectId, true) |> ignore

    // 예산 계산의 근거가 실제 편집 경로에서 채워지는지 — 여기가 0 이면 상한은 영원히 걸리지 않는다.
    let editorState = StoreEditorState.get store
    Assert.True(
        editorState.UndoManager.ApproxSnapshotBytes > 0L,
        "편집 트랜잭션이 스냅샷 크기를 기록하지 않았다")
