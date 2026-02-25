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
        Ds2.Serialization.JsonConverter.serialize s
        |> Ds2.Serialization.JsonConverter.deserialize<DsStore>

    let ensureValidStoreOrThrow (targetStore: DsStore) (context: string) =
        match ValidationHelpers.validateStore targetStore with
        | Valid -> ()
        | Invalid errors ->
            let message =
                if List.isEmpty errors then "Unknown validation failure"
                else String.concat " | " errors
            invalidOp $"Store validation failed ({context}): {message}"

    let withEntity (findById: Guid -> DsStore -> 'T option) (id: Guid) (onFound: 'T -> unit) =
        match findById id store with
        | Some entity -> onFound entity
        | None -> ()

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
        let events = CommandExecutor.execute cmd store
        try
            ensureValidStoreOrThrow store (sprintf "execute %A" cmd)
        with ex ->
            try
                CommandExecutor.undo cmd store |> ignore
            with rollbackEx ->
                raise (InvalidOperationException($"Command failed and rollback failed: {rollbackEx.Message}", rollbackEx))
            eventBus.Trigger(StoreRefreshed)
            eventBus.Trigger(UndoRedoChanged(undoManager.CanUndo, undoManager.CanRedo))
            raise (InvalidOperationException($"Command rejected by validation: {ex.Message}", ex))
        undoManager.Push(cmd)
        this.PublishCommandResult(cmd, events)

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

    member this.RemoveProject(projectId: Guid) =
        this.Exec(RemoveOps.buildRemoveProjectCmd store projectId)

    // =====================================================================
    // System API
    // =====================================================================
    member this.AddSystem(name: string, projectId: Guid, isActive: bool) : DsSystem =
        let system = DsSystem(name)
        this.Exec(AddSystem(system, projectId, isActive))
        system

    member this.RemoveSystem(systemId: Guid) =
        this.Exec(RemoveOps.buildRemoveSystemCmd store systemId)

    // =====================================================================
    // Flow API
    // =====================================================================
    member this.AddFlow(name: string, systemId: Guid) : Flow =
        let flow = Flow(name, systemId)
        this.Exec(AddFlow flow)
        flow

    member this.RemoveFlow(flowId: Guid) =
        this.Exec(RemoveOps.buildRemoveFlowCmd store flowId)

    // =====================================================================
    // Work API
    // =====================================================================
    member this.AddWork(name: string, flowId: Guid) : Work =
        let work = Work(name, flowId)
        this.Exec(AddWork work)
        work

    member this.RemoveWork(workId: Guid) =
        this.Exec(RemoveOps.buildRemoveWorkCmd store workId)

    member this.MoveEntities(requests: seq<MoveEntityRequest>) : int =
        this.ExecBatch("Move Selected Nodes", RemoveOps.buildMoveEntitiesCmds store requests)

    member this.MoveWork(workId: Guid, newPos: Xywh option) =
        this.MoveEntities([ MoveEntityRequest(EntityTypeNames.Work, workId, newPos) ]) |> ignore

    member this.RenameWork(workId: Guid, newName: string) =
        withEntity DsQuery.getWork workId (fun work ->
            let oldName = work.Name
            if oldName <> newName then
                this.Exec(RenameWork(workId, oldName, newName)))

    member this.UpdateWorkProperties(workId: Guid, newProps: WorkProperties) =
        withEntity DsQuery.getWork workId (fun work ->
            let oldProps = work.Properties.DeepCopy()
            this.Exec(UpdateWorkProps(workId, oldProps, newProps)))

    // =====================================================================
    // Call API
    // =====================================================================
    member this.AddCall(devicesAlias: string, apiName: string, workId: Guid) : Call =
        let call = Call(devicesAlias, apiName, workId)
        this.Exec(AddCall call)
        call

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

    member this.RemoveCall(callId: Guid) =
        this.Exec(RemoveOps.buildRemoveCallCmd store callId)

    member this.MoveCall(callId: Guid, newPos: Xywh option) =
        this.MoveEntities([ MoveEntityRequest(EntityTypeNames.Call, callId, newPos) ]) |> ignore

    member this.RenameCall(callId: Guid, newName: string) =
        withEntity DsQuery.getCall callId (fun call ->
            let oldName = call.Name
            if oldName <> newName then
                this.Exec(RenameCall(callId, oldName, newName)))

    member this.UpdateCallProperties(callId: Guid, newProps: CallProperties) =
        withEntity DsQuery.getCall callId (fun call ->
            let oldProps = call.Properties.DeepCopy()
            this.Exec(UpdateCallProps(callId, oldProps, newProps)))

    // =====================================================================
    // Arrow API
    // =====================================================================
    member this.AddArrowBetweenWorks(flowId: Guid, sourceId: Guid, targetId: Guid, arrowType: ArrowType) =
        let arrow = ArrowBetweenWorks(flowId, sourceId, targetId, arrowType)
        this.Exec(AddArrowWork arrow)
        arrow

    member this.RemoveArrowBetweenWorks(arrowId: Guid) =
        withEntity DsQuery.getArrowWork arrowId (fun arrow ->
            this.Exec(RemoveArrowWork(DeepCopyHelper.backupEntityAs arrow)))

    member this.AddArrowBetweenCalls(flowId: Guid, sourceId: Guid, targetId: Guid, arrowType: ArrowType) =
        let arrow = ArrowBetweenCalls(flowId, sourceId, targetId, arrowType)
        this.Exec(AddArrowCall arrow)
        arrow

    /// Unified arrow add entrypoint for UI callers.
    /// Returns true when the entity type is supported and an arrow was created.
    member this.AddArrow(entityType: string, flowId: Guid, sourceId: Guid, targetId: Guid, arrowType: ArrowType) : bool =
        match EntityKind.tryOfString entityType with
        | ValueSome Work ->
            this.AddArrowBetweenWorks(flowId, sourceId, targetId, arrowType) |> ignore
            true
        | ValueSome Call ->
            this.AddArrowBetweenCalls(flowId, sourceId, targetId, arrowType) |> ignore
            true
        | _ -> false

    member this.RemoveArrowBetweenCalls(arrowId: Guid) =
        withEntity DsQuery.getArrowCall arrowId (fun arrow ->
            this.Exec(RemoveArrowCall(DeepCopyHelper.backupEntityAs arrow)))

    /// Flow 내 모든 화살표의 경로를 일괄 계산 (C# 렌더링용)
    member _.GetFlowArrowPaths(flowId: Guid) : Map<Guid, ArrowPathCalculator.ArrowVisual> =
        ArrowPathCalculator.computeFlowArrowPaths store flowId

    member this.RemoveArrows(arrowIds: seq<Guid>) : int =
        this.ExecBatch("Delete Arrows", ArrowOps.buildRemoveArrowsCmds store arrowIds)

    /// 화살표 타입 자동 판별 후 삭제 (ArrowWorks/ArrowCalls 딕셔너리 체크)
    member this.RemoveArrow(arrowId: Guid) =
        this.RemoveArrows([ arrowId ]) |> ignore

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
        withEntity DsQuery.getApiDef apiDefId (fun apiDef ->
            this.Exec(RemoveApiDef(DeepCopyHelper.backupEntityAs apiDef)))

    member this.UpdateApiDefProperties(apiDefId: Guid, newProps: ApiDefProperties) =
        withEntity DsQuery.getApiDef apiDefId (fun apiDef ->
            let oldProps = apiDef.Properties.DeepCopy()
            this.Exec(UpdateApiDefProps(apiDefId, oldProps, newProps)))

    // =====================================================================
    // ApiCall API (Call 내부 ApiCall 관리)
    // =====================================================================
    member this.AddApiCallToCall(callId: Guid, apiCall: ApiCall) =
        this.Exec(AddApiCallToCall(callId, apiCall))

    member this.RemoveApiCallFromCall(callId: Guid, apiCallId: Guid) =
        withEntity DsQuery.getCall callId (fun call ->
            match call.ApiCalls |> Seq.tryFind (fun ac -> ac.Id = apiCallId) with
            | Some apiCall -> this.Exec(RemoveApiCallFromCall(callId, apiCall))
            | None -> ())

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
    // HW Component API
    // =====================================================================
    member this.AddButton(name: string, systemId: Guid) : HwButton =
        let button = HwButton(name, systemId)
        this.Exec(AddButton button)
        button

    member this.RemoveButton(buttonId: Guid) =
        withEntity DsQuery.getButton buttonId (fun button ->
            this.Exec(RemoveButton(DeepCopyHelper.backupEntityAs button)))

    member this.AddLamp(name: string, systemId: Guid) : HwLamp =
        let lamp = HwLamp(name, systemId)
        this.Exec(AddLamp lamp)
        lamp

    member this.RemoveLamp(lampId: Guid) =
        withEntity DsQuery.getLamp lampId (fun lamp ->
            this.Exec(RemoveLamp(DeepCopyHelper.backupEntityAs lamp)))

    member this.AddHwCondition(name: string, systemId: Guid) : HwCondition =
        let condition = HwCondition(name, systemId)
        this.Exec(AddHwCondition condition)
        condition

    member this.RemoveHwCondition(conditionId: Guid) =
        withEntity DsQuery.getCondition conditionId (fun condition ->
            this.Exec(RemoveHwCondition(DeepCopyHelper.backupEntityAs condition)))

    member this.AddHwAction(name: string, systemId: Guid) : HwAction =
        let action = HwAction(name, systemId)
        this.Exec(AddHwAction action)
        action

    member this.RemoveHwAction(actionId: Guid) =
        withEntity DsQuery.getAction actionId (fun action ->
            this.Exec(RemoveHwAction(DeepCopyHelper.backupEntityAs action)))

    // =====================================================================
    // 범용 Remove (엔티티 타입 디스패치)
    // =====================================================================
    /// 엔티티 타입 문자열 + ID로 적절한 Remove 호출
    member this.RemoveEntity(entityType: string, entityId: Guid) =
        match EntityKind.tryOfString entityType with
        | ValueSome Project   -> this.RemoveProject(entityId)
        | ValueSome System    -> this.RemoveSystem(entityId)
        | ValueSome Flow      -> this.RemoveFlow(entityId)
        | ValueSome Work      -> this.RemoveWork(entityId)
        | ValueSome Call      -> this.RemoveCall(entityId)
        | ValueSome ApiDef    -> this.RemoveApiDef(entityId)
        | ValueSome Button    -> this.RemoveButton(entityId)
        | ValueSome Lamp      -> this.RemoveLamp(entityId)
        | ValueSome Condition -> this.RemoveHwCondition(entityId)
        | ValueSome Action    -> this.RemoveHwAction(entityId)
        | ValueNone           -> ()

    /// 여러 엔티티를 Composite 명령 1개로 일괄 삭제 (Undo/Redo 1회 단위)
    /// Call의 부모 Work가 같은 선택에 포함된 경우 해당 Call은 건너뜀 (Work cascade가 처리)
    member this.RemoveEntities(selections: seq<string * Guid>) =
        let selList = selections |> Seq.distinctBy snd |> Seq.toList
        let selIds  = selList |> List.map snd |> Set.ofList

        let buildCmd (entityType: string) (id: Guid) : EditorCommand option =
            match EntityKind.tryOfString entityType with
            | ValueSome Call ->
                match DsQuery.getCall id store with
                | Some call when Set.contains call.ParentId selIds -> None
                | Some _ -> Some(RemoveOps.buildRemoveCallCmd store id)
                | None   -> None
            | ValueSome Work      -> DsQuery.getWork id store      |> Option.map (fun _ -> RemoveOps.buildRemoveWorkCmd store id)
            | ValueSome Flow      -> DsQuery.getFlow id store      |> Option.map (fun _ -> RemoveOps.buildRemoveFlowCmd store id)
            | ValueSome System    -> DsQuery.getSystem id store    |> Option.map (fun _ -> RemoveOps.buildRemoveSystemCmd store id)
            | ValueSome Project   -> DsQuery.getProject id store   |> Option.map (fun _ -> RemoveOps.buildRemoveProjectCmd store id)
            | ValueSome ApiDef    -> DsQuery.getApiDef id store    |> Option.map (fun a -> RemoveApiDef(DeepCopyHelper.backupEntityAs a))
            | ValueSome Button    -> DsQuery.getButton id store    |> Option.map (fun b -> RemoveButton(DeepCopyHelper.backupEntityAs b))
            | ValueSome Lamp      -> DsQuery.getLamp id store      |> Option.map (fun l -> RemoveLamp(DeepCopyHelper.backupEntityAs l))
            | ValueSome Condition -> DsQuery.getCondition id store |> Option.map (fun c -> RemoveHwCondition(DeepCopyHelper.backupEntityAs c))
            | ValueSome Action    -> DsQuery.getAction id store    |> Option.map (fun a -> RemoveHwAction(DeepCopyHelper.backupEntityAs a))
            | ValueNone -> None

        let commands = selList |> List.choose (fun (et, id) -> buildCmd et id)
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
    /// Copy된 Flow/Work/Call을 대상 위치에 Paste한다.
    /// ExecBatch로 감싸 Undo 1회를 보장한다.
    member this.PasteEntity(copiedEntityType: string, copiedEntityId: Guid, targetEntityType: string, targetEntityId: Guid) : Guid option =
        if not (PasteResolvers.isCopyableEntityType copiedEntityType) then None
        else
            match EntityKind.tryOfString copiedEntityType with
            | ValueSome Flow ->
                DsQuery.getFlow copiedEntityId store
                |> Option.map (fun sourceFlow ->
                    let targetSystemId =
                        PasteResolvers.resolveSystemTarget store targetEntityType targetEntityId
                        |> Option.defaultValue sourceFlow.ParentId
                    this.ExecBatch("Paste Flow", fun exec ->
                        PasteOps.pasteFlowToSystem exec store sourceFlow targetSystemId))
            | ValueSome Work ->
                DsQuery.getWork copiedEntityId store
                |> Option.map (fun sourceWork ->
                    let targetFlowId =
                        PasteResolvers.resolveFlowTarget store targetEntityType targetEntityId
                        |> Option.defaultValue sourceWork.ParentId
                    this.ExecBatch("Paste Work", fun exec ->
                        match PasteOps.pasteWorksToFlowBatch exec store [ sourceWork ] targetFlowId with
                        | pastedWorkId :: _ -> pastedWorkId
                        | [] -> sourceWork.Id))
            | ValueSome Call ->
                DsQuery.getCall copiedEntityId store
                |> Option.map (fun sourceCall ->
                    let targetWorkId =
                        PasteResolvers.resolveWorkTarget store targetEntityType targetEntityId
                        |> Option.defaultValue sourceCall.ParentId
                    this.ExecBatch("Paste Call", fun exec ->
                        match PasteOps.pasteCallsToWorkBatch exec store [ sourceCall ] targetWorkId with
                        | pastedCallId :: _ -> pastedCallId
                        | [] -> sourceCall.Id))
            | _ -> None

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
            let events = CommandExecutor.undo cmd store
            this.PublishCommandResult(cmd, events)

    member this.Redo() =
        match undoManager.PopRedo() with
        | None -> ()
        | Some cmd ->
            let events = CommandExecutor.execute cmd store
            this.PublishCommandResult(cmd, events)

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
