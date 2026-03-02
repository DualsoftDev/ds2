namespace Ds2.UI.Core

open System
open Ds2.Core
open log4net

// =============================================================================
// EditorApi — UI의 편집(쓰기) 명령 진입점
// 서브-API 클래스(Query/Nodes/Arrows/Panel)를 통해 도메인별 접근 제공.
// =============================================================================
type EditorApi(store: DsStore, ?maxUndoSize: int) as this =
    let log = LogManager.GetLogger(typedefof<EditorApi>)
    let undoManager = UndoRedoManager(defaultArg maxUndoSize 100)
    let eventBus = Event<EditorEvent>()
    let mutable suppressEvents = false
    let cloneStore (s: DsStore) =
        StoreCopy.deepClone s

    let ensureValidStoreOrThrow (targetStore: DsStore) (context: string) =
        match ValidationHelpers.validateStore targetStore with
        | Valid -> ()
        | Invalid errors ->
            let message =
                if List.isEmpty errors then "Unknown validation failure"
                else String.concat " | " errors
            invalidOp $"Store validation failed ({context}): {message}"

    // --- 서브-API 초기화: ref 패턴으로 지연 바인딩 ---
    // let 바인딩 단계에서 this.Exec/BatchExec를 직접 참조할 수 없으므로
    // ref를 통해 do 블록에서 실제 함수로 연결한다.
    let execRef      : ExecFn ref         = ref (fun _ -> ())
    let batchExecRef : BatchExecFn ref    = ref (fun _ _ -> ())
    let execOptRef   : (EditorCommand option -> bool) ref = ref (fun _ -> false)
    let applyTryRef  : ((bool * EditorCommand option) -> bool) ref = ref (fun _ -> false)

    let query  = EditorQueryApi(store)
    let nodes  = EditorNodeApi(store,
                     (fun cmd  -> execRef.Value cmd),
                     (fun l a  -> batchExecRef.Value l a))
    let arrows = EditorArrowApi(store,
                     (fun cmd  -> execRef.Value cmd),
                     (fun l a  -> batchExecRef.Value l a))
    let panel  = EditorPanelApi(store,
                     (fun cmd  -> execRef.Value cmd),
                     (fun cmdOpt -> execOptRef.Value cmdOpt),
                     (fun r    -> applyTryRef.Value r))

    // do 블록: 서브-API에 주입한 ref를 실제 구현으로 연결
    do
        execRef.Value      <- fun cmd    -> this.ExecuteCommand(cmd)
        batchExecRef.Value <- fun label action -> this.ExecBatch(label, action)
        execOptRef.Value   <- fun cmdOpt -> this.ExecOpt(cmdOpt)
        applyTryRef.Value  <- fun result -> this.ApplyTryBuildResult(result)

    // --- 이벤트 ---
    member _.OnEvent = eventBus.Publish

    member _.TryGetAddedEntityId(evt: EditorEvent) : Guid option =
        match evt with
        | ProjectAdded project -> Some project.Id
        | SystemAdded system -> Some system.Id
        | FlowAdded flow -> Some flow.Id
        | WorkAdded work -> Some work.Id
        | CallAdded call -> Some call.Id
        | ApiDefAdded apiDef -> Some apiDef.Id
        | HwComponentAdded(_, id, _) -> Some id
        | _ -> None

    member _.IsTreeStructuralEvent(evt: EditorEvent) : bool =
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

    // --- 내부: store 접근 ---
    member internal _.Store = store

    // --- 내부: 명령 실행 공통 ---
    member private _.PublishCommandResult(cmd: EditorCommand, events: EditorEvent list) =
        if not suppressEvents then
            match cmd with
            | Composite _ -> eventBus.Trigger(StoreRefreshed)
            | _ -> events |> List.iter eventBus.Trigger
            eventBus.Trigger(HistoryChanged(undoManager.UndoLabels, undoManager.RedoLabels))

    member private this.ExecuteCommand(cmd: EditorCommand) =
        ensureValidStoreOrThrow store (sprintf "before execute %A" cmd)
        let snapshot = cloneStore store
        try
            let events = CommandExecutor.execute cmd store
            ensureValidStoreOrThrow store (sprintf "after execute %A" cmd)
            undoManager.Push(cmd)
            log.Info($"Executed: {CommandLabel.ofCommand cmd}")
            this.PublishCommandResult(cmd, events)
        with ex ->
            StoreCopy.replaceAllCollections snapshot store
            log.Error($"Command failed: {CommandLabel.ofCommand cmd} — {ex.Message}", ex)
            if not suppressEvents then
                eventBus.Trigger(StoreRefreshed)
                eventBus.Trigger(HistoryChanged(undoManager.UndoLabels, undoManager.RedoLabels))
            raise (InvalidOperationException($"Command execution failed and state was restored: {ex.Message}", ex))

    member internal this.Exec(cmd: EditorCommand) =
        this.ExecuteCommand(cmd)

    member private this.ExecOpt(cmdOpt: EditorCommand option) =
        match cmdOpt with Some cmd -> this.Exec cmd; true | None -> false

    member private this.ApplyTryBuildResult(result: bool * EditorCommand option) : bool =
        match result with
        | false, _ -> false
        | true, Some cmd -> this.Exec cmd; true
        | true, None -> true

    /// 여러 exec 호출을 하나의 Composite으로 묶어 Undo 1회를 보장한다.
    member internal this.ExecBatch(label: string, action: ExecFn -> 'a) : 'a =
        let buffer = ResizeArray<EditorCommand>()
        let bufExec (cmd: EditorCommand) = buffer.Add(cmd)
        let result = action bufExec
        match buffer |> Seq.toList with
        | []       -> ()
        | [single] -> this.Exec single
        | cmds     -> this.Exec(Composite(label, cmds))
        result

    // =====================================================================
    // 서브-API 인스턴스 — 도메인별 접근 진입점
    // =====================================================================
    member _.Query  = query
    member _.Nodes  = nodes
    member _.Arrows = arrows
    member _.Panel  = panel

    // =====================================================================
    // Undo / Redo
    // =====================================================================
    member private this.RunUndoRedoStep(
        pop: unit -> EditorCommand option,
        apply: EditorCommand -> DsStore -> EditorEvent list,
        restore: EditorCommand -> unit,
        label: string) =
        match pop() with
        | None -> ()
        | Some cmd ->
            let snapshot = cloneStore store
            try
                ensureValidStoreOrThrow store (sprintf "before %s %A" label cmd)
                let events = apply cmd store
                ensureValidStoreOrThrow store (sprintf "after %s %A" label cmd)
                log.Debug($"{label}: {CommandLabel.ofCommand cmd}")
                this.PublishCommandResult(cmd, events)
            with ex ->
                StoreCopy.replaceAllCollections snapshot store
                restore cmd
                log.Error($"{label} failed: {CommandLabel.ofCommand cmd} — {ex.Message}", ex)
                if not suppressEvents then
                    eventBus.Trigger(StoreRefreshed)
                    eventBus.Trigger(HistoryChanged(undoManager.UndoLabels, undoManager.RedoLabels))
                raise (InvalidOperationException($"{label} failed and state was restored: {ex.Message}", ex))

    member this.Undo() =
        this.RunUndoRedoStep(undoManager.PopUndo, CommandExecutor.undo, undoManager.RestoreAfterFailedUndo, "Undo")

    member this.Redo() =
        this.RunUndoRedoStep(undoManager.PopRedo, CommandExecutor.execute, undoManager.RestoreAfterFailedRedo, "Redo")

    member private this.RunBatchedSteps(n: int, step: unit -> unit) =
        if n <= 1 then
            for _ in 1 .. n do step()
        else
            suppressEvents <- true
            try
                for _ in 1 .. n do step()
            finally
                suppressEvents <- false
                eventBus.Trigger(StoreRefreshed)
                eventBus.Trigger(HistoryChanged(undoManager.UndoLabels, undoManager.RedoLabels))

    member this.UndoTo(steps: int) = this.RunBatchedSteps(max 0 steps, this.Undo)
    member this.RedoTo(steps: int) = this.RunBatchedSteps(max 0 steps, this.Redo)

    // =====================================================================
    // 파일 I/O
    // =====================================================================
    member this.SaveToFile(path: string) =
        try
            Ds2.Serialization.JsonConverter.saveToFile path store
            log.Info($"저장 완료: {path}")
        with ex ->
            log.Error($"저장 실패: {path} — {ex.Message}", ex)
            raise (InvalidOperationException($"저장 실패: {ex.Message}", ex))

    member private this.ApplyNewStore(newStore: DsStore, contextLabel: string) =
        let backup = cloneStore store
        try
            ensureValidStoreOrThrow newStore contextLabel
            StoreCopy.replaceAllCollections newStore store
            ensureValidStoreOrThrow store $"apply {contextLabel}"
            undoManager.Clear()
            log.Info($"Store applied: {contextLabel}")
            eventBus.Trigger(StoreRefreshed)
            eventBus.Trigger(HistoryChanged(undoManager.UndoLabels, undoManager.RedoLabels))
        with ex ->
            StoreCopy.replaceAllCollections backup store
            log.Error($"ApplyNewStore failed: {contextLabel} — {ex.Message}", ex)
            raise (InvalidOperationException($"{contextLabel} failed: {ex.Message}", ex))

    member this.LoadFromFile(path: string) =
        let loaded = Ds2.Serialization.JsonConverter.loadFromFile<DsStore> path
        if isNull (box loaded) then
            invalidOp "Loaded store is null."
        this.ApplyNewStore(loaded, sprintf "load file '%s'" path)

    /// 외부에서 구성된 DsStore를 현재 store에 적용하고 StoreRefreshed를 발행한다.
    /// AASX 임포트 등 파일 I/O 경로에서 사용.
    member this.ReplaceStore(newStore: DsStore) =
        this.ApplyNewStore(newStore, "ReplaceStore")
