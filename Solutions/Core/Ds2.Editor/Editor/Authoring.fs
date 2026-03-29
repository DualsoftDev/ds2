namespace Ds2.Editor

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Store
open log4net

[<RequireQualifiedAccess>]
module internal StoreAuthoring =
    let private log = LogManager.GetLogger("Ds2.Editor.Authoring")

    let private state (store: DsStore) = StoreEditorState.get store

    let recordUndo (store: DsStore) (record: UndoRecord) =
        match (state store).CurrentRecords with
        | Some records -> records.Add(record)
        | None -> invalidOp "RecordUndo called outside transaction"

    let withTransaction (store: DsStore) (label: string) (action: unit -> 'T) : 'T =
        let editorState = state store
        if editorState.CurrentRecords.IsSome then
            invalidOp "Nested transactions are not supported"

        let records = ResizeArray<UndoRecord>()
        editorState.CurrentRecords <- Some records

        try
            try
                let result = action()
                if records.Count > 0 then
                    editorState.UndoManager.Push({ Label = label; Records = Seq.toList records })
                log.Debug($"Executed: {label}")
                result
            with ex ->
                editorState.CurrentRecords <- None
                for i in records.Count - 1 .. -1 .. 0 do
                    records.[i].Undo()
                log.Error($"Transaction failed: {label} - {ex.Message}", ex)
                reraise()
        finally
            editorState.CurrentRecords <- None

    let trackAdd<'T when 'T :> DsEntity> (store: DsStore) (dict: Dictionary<Guid, 'T>) (entity: 'T) =
        let backup = DeepCopyHelper.backupEntityAs entity
        dict.[entity.Id] <- entity
        recordUndo store {
            Undo = fun () -> dict.Remove(entity.Id) |> ignore
            Redo = fun () -> dict.[entity.Id] <- backup
            Description = $"Add {typeof<'T>.Name} {entity.Id}"
        }

    let trackRemove<'T when 'T :> DsEntity> (store: DsStore) (dict: Dictionary<Guid, 'T>) (id: Guid) =
        match dict.TryGetValue(id) with
        | true, entity ->
            let backup = DeepCopyHelper.backupEntityAs entity
            dict.Remove(id) |> ignore
            recordUndo store {
                Undo = fun () -> dict.[id] <- backup
                Redo = fun () -> dict.Remove(id) |> ignore
                Description = $"Remove {typeof<'T>.Name} {id}"
            }
        | false, _ -> ()

    let trackMutate<'T when 'T :> DsEntity> (store: DsStore) (dict: Dictionary<Guid, 'T>) (id: Guid) (mutate: 'T -> unit) =
        match dict.TryGetValue(id) with
        | true, entity ->
            let oldSnapshot = DeepCopyHelper.backupEntityAs entity
            mutate entity
            let newSnapshot = DeepCopyHelper.backupEntityAs entity
            recordUndo store {
                Undo = fun () -> dict.[id] <- oldSnapshot
                Redo = fun () -> dict.[id] <- newSnapshot
                Description = $"Mutate {typeof<'T>.Name} {id}"
            }
        | false, _ -> invalidOp $"Entity not found: {id}"

    let emitEvent (store: DsStore) (evt: EditorEvent) =
        let editorState = state store
        if not editorState.SuppressEvents then
            editorState.EventBus.Trigger(evt)

    let emitHistoryChanged (store: DsStore) =
        let editorState = state store
        if not editorState.SuppressEvents then
            editorState.EventBus.Trigger(HistoryChanged(editorState.UndoManager.UndoLabels, editorState.UndoManager.RedoLabels))

    let emitAndHistory (store: DsStore) (evt: EditorEvent) =
        emitEvent store evt
        emitHistoryChanged store

    let emitConnectionsChangedAndHistory (store: DsStore) =
        emitEvent store ConnectionsChanged
        emitHistoryChanged store

    let emitRefreshAndHistory (store: DsStore) =
        emitEvent store StoreRefreshed
        emitHistoryChanged store

    let observeEvents (store: DsStore) =
        (state store).EventBus.Publish

    let tryGetAddedEntityId (evt: EditorEvent) : Guid option =
        match evt with
        | ProjectAdded project -> Some project.Id
        | SystemAdded system -> Some system.Id
        | FlowAdded flow -> Some flow.Id
        | WorkAdded work -> Some work.Id
        | CallAdded call -> Some call.Id
        | ApiDefAdded apiDef -> Some apiDef.Id
        | HwComponentAdded(_, id, _) -> Some id
        | _ -> None

    let isTreeStructuralEvent (evt: EditorEvent) =
        match evt with
        | ProjectAdded _
        | ProjectRemoved _
        | SystemAdded _
        | SystemRemoved _
        | FlowAdded _
        | FlowRemoved _
        | WorkAdded _
        | WorkRemoved _
        | CallAdded _
        | CallRemoved _
        | ApiDefAdded _
        | ApiDefRemoved _
        | HwComponentAdded _
        | HwComponentRemoved _ -> true
        | _ -> false

    let private applyTransaction (store: DsStore) pop push apply label =
        match pop() with
        | None -> ()
        | Some tx ->
            try
                apply tx.Records
                store.RewireApiCallReferences()
                push tx
                log.Debug($"{label}: {tx.Label}")
            finally
                emitRefreshAndHistory store

    let undo (store: DsStore) =
        let editorState = state store
        applyTransaction store editorState.UndoManager.PopUndo editorState.UndoManager.PushRedo (fun rs -> for r in List.rev rs do r.Undo()) "Undo"

    let redo (store: DsStore) =
        let editorState = state store
        applyTransaction store editorState.UndoManager.PopRedo editorState.UndoManager.PushUndo (fun rs -> for r in rs do r.Redo()) "Redo"

    let private runBatch (store: DsStore) (action: unit -> unit) (steps: int) =
        let editorState = state store
        let n = max 0 steps
        if n <= 1 then
            for _ in 1 .. n do
                action()
        else
            editorState.SuppressEvents <- true
            try
                for _ in 1 .. n do
                    action()
            finally
                editorState.SuppressEvents <- false
                emitRefreshAndHistory store

    let undoTo (store: DsStore) (steps: int) = runBatch store (fun () -> undo store) steps
    let redoTo (store: DsStore) (steps: int) = runBatch store (fun () -> redo store) steps

    let clearHistory (store: DsStore) =
        let editorState = state store
        editorState.UndoManager.Clear()
        emitHistoryChanged store

[<Extension>]
type DsStoreAuthoringExtensions =
    [<Extension>]
    static member WithTransaction(store: DsStore, label: string, action: unit -> 'T) : 'T =
        StoreAuthoring.withTransaction store label action

    [<Extension>]
    static member TrackAdd<'T when 'T :> DsEntity>(store: DsStore, dict: Dictionary<Guid, 'T>, entity: 'T) =
        StoreAuthoring.trackAdd store dict entity

    [<Extension>]
    static member TrackRemove<'T when 'T :> DsEntity>(store: DsStore, dict: Dictionary<Guid, 'T>, id: Guid) =
        StoreAuthoring.trackRemove store dict id

    [<Extension>]
    static member TrackMutate<'T when 'T :> DsEntity>(store: DsStore, dict: Dictionary<Guid, 'T>, id: Guid, mutate: 'T -> unit) =
        StoreAuthoring.trackMutate store dict id mutate

    [<Extension>]
    static member EmitEvent(store: DsStore, evt: EditorEvent) =
        StoreAuthoring.emitEvent store evt

    [<Extension>]
    static member EmitHistoryChanged(store: DsStore) =
        StoreAuthoring.emitHistoryChanged store

    [<Extension>]
    static member EmitAndHistory(store: DsStore, evt: EditorEvent) =
        StoreAuthoring.emitAndHistory store evt

    [<Extension>]
    static member EmitConnectionsChangedAndHistory(store: DsStore) =
        StoreAuthoring.emitConnectionsChangedAndHistory store

    [<Extension>]
    static member EmitRefreshAndHistory(store: DsStore) =
        StoreAuthoring.emitRefreshAndHistory store

    [<Extension>]
    static member ObserveEvents(store: DsStore) =
        StoreAuthoring.observeEvents store

    [<Extension>]
    static member AddedEntityIdOrNull(store: DsStore, evt: EditorEvent) : Nullable<Guid> =
        store.TryGetAddedEntityId(evt)
        |> Option.toNullable

    [<Extension>]
    static member TryGetAddedEntityId(_store: DsStore, evt: EditorEvent) : Guid option =
        StoreAuthoring.tryGetAddedEntityId evt

    [<Extension>]
    static member IsTreeStructuralEvent(_store: DsStore, evt: EditorEvent) =
        StoreAuthoring.isTreeStructuralEvent evt

    [<Extension>]
    static member Undo(store: DsStore) =
        StoreAuthoring.undo store

    [<Extension>]
    static member Redo(store: DsStore) =
        StoreAuthoring.redo store

    [<Extension>]
    static member UndoTo(store: DsStore, steps: int) =
        StoreAuthoring.undoTo store steps

    [<Extension>]
    static member RedoTo(store: DsStore, steps: int) =
        StoreAuthoring.redoTo store steps

    [<Extension>]
    static member ClearHistory(store: DsStore) =
        StoreAuthoring.clearHistory store
