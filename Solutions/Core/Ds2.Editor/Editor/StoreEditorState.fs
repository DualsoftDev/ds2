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
    /// 진행 중인 트랜잭션이 지금까지 쌓은 undo 스냅샷 크기 합(바이트 근사치).
    /// withTransaction 이 0 으로 시작해 commit 시 UndoTransaction 으로 넘긴다.
    mutable CurrentSnapshotBytes: int64
    /// 직전 undo/redo 가 가벼운 이벤트로 처리된 트랜잭션이면 그 이벤트, 아니면 None.
    /// C# 측에서 추가 RebuildAll 을 건너뛰는 데 사용한다.
    mutable LastUndoRedoLightEvent: EditorEvent option
}

[<RequireQualifiedAccess>]
module internal StoreEditorState =
    /// undo 이력 상한 — 건수와 용량 둘 다 건다.
    ///
    /// 건수만 걸던 시절에는 대량 삭제 1건이 엔티티 복사본 수천 개를 붙든 채 뒤이어 99건이 더
    /// 쌓일 때까지 풀리지 않았다. 현장 PC 가 커밋 메모리 94% 에서 OOM 을 내던 경로다.
    /// 용량 64MB 는 일반 편집(1건 수 KB) 이 건수 상한에 먼저 걸리도록 넉넉히 잡은 값 —
    /// 대량 작업만 이 상한에 걸린다. 최신 1건은 크기와 무관하게 남으므로 방금 한 대량 삭제는
    /// 예산을 통째로 넘겨도 되돌릴 수 있다.
    let private MaxUndoTransactions = 100
    let private MaxUndoSnapshotBytes = 64L * 1024L * 1024L

    let private table = ConditionalWeakTable<DsStore, StoreEditorState>()

    let get (store: DsStore) =
        table.GetValue(
            store,
            ConditionalWeakTable<DsStore, StoreEditorState>.CreateValueCallback(fun _ ->
                {
                    UndoManager = UndoRedoManager(MaxUndoTransactions, MaxUndoSnapshotBytes)
                    EventBus = Event<EditorEvent>()
                    SuppressEvents = false
                    CurrentRecords = None
                    CurrentAffectedIds = None
                    CurrentSnapshotBytes = 0L
                    LastUndoRedoLightEvent = None
                }))
