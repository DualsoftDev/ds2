module Ds2.UI.Core.ArrowOps

open System
open Ds2.Core

let buildRemoveArrowsCmds (store: DsStore) (arrowIds: seq<Guid>) : EditorCommand list =
    arrowIds
    |> Seq.distinct
    |> Seq.choose (fun arrowId ->
        match DsQuery.getArrowWork arrowId store with
        | Some arrow ->
            Some(RemoveArrowWork(DeepCopyHelper.backupEntityAs arrow))
        | None ->
            match DsQuery.getArrowCall arrowId store with
            | Some arrow -> Some(RemoveArrowCall(DeepCopyHelper.backupEntityAs arrow))
            | None -> None)
    |> Seq.toList

let buildConnectSelectionCmds (store: DsStore) (orderedNodeIds: seq<Guid>) (arrowType: ArrowType) : EditorCommand list =
    let links = ConnectionQueries.orderedArrowLinksForSelection store orderedNodeIds
    links
    |> List.choose (fun (entityType, flowId, sourceId, targetId) ->
        match entityType with
        | EntityTypeNames.Work -> Some(AddArrowWork(ArrowBetweenWorks(flowId, sourceId, targetId, arrowType)))
        | EntityTypeNames.Call -> Some(AddArrowCall(ArrowBetweenCalls(flowId, sourceId, targetId, arrowType)))
        | _ -> None)

/// (keepId, newSourceId, newTargetId) 계산 — 엔드포인트가 변경되지 않았거나 자기 자신을 가리키면 None
let private resolveEndpoints (replaceSource: bool) (srcId: Guid) (tgtId: Guid) (newEndpointId: Guid) : (Guid * Guid * Guid) option =
    let keepId      = if replaceSource then tgtId else srcId
    let newSourceId = if replaceSource then newEndpointId else keepId
    let newTargetId = if replaceSource then keepId else newEndpointId
    if newSourceId = newTargetId || (newSourceId = srcId && newTargetId = tgtId) then None
    else Some(keepId, newSourceId, newTargetId)

let tryResolveReconnectArrowCmd (store: DsStore) (arrowId: Guid) (replaceSource: bool) (newEndpointId: Guid) : EditorCommand option =
    let buildWorkReconnectCommand (arrow: ArrowBetweenWorks) =
        match resolveEndpoints replaceSource arrow.SourceId arrow.TargetId newEndpointId with
        | None -> None
        | Some(keepId, newSourceId, newTargetId) ->
            match DsQuery.getWork newEndpointId store, DsQuery.getWork keepId store with
            | Some newWork, Some keepWork
                when newWork.ParentId = arrow.ParentId && keepWork.ParentId = arrow.ParentId ->
                let hasDuplicate =
                    DsQuery.arrowWorksOf arrow.ParentId store
                    |> List.exists (fun e ->
                        e.Id <> arrow.Id
                        && e.SourceId = newSourceId
                        && e.TargetId = newTargetId)
                if hasDuplicate then None
                else Some(ReconnectArrowWork(arrow.Id, arrow.SourceId, arrow.TargetId, newSourceId, newTargetId))
            | _ -> None

    let buildCallReconnectCommand (arrow: ArrowBetweenCalls) =
        match resolveEndpoints replaceSource arrow.SourceId arrow.TargetId newEndpointId with
        | None -> None
        | Some(keepId, newSourceId, newTargetId) ->
            match DsQuery.getCall newEndpointId store, DsQuery.getCall keepId store with
            | Some newCall, Some keepCall when newCall.ParentId = keepCall.ParentId ->
                match DsQuery.getWork newCall.ParentId store with
                | Some work when work.ParentId = arrow.ParentId ->
                    let hasDuplicate =
                        DsQuery.arrowCallsOf arrow.ParentId store
                        |> List.exists (fun e ->
                            e.Id <> arrow.Id
                            && e.SourceId = newSourceId
                            && e.TargetId = newTargetId)
                    if hasDuplicate then None
                    else Some(ReconnectArrowCall(arrow.Id, arrow.SourceId, arrow.TargetId, newSourceId, newTargetId))
                | _ -> None
            | _ -> None

    match DsQuery.getArrowWork arrowId store, DsQuery.getArrowCall arrowId store with
    | Some arrow, _ -> buildWorkReconnectCommand arrow
    | None, Some arrow -> buildCallReconnectCommand arrow
    | _ -> None
