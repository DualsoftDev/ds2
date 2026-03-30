module Ds2.Editor.ConnectionQueries

open System
open Ds2.Core
open Ds2.Store

let arrowKey (sourceId: Guid) (targetId: Guid) (arrowType: ArrowType) =
    struct (sourceId, targetId, arrowType)

let arrowKeyOf (arrow: #DsArrow) =
    arrowKey arrow.SourceId arrow.TargetId arrow.ArrowType

let hasArrowKeyExcept
    (expectedKey: struct(Guid * Guid * ArrowType))
    (exceptId: Guid option)
    (arrows: seq<#DsArrow>) =
    arrows
    |> Seq.exists (fun arrow ->
        exceptId <> Some arrow.Id &&
        arrowKeyOf arrow = expectedKey)

/// Ordered multi-selection -> connectable arrow links.
/// Result tuple: (entityKind, parentId, sourceId, targetId)
///   Work arrow: parentId = systemId
///   Call arrow: parentId = workId
let orderedArrowLinksForSelection
    (store: DsStore)
    (orderedNodeIds: seq<Guid>)
    (arrowType: ArrowType)
    : (EntityKind * Guid * Guid * Guid) list =

    let resolveOrderedNodeContext (nodeId: Guid) : (EntityKind * Guid * Guid) option =
        match DsQuery.getWork nodeId store with
        | Some work ->
            DsQuery.trySystemIdOfWork work.Id store
            |> Option.map (fun systemId -> (EntityKind.Work, systemId, work.Id))
        | None ->
            match DsQuery.getCall nodeId store with
            | Some call -> Some (EntityKind.Call, call.ParentId, call.Id)
            | None -> None

    let distinctIds =
        let seen = System.Collections.Generic.HashSet<Guid>()
        orderedNodeIds
        |> Seq.filter seen.Add
        |> Seq.toList

    let orderedContexts =
        distinctIds
        |> List.choose resolveOrderedNodeContext

    // 기존 화살표를 (Source, Target, ArrowType)으로 프리빌드 — 동일 타입만 중복 제외
    let existingWorkArrows =
        DsQuery.allArrowWorks store
        |> List.map arrowKeyOf
        |> System.Collections.Generic.HashSet

    let existingCallArrows =
        DsQuery.allArrowCalls store
        |> List.map arrowKeyOf
        |> System.Collections.Generic.HashSet

    orderedContexts
    |> List.pairwise
    |> List.choose (fun ((sourceType, sourceParent, sourceId), (targetType, targetParent, targetId)) ->
        if sourceType <> targetType
            || sourceId = targetId
            || not (EntityKindRules.isArrowTypeAllowedForKind sourceType arrowType) then
            None
        else
            match sourceType with
            | EntityKind.Work when sourceParent = targetParent ->
                // 같은 System에 속한 Work끼리만 연결, 동일 소스+타겟+타입만 중복
                if existingWorkArrows.Contains(arrowKey sourceId targetId arrowType) then None
                else Some (sourceType, sourceParent, sourceId, targetId)
            | EntityKind.Call when sourceParent = targetParent ->
                // 같은 Work에 속한 Call끼리만 연결, 동일 소스+타겟+타입만 중복
                if existingCallArrows.Contains(arrowKey sourceId targetId arrowType) then None
                else Some (sourceType, sourceParent, sourceId, targetId)
            | _ -> None)
