module Ds2.Store.Editor.Tests.StoreRevisionTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

// 핵심 invariant "1 transaction = Revision +=1" 회귀 방어.
// 이 invariant 가 깨지면 Revision 으로 변경을 감지하는 쪽(파일 감시·캐시)이 변경을 놓치거나
// 과다 반응한다.
//
// 검증 hook 3 지점 (§1):
//   - Authoring.fs:47  withTransaction commit 성공 시
//   - Authoring.fs:180 applyTransaction (undo/redo) 성공 시
//   - DsStore.fs:127   ApplyNewStore (load / replace)

let private noop () = ()
let private dummyRecord label : UndoRecord =
    { Undo = noop; Redo = noop; Description = label }

[<Fact>]
let ``Revision starts at 0`` () =
    let store = createStore ()
    Assert.Equal(0, store.Revision)

[<Fact>]
let ``Empty WithTransaction does not bump revision`` () =
    let store = createStore ()
    let revBefore = store.Revision
    store.WithTransaction("noop", fun () -> ())
    Assert.Equal(revBefore, store.Revision)

[<Fact>]
let ``WithTransaction with a single recorded op bumps revision once`` () =
    let store = createStore ()
    let revBefore = store.Revision
    store.WithTransaction("single", fun () ->
        StoreAuthoring.recordUndo store (dummyRecord "a"))
    Assert.Equal(revBefore + 1, store.Revision)

[<Fact>]
let ``WithTransaction with multiple recorded ops bumps revision once (batch invariant)`` () =
    // 핵심 invariant — `apply_operations` N op batch 가 단일 WithTransaction 안에서 처리되더라도
    // Revision 은 +1 만. doc §1.0 Case A (ImportPlanApply.fs:48-52) 의 회귀 방어.
    let store = createStore ()
    let revBefore = store.Revision
    store.WithTransaction("batch", fun () ->
        for i in 1 .. 5 do
            StoreAuthoring.recordUndo store (dummyRecord (sprintf "op%d" i)))
    Assert.Equal(revBefore + 1, store.Revision)

[<Fact>]
let ``Consecutive transactions each bump revision`` () =
    let store = createStore ()
    let revBefore = store.Revision
    store.WithTransaction("tx1", fun () ->
        StoreAuthoring.recordUndo store (dummyRecord "a"))
    store.WithTransaction("tx2", fun () ->
        StoreAuthoring.recordUndo store (dummyRecord "b"))
    Assert.Equal(revBefore + 2, store.Revision)

[<Fact>]
let ``Failed transaction does not bump revision (rollback)`` () =
    // throw 시 records.Count > 0 일 수 있으나 BumpRevision 은 try 블록 안 → catch 분기로 가서 skip.
    let store = createStore ()
    let revBefore = store.Revision
    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            store.WithTransaction("failing", fun () ->
                StoreAuthoring.recordUndo store (dummyRecord "a")
                invalidOp "intentional failure") |> ignore)
    Assert.Contains("intentional failure", ex.Message)
    Assert.Equal(revBefore, store.Revision)

[<Fact>]
let ``Undo bumps revision`` () =
    let store = createStore ()
    store.WithTransaction("tx", fun () ->
        StoreAuthoring.recordUndo store (dummyRecord "a"))
    let revBefore = store.Revision
    store.Undo()
    Assert.Equal(revBefore + 1, store.Revision)

[<Fact>]
let ``Redo bumps revision`` () =
    let store = createStore ()
    store.WithTransaction("tx", fun () ->
        StoreAuthoring.recordUndo store (dummyRecord "a"))
    store.Undo()
    let revBefore = store.Revision
    store.Redo()
    Assert.Equal(revBefore + 1, store.Revision)

[<Fact>]
let ``ReplaceStore bumps revision`` () =
    // ApplyNewStore hook (DsStore.fs:127) 회귀 방어.
    let store = createStore ()
    let revBefore = store.Revision
    let other = createStore ()
    addProject other "P" |> ignore
    store.ReplaceStore(other)
    Assert.Equal(revBefore + 1, store.Revision)

[<Fact>]
let ``AddProject extension call bumps revision once`` () =
    // store.AddProject 가 내부적으로 자체 WithTransaction 을 가지므로 1회 호출 = +1.
    // Nodes.fs:24-34 "자체 WithTransaction" 패턴이 hook 통과 보장.
    let store = createStore ()
    let revBefore = store.Revision
    addProject store "P1" |> ignore
    Assert.Equal(revBefore + 1, store.Revision)

[<Fact>]
let ``Nested WithTransaction is rejected and outer does not bump`` () =
    // doc §1.0 / Authoring.fs:32-33 — nested transaction 차단 invariant. 만약 nested 허용되면
    // outer 의 records.Count 가 inner 결과까지 누적되어 BumpRevision 횟수가 의미 변경됨.
    // outer 가 throw 로 빠져나가므로 revision 무변경 (rollback 분기).
    let store = createStore ()
    let revBefore = store.Revision
    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            store.WithTransaction("outer", fun () ->
                store.WithTransaction("inner", fun () ->
                    StoreAuthoring.recordUndo store (dummyRecord "x"))) |> ignore)
    Assert.Contains("Nested", ex.Message)
    Assert.Equal(revBefore, store.Revision)
