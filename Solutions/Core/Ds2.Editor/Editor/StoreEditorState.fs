namespace Ds2.Editor

open System.Collections.Generic
open System.Runtime.CompilerServices
open Ds2.Store

type internal StoreEditorState = {
    UndoManager: UndoRedoManager
    EventBus: Event<EditorEvent>
    mutable SuppressEvents: bool
    mutable CurrentRecords: ResizeArray<UndoRecord> option
}

[<RequireQualifiedAccess>]
module internal StoreEditorState =
    let private table = ConditionalWeakTable<DsStore, StoreEditorState>()

    let get (store: DsStore) =
        table.GetValue(
            store,
            ConditionalWeakTable<DsStore, StoreEditorState>.CreateValueCallback(fun _ ->
                {
                    UndoManager = UndoRedoManager(100)
                    EventBus = Event<EditorEvent>()
                    SuppressEvents = false
                    CurrentRecords = None
                }))
