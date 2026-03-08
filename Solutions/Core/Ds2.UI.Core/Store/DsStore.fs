namespace rec Ds2.UI.Core

open System
open System.Collections.Generic
open Ds2.Core
open log4net

/// 중앙 도메인 저장소 — 단일 진입점 (God Object).
/// 모든 읽기/쓰기는 store.Method() 호출.
/// 변경은 반드시 WithTransaction을 통해 수행 (증분 UndoRecord 기반 Undo 보장).
type DsStore() =
    let log = LogManager.GetLogger(typedefof<DsStore>)
    let undoManager = UndoRedoManager(100)
    let eventBus = Event<EditorEvent>()
    let mutable suppressEvents = false
    let mutable currentRecords: ResizeArray<UndoRecord> option = None

    // ─── 컬렉션 (JSON 직렬화를 위해 public) ───
    member val Projects     = Dictionary<Guid, Project>()           with get, set
    member val Systems      = Dictionary<Guid, DsSystem>()          with get, set
    member val Flows        = Dictionary<Guid, Flow>()              with get, set
    member val Works        = Dictionary<Guid, Work>()              with get, set
    member val Calls        = Dictionary<Guid, Call>()              with get, set
    member val ApiDefs      = Dictionary<Guid, ApiDef>()            with get, set
    member val ApiCalls     = Dictionary<Guid, ApiCall>()           with get, set
    member val ArrowWorks   = Dictionary<Guid, ArrowBetweenWorks>() with get, set
    member val ArrowCalls   = Dictionary<Guid, ArrowBetweenCalls>() with get, set
    member val HwButtons    = Dictionary<Guid, HwButton>()          with get, set
    member val HwLamps      = Dictionary<Guid, HwLamp>()            with get, set
    member val HwConditions = Dictionary<Guid, HwCondition>()       with get, set
    member val HwActions    = Dictionary<Guid, HwAction>()          with get, set

    // ─── ReadOnly 뷰 ───
    member this.ProjectsReadOnly     : IReadOnlyDictionary<Guid, Project>           = this.Projects     :> IReadOnlyDictionary<_, _>
    member this.SystemsReadOnly      : IReadOnlyDictionary<Guid, DsSystem>          = this.Systems      :> IReadOnlyDictionary<_, _>
    member this.FlowsReadOnly        : IReadOnlyDictionary<Guid, Flow>              = this.Flows        :> IReadOnlyDictionary<_, _>
    member this.WorksReadOnly        : IReadOnlyDictionary<Guid, Work>              = this.Works        :> IReadOnlyDictionary<_, _>
    member this.CallsReadOnly        : IReadOnlyDictionary<Guid, Call>              = this.Calls        :> IReadOnlyDictionary<_, _>
    member this.ApiDefsReadOnly      : IReadOnlyDictionary<Guid, ApiDef>            = this.ApiDefs      :> IReadOnlyDictionary<_, _>
    member this.ApiCallsReadOnly     : IReadOnlyDictionary<Guid, ApiCall>           = this.ApiCalls     :> IReadOnlyDictionary<_, _>
    member this.ArrowWorksReadOnly   : IReadOnlyDictionary<Guid, ArrowBetweenWorks> = this.ArrowWorks   :> IReadOnlyDictionary<_, _>
    member this.ArrowCallsReadOnly   : IReadOnlyDictionary<Guid, ArrowBetweenCalls> = this.ArrowCalls   :> IReadOnlyDictionary<_, _>
    member this.HwButtonsReadOnly    : IReadOnlyDictionary<Guid, HwButton>          = this.HwButtons    :> IReadOnlyDictionary<_, _>
    member this.HwLampsReadOnly      : IReadOnlyDictionary<Guid, HwLamp>            = this.HwLamps      :> IReadOnlyDictionary<_, _>
    member this.HwConditionsReadOnly : IReadOnlyDictionary<Guid, HwCondition>       = this.HwConditions :> IReadOnlyDictionary<_, _>
    member this.HwActionsReadOnly    : IReadOnlyDictionary<Guid, HwAction>          = this.HwActions    :> IReadOnlyDictionary<_, _>

    static member empty() = DsStore()

    // ═════════════════════════════════════════════════════════════════════════
    // 증분 Undo 인프라
    // ═════════════════════════════════════════════════════════════════════════

    member private this.RecordUndo(record: UndoRecord) =
        match currentRecords with
        | Some records -> records.Add(record)
        | None -> invalidOp "RecordUndo called outside transaction"

    /// 쓰기 작업을 증분 Undo 트랜잭션으로 감싼다.
    /// 실패 시 기록된 UndoRecord를 역순 실행하여 자동 복원 후 reraise.
    member internal this.WithTransaction(label: string, action: unit -> 'T) : 'T =
        if currentRecords.IsSome then invalidOp "중첩 트랜잭션은 지원하지 않습니다"
        let records = ResizeArray<UndoRecord>()
        currentRecords <- Some records
        try
            try
                let result = action()
                if records.Count > 0 then
                    undoManager.Push({ Label = label; Records = Seq.toList records })
                log.Debug($"Executed: {label}")
                result
            with ex ->
                // Rollback: 기록된 변경을 역순 Undo
                currentRecords <- None
                for i in records.Count - 1 .. -1 .. 0 do
                    records.[i].Undo()
                log.Error($"Transaction failed: {label} — {ex.Message}", ex)
                reraise()
        finally
            currentRecords <- None

    // ─── Track 헬퍼 (dict 조작 + UndoRecord 기록을 하나로 묶음) ───

    member internal this.TrackAdd<'T when 'T :> DsEntity>(dict: Dictionary<Guid, 'T>, entity: 'T) =
        let backup = DeepCopyHelper.backupEntityAs entity
        dict.[entity.Id] <- entity
        this.RecordUndo({
            Undo = fun () -> dict.Remove(entity.Id) |> ignore
            Redo = fun () -> dict.[entity.Id] <- backup
            Description = $"Add {typeof<'T>.Name} {entity.Id}" })

    member internal this.TrackRemove<'T when 'T :> DsEntity>(dict: Dictionary<Guid, 'T>, id: Guid) =
        match dict.TryGetValue(id) with
        | true, entity ->
            let backup = DeepCopyHelper.backupEntityAs entity
            dict.Remove(id) |> ignore
            this.RecordUndo({
                Undo = fun () -> dict.[id] <- backup
                Redo = fun () -> dict.Remove(id) |> ignore
                Description = $"Remove {typeof<'T>.Name} {id}" })
        | false, _ -> ()

    member internal this.TrackMutate<'T when 'T :> DsEntity>(dict: Dictionary<Guid, 'T>, id: Guid, mutate: 'T -> unit) =
        match dict.TryGetValue(id) with
        | true, entity ->
            let oldSnapshot = DeepCopyHelper.backupEntityAs entity
            mutate entity
            let newSnapshot = DeepCopyHelper.backupEntityAs entity
            this.RecordUndo({
                Undo = fun () -> dict.[id] <- oldSnapshot
                Redo = fun () -> dict.[id] <- newSnapshot
                Description = $"Mutate {typeof<'T>.Name} {id}" })
        | false, _ -> invalidOp $"Entity not found: {id}"

    // ─── 비 Undo 직접 쓰기 (AASX 임포트 등 새 스토어 구성용) ───

    member internal _.DirectWrite<'T when 'T :> DsEntity>(dict: Dictionary<Guid, 'T>, entity: 'T) =
        dict.[entity.Id] <- entity

    // ─── 이벤트 헬퍼 ───

    member internal _.EmitEvent(evt: EditorEvent) =
        if not suppressEvents then eventBus.Trigger(evt)

    member internal _.EmitHistoryChanged() =
        if not suppressEvents then
            eventBus.Trigger(HistoryChanged(undoManager.UndoLabels, undoManager.RedoLabels))

    /// 쓰기 후 이벤트 + 히스토리 변경을 한 번에 발행
    member internal this.EmitAndHistory(evt: EditorEvent) =
        this.EmitEvent(evt)
        this.EmitHistoryChanged()

    /// StoreRefreshed + HistoryChanged 한 번에 발행 (Composite 연산용)
    member internal this.EmitRefreshAndHistory() =
        this.EmitEvent(StoreRefreshed)
        this.EmitHistoryChanged()

    // ═════════════════════════════════════════════════════════════════════════
    // 이벤트 공개 API
    // ═════════════════════════════════════════════════════════════════════════

    member _.OnEvent = eventBus.Publish

    member _.TryGetAddedEntityId(evt: EditorEvent) : Guid option =
        match evt with
        | ProjectAdded project       -> Some project.Id
        | SystemAdded system         -> Some system.Id
        | FlowAdded flow             -> Some flow.Id
        | WorkAdded work             -> Some work.Id
        | CallAdded call             -> Some call.Id
        | ApiDefAdded apiDef         -> Some apiDef.Id
        | HwComponentAdded(_, id, _) -> Some id
        | _ -> None

    member _.IsTreeStructuralEvent(evt: EditorEvent) : bool =
        match evt with
        | ProjectAdded _     | ProjectRemoved _
        | SystemAdded _      | SystemRemoved _
        | FlowAdded _        | FlowRemoved _
        | WorkAdded _        | WorkRemoved _
        | CallAdded _        | CallRemoved _
        | ApiDefAdded _      | ApiDefRemoved _
        | HwComponentAdded _ | HwComponentRemoved _ -> true
        | _ -> false

    // ═════════════════════════════════════════════════════════════════════════
    // Undo / Redo (증분 replay)
    // ═════════════════════════════════════════════════════════════════════════

    /// Undo/Redo 후 Call↔ApiCall 공유 참조를 재연결한다.
    /// Undo/Redo가 deep copy된 별도 객체를 복원하므로
    /// Call.ApiCalls 항목과 store.ApiCalls 항목이 다른 인스턴스가 될 수 있음.
    member private this.RewireApiCallReferences() =
        let rewire (source: ResizeArray<ApiCall>) =
            let result = ResizeArray<ApiCall>(source.Count)
            for ac in source do
                match this.ApiCalls.TryGetValue(ac.Id) with
                | true, storeAc -> result.Add(storeAc)
                | false, _ -> log.Warn($"RewireApiCallReferences: ApiCall {ac.Id} not found in store — skipped")
            result
        for call in this.Calls.Values do
            call.ApiCalls <- rewire call.ApiCalls
            for cond in call.CallConditions do
                cond.Conditions <- rewire cond.Conditions

    member private this.ApplyTransaction(pop, push, apply, label) =
        match pop() with
        | None -> ()
        | Some tx ->
            apply tx.Records
            this.RewireApiCallReferences()
            push(tx)
            log.Debug($"{label}: {tx.Label}")
            this.EmitRefreshAndHistory()

    member this.Undo() =
        this.ApplyTransaction(undoManager.PopUndo, undoManager.PushRedo,
            (fun rs -> for r in List.rev rs do r.Undo()), "Undo")

    member this.Redo() =
        this.ApplyTransaction(undoManager.PopRedo, undoManager.PushUndo,
            (fun rs -> for r in rs do r.Redo()), "Redo")

    member private this.RunBatch(action, steps) =
        let n = max 0 steps
        if n <= 1 then
            for _ in 1 .. n do action()
        else
            suppressEvents <- true
            try for _ in 1 .. n do action()
            finally
                suppressEvents <- false
                this.EmitRefreshAndHistory()

    member this.UndoTo(steps: int) = this.RunBatch(this.Undo, steps)
    member this.RedoTo(steps: int) = this.RunBatch(this.Redo, steps)

    // ═════════════════════════════════════════════════════════════════════════
    // 파일 I/O
    // ═════════════════════════════════════════════════════════════════════════

    member this.SaveToFile(path: string) =
        try
            Ds2.Serialization.JsonConverter.saveToFile path this
            log.Info($"저장 완료: {path}")
        with ex ->
            log.Error($"저장 실패: {path} — {ex.Message}", ex)
            raise (InvalidOperationException($"저장 실패: {ex.Message}", ex))

    member private this.ReplaceAllCollections(source: DsStore) =
        let replace (src: Dictionary<Guid, 'T>) (dst: Dictionary<Guid, 'T>) =
            dst.Clear()
            for kv in src do
                dst.[kv.Key] <- kv.Value
        replace source.Projects     this.Projects
        replace source.Systems      this.Systems
        replace source.Flows        this.Flows
        replace source.Works        this.Works
        replace source.Calls        this.Calls
        replace source.ApiDefs      this.ApiDefs
        replace source.ApiCalls     this.ApiCalls
        replace source.ArrowWorks   this.ArrowWorks
        replace source.ArrowCalls   this.ArrowCalls
        replace source.HwButtons    this.HwButtons
        replace source.HwLamps      this.HwLamps
        replace source.HwConditions this.HwConditions
        replace source.HwActions    this.HwActions

    member private this.ApplyNewStore(newStore: DsStore, contextLabel: string) =
        try
            this.ReplaceAllCollections(newStore)
            undoManager.Clear()
            log.Info($"Store applied: {contextLabel}")
            this.EmitRefreshAndHistory()
        with ex ->
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
