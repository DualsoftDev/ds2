module Ds2.UI.Core.ConnectionQueries

open System
open Ds2.Core

/// Work → (EntityKind.Work, systemId, workId)
/// Call → (EntityKind.Call, workId, callId)
let private resolveOrderedNodeContext (store: DsStore) (nodeId: Guid) : (EntityKind * Guid * Guid) option =
    match DsQuery.getWork nodeId store with
    | Some work ->
        DsQuery.trySystemIdOfWork work.Id store
        |> Option.map (fun systemId -> (EntityKind.Work, systemId, work.Id))
    | None ->
        match DsQuery.getCall nodeId store with
        | Some call -> Some (EntityKind.Call, call.ParentId, call.Id)
        | None -> None

/// Ordered multi-selection -> connectable arrow links.
/// Result tuple: (entityKind, parentId, sourceId, targetId)
///   Work arrow: parentId = systemId
///   Call arrow: parentId = workId
let orderedArrowLinksForSelection
    (store: DsStore)
    (orderedNodeIds: seq<Guid>)
    : (EntityKind * Guid * Guid * Guid) list =

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
    |> List.choose (fun ((sourceType, sourceParent, sourceId), (targetType, targetParent, targetId)) ->
        if sourceType <> targetType || sourceId = targetId then
            None
        else
            match sourceType with
            | EntityKind.Work when sourceParent = targetParent ->
                // 같은 System에 속한 Work끼리만 연결
                if existingWorkArrows.Contains(struct (sourceId, targetId)) then None
                else Some (sourceType, sourceParent, sourceId, targetId)
            | EntityKind.Call when sourceParent = targetParent ->
                // 같은 Work에 속한 Call끼리만 연결
                if existingCallArrows.Contains(struct (sourceId, targetId)) then None
                else Some (sourceType, sourceParent, sourceId, targetId)
            | _ -> None)
