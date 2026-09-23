namespace Ds2.Editor

open Ds2.Core.Store
open System.Collections.Generic

// =============================================================================
// UndoRedoManager — UndoTransaction 기반 증분 Undo/Redo 스택 관리
// =============================================================================

/// 건수(<paramref name="maxSize"/>) 와 용량(<paramref name="maxSnapshotBytes"/>) 을 함께 건다.
///
/// 건수만 걸면 대량 삭제 1건이 엔티티 복사본 수천 개(변경 1회당 old·new 2벌) 를 붙든 채
/// 뒤이어 99건이 더 쌓일 때까지 풀리지 않는다. 용량 상한은 그 경로를 끊는다.
/// 단, **최신 1건은 크기와 무관하게 남긴다** — 방금 한 대량 작업은 그 자체로 예산을 넘겨도
/// 되돌릴 수 있어야 하기 때문.
type UndoRedoManager(maxSize: int, maxSnapshotBytes: int64) =
    do
        if maxSize < 1 then
            invalidArg "maxSize" "maxSize must be greater than 0."
        if maxSnapshotBytes < 1L then
            invalidArg "maxSnapshotBytes" "maxSnapshotBytes must be greater than 0."

    let undoStack = LinkedList<UndoTransaction>()
    let redoStack = LinkedList<UndoTransaction>()

    // 누적 스냅샷 크기. 스택 조작 지점마다 같이 움직여야 하므로 아래 헬퍼 밖에서 직접 만지지 말 것.
    let mutable undoBytes = 0L
    let mutable redoBytes = 0L

    let popFirst (stack: LinkedList<UndoTransaction>) =
        match stack.First with
        | null -> None
        | first ->
            let tx = first.Value
            stack.RemoveFirst()
            Some tx

    let popUndo () =
        match popFirst undoStack with
        | Some tx ->
            undoBytes <- undoBytes - tx.ApproxSnapshotBytes
            Some tx
        | None -> None

    let popRedo () =
        match popFirst redoStack with
        | Some tx ->
            redoBytes <- redoBytes - tx.ApproxSnapshotBytes
            Some tx
        | None -> None

    let pushUndo (tx: UndoTransaction) =
        undoStack.AddFirst(tx) |> ignore
        undoBytes <- undoBytes + tx.ApproxSnapshotBytes

    let pushRedo (tx: UndoTransaction) =
        redoStack.AddFirst(tx) |> ignore
        redoBytes <- redoBytes + tx.ApproxSnapshotBytes

    let clearRedo () =
        redoStack.Clear()
        redoBytes <- 0L

    /// 가장 오래된 undo 트랜잭션을 버린다 (스택 꼬리).
    let dropOldestUndo () =
        match undoStack.Last with
        | null -> ()
        | last ->
            undoBytes <- undoBytes - last.Value.ApproxSnapshotBytes
            undoStack.RemoveLast()

    member _.UndoLabels = undoStack |> Seq.map (fun t -> t.Label) |> Seq.toList
    member _.RedoLabels = redoStack |> Seq.map (fun t -> t.Label) |> Seq.toList

    /// 현재 undo/redo 이력이 붙들고 있는 스냅샷 크기 합(바이트 근사치). 진단·테스트용.
    member _.ApproxSnapshotBytes = undoBytes + redoBytes

    member _.Push(tx: UndoTransaction) =
        pushUndo tx
        clearRedo ()
        while undoStack.Count > maxSize do
            dropOldestUndo ()
        // 용량 초과분은 오래된 것부터. 최신 1건은 남긴다 (위 주석 참조).
        while undoBytes > maxSnapshotBytes && undoStack.Count > 1 do
            dropOldestUndo ()

    member _.PopUndo() = popUndo ()
    member _.PopRedo() = popRedo ()

    member _.PushUndo(tx: UndoTransaction) = pushUndo tx
    member _.PushRedo(tx: UndoTransaction) = pushRedo tx

    member _.MergeTop(count: int, label: string) =
        if count >= 2 && undoStack.Count >= count then
            let records = ResizeArray<UndoRecord>()
            let mutable bytes = 0L
            for _ in 1 .. count do
                match popUndo () with
                | Some tx ->
                    records.InsertRange(0, tx.Records)
                    bytes <- bytes + tx.ApproxSnapshotBytes
                | None -> ()
            pushUndo { Label = label; Records = Seq.toList records; AffectedEntityIds = []; ApproxSnapshotBytes = bytes; LightEventOnUndo = None }
            clearRedo ()

    /// 가장 최근에 push 된 undo 트랜잭션에 가벼운 이벤트 힌트를 설정한다.
    /// (pure 이동 등의 트랜잭션이 undo/redo 시 StoreRefreshed 대신 EntitiesMoved 같은 가벼운 이벤트를 발행하도록.)
    member _.SetTopLightEvent(evt: EditorEvent) =
        match undoStack.First with
        | null -> ()
        | first -> first.Value.LightEventOnUndo <- Some evt

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
        undoBytes <- 0L
        redoBytes <- 0L
