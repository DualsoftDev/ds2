namespace Ds2.UI.Core

open System
open System.Globalization
open Ds2.Core

// =============================================================================
// EditorApi — UI가 호출하는 유일한 진입점
// =============================================================================
type EditorApi(store: DsStore, ?maxUndoSize: int) =
    let undoManager = UndoRedoManager(defaultArg maxUndoSize 100)
    let eventBus = Event<EditorEvent>()
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

    let withEntityOrThrow (entityType: string) (findById: Guid -> DsStore -> 'T option) (id: Guid) (onFound: 'T -> unit) =
        match findById id store with
        | Some entity -> onFound entity
        | None -> invalidOp $"'{entityType}' entity was not found. id={id}"

    // --- 이벤트 ---
    member _.OnEvent = eventBus.Publish
    member _.CanUndo = undoManager.CanUndo
    member _.CanRedo = undoManager.CanRedo
    member _.Store = store

    // --- 내부: 명령 실행 공통 ---
    member private _.PublishCommandResult(cmd: EditorCommand, events: EditorEvent list) =
        match cmd with
        | Composite _ -> eventBus.Trigger(StoreRefreshed)
        | _ -> events |> List.iter eventBus.Trigger
        eventBus.Trigger(UndoRedoChanged(undoManager.CanUndo, undoManager.CanRedo))

    member private this.ExecuteCommand(cmd: EditorCommand) =
        ensureValidStoreOrThrow store (sprintf "before execute %A" cmd)
        let snapshot = cloneStore store
        try
            let events = CommandExecutor.execute cmd store
            ensureValidStoreOrThrow store (sprintf "after execute %A" cmd)
            undoManager.Push(cmd)
            this.PublishCommandResult(cmd, events)
        with ex ->
            StoreCopy.replaceAllCollections snapshot store
            eventBus.Trigger(StoreRefreshed)
            eventBus.Trigger(UndoRedoChanged(undoManager.CanUndo, undoManager.CanRedo))
            raise (InvalidOperationException($"Command execution failed and state was restored: {ex.Message}", ex))

    member internal this.Exec(cmd: EditorCommand) =
        this.ExecuteCommand(cmd)

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
    member this.AddProject(name: string) : Project =
        let project = Project(name)
        this.Exec(AddProject project)
        project

    // =====================================================================
    // System API
    // =====================================================================
    member this.AddSystem(name: string, projectId: Guid, isActive: bool) : DsSystem =
        let system = DsSystem(name)
        this.Exec(AddSystem(system, projectId, isActive))
        system

    // =====================================================================
    // Flow API
    // =====================================================================
    member this.AddFlow(name: string, systemId: Guid) : Flow =
        let flow = Flow(name, systemId)
        this.Exec(AddFlow flow)
        flow

    // =====================================================================
    // Work API
    // =====================================================================
    member this.AddWork(name: string, flowId: Guid) : Work =
        let work = Work(name, flowId)
        this.Exec(AddWork work)
        work

    member this.MoveEntities(requests: seq<MoveEntityRequest>) : int =
        this.ExecBatch("Move Selected Nodes", RemoveOps.buildMoveEntitiesCmds store requests)

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
        let cmdList = DeviceOps.buildAddCallsWithDeviceCmds store projectId workId (callNames |> Seq.toList) createDeviceSystem
        match cmdList with
        | []       -> ()
        | [single] -> this.Exec(single)
        | _        -> this.Exec(Composite("Add Calls", cmdList))

    member this.AddCallWithLinkedApiDefs
        (workId: Guid)
        (devicesAlias: string)
        (apiName: string)
        (apiDefIds: Guid seq)
        : Call =
        let call = Call(devicesAlias, apiName, workId)
        let apiCallCmds =
            apiDefIds
            |> Seq.choose (fun id -> DsQuery.getApiDef id store)
            |> Seq.map (fun apiDef ->
                let apiCall = ApiCall($"{devicesAlias}.{apiDef.Name}")
                apiCall.ApiDefId <- Some apiDef.Id
                AddApiCallToCall(call.Id, apiCall))
            |> Seq.toList
        this.Exec(Composite("Add Call", AddCall call :: apiCallCmds))
        call

    // =====================================================================
    // Arrow API
    // =====================================================================
    /// Unified arrow add entrypoint for UI callers.
    /// Returns true when the entity type is supported and an arrow was created.
    member this.AddArrow(entityType: string, flowId: Guid, sourceId: Guid, targetId: Guid, arrowType: ArrowType) : bool =
        match EntityKind.tryOfString entityType with
        | ValueSome Work ->
            this.Exec(AddArrowWork(ArrowBetweenWorks(flowId, sourceId, targetId, arrowType)))
            true
        | ValueSome Call ->
            this.Exec(AddArrowCall(ArrowBetweenCalls(flowId, sourceId, targetId, arrowType)))
            true
        | _ -> false

    /// Flow 내 모든 화살표의 경로를 일괄 계산 (C# 렌더링용)
    member _.GetFlowArrowPaths(flowId: Guid) : Map<Guid, ArrowPathCalculator.ArrowVisual> =
        ArrowPathCalculator.computeFlowArrowPaths store flowId

    member this.RemoveArrows(arrowIds: seq<Guid>) : int =
        this.ExecBatch("Delete Arrows", ArrowOps.buildRemoveArrowsCmds store arrowIds)

    member this.ReconnectArrow(arrowId: Guid, replaceSource: bool, newEndpointId: Guid) : bool =
        match ArrowOps.tryResolveReconnectArrowCmd store arrowId replaceSource newEndpointId with
        | Some cmd -> this.Exec(cmd); true
        | None -> false

    /// Ordered node selection (Work/Call) -> sequential arrow connect.
    /// Returns number of arrows actually created.
    member this.ConnectSelectionInOrder(orderedNodeIds: seq<Guid>, ?arrowType: ArrowType) : int =
        let connectArrowType = defaultArg arrowType ArrowType.Start
        this.ExecBatch("Connect Selected Nodes In Order", ArrowOps.buildConnectSelectionCmds store orderedNodeIds connectArrowType)

    // =====================================================================
    // ApiDef API
    // =====================================================================
    member this.AddApiDef(name: string, systemId: Guid) : ApiDef =
        let apiDef = ApiDef(name, systemId)
        this.Exec(AddApiDef apiDef)
        apiDef

    member this.RemoveApiDef(apiDefId: Guid) =
        withEntityOrThrow EntityTypeNames.ApiDef DsQuery.getApiDef apiDefId (fun apiDef ->
            this.Exec(RemoveApiDef(DeepCopyHelper.backupEntityAs apiDef)))

    member this.UpdateApiDefProperties(apiDefId: Guid, newProps: ApiDefProperties) =
        withEntityOrThrow EntityTypeNames.ApiDef DsQuery.getApiDef apiDefId (fun apiDef ->
            let oldProps = apiDef.Properties.DeepCopy()
            this.Exec(UpdateApiDefProps(apiDefId, oldProps, newProps)))

    // =====================================================================
    // ApiCall API (Call 내부 ApiCall 관리)
    // =====================================================================
    member this.RemoveApiCallFromCall(callId: Guid, apiCallId: Guid) =
        withEntityOrThrow EntityTypeNames.Call DsQuery.getCall callId (fun call ->
            match call.ApiCalls |> Seq.tryFind (fun ac -> ac.Id = apiCallId) with
            | Some apiCall -> this.Exec(RemoveApiCallFromCall(callId, apiCall))
            | None -> invalidOp $"ApiCall was not found in Call. callId={callId}, apiCallId={apiCallId}")

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
        | true, None -> true

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

    member this.AddApiCallFromPanel(callId: Guid, apiDefId: Guid, apiCallName: string, outputAddress: string, inputAddress: string, valueSpecText: string, inputValueSpecText: string) : Guid option =
        match PanelOps.buildAddApiCallCmd store callId apiDefId apiCallName outputAddress inputAddress valueSpecText inputValueSpecText with
        | Some (newId, cmd) -> this.Exec cmd; Some newId
        | None -> None

    member this.UpdateApiCallFromPanel(callId: Guid, apiCallId: Guid, apiDefId: Guid, apiCallName: string, outputAddress: string, inputAddress: string, valueSpecText: string, inputValueSpecText: string) : bool =
        match PanelOps.buildUpdateApiCallCmd store callId apiCallId apiDefId apiCallName outputAddress inputAddress valueSpecText inputValueSpecText with
        | Some cmd -> this.Exec cmd; true
        | None -> false

    // =====================================================================
    // 범용 Remove
    // =====================================================================
    /// 여러 엔티티를 Composite 명령 1개로 일괄 삭제 (Undo/Redo 1회 단위)
    /// Work/Call은 선택 집합 전체의 화살표를 한 번에 수집해 중복 제거 후 삭제.
    /// Call의 부모 Work가 같은 선택에 포함된 경우 해당 Call은 건너뜀 (Work cascade가 처리).
    member this.RemoveEntities(selections: seq<string * Guid>) =
        let selList = selections |> Seq.distinctBy snd |> Seq.toList
        let selIds  = selList |> List.map snd |> Set.ofList

        // ── Work 배치 ─────────────────────────────────────────────────────────
        let selectedWorks =
            selList |> List.choose (fun (et, id) ->
                match EntityKind.tryOfString et with
                | ValueSome EntityKind.Work -> DsQuery.getWork id store
                | _ -> None)

        // Work cascade: 각 Work 내 Call 전체 수집
        let cascadeCalls = selectedWorks |> List.collect (fun w -> DsQuery.callsOf w.Id store)

        let workIds     = selectedWorks |> List.map (fun w -> w.Id) |> Set.ofList
        let cascadeIds  = cascadeCalls  |> List.map (fun c -> c.Id) |> Set.ofList

        let batchArrowWorks =
            if workIds.IsEmpty then []
            else CascadeHelpers.arrowWorksFor store workIds

        // ── Call 배치 (부모 Work가 선택 집합에 없는 Call만) ─────────────────
        let selectedCalls =
            selList |> List.choose (fun (et, id) ->
                match EntityKind.tryOfString et with
                | ValueSome EntityKind.Call ->
                    match DsQuery.getCall id store with
                    | Some call when not (selIds.Contains call.ParentId) -> Some call
                    | _ -> None
                | _ -> None)

        let directCallIds = selectedCalls |> List.map (fun c -> c.Id) |> Set.ofList
        let allCallIds    = Set.union cascadeIds directCallIds

        let batchArrowCalls =
            if allCallIds.IsEmpty then []
            else CascadeHelpers.arrowCallsFor store allCallIds

        // ── 나머지 엔티티 (Flow/System/Project/HW) ───────────────────────────
        let otherCmds =
            selList |> List.choose (fun (et, id) ->
                match EntityKind.tryOfString et with
                | ValueSome EntityKind.Work | ValueSome EntityKind.Call -> None
                | ValueSome EntityKind.Flow      -> DsQuery.getFlow id store      |> Option.map (fun _ -> RemoveOps.buildRemoveFlowCmd store id)
                | ValueSome EntityKind.System    -> DsQuery.getSystem id store    |> Option.map (fun _ -> RemoveOps.buildRemoveSystemCmd store id)
                | ValueSome EntityKind.Project   -> DsQuery.getProject id store   |> Option.map (fun _ -> RemoveOps.buildRemoveProjectCmd store id)
                | ValueSome EntityKind.ApiDef    -> DsQuery.getApiDef id store    |> Option.map (fun a -> RemoveApiDef(DeepCopyHelper.backupEntityAs a))
                | ValueSome EntityKind.Button    -> DsQuery.getButton id store    |> Option.map (fun b -> RemoveButton(DeepCopyHelper.backupEntityAs b))
                | ValueSome EntityKind.Lamp      -> DsQuery.getLamp id store      |> Option.map (fun l -> RemoveLamp(DeepCopyHelper.backupEntityAs l))
                | ValueSome EntityKind.Condition -> DsQuery.getCondition id store |> Option.map (fun c -> RemoveHwCondition(DeepCopyHelper.backupEntityAs c))
                | ValueSome EntityKind.Action    -> DsQuery.getAction id store    |> Option.map (fun a -> RemoveHwAction(DeepCopyHelper.backupEntityAs a))
                | ValueNone -> None)

        // ── 실행 순서: 말단(ArrowCall) → Call → ArrowWork → Work → 기타 ──────
        let commands = [
            yield! batchArrowCalls |> List.map (fun a -> RemoveArrowCall(DeepCopyHelper.backupEntityAs a))
            yield! cascadeCalls    |> List.map (fun c -> RemoveCall(DeepCopyHelper.backupEntityAs c))
            yield! selectedCalls   |> List.map (fun c -> RemoveCall(DeepCopyHelper.backupEntityAs c))
            yield! batchArrowWorks |> List.map (fun a -> RemoveArrowWork(DeepCopyHelper.backupEntityAs a))
            yield! selectedWorks   |> List.map (fun w -> RemoveWork(DeepCopyHelper.backupEntityAs w))
            yield! otherCmds
        ]
        this.ExecBatch("Delete Entities", commands) |> ignore

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
        if not (PasteResolvers.isCopyableEntityType copiedEntityType) then 0
        else
            let ids = copiedEntityIds |> Seq.distinct |> Seq.toList
            match EntityKind.tryOfString copiedEntityType with
            | ValueSome Flow ->
                let sourceFlows = ids |> List.choose (fun id -> DsQuery.getFlow id store)
                if sourceFlows.IsEmpty then 0
                else
                    this.ExecBatch("Paste Flows", fun exec ->
                        sourceFlows |> List.sumBy (fun sourceFlow ->
                            let targetSystemId =
                                PasteResolvers.resolveSystemTarget store targetEntityType targetEntityId
                                |> Option.defaultValue sourceFlow.ParentId
                            PasteOps.pasteFlowToSystem exec store sourceFlow targetSystemId |> ignore
                            1))
            | ValueSome Work ->
                let sourceWorks = ids |> List.choose (fun id -> DsQuery.getWork id store)
                if sourceWorks.IsEmpty then 0
                else
                    let targetFlowId =
                        PasteResolvers.resolveFlowTarget store targetEntityType targetEntityId
                        |> Option.defaultValue sourceWorks.Head.ParentId
                    this.ExecBatch("Paste Works", fun exec ->
                        PasteOps.pasteWorksToFlowBatch exec store sourceWorks targetFlowId |> List.length)
            | ValueSome Call ->
                let sourceCalls = ids |> List.choose (fun id -> DsQuery.getCall id store)
                if sourceCalls.IsEmpty then 0
                else
                    let targetWorkId =
                        PasteResolvers.resolveWorkTarget store targetEntityType targetEntityId
                        |> Option.defaultValue sourceCalls.Head.ParentId
                    this.ExecBatch("Paste Calls", fun exec ->
                        PasteOps.pasteCallsToWorkBatch exec store sourceCalls targetWorkId |> List.length)
            | _ -> 0

    // =====================================================================
    // Undo / Redo
    // =====================================================================
    member this.Undo() =
        match undoManager.PopUndo() with
        | None -> ()
        | Some cmd ->
            let snapshot = cloneStore store
            try
                ensureValidStoreOrThrow store (sprintf "before undo %A" cmd)
                let events = CommandExecutor.undo cmd store
                ensureValidStoreOrThrow store (sprintf "after undo %A" cmd)
                this.PublishCommandResult(cmd, events)
            with ex ->
                StoreCopy.replaceAllCollections snapshot store
                undoManager.RestoreAfterFailedUndo(cmd)
                eventBus.Trigger(StoreRefreshed)
                eventBus.Trigger(UndoRedoChanged(undoManager.CanUndo, undoManager.CanRedo))
                raise (InvalidOperationException($"Undo failed and state was restored: {ex.Message}", ex))

    member this.Redo() =
        match undoManager.PopRedo() with
        | None -> ()
        | Some cmd ->
            let snapshot = cloneStore store
            try
                ensureValidStoreOrThrow store (sprintf "before redo %A" cmd)
                let events = CommandExecutor.execute cmd store
                ensureValidStoreOrThrow store (sprintf "after redo %A" cmd)
                this.PublishCommandResult(cmd, events)
            with ex ->
                StoreCopy.replaceAllCollections snapshot store
                undoManager.RestoreAfterFailedRedo(cmd)
                eventBus.Trigger(StoreRefreshed)
                eventBus.Trigger(UndoRedoChanged(undoManager.CanUndo, undoManager.CanRedo))
                raise (InvalidOperationException($"Redo failed and state was restored: {ex.Message}", ex))

    // =====================================================================
    // 파일 I/O
    // =====================================================================
    member this.SaveToFile(path: string) =
        Ds2.Serialization.JsonConverter.saveToFile path store

    member this.LoadFromFile(path: string) =
        let backup = cloneStore store
        try
            let loaded = Ds2.Serialization.JsonConverter.loadFromFile<DsStore> path
            if isNull (box loaded) then
                invalidOp "Loaded store is null."

            ensureValidStoreOrThrow loaded (sprintf "load file '%s'" path)
            StoreCopy.replaceAllCollections loaded store
            ensureValidStoreOrThrow store "apply loaded store"

            undoManager.Clear()
            eventBus.Trigger(StoreRefreshed)
            eventBus.Trigger(UndoRedoChanged(false, false))
        with ex ->
            StoreCopy.replaceAllCollections backup store
            raise (InvalidOperationException($"LoadFromFile failed: {ex.Message}", ex))
