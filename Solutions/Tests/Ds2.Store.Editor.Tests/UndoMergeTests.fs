module Ds2.Store.Editor.Tests.UndoMergeTests

open Xunit
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

let private noop () = ()
let private dummyRecord label = { Undo = noop; Redo = noop; Description = label }

[<Fact>]
let ``MergeTop combines two transactions into one`` () =
    let mgr = UndoRedoManager(10)

    mgr.Push({ Label = "First"; Records = [dummyRecord "a"] })
    mgr.Push({ Label = "Second"; Records = [dummyRecord "b"] })

    Assert.Equal(2, mgr.UndoLabels.Length)

    mgr.MergeTop(2, "Merged")

    Assert.Equal(1, mgr.UndoLabels.Length)
    Assert.Equal("Merged", mgr.UndoLabels.[0])

[<Fact>]
let ``MergeTop preserves record order for undo and redo`` () =
    let trace = ResizeArray<string>()

    let mgr = UndoRedoManager(10)
    let createRec = { Undo = fun () -> trace.Add("undo-create")
                      Redo = fun () -> trace.Add("redo-create")
                      Description = "c" }
    let moveRec =   { Undo = fun () -> trace.Add("undo-move")
                      Redo = fun () -> trace.Add("redo-move")
                      Description = "m" }
    mgr.Push({ Label = "Create"; Records = [createRec] })
    mgr.Push({ Label = "Move"; Records = [moveRec] })

    mgr.MergeTop(2, "Create+Move")

    let tx = mgr.PopUndo()
    Assert.True(tx.IsSome)
    for r in List.rev tx.Value.Records do r.Undo()

    Assert.Equal(2, trace.Count)
    Assert.Equal("undo-move", trace.[0])
    Assert.Equal("undo-create", trace.[1])

    trace.Clear()
    mgr.PushRedo(tx.Value)
    let redoTx = mgr.PopRedo()
    Assert.True(redoTx.IsSome)
    for r in redoTx.Value.Records do r.Redo()

    Assert.Equal(2, trace.Count)
    Assert.Equal("redo-create", trace.[0])
    Assert.Equal("redo-move", trace.[1])

[<Fact>]
let ``MergeTop with insufficient stack does nothing`` () =
    let mgr = UndoRedoManager(10)
    mgr.Push({ Label = "Only"; Records = [dummyRecord "x"] })

    mgr.MergeTop(2, "Merged")

    Assert.Equal(1, mgr.UndoLabels.Length)
    Assert.Equal("Only", mgr.UndoLabels.[0])

[<Fact>]
let ``MergeLastTransactions on store merges create and move into single undo`` () =
    let store = createStore()
    let _project, _system, flow, _work = setupBasicHierarchy store

    let w1Id = store.AddWork("W1", flow.Id)
    let w2Id = store.AddWork("W2", flow.Id)

    Assert.True(store.Works.ContainsKey(w1Id))
    Assert.True(store.Works.ContainsKey(w2Id))

    store.MergeLastTransactions(2, "Add 2 Works")

    // Single undo should remove both works
    store.Undo()

    Assert.False(store.Works.ContainsKey(w1Id))
    Assert.False(store.Works.ContainsKey(w2Id))

    // Single redo should restore both works
    store.Redo()

    Assert.True(store.Works.ContainsKey(w1Id))
    Assert.True(store.Works.ContainsKey(w2Id))
