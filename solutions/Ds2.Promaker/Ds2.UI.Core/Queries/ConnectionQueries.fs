module Ds2.UI.Core.ConnectionQueries

open System
open Ds2.Core

let resolveFlowIdForConnect
    (store: DsStore)
    (sourceEntityType: string)
    (sourceParentId: Guid option)
    (targetEntityType: string)
    (targetParentId: Guid option)
    : Guid option =
    match sourceEntityType, targetEntityType, sourceParentId, targetParentId with
    | "Work", "Work", Some sourceFlowId, Some targetFlowId when sourceFlowId = targetFlowId ->
        Some sourceFlowId
    | "Call", "Call", Some sourceWorkId, Some targetWorkId when sourceWorkId = targetWorkId ->
        DsQuery.getWork sourceWorkId store
        |> Option.map (fun work -> work.ParentId)
    | _ -> None

let private resolveOrderedNodeContext (store: DsStore) (nodeId: Guid) : (string * Guid * Guid) option =
    match DsQuery.getWork nodeId store with
    | Some work -> Some ("Work", work.ParentId, work.Id)
    | None ->
        match DsQuery.getCall nodeId store with
        | Some call ->
            DsQuery.getWork call.ParentId store
            |> Option.map (fun work -> ("Call", work.ParentId, call.Id))
        | None -> None

let private hasArrowWork (store: DsStore) (flowId: Guid) (sourceId: Guid) (targetId: Guid) =
    DsQuery.allArrowWorks store
    |> List.exists (fun arrow ->
        arrow.ParentId = flowId
        && arrow.SourceId = sourceId
        && arrow.TargetId = targetId)

let private hasArrowCall (store: DsStore) (flowId: Guid) (sourceId: Guid) (targetId: Guid) =
    DsQuery.allArrowCalls store
    |> List.exists (fun arrow ->
        arrow.ParentId = flowId
        && arrow.SourceId = sourceId
        && arrow.TargetId = targetId)

/// Ordered multi-selection -> connectable arrow links.
/// Result tuple: (entityType, flowId, sourceId, targetId)
let orderedArrowLinksForSelection
    (store: DsStore)
    (orderedNodeIds: seq<Guid>)
    : (string * Guid * Guid * Guid) list =

    let distinctIds =
        let seen = System.Collections.Generic.HashSet<Guid>()
        orderedNodeIds
        |> Seq.filter seen.Add
        |> Seq.toList

    let orderedContexts =
        distinctIds
        |> List.choose (resolveOrderedNodeContext store)

    orderedContexts
    |> List.pairwise
    |> List.choose (fun ((sourceType, sourceFlowId, sourceId), (targetType, targetFlowId, targetId)) ->
        if sourceType <> targetType || sourceFlowId <> targetFlowId || sourceId = targetId then
            None
        else
            let alreadyExists =
                match sourceType with
                | "Work" -> hasArrowWork store sourceFlowId sourceId targetId
                | "Call" -> hasArrowCall store sourceFlowId sourceId targetId
                | _ -> true

            if alreadyExists then None
            else Some (sourceType, sourceFlowId, sourceId, targetId))
