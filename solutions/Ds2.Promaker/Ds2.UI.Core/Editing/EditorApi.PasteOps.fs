module Ds2.UI.Core.PasteOps

open System
open Ds2.Core

// =============================================================================
// Paste execution algorithms.
// ExecFn threads command dispatch from EditorApi without coupling to the class.
// =============================================================================

type ExecFn = EditorCommand -> unit

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

/// sourceCall의 ParentId(Work)와 targetWorkId를 비교해 컨텍스트 결정
let detectCopyContext (store: DsStore) (sourceCall: Call) (targetWorkId: Guid) : CallCopyContext =
    if sourceCall.ParentId = targetWorkId then
        SameWork
    else
        match DsQuery.getWork targetWorkId store, DsQuery.getWork sourceCall.ParentId store with
        | Some tw, Some sw when tw.ParentId = sw.ParentId -> DifferentWork
        | _ -> DifferentFlow

let pasteCallToWork (exec: ExecFn) (context: CallCopyContext) (sourceCall: Call) (targetWorkId: Guid) : Call =
    let pastedCall = Call(sourceCall.DevicesAlias, sourceCall.ApiName, targetWorkId)
    exec(AddCall pastedCall)
    exec(UpdateCallProps(pastedCall.Id, pastedCall.Properties.DeepCopy(), sourceCall.Properties.DeepCopy()))
    let newPos = PasteResolvers.offsetPosition sourceCall.Position
    if pastedCall.Position <> newPos then
        exec(MoveCall(pastedCall.Id, pastedCall.Position, newPos))
    match context with
    | SameWork -> shareApiCalls exec sourceCall pastedCall.Id
    | DifferentWork | DifferentFlow -> copyApiCalls exec sourceCall pastedCall.Id
    pastedCall

let pasteWorkToFlow (exec: ExecFn) (store: DsStore) (sourceWork: Work) (targetFlowId: Guid) : Work * Map<Guid, Guid> =
    let pastedWork = Work(sourceWork.Name, targetFlowId)
    exec(AddWork pastedWork)
    exec(UpdateWorkProps(pastedWork.Id, pastedWork.Properties.DeepCopy(), sourceWork.Properties.DeepCopy()))
    let newPos = PasteResolvers.offsetPosition sourceWork.Position
    if pastedWork.Position <> newPos then
        exec(MoveWork(pastedWork.Id, pastedWork.Position, newPos))

    let callMap =
        DsQuery.callsOf sourceWork.Id store
        |> List.map (fun sourceCall ->
            let pastedCall = pasteCallToWork exec DifferentWork sourceCall pastedWork.Id
            sourceCall.Id, pastedCall.Id)
        |> Map.ofList

    pastedWork, callMap

