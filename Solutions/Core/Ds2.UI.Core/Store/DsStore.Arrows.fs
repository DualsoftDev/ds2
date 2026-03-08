namespace Ds2.UI.Core

open System
open System.Runtime.CompilerServices
open Ds2.Core

// =============================================================================
// 내부 헬퍼 — 화살표 재연결 해석
// =============================================================================

module internal DirectArrowOps =
    let private resolveEndpoints (replaceSource: bool) (srcId: Guid) (tgtId: Guid) (newEndpointId: Guid) =
        let keepId = if replaceSource then tgtId else srcId
        let newSourceId = if replaceSource then newEndpointId else keepId
        let newTargetId = if replaceSource then keepId else newEndpointId
        if newSourceId = newTargetId || (newSourceId = srcId && newTargetId = tgtId) then None
        else Some(keepId, newSourceId, newTargetId)

    let tryReconnectArrow (store: DsStore) (arrowId: Guid) (replaceSource: bool) (newEndpointId: Guid) : bool =
        let tryWork (arrow: ArrowBetweenWorks) =
            match resolveEndpoints replaceSource arrow.SourceId arrow.TargetId newEndpointId with
            | None -> false
            | Some(keepId, newSourceId, newTargetId) ->
                // ArrowBetweenWorks.parentId = systemId — 새 endpoint도 같은 System에 속해야 함
                match DsQuery.trySystemIdOfWork newEndpointId store, DsQuery.trySystemIdOfWork keepId store with
                | Some newSysId, Some keepSysId
                    when newSysId = arrow.ParentId && keepSysId = arrow.ParentId ->
                    let hasDuplicate =
                        DsQuery.arrowWorksOf arrow.ParentId store
                        |> List.exists (fun e -> e.Id <> arrow.Id && e.SourceId = newSourceId && e.TargetId = newTargetId)
                    if hasDuplicate then false
                    else
                        store.TrackMutate(store.ArrowWorks, arrowId, fun a ->
                            a.SourceId <- newSourceId
                            a.TargetId <- newTargetId)
                        true
                | _ -> false

        let tryCall (arrow: ArrowBetweenCalls) =
            match resolveEndpoints replaceSource arrow.SourceId arrow.TargetId newEndpointId with
            | None -> false
            | Some(keepId, newSourceId, newTargetId) ->
                // ArrowBetweenCalls.parentId = workId — 새 endpoint도 같은 Work에 속해야 함
                match DsQuery.getCall newEndpointId store, DsQuery.getCall keepId store with
                | Some newCall, Some keepCall
                    when newCall.ParentId = arrow.ParentId && keepCall.ParentId = arrow.ParentId ->
                    let hasDuplicate =
                        DsQuery.arrowCallsOf arrow.ParentId store
                        |> List.exists (fun e -> e.Id <> arrow.Id && e.SourceId = newSourceId && e.TargetId = newTargetId)
                    if hasDuplicate then false
                    else
                        store.TrackMutate(store.ArrowCalls, arrowId, fun a ->
                            a.SourceId <- newSourceId
                            a.TargetId <- newTargetId)
                        true
                | _ -> false

        match DsQuery.getArrowWork arrowId store, DsQuery.getArrowCall arrowId store with
        | Some arrow, _ -> tryWork arrow
        | None, Some arrow -> tryCall arrow
        | _ -> false

// =============================================================================
// DsStore 화살표 확장 — 삭제/재연결/순서 연결
// =============================================================================

[<Extension>]
type DsStoreArrowsExtensions =

    [<Extension>]
    static member RemoveArrows(store: DsStore, arrowIds: seq<Guid>) : int =
        let toRemove =
            arrowIds
            |> Seq.distinct
            |> Seq.choose (fun arrowId ->
                match DsQuery.getArrowWork arrowId store with
                | Some _ -> Some(arrowId, true)
                | None ->
                    match DsQuery.getArrowCall arrowId store with
                    | Some _ -> Some(arrowId, false)
                    | None -> None)
            |> Seq.toList
        if toRemove.IsEmpty then 0
        else
            StoreLog.debug($"count={toRemove.Length}")
            store.WithTransaction("Delete Arrows", fun () ->
                for (arrowId, isWork) in toRemove do
                    if isWork then store.TrackRemove(store.ArrowWorks, arrowId)
                    else store.TrackRemove(store.ArrowCalls, arrowId))
            store.EmitRefreshAndHistory()
            toRemove.Length

    [<Extension>]
    static member ReconnectArrow(store: DsStore, arrowId: Guid, replaceSource: bool, newEndpointId: Guid) : bool =
        StoreLog.debug($"arrowId={arrowId}, replaceSource={replaceSource}, newEndpointId={newEndpointId}")
        let mutable success = false
        store.WithTransaction("화살표 재연결", fun () ->
            success <- DirectArrowOps.tryReconnectArrow store arrowId replaceSource newEndpointId)
        if success then store.EmitRefreshAndHistory()
        else StoreLog.warn($"Reconnect failed. arrowId={arrowId}")
        success

    [<Extension>]
    static member ConnectSelectionInOrder(store: DsStore, orderedNodeIds: seq<Guid>, arrowType: ArrowType) : int =
        let links = ConnectionQueries.orderedArrowLinksForSelection store orderedNodeIds
        if links.IsEmpty then 0
        else
            StoreLog.debug($"count={links.Length}, arrowType={arrowType}")
            store.WithTransaction("Connect Selected Nodes In Order", fun () ->
                for (entityKind, parentId, sourceId, targetId) in links do
                    match entityKind with
                    | EntityKind.Work ->
                        let arrow = ArrowBetweenWorks(parentId, sourceId, targetId, arrowType)
                        store.TrackAdd(store.ArrowWorks, arrow)
                    | EntityKind.Call ->
                        let arrow = ArrowBetweenCalls(parentId, sourceId, targetId, arrowType)
                        store.TrackAdd(store.ArrowCalls, arrow)
                    | _ -> ())
            store.EmitRefreshAndHistory()
            links.Length
