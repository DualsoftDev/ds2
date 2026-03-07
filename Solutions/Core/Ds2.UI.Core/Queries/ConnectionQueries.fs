module Ds2.UI.Core.ConnectionQueries

open System
open Ds2.Core

let private trySystemIdOfFlow (store: DsStore) (flowId: Guid) : Guid option =
    DsQuery.getFlow flowId store |> Option.map (fun flow -> flow.ParentId)

let private areFlowsInSameSystem (store: DsStore) (sourceFlowId: Guid) (targetFlowId: Guid) : bool =
    if sourceFlowId = targetFlowId then
        true
    else
        match trySystemIdOfFlow store sourceFlowId, trySystemIdOfFlow store targetFlowId with
        | Some sourceSystemId, Some targetSystemId -> sourceSystemId = targetSystemId
        | _ -> false

let resolveFlowIdForConnect
    (store: DsStore)
    (sourceEntityType: string)
    (sourceParentId: Guid option)
    (targetEntityType: string)
    (targetParentId: Guid option)
    : Guid option =
    match sourceEntityType, targetEntityType, sourceParentId, targetParentId with
    | EntityTypeNames.Work, EntityTypeNames.Work, Some sourceFlowId, Some targetFlowId
        when areFlowsInSameSystem store sourceFlowId targetFlowId ->
        Some sourceFlowId
    | EntityTypeNames.Call, EntityTypeNames.Call, Some sourceWorkId, Some targetWorkId when sourceWorkId = targetWorkId ->
        DsQuery.getWork sourceWorkId store
        |> Option.map (fun work -> work.ParentId)
    | _ -> None

let private resolveOrderedNodeContext (store: DsStore) (nodeId: Guid) : (string * Guid * Guid) option =
    match DsQuery.getWork nodeId store with
    | Some work -> Some (EntityTypeNames.Work, work.ParentId, work.Id)
    | None ->
        match DsQuery.getCall nodeId store with
        | Some call ->
            DsQuery.getWork call.ParentId store
            |> Option.map (fun work -> (EntityTypeNames.Call, work.ParentId, call.Id))
        | None -> None

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

    // 기존 화살표를 HashSet으로 프리빌드 — O(1) 중복 체크
    let existingWorkArrows =
        DsQuery.allArrowWorks store
        |> List.map (fun a -> struct (a.SourceId, a.TargetId))
        |> System.Collections.Generic.HashSet

    let existingCallArrows =
        DsQuery.allArrowCalls store
        |> List.map (fun a -> struct (a.SourceId, a.TargetId))
        |> System.Collections.Generic.HashSet

    orderedContexts
    |> List.pairwise
    |> List.choose (fun ((sourceType, sourceFlowId, sourceId), (targetType, targetFlowId, targetId)) ->
        if sourceType <> targetType || sourceId = targetId then
            None
        else
            match sourceType with
            | EntityTypeNames.Work when areFlowsInSameSystem store sourceFlowId targetFlowId ->
                if existingWorkArrows.Contains(struct (sourceId, targetId)) then None
                else Some (sourceType, sourceFlowId, sourceId, targetId)
            | EntityTypeNames.Call when sourceFlowId = targetFlowId ->
                if existingCallArrows.Contains(struct (sourceId, targetId)) then None
                else Some (sourceType, sourceFlowId, sourceId, targetId)
            | _ -> None)
