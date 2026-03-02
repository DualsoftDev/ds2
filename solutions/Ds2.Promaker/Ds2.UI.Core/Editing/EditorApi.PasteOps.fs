module Ds2.UI.Core.PasteOps

open System
open Ds2.Core

// =============================================================================
// Paste execution algorithms.
// ExecFn threads command dispatch from EditorApi without coupling to the class.
// =============================================================================

type ExecFn = EditorCommand -> unit

// =============================================================================
// Device System paste state — shared across calls in a single paste batch.
// Tracks device systems cloned/reused during DifferentFlow paste operations.
// =============================================================================

type DevicePasteState = {
    // key: targetSystemName (e.g., "FlowB_Dev1")
    // value: (targetSystem, Map<sourceApiDefId → targetApiDefId>)
    ClonedSystems: Map<string, DsSystem * Map<Guid, Guid>>
}

let private initialDevicePasteState = { ClonedSystems = Map.empty }

type private DeviceFlowCtx = {
    Store: DsStore
    ProjectId: Guid
    TargetFlowName: string
}

// Arrow 재연결: sourceWorkArrows/sourceCallArrows를 id 매핑으로 변환해 exec
let private replayArrows
    (exec: ExecFn) (flowId: Guid)
    (sourceWorkArrows: ArrowBetweenWorks list) (sourceCallArrows: ArrowBetweenCalls list)
    (workMap: Map<Guid, Guid>) (callMap: Map<Guid, Guid>) =
    for arrow in sourceWorkArrows do
        match Map.tryFind arrow.SourceId workMap, Map.tryFind arrow.TargetId workMap with
        | Some src, Some tgt -> exec(AddArrowWork(ArrowBetweenWorks(flowId, src, tgt, arrow.ArrowType)))
        | _ -> ()
    for arrow in sourceCallArrows do
        match Map.tryFind arrow.SourceId callMap, Map.tryFind arrow.TargetId callMap with
        | Some src, Some tgt -> exec(AddArrowCall(ArrowBetweenCalls(flowId, src, tgt, arrow.ArrowType)))
        | _ -> ()

