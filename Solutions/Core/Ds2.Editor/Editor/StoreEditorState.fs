namespace Ds2.Editor

open System.Collections.Generic
open System.Runtime.CompilerServices
open Ds2.Core.Store

type internal StoreEditorState = {
    UndoManager: UndoRedoManager
    EventBus: Event<EditorEvent>
    mutable SuppressEvents: bool
    mutable CurrentRecords: ResizeArray<UndoRecord> option
    mutable CurrentAffectedIds: ResizeArray<System.Guid> option
    /// 직전 undo/redo 가 가벼운 이벤트로 처리된 트랜잭션이면 그 이벤트, 아니면 None.
    /// C# 측에서 추가 RebuildAll 을 건너뛰는 데 사용한다.
    mutable LastUndoRedoLightEvent: EditorEvent option
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
                    CurrentAffectedIds = None
                    LastUndoRedoLightEvent = None
                }))