let pasteFlowToSystem (exec: ExecFn) (store: DsStore) (sourceFlow: Flow) (targetSystemId: Guid) : Guid =
    let pastedFlow = Flow(sourceFlow.Name, targetSystemId)
    exec(AddFlow pastedFlow)

    let sourceWorkArrows = DsQuery.arrowWorksOf sourceFlow.Id store
    let sourceCallArrows = DsQuery.arrowCallsOf sourceFlow.Id store

    let workMap, callMap =
        DsQuery.worksOf sourceFlow.Id store
        |> List.fold (fun (workMap, callMap) sourceWork ->
            let pastedWork, localCallMap = pasteWorkToFlow exec store sourceWork pastedFlow.Id
            let mergedCallMap = Map.fold (fun s k v -> Map.add k v s) callMap localCallMap
            Map.add sourceWork.Id pastedWork.Id workMap, mergedCallMap) (Map.empty<Guid, Guid>, Map.empty<Guid, Guid>)

    for arrow in sourceWorkArrows do
        match Map.tryFind arrow.SourceId workMap, Map.tryFind arrow.TargetId workMap with
        | Some src, Some tgt ->
            exec(AddArrowWork(ArrowBetweenWorks(pastedFlow.Id, src, tgt, arrow.ArrowType)))
        | _ -> ()

    for arrow in sourceCallArrows do
        match Map.tryFind arrow.SourceId callMap, Map.tryFind arrow.TargetId callMap with
        | Some src, Some tgt ->
            exec(AddArrowCall(ArrowBetweenCalls(pastedFlow.Id, src, tgt, arrow.ArrowType)))
        | _ -> ()

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
        sourceFlowIds
        |> Set.toList
        |> List.collect (fun flowId -> DsQuery.arrowWorksOf flowId store)
        |> List.filter (fun a ->
            selectedWorkIds.Contains a.SourceId
            && selectedWorkIds.Contains a.TargetId)
    let sourceCallArrows =
        sourceFlowIds
        |> Set.toList
        |> List.collect (fun flowId -> DsQuery.arrowCallsOf flowId store)
        |> List.filter (fun a ->
            selectedCallIds.Contains a.SourceId
            && selectedCallIds.Contains a.TargetId)

    let workMap, callMap, pastedWorkIdsRev =
        sourceWorks
        |> List.fold (fun (workMap, callMap, pastedWorkIdsRev) sourceWork ->
            let pastedWork, localCallMap = pasteWorkToFlow exec store sourceWork targetFlowId
            let mergedCallMap = Map.fold (fun s k v -> Map.add k v s) callMap localCallMap
            Map.add sourceWork.Id pastedWork.Id workMap, mergedCallMap, pastedWork.Id :: pastedWorkIdsRev) (Map.empty<Guid, Guid>, Map.empty<Guid, Guid>, [])

    for arrow in sourceWorkArrows do
        match Map.tryFind arrow.SourceId workMap, Map.tryFind arrow.TargetId workMap with
        | Some src, Some tgt ->
            exec(AddArrowWork(ArrowBetweenWorks(targetFlowId, src, tgt, arrow.ArrowType)))
        | _ -> ()

    for arrow in sourceCallArrows do
        match Map.tryFind arrow.SourceId callMap, Map.tryFind arrow.TargetId callMap with
        | Some src, Some tgt ->
            exec(AddArrowCall(ArrowBetweenCalls(targetFlowId, src, tgt, arrow.ArrowType)))
        | _ -> ()

    pastedWorkIdsRev |> List.rev

let pasteCallsToWorkBatch (exec: ExecFn) (store: DsStore) (sourceCalls: Call list) (targetWorkId: Guid) : Guid list =
    let selectedCallIds = sourceCalls |> List.map (fun c -> c.Id) |> Set.ofList
    let sourceFlowIds =
        sourceCalls
        |> List.choose (fun c -> DsQuery.getWork c.ParentId store |> Option.map (fun w -> w.ParentId))
        |> Set.ofList
    let sourceCallArrows =
        sourceFlowIds
        |> Set.toList
        |> List.collect (fun flowId -> DsQuery.arrowCallsOf flowId store)
        |> List.filter (fun a ->
            selectedCallIds.Contains a.SourceId
            && selectedCallIds.Contains a.TargetId)
    let targetFlowIdOpt =
        DsQuery.getWork targetWorkId store |> Option.map (fun w -> w.ParentId)

    let callMap, pastedCallIdsRev =
        sourceCalls
        |> List.fold (fun (callMap, pastedCallIdsRev) sourceCall ->
            let context = detectCopyContext store sourceCall targetWorkId
            let pastedCall = pasteCallToWork exec context sourceCall targetWorkId
            Map.add sourceCall.Id pastedCall.Id callMap, pastedCall.Id :: pastedCallIdsRev) (Map.empty<Guid, Guid>, [])

    match targetFlowIdOpt with
    | Some targetFlowId ->
        for arrow in sourceCallArrows do
            match Map.tryFind arrow.SourceId callMap, Map.tryFind arrow.TargetId callMap with
            | Some src, Some tgt ->
                exec(AddArrowCall(ArrowBetweenCalls(targetFlowId, src, tgt, arrow.ArrowType)))
            | _ -> ()
    | None -> ()

    pastedCallIdsRev |> List.rev
