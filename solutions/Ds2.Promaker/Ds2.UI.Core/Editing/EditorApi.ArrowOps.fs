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

let tryResolveReconnectArrowCmd (store: DsStore) (arrowId: Guid) (replaceSource: bool) (newEndpointId: Guid) : EditorCommand option =
    let buildWorkReconnectCommand () =
        let arrow = DsQuery.getArrowWork arrowId store |> Option.get
        let keepId = if replaceSource then arrow.TargetId else arrow.SourceId
        let newSourceId = if replaceSource then newEndpointId else keepId
        let newTargetId = if replaceSource then keepId else newEndpointId

        if newSourceId = newTargetId then None
        elif newSourceId = arrow.SourceId && newTargetId = arrow.TargetId then None
        else
            match DsQuery.getWork newEndpointId store, DsQuery.getWork keepId store with
            | Some newWork, Some keepWork
                when newWork.ParentId = arrow.ParentId && keepWork.ParentId = arrow.ParentId ->
                let hasDuplicate =
                    DsQuery.allArrowWorks store
                    |> List.exists (fun e ->
                        e.Id <> arrow.Id
                        && e.ParentId = arrow.ParentId
                        && e.SourceId = newSourceId
                        && e.TargetId = newTargetId)
                if hasDuplicate then None
                else Some(ReconnectArrowWork(arrow.Id, arrow.SourceId, arrow.TargetId, newSourceId, newTargetId))
            | _ -> None

    let buildCallReconnectCommand () =
        let arrow = DsQuery.getArrowCall arrowId store |> Option.get
        let keepId = if replaceSource then arrow.TargetId else arrow.SourceId
        let newSourceId = if replaceSource then newEndpointId else keepId
        let newTargetId = if replaceSource then keepId else newEndpointId

        if newSourceId = newTargetId then None
        elif newSourceId = arrow.SourceId && newTargetId = arrow.TargetId then None
        else
            match DsQuery.getCall newEndpointId store, DsQuery.getCall keepId store with
            | Some newCall, Some keepCall when newCall.ParentId = keepCall.ParentId ->
                match DsQuery.getWork newCall.ParentId store with
                | Some work when work.ParentId = arrow.ParentId ->
                    let hasDuplicate =
                        DsQuery.allArrowCalls store
                        |> List.exists (fun e ->
                            e.Id <> arrow.Id
                            && e.ParentId = arrow.ParentId
                            && e.SourceId = newSourceId
                            && e.TargetId = newTargetId)
                    if hasDuplicate then None
                    else Some(ReconnectArrowCall(arrow.Id, arrow.SourceId, arrow.TargetId, newSourceId, newTargetId))
                | _ -> None
            | _ -> None

    match DsQuery.getArrowWork arrowId store, DsQuery.getArrowCall arrowId store with
    | Some _, _ -> buildWorkReconnectCommand ()
    | None, Some _ -> buildCallReconnectCommand ()
    | _ -> None
