namespace Ds2.Editor

open Ds2.Core.Store
open System.Collections.Generic

// =============================================================================
// UndoRedoManager — UndoTransaction 기반 증분 Undo/Redo 스택 관리
// =============================================================================

type UndoRedoManager(maxSize: int) =
    do
        if maxSize < 1 then
            invalidArg "maxSize" "maxSize must be greater than 0."

    let undoStack = LinkedList<UndoTransaction>()
    let redoStack = LinkedList<UndoTransaction>()

    let popFirst (stack: LinkedList<UndoTransaction>) =
        match stack.First with
        | null -> None
        | first ->
            let tx = first.Value
            stack.RemoveFirst()
            Some tx

    member _.UndoLabels = undoStack |> Seq.map (fun t -> t.Label) |> Seq.toList
    member _.RedoLabels = redoStack |> Seq.map (fun t -> t.Label) |> Seq.toList

    member _.Push(tx: UndoTransaction) =
        undoStack.AddFirst(tx) |> ignore
        redoStack.Clear()
        if undoStack.Count > maxSize then
            undoStack.RemoveLast()

    member _.PopUndo() = popFirst undoStack
    member _.PopRedo() = popFirst redoStack

    member _.PushUndo(tx: UndoTransaction) = undoStack.AddFirst(tx) |> ignore
    member _.PushRedo(tx: UndoTransaction) = redoStack.AddFirst(tx) |> ignore

    member _.MergeTop(count: int, label: string) =
        if count >= 2 && undoStack.Count >= count then
            let records = ResizeArray<UndoRecord>()
            for _ in 1 .. count do
                match popFirst undoStack with
                | Some tx -> records.InsertRange(0, tx.Records)
                | None -> ()
            undoStack.AddFirst({ Label = label; Records = Seq.toList records; AffectedEntityIds = [] }) |> ignore
            redoStack.Clear()

    /// Undo 스택에서 index번째 트랜잭션의 AffectedEntityIds 조회 (0 = 가장 최근)
    member _.TryGetUndoAffectedIds(index: int) =
        if index < 0 || index >= undoStack.Count then []
        else (undoStack |> Seq.item index).AffectedEntityIds

    /// Redo 스택에서 index번째 트랜잭션의 AffectedEntityIds 조회 (0 = 가장 최근)
    member _.TryGetRedoAffectedIds(index: int) =
        if index < 0 || index >= redoStack.Count then []
        else (redoStack |> Seq.item index).AffectedEntityIds

    member _.Clear() =
        undoStack.Clear()
        redoStack.Clear()
