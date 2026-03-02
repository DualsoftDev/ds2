namespace Ds2.UI.Core

open System
open Ds2.Core
open log4net

// =============================================================================
// EditorApi — UI의 편집(쓰기) 명령 진입점
// =============================================================================
type EditorApi(store: DsStore, ?maxUndoSize: int) =
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

    /// 여러 exec 호출을 하나의 Composite으로 묶어 Undo 1회를 보장한다.
    member internal this.ExecBatch(label: string, action: PasteOps.ExecFn -> 'a) : 'a =
        let buffer = ResizeArray<EditorCommand>()
        let bufExec (cmd: EditorCommand) = buffer.Add(cmd)
        let result = action bufExec
        match buffer |> Seq.toList with
        | []       -> ()
        | [single] -> this.Exec single
        | cmds     -> this.Exec(Composite(label, cmds))
        result

    /// 사전에 빌드된 command 목록을 단일 Undo 단위로 실행. 실행된 개수 반환.
    member private this.ExecBatch(label: string, commands: EditorCommand list) : int =
        this.ExecBatch(label, fun exec -> commands |> List.iter exec; commands.Length)

    // =====================================================================
    // Project API
    // =====================================================================
    member internal this.AddProject(name: string) : Project =
        let project = Project(name)
        this.Exec(AddProject project)
        project

    member this.AddProjectAndGetId(name: string) : Guid =
        (this.AddProject name).Id


    // =====================================================================
    // System API
    // =====================================================================
    member internal this.AddSystem(name: string, projectId: Guid, isActive: bool) : DsSystem =
        let system = DsSystem(name)
        this.Exec(AddSystem(system, projectId, isActive))
        system

    member this.AddSystemAndGetId(name: string, projectId: Guid, isActive: bool) : Guid =
        (this.AddSystem(name, projectId, isActive)).Id


    // =====================================================================
    // Flow API
    // =====================================================================
    member internal this.AddFlow(name: string, systemId: Guid) : Flow =
        let flow = Flow(name, systemId)
        this.Exec(AddFlow flow)
        flow

    member this.AddFlowAndGetId(name: string, systemId: Guid) : Guid =
        (this.AddFlow(name, systemId)).Id


    // =====================================================================
    // Work API
    // =====================================================================
    member internal this.AddWork(name: string, flowId: Guid) : Work =
        let work = Work(name, flowId)
        this.Exec(AddWork work)
        work

    member this.AddWorkAndGetId(name: string, flowId: Guid) : Guid =
        (this.AddWork(name, flowId)).Id


    member this.MoveEntities(requests: seq<MoveEntityRequest>) : int =
        this.ExecBatch("Move Selected Nodes", RemoveOps.buildMoveEntitiesCmds store requests)

    // =====================================================================
    // Query / Projection API for UI
    // =====================================================================
    member _.BuildTrees() : TreeNodeInfo list * TreeNodeInfo list =
        TreeProjection.buildTrees store

    member _.CanvasContentForTab(kind: TabKind, rootId: Guid) : CanvasContent =
        CanvasProjection.canvasContentForTab store kind rootId

    member _.TryOpenTabForEntity(entityType: string, entityId: Guid) : TabOpenInfo option =
        EntityHierarchyQueries.tryOpenTabForEntity store entityType entityId

    member _.FlowIdsForTab(kind: TabKind, rootId: Guid) : Guid list =
        EntityHierarchyQueries.flowIdsForTab store kind rootId

    member _.TabExists(kind: TabKind, rootId: Guid) : bool =
        EntityHierarchyQueries.tabExists store kind rootId

    member _.TabTitle(kind: TabKind, rootId: Guid) : string option =
        EntityHierarchyQueries.tabTitle store kind rootId

    member _.TryFindProjectIdForEntity(entityType: string, entityId: Guid) : Guid option =
        EntityHierarchyQueries.tryFindProjectIdForEntity store entityType entityId

    member _.FindApiDefsByName(filterName: string) : ApiDefMatch list =
        EntityHierarchyQueries.findApiDefs store "" filterName

    member _.TryResolveAddSystemTarget
        (selectedEntityType: string option)
        (selectedEntityId: Guid option)
        (activeTabKind: TabKind option)
        (activeTabRootId: Guid option)
        : Guid option =
        AddTargetQueries.tryResolveAddSystemTarget store selectedEntityType selectedEntityId activeTabKind activeTabRootId

    member _.TryResolveAddFlowTarget
        (selectedEntityType: string option)
        (selectedEntityId: Guid option)
        (activeTabKind: TabKind option)
        (activeTabRootId: Guid option)
        : Guid option =
        AddTargetQueries.tryResolveAddFlowTarget store selectedEntityType selectedEntityId activeTabKind activeTabRootId

    // =====================================================================
    // Call API
    // =====================================================================
    /// Call 목록을 한 번에 추가한다.
    /// callNames: 각 항목은 "devAlias.apiName" 형식.
    /// createDeviceSystem=true 이면 Passive System / Flow / Work / ApiDef / ApiCall 을 자동 생성 또는 재사용한다.
    member this.AddCallsWithDevice
        (projectId: Guid)
        (workId: Guid)
        (callNames: string seq)
        (createDeviceSystem: bool)
        =
        this.ExecBatch("Add Calls", DeviceOps.buildAddCallsWithDeviceCmds store projectId workId (callNames |> Seq.toList) createDeviceSystem) |> ignore

    member internal this.AddCallWithLinkedApiDefs
        (workId: Guid)
        (devicesAlias: string)
        (apiName: string)
        (apiDefIds: Guid seq)
        : Call =
        let call, cmd = DeviceOps.buildAddCallWithLinkedApiDefsCmd store workId devicesAlias apiName apiDefIds
        this.Exec cmd
        call

    member this.AddCallWithLinkedApiDefsAndGetId
        (workId: Guid)
        (devicesAlias: string)
        (apiName: string)
        (apiDefIds: Guid seq)
        : Guid =
        (this.AddCallWithLinkedApiDefs workId devicesAlias apiName apiDefIds).Id


    // =====================================================================
    // Arrow API
    // =====================================================================
    /// Flow 내 모든 화살표의 경로를 일괄 계산 (C# 렌더링용)
    member _.GetFlowArrowPaths(flowId: Guid) : Map<Guid, ArrowPathCalculator.ArrowVisual> =
        ArrowPathCalculator.computeFlowArrowPaths store flowId

    member this.RemoveArrows(arrowIds: seq<Guid>) : int =
        this.ExecBatch("Delete Arrows", ArrowOps.buildRemoveArrowsCmds store arrowIds)

    member this.ReconnectArrow(arrowId: Guid, replaceSource: bool, newEndpointId: Guid) : bool =
        this.ExecOpt(ArrowOps.tryResolveReconnectArrowCmd store arrowId replaceSource newEndpointId)

    /// Ordered node selection (Work/Call) -> sequential arrow connect.
    /// Returns number of arrows actually created.
    member this.ConnectSelectionInOrder(orderedNodeIds: seq<Guid>, ?arrowType: ArrowType) : int =
        let connectArrowType = defaultArg arrowType ArrowType.Start
        this.ExecBatch("Connect Selected Nodes In Order", ArrowOps.buildConnectSelectionCmds store orderedNodeIds connectArrowType)

    // =====================================================================
    // ApiDef API
    // =====================================================================
    member internal this.AddApiDef(name: string, systemId: Guid) : ApiDef =
        let apiDef = ApiDef(name, systemId)
        this.Exec(AddApiDef apiDef)
        apiDef

    member this.AddApiDefAndGetId(name: string, systemId: Guid) : Guid =
        (this.AddApiDef(name, systemId)).Id


    member this.UpdateApiDefProperties
        (apiDefId: Guid, isPush: bool,
         txGuid: Nullable<Guid>, rxGuid: Nullable<Guid>,
         duration: int, description: string) =
        this.Exec(PanelOps.buildUpdateApiDefPropertiesCmd store apiDefId isPush txGuid rxGuid duration description)

    // =====================================================================
    // ApiCall API (Call 내부 ApiCall 관리)
    // =====================================================================
    member this.RemoveApiCallFromCall(callId: Guid, apiCallId: Guid) =
        this.Exec(PanelOps.buildRemoveApiCallFromCallCmd store callId apiCallId)

    // =====================================================================
    // Property Panel API
    // =====================================================================
    member _.GetWorkDurationText(workId: Guid) : string =
        PanelOps.getWorkDurationText store workId

    member this.TryUpdateWorkDuration(workId: Guid, durationText: string) : bool =
        match PanelOps.tryBuildUpdateWorkDurationCmd store workId durationText with
        | false, _ -> false
        | true, Some cmd -> this.Exec cmd; true
        | true, None -> true

    member _.GetCallTimeoutText(callId: Guid) : string =
        PanelOps.getCallTimeoutText store callId

    member this.TryUpdateCallTimeout(callId: Guid, msText: string) : bool =
        match PanelOps.tryBuildUpdateCallTimeoutCmd store callId msText with
        | false, _ -> false
        | true, Some cmd -> this.Exec cmd; true
        | true, None     -> true

    member _.GetApiDefsForSystem(systemId: Guid) : ApiDefPanelItem list =
        PanelOps.getApiDefsForSystem store systemId

    member _.GetWorksForSystem(systemId: Guid) : WorkDropdownItem list =
        PanelOps.getWorksForSystem store systemId

    member _.GetApiDefParentSystemId(apiDefId: Guid) : Guid option =
        PanelOps.getApiDefParentSystemId store apiDefId

    member _.GetDeviceApiDefOptionsForCall(callId: Guid) : DeviceApiDefOption list =
        PanelOps.getDeviceApiDefOptionsForCall store callId

    member _.GetCallApiCallsForPanel(callId: Guid) : CallApiCallPanelItem list =
        PanelOps.getCallApiCallsForPanel store callId

    member _.GetAllApiCallsForPanel() : CallApiCallPanelItem list =
        PanelOps.getAllApiCallsForPanel store

    member this.AddApiCallFromPanel(callId: Guid, apiDefId: Guid, apiCallName: string, outputAddress: string, inputAddress: string, valueSpecText: string, inputValueSpecText: string) : Guid option =
        match PanelOps.buildAddApiCallCmd store callId apiDefId apiCallName outputAddress inputAddress valueSpecText inputValueSpecText with
        | Some (newId, cmd) -> this.Exec cmd; Some newId
        | None -> None

    member this.UpdateApiCallFromPanel(callId: Guid, apiCallId: Guid, apiDefId: Guid, apiCallName: string, outputAddress: string, inputAddress: string, outputTypeIndex: int, valueSpecText: string, inputTypeIndex: int, inputValueSpecText: string) : bool =
        this.ExecOpt(PanelOps.buildUpdateApiCallCmd store callId apiCallId apiDefId apiCallName outputAddress inputAddress outputTypeIndex valueSpecText inputTypeIndex inputValueSpecText)

    member _.GetCallConditionsForPanel(callId: Guid) : CallConditionPanelItem list =
        PanelOps.getCallConditionsForPanel store callId

    member this.AddCallCondition(callId: Guid, conditionType: CallConditionType) : bool =
        this.ExecOpt(PanelOps.buildAddCallConditionCmd store callId conditionType)

    member this.RemoveCallCondition(callId: Guid, conditionId: Guid) : bool =
        this.ExecOpt(PanelOps.buildRemoveCallConditionCmd store callId conditionId)

    member this.UpdateCallConditionSettings(callId: Guid, conditionId: Guid, isOR: bool, isRising: bool) : bool =
        this.ExecOpt(PanelOps.buildUpdateCallConditionSettingsCmd store callId conditionId isOR isRising)

    /// 다중 ApiCall을 조건에 한 번에 추가 (Composite 1건 → Undo 1회). 추가된 개수 반환.
    member this.AddApiCallsToConditionBatch(callId: Guid, conditionId: Guid, sourceApiCallIds: Guid[]) : int =
        match PanelOps.buildAddApiCallsToConditionBatchCmd store callId conditionId (List.ofArray sourceApiCallIds) with
        | Some (cmd, count) -> this.Exec cmd; count
        | None -> 0

    member this.RemoveApiCallFromCondition(callId: Guid, conditionId: Guid, apiCallId: Guid) : bool =
        this.ExecOpt(PanelOps.buildRemoveApiCallFromConditionCmd store callId conditionId apiCallId)

    member this.UpdateConditionApiCallOutputSpec(callId: Guid, conditionId: Guid, apiCallId: Guid, newSpecText: string) : bool =
        this.ExecOpt(PanelOps.buildUpdateConditionApiCallOutputSpecCmd store callId conditionId apiCallId newSpecText)

    // =====================================================================
    // 범용 Remove
    // =====================================================================
    /// 여러 엔티티를 Composite 명령 1개로 일괄 삭제 (Undo/Redo 1회 단위)
    member this.RemoveEntities(selections: seq<string * Guid>) =
        let selList = selections |> Seq.distinctBy snd |> Seq.toList
        this.ExecBatch("Delete Entities", RemoveOps.buildRemoveEntitiesCmds store selList) |> ignore

    // =====================================================================
    // 범용 Rename
    // =====================================================================
    member this.RenameEntity(id: Guid, entityType: string, newName: string) =
        match EntityNameAccess.tryGetName store entityType id with
        | Some oldName when oldName <> newName ->
            this.Exec(RenameEntity(id, entityType, oldName, newName))
        | _ -> ()

    // =====================================================================
    // Copy / Paste API
    // =====================================================================
    /// Batch paste API for same-type copied entities.
    /// 모든 배치 타입이 ExecBatch로 감싸 Undo 1회를 보장한다.
    member this.PasteEntities
        (copiedEntityType: string, copiedEntityIds: seq<Guid>, targetEntityType: string, targetEntityId: Guid)
        : int =
        let ids = copiedEntityIds |> Seq.distinct |> Seq.toList
        this.ExecBatch($"Paste {copiedEntityType}s", fun exec ->
            PasteOps.dispatchPaste exec store copiedEntityType ids targetEntityType targetEntityId)

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