// flowIds 내 모든 arrows 수집 → source/target 모두 selectedIds에 속하는 것만 반환
let private collectArrowsWithinSet
    (getArrows: Guid -> DsStore -> 'a list)
    (getSourceId: 'a -> Guid) (getTargetId: 'a -> Guid)
    (flowIds: Set<Guid>) (selectedIds: Set<Guid>) (store: DsStore) : 'a list =
    flowIds
    |> Set.toList
    |> List.collect (fun flowId -> getArrows flowId store)
    |> List.filter (fun a -> selectedIds.Contains(getSourceId a) && selectedIds.Contains(getTargetId a))

let private isPassiveSystemId (store: DsStore) (systemId: Guid) : bool =
    DsQuery.allProjects store
    |> List.exists (fun p -> p.PassiveSystemIds.Contains(systemId))

let private ensureTargetDeviceSystem
    (store: DsStore)
    (exec: ExecFn)
    (projectId: Guid)
    (targetFlowName: string)
    (devAlias: string)
    (sourceSystemId: Guid)
    (state: DevicePasteState)
    : DevicePasteState * Map<Guid, Guid> =
    let targetName = $"{targetFlowName}_{devAlias}"
    match Map.tryFind targetName state.ClonedSystems with
    | Some (_, mapping) -> state, mapping
    | None ->
        let existing =
            DsQuery.passiveSystemsOf projectId store
            |> List.tryFind (fun s -> s.Name = targetName)
        let targetSystem, mapping =
            match existing with
            | Some sys ->
                let targetApiDefs = DsQuery.apiDefsOf sys.Id store
                let sourceApiDefs = DsQuery.apiDefsOf sourceSystemId store
                let mapping =
                    sourceApiDefs
                    |> List.choose (fun src ->
                        targetApiDefs
                        |> List.tryFind (fun t -> t.Name = src.Name)
                        |> Option.map (fun t -> src.Id, t.Id))
                    |> Map.ofList
                sys, mapping
            | None ->
                let newSystem = DsSystem(targetName)
                exec(AddSystem(newSystem, projectId, false))
                let sourceApiDefs = DsQuery.apiDefsOf sourceSystemId store
                let mapping =
                    sourceApiDefs
                    |> List.map (fun src ->
                        let cloned = ApiDef(src.Name, newSystem.Id)
                        cloned.Properties.IsPush <- src.Properties.IsPush
                        exec(AddApiDef cloned)
                        src.Id, cloned.Id)
                    |> Map.ofList
                newSystem, mapping
        let newState = { ClonedSystems = Map.add targetName (targetSystem, mapping) state.ClonedSystems }
        newState, mapping

/// 새 GUID로 ApiCall 복사 — 다른 Work/Flow로 붙여넣을 때 사용
let copyApiCalls (exec: ExecFn) (sourceCall: Call) (targetCallId: Guid) =
    for apiCall in sourceCall.ApiCalls do
        let copied = ApiCall(apiCall.Name)
        copied.InTag  <- apiCall.InTag  |> Option.map (fun t -> IOTag(t.Name, t.Address, t.Description))
        copied.OutTag <- apiCall.OutTag |> Option.map (fun t -> IOTag(t.Name, t.Address, t.Description))
        copied.ApiDefId <- apiCall.ApiDefId
        copied.InputSpec  <- apiCall.InputSpec
        copied.OutputSpec <- apiCall.OutputSpec
        exec(AddApiCallToCall(targetCallId, copied))

/// 기존 ApiCall GUID 공유 — 동일 Work 내 붙여넣을 때 사용 (store.ApiCalls 불변)
let shareApiCalls (exec: ExecFn) (sourceCall: Call) (targetCallId: Guid) =
    for apiCall in sourceCall.ApiCalls do
        exec(AddSharedApiCallToCall(targetCallId, apiCall.Id))

/// DifferentFlow 케이스: ApiCall 복사 + Device System 복제/재사용 + ApiDefId 매핑
let private copyApiCallsAcrossFlows
    (store: DsStore)
    (exec: ExecFn)
    (projectId: Guid)
    (targetFlowName: string)
    (sourceCall: Call)
    (targetCallId: Guid)
    (state: DevicePasteState)
    : DevicePasteState =
    // Passive system에 연결된 ApiDef가 있는지 확인
    let sourceSystemIdOpt =
        sourceCall.ApiCalls
        |> Seq.tryPick (fun ac ->
            ac.ApiDefId
            |> Option.bind (fun id -> DsQuery.getApiDef id store)
            |> Option.bind (fun d ->
                if isPassiveSystemId store d.ParentId then Some d.ParentId
                else None))
    match sourceSystemIdOpt with
    | None ->
        // Passive system ApiDef 없음 — 일반 복사
        copyApiCalls exec sourceCall targetCallId
        state
    | Some sourceSystemId ->
        let devAlias = sourceCall.DevicesAlias
        let newState, apiDefMapping =
            ensureTargetDeviceSystem store exec projectId targetFlowName devAlias sourceSystemId state
        for apiCall in sourceCall.ApiCalls do
            let copied = ApiCall(apiCall.Name)
            copied.InTag  <- apiCall.InTag  |> Option.map (fun t -> IOTag(t.Name, t.Address, t.Description))
            copied.OutTag <- apiCall.OutTag |> Option.map (fun t -> IOTag(t.Name, t.Address, t.Description))
            copied.ApiDefId <-
                apiCall.ApiDefId
                |> Option.bind (fun id -> Map.tryFind id apiDefMapping)
                |> Option.orElse apiCall.ApiDefId
            copied.InputSpec  <- apiCall.InputSpec
            copied.OutputSpec <- apiCall.OutputSpec
            exec(AddApiCallToCall(targetCallId, copied))
        newState

/// sourceCall의 ParentId(Work)와 targetWorkId를 비교해 컨텍스트 결정
let detectCopyContext (store: DsStore) (sourceCall: Call) (targetWorkId: Guid) : CallCopyContext =
    if sourceCall.ParentId = targetWorkId then
        SameWork
    else
        match DsQuery.getWork targetWorkId store, DsQuery.getWork sourceCall.ParentId store with
        | Some tw, Some sw when tw.ParentId = sw.ParentId -> DifferentWork
        | _ -> DifferentFlow

let private pasteCallToWork
    (exec: ExecFn)
    (context: CallCopyContext)
    (sourceCall: Call)
    (targetWorkId: Guid)
    (deviceState: DevicePasteState)
    (deviceFlowCtxOpt: DeviceFlowCtx option)
    : Call * DevicePasteState =
    let pastedCall = Call(sourceCall.DevicesAlias, sourceCall.ApiName, targetWorkId)
    exec(AddCall pastedCall)
    exec(UpdateCallProps(pastedCall.Id, pastedCall.Properties.DeepCopy(), sourceCall.Properties.DeepCopy()))
    let newPos = PasteResolvers.offsetPosition sourceCall.Position
    if pastedCall.Position <> newPos then
        exec(MoveCall(pastedCall.Id, pastedCall.Position, newPos))
    match context with
    | SameWork ->
        shareApiCalls exec sourceCall pastedCall.Id
        pastedCall, deviceState
    | DifferentWork ->
        copyApiCalls exec sourceCall pastedCall.Id
        pastedCall, deviceState
    | DifferentFlow ->
        match deviceFlowCtxOpt with
        | Some ctx ->
            let newState = copyApiCallsAcrossFlows ctx.Store exec ctx.ProjectId ctx.TargetFlowName sourceCall pastedCall.Id deviceState
            pastedCall, newState
        | None ->
            copyApiCalls exec sourceCall pastedCall.Id
            pastedCall, deviceState

/// store에서 Flow 조회 → DeviceFlowCtx 구성 (Flow가 이미 store에 있는 경우에만 사용)
let private makeDeviceFlowCtx (store: DsStore) (targetFlowId: Guid) : DeviceFlowCtx option =
    match DsQuery.getFlow targetFlowId store with
    | None -> None
    | Some targetFlow ->
        let projectIdOpt =
            DsQuery.getSystem targetFlow.ParentId store
            |> Option.bind (fun s -> EntityHierarchyQueries.findProjectOfSystem store s.Id)
        match projectIdOpt with
        | None -> None
        | Some projectId ->
            Some {
                Store = store
                ProjectId = projectId
                TargetFlowName = targetFlow.Name
            }

/// System에서 Project 역탐색 → DeviceFlowCtx 직접 구성
/// pasteFlowToSystem에서 pastedFlow는 exec 후에도 store snapshot에 없으므로 이 오버로드를 사용
let private makeDeviceFlowCtxDirect (store: DsStore) (targetSystemId: Guid) (targetFlowName: string) : DeviceFlowCtx option =
    EntityHierarchyQueries.findProjectOfSystem store targetSystemId
    |> Option.map (fun projectId ->
        { Store = store; ProjectId = projectId; TargetFlowName = targetFlowName })

/// deviceFlowCtxOpt를 외부에서 주입받아 DifferentFlow paste를 수행.
/// pasteWorksToFlowBatch: makeDeviceFlowCtx store targetFlowId (store에 있는 Flow)
/// pasteFlowToSystem: makeDeviceFlowCtxDirect (pastedFlow는 store에 없으므로 직접 구성)
let private pasteWorkToFlow
    (exec: ExecFn)
    (store: DsStore)
    (sourceWork: Work)
    (targetFlowId: Guid)
    (deviceState: DevicePasteState)
    (deviceFlowCtxOpt: DeviceFlowCtx option)
    : Work * Map<Guid, Guid> * DevicePasteState =
    let pastedWork = Work(sourceWork.Name, targetFlowId)
    exec(AddWork pastedWork)
    exec(UpdateWorkProps(pastedWork.Id, pastedWork.Properties.DeepCopy(), sourceWork.Properties.DeepCopy()))
    let newPos = PasteResolvers.offsetPosition sourceWork.Position
    if pastedWork.Position <> newPos then
        exec(MoveWork(pastedWork.Id, pastedWork.Position, newPos))

    let isDifferentFlow = sourceWork.ParentId <> targetFlowId
    let context = if isDifferentFlow then DifferentFlow else DifferentWork
    let ctxOpt = if isDifferentFlow then deviceFlowCtxOpt else None

    let callMap, finalDeviceState =
        DsQuery.callsOf sourceWork.Id store
        |> List.fold (fun (callMap, devState) sourceCall ->
            let pastedCall, newDevState = pasteCallToWork exec context sourceCall pastedWork.Id devState ctxOpt
            Map.add sourceCall.Id pastedCall.Id callMap, newDevState
        ) (Map.empty, deviceState)

    pastedWork, callMap, finalDeviceState

let pasteFlowToSystem (exec: ExecFn) (store: DsStore) (sourceFlow: Flow) (targetSystemId: Guid) : Guid =
    let pastedFlow = Flow(sourceFlow.Name, targetSystemId)
    exec(AddFlow pastedFlow)

    // pastedFlow는 exec 후에도 store snapshot에 없으므로 DeviceFlowCtx를 직접 구성
    let deviceFlowCtxOpt = makeDeviceFlowCtxDirect store targetSystemId pastedFlow.Name

    let sourceWorkArrows = DsQuery.arrowWorksOf sourceFlow.Id store
    let sourceCallArrows = DsQuery.arrowCallsOf sourceFlow.Id store

    let workMap, callMap, _ =
        DsQuery.worksOf sourceFlow.Id store
        |> List.fold (fun (workMap, callMap, devState) sourceWork ->
            let pastedWork, localCallMap, newDevState = pasteWorkToFlow exec store sourceWork pastedFlow.Id devState deviceFlowCtxOpt
            let mergedCallMap = Map.fold (fun s k v -> Map.add k v s) callMap localCallMap
            Map.add sourceWork.Id pastedWork.Id workMap, mergedCallMap, newDevState
        ) (Map.empty<Guid, Guid>, Map.empty<Guid, Guid>, initialDevicePasteState)

    replayArrows exec pastedFlow.Id sourceWorkArrows sourceCallArrows workMap callMap

    pastedFlow.Id

let pasteWorksToFlowBatch (exec: ExecFn) (store: DsStore) (sourceWorks: Work list) (targetFlowId: Guid) : Guid list =
    let selectedWorkIds = sourceWorks |> List.map (fun w -> w.Id) |> Set.ofList
    let selectedCallIds =
        sourceWorks
        |> List.collect (fun w -> DsQuery.callsOf w.Id store)
        |> List.map (fun c -> c.Id)
        |> Set.ofList
    let sourceFlowIds = sourceWorks |> List.map (fun w -> w.ParentId) |> Set.ofList

    let sourceWorkArrows =
        collectArrowsWithinSet DsQuery.arrowWorksOf (fun a -> a.SourceId) (fun a -> a.TargetId) sourceFlowIds selectedWorkIds store
    let sourceCallArrows =
        collectArrowsWithinSet DsQuery.arrowCallsOf (fun a -> a.SourceId) (fun a -> a.TargetId) sourceFlowIds selectedCallIds store

    // targetFlow는 store에 이미 있으므로 makeDeviceFlowCtx로 정상 조회
    let deviceFlowCtxOpt = makeDeviceFlowCtx store targetFlowId

    let workMap, callMap, pastedWorkIdsRev, _ =
        sourceWorks
        |> List.fold (fun (workMap, callMap, pastedWorkIdsRev, devState) sourceWork ->
            let pastedWork, localCallMap, newDevState = pasteWorkToFlow exec store sourceWork targetFlowId devState deviceFlowCtxOpt
            let mergedCallMap = Map.fold (fun s k v -> Map.add k v s) callMap localCallMap
            Map.add sourceWork.Id pastedWork.Id workMap, mergedCallMap, pastedWork.Id :: pastedWorkIdsRev, newDevState
        ) (Map.empty<Guid, Guid>, Map.empty<Guid, Guid>, [], initialDevicePasteState)

    replayArrows exec targetFlowId sourceWorkArrows sourceCallArrows workMap callMap

    pastedWorkIdsRev |> List.rev

let pasteCallsToWorkBatch (exec: ExecFn) (store: DsStore) (sourceCalls: Call list) (targetWorkId: Guid) : Guid list =
    let selectedCallIds = sourceCalls |> List.map (fun c -> c.Id) |> Set.ofList
    let sourceFlowIds =
        sourceCalls
        |> List.choose (fun c -> DsQuery.getWork c.ParentId store |> Option.map (fun w -> w.ParentId))
        |> Set.ofList
    let sourceCallArrows =
        collectArrowsWithinSet DsQuery.arrowCallsOf (fun a -> a.SourceId) (fun a -> a.TargetId) sourceFlowIds selectedCallIds store
    let targetFlowIdOpt =
        DsQuery.getWork targetWorkId store |> Option.map (fun w -> w.ParentId)

    // DifferentFlow calls에 사용할 Device context 준비
    let deviceFlowCtxForDiffFlow =
        targetFlowIdOpt |> Option.bind (makeDeviceFlowCtx store)

    let callMap, pastedCallIdsRev, _ =
        sourceCalls
        |> List.fold (fun (callMap, pastedCallIdsRev, devState) sourceCall ->
            let context = detectCopyContext store sourceCall targetWorkId
            let deviceFlowCtxOpt =
                match context with
                | DifferentFlow -> deviceFlowCtxForDiffFlow
                | _ -> None
            let pastedCall, newDevState = pasteCallToWork exec context sourceCall targetWorkId devState deviceFlowCtxOpt
            Map.add sourceCall.Id pastedCall.Id callMap, pastedCall.Id :: pastedCallIdsRev, newDevState
        ) (Map.empty<Guid, Guid>, [], initialDevicePasteState)

    match targetFlowIdOpt with
    | Some targetFlowId ->
        for arrow in sourceCallArrows do
            match Map.tryFind arrow.SourceId callMap, Map.tryFind arrow.TargetId callMap with
            | Some src, Some tgt ->
                exec(AddArrowCall(ArrowBetweenCalls(targetFlowId, src, tgt, arrow.ArrowType)))
            | _ -> ()
    | None -> ()

    pastedCallIdsRev |> List.rev

/// PasteEntities 분기 dispatch.
/// exec 콜백으로 command를 즉시 전달하고 붙여넣기 수행. 반환값: 실제 붙여넣은 엔티티 수.
let dispatchPaste
    (exec: ExecFn)
    (store: DsStore)
    (copiedEntityType: string)
    (copiedIds: Guid list)
    (targetEntityType: string)
    (targetEntityId: Guid)
    : int =
    if not (PasteResolvers.isCopyableEntityType copiedEntityType) then 0
    else
        match EntityKind.tryOfString copiedEntityType with
        | ValueSome Flow ->
            let sourceFlows = copiedIds |> List.choose (fun id -> DsQuery.getFlow id store)
            if sourceFlows.IsEmpty then 0
            else
                sourceFlows |> List.sumBy (fun sourceFlow ->
                    let targetSystemId =
                        PasteResolvers.resolveSystemTarget store targetEntityType targetEntityId
                        |> Option.defaultValue sourceFlow.ParentId
                    pasteFlowToSystem exec store sourceFlow targetSystemId |> ignore
                    1)
        | ValueSome Work ->
            let sourceWorks = copiedIds |> List.choose (fun id -> DsQuery.getWork id store)
            if sourceWorks.IsEmpty then 0
            else
                let targetFlowId =
                    PasteResolvers.resolveFlowTarget store targetEntityType targetEntityId
                    |> Option.defaultValue sourceWorks.Head.ParentId
                pasteWorksToFlowBatch exec store sourceWorks targetFlowId |> List.length
        | ValueSome Call ->
            let sourceCalls = copiedIds |> List.choose (fun id -> DsQuery.getCall id store)
            if sourceCalls.IsEmpty then 0
            else
                let targetWorkId =
                    PasteResolvers.resolveWorkTarget store targetEntityType targetEntityId
                    |> Option.defaultValue sourceCalls.Head.ParentId
                pasteCallsToWorkBatch exec store sourceCalls targetWorkId |> List.length
        | _ -> 0
