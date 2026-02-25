namespace Ds2.UI.Core

open System.Collections.Generic

// =============================================================================
// UndoRedoManager — 스택 관리 (STRUCTURE.md 6)
// 실행은 하지 않음. EditorApi가 CommandExecutor를 호출.
// =============================================================================

type UndoRedoManager(maxSize: int) =
    do
        if maxSize < 1 then
            invalidArg "maxSize" "maxSize must be greater than 0."

    let undoList = LinkedList<EditorCommand>()
    let redoList = LinkedList<EditorCommand>()

    let pushFront (list: LinkedList<EditorCommand>) (cmd: EditorCommand) =
        list.AddFirst(cmd) |> ignore

    member _.CanUndo = undoList.Count > 0
    member _.CanRedo = redoList.Count > 0
    member _.UndoCount = undoList.Count
    member _.RedoCount = redoList.Count

    member _.Push(cmd: EditorCommand) =
        pushFront undoList cmd
        redoList.Clear()
        if undoList.Count > maxSize then
            undoList.RemoveLast()

    member _.PopUndo() : EditorCommand option =
        match undoList.First with
        | null -> None
        | first ->
            let cmd = first.Value
            undoList.RemoveFirst()
            pushFront redoList cmd
            Some cmd

    member _.PopRedo() : EditorCommand option =
        match redoList.First with
        | null -> None
        | first ->
            let cmd = first.Value
            redoList.RemoveFirst()
            pushFront undoList cmd
            Some cmd

    member _.RestoreAfterFailedUndo(cmd: EditorCommand) =
        match redoList.First with
        | null -> ()
        | first when first.Value = cmd ->
            redoList.RemoveFirst()
            pushFront undoList cmd
        | _ ->
            if redoList.Remove(cmd) then
                pushFront undoList cmd

    member _.RestoreAfterFailedRedo(cmd: EditorCommand) =
        match undoList.First with
        | null -> ()
        | first when first.Value = cmd ->
            undoList.RemoveFirst()
            pushFront redoList cmd
        | _ ->
            if undoList.Remove(cmd) then
                pushFront redoList cmd

    member _.Clear() =
        undoList.Clear()
        redoList.Clear()
