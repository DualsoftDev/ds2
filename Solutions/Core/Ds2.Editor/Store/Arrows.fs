namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Store
open Ds2.Store.DsQuery

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
                match Queries.trySystemIdOfWork newEndpointId store, Queries.trySystemIdOfWork keepId store with
                | Some newSysId, Some keepSysId
                    when newSysId = arrow.ParentId && keepSysId = arrow.ParentId ->
                    let expectedKey = ConnectionQueries.arrowKey newSourceId newTargetId arrow.ArrowType
                    let hasDuplicate =
                        Queries.arrowWorksOf arrow.ParentId store
                        |> ConnectionQueries.hasArrowKeyExcept expectedKey (Some arrow.Id)
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
                match Queries.getCall newEndpointId store, Queries.getCall keepId store with
                | Some newCall, Some keepCall
                    when newCall.ParentId = arrow.ParentId && keepCall.ParentId = arrow.ParentId ->
                    let expectedKey = ConnectionQueries.arrowKey newSourceId newTargetId arrow.ArrowType
                    let hasDuplicate =
                        Queries.arrowCallsOf arrow.ParentId store
                        |> ConnectionQueries.hasArrowKeyExcept expectedKey (Some arrow.Id)
                    if hasDuplicate then false
                    else
                        store.TrackMutate(store.ArrowCalls, arrowId, fun a ->
                            a.SourceId <- newSourceId
                            a.TargetId <- newTargetId)
                        true
                | _ -> false

        match Queries.getArrowWork arrowId store, Queries.getArrowCall arrowId store with
        | Some arrow, _ -> tryWork arrow
        | None, Some arrow -> tryCall arrow
        | _ -> false

    let orderedWorkChainLinks (store: DsStore) (orderedWorkIds: seq<Guid>) =
        orderedWorkIds
        |> Seq.distinct
        |> Seq.pairwise
        |> Seq.choose (fun (sourceId, targetId) ->
            match Queries.trySystemIdOfWork sourceId store, Queries.trySystemIdOfWork targetId store with
            | Some sourceSystemId, Some targetSystemId when sourceSystemId = targetSystemId ->
                Some(sourceSystemId, sourceId, targetId)
            | _ -> None)
        |> Seq.toList

// =============================================================================
// DsStore 화살표 확장 — 삭제/재연결/순서 연결
// =============================================================================

[<Extension>]
type DsStoreArrowsExtensions =

    [<Extension>]
    static member TrackSyncOrderedWorkChain(store: DsStore, orderedWorkIds: seq<Guid>, arrowType: ArrowType) : int =
        let orderedIds = orderedWorkIds |> Seq.distinct |> Seq.toList
        let workIdSet = orderedIds |> Set.ofList
        let arrowIdsToRemove =
            orderedIds
            |> Seq.choose (fun workId -> Queries.trySystemIdOfWork workId store |> Option.map (fun systemId -> systemId, workId))
            |> Seq.groupBy fst
            |> Seq.collect (fun (systemId, pairs) ->
                let systemWorkIds = pairs |> Seq.map snd |> Set.ofSeq
                Queries.arrowWorksOf systemId store
                |> Seq.filter (fun arrow -> Set.contains arrow.SourceId systemWorkIds && Set.contains arrow.TargetId systemWorkIds)
                |> Seq.map _.Id)
            |> Seq.distinct
            |> Seq.toList
        let links = DirectArrowOps.orderedWorkChainLinks store orderedIds
        for arrowId in arrowIdsToRemove do
            store.TrackRemove(store.ArrowWorks, arrowId)
        for (systemId, sourceId, targetId) in links do
            if Set.contains sourceId workIdSet && Set.contains targetId workIdSet then
                store.TrackAdd(store.ArrowWorks, ArrowBetweenWorks(systemId, sourceId, targetId, arrowType))
        links.Length

    [<Extension>]
    static member RemoveArrows(store: DsStore, arrowIds: seq<Guid>) : int =
        let toRemove =
            arrowIds
            |> Seq.distinct
            |> Seq.choose (fun arrowId ->
                match Queries.getArrowWork arrowId store with
                | Some _ -> Some(arrowId, true)
                | None ->
                    match Queries.getArrowCall arrowId store with
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
            store.EmitConnectionsChangedAndHistory()
            toRemove.Length

    [<Extension>]
    static member ReconnectArrow(store: DsStore, arrowId: Guid, replaceSource: bool, newEndpointId: Guid) : bool =
        StoreLog.debug($"arrowId={arrowId}, replaceSource={replaceSource}, newEndpointId={newEndpointId}")
        let mutable success = false
        store.WithTransaction("화살표 재연결", fun () ->
            success <- DirectArrowOps.tryReconnectArrow store arrowId replaceSource newEndpointId)
        if success then store.EmitConnectionsChangedAndHistory()
        else StoreLog.warn($"Reconnect failed. arrowId={arrowId}")
        success

    [<Extension>]
    static member UpdateArrowType(store: DsStore, arrowId: Guid, newArrowType: ArrowType) : bool =
        StoreLog.debug($"arrowId={arrowId}, newArrowType={newArrowType}")
        match Queries.getArrowWork arrowId store with
        | Some arrow when arrow.ArrowType <> newArrowType
                          && EntityKindRules.isArrowTypeAllowedForKind EntityKind.Work newArrowType ->
            store.WithTransaction("화살표 타입 변경", fun () ->
                store.TrackMutate(store.ArrowWorks, arrowId, fun a -> a.ArrowType <- newArrowType))
            store.EmitConnectionsChangedAndHistory()
            true
        | Some _ -> false
        | None ->
            match Queries.getArrowCall arrowId store with
            | Some arrow when arrow.ArrowType <> newArrowType
                              && EntityKindRules.isArrowTypeAllowedForKind EntityKind.Call newArrowType ->
                store.WithTransaction("화살표 타입 변경", fun () ->
                    store.TrackMutate(store.ArrowCalls, arrowId, fun a -> a.ArrowType <- newArrowType))
                store.EmitConnectionsChangedAndHistory()
                true
            | _ -> false

    [<Extension>]
    static member ReverseArrow(store: DsStore, arrowId: Guid) : bool =
        StoreLog.debug($"arrowId={arrowId}")
        match Queries.getArrowWork arrowId store with
        | Some arrow ->
            let expectedKey = ConnectionQueries.arrowKey arrow.TargetId arrow.SourceId arrow.ArrowType
            let hasDuplicate =
                Queries.arrowWorksOf arrow.ParentId store
                |> ConnectionQueries.hasArrowKeyExcept expectedKey (Some arrow.Id)
            if hasDuplicate then false
            else
                store.WithTransaction("화살표 방향 변경", fun () ->
                    store.TrackMutate(store.ArrowWorks, arrowId, fun a ->
                        let src = a.SourceId
                        a.SourceId <- a.TargetId
                        a.TargetId <- src))
                store.EmitConnectionsChangedAndHistory()
                true
        | None ->
            match Queries.getArrowCall arrowId store with
            | Some arrow ->
                let expectedKey = ConnectionQueries.arrowKey arrow.TargetId arrow.SourceId arrow.ArrowType
                let hasDuplicate =
                    Queries.arrowCallsOf arrow.ParentId store
                    |> ConnectionQueries.hasArrowKeyExcept expectedKey (Some arrow.Id)
                if hasDuplicate then false
                else
                    store.WithTransaction("화살표 방향 변경", fun () ->
                        store.TrackMutate(store.ArrowCalls, arrowId, fun a ->
                            let src = a.SourceId
                            a.SourceId <- a.TargetId
                            a.TargetId <- src))
                    store.EmitConnectionsChangedAndHistory()
                    true
            | None -> false

    [<Extension>]
    static member ConnectSelectionInOrder(store: DsStore, orderedNodeIds: seq<Guid>, arrowType: ArrowType) : int =
        let links = ConnectionQueries.orderedArrowLinksForSelection store orderedNodeIds arrowType
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
            store.EmitConnectionsChangedAndHistory()
            links.Length

    /// 지정된 Work 순서를 기준으로 같은 집합 내부 화살표를 제거하고 새 선형 체인으로 재구성.
    /// 순서 기반 편집기에서 Flow 내 Work 인과 관계를 트리 순서에 맞춰 동기화할 때 사용.
    [<Extension>]
    static member SyncOrderedWorkChain(store: DsStore, orderedWorkIds: seq<Guid>, arrowType: ArrowType) : int =
        let orderedIds = orderedWorkIds |> Seq.distinct |> Seq.toList
        if orderedIds.IsEmpty then 0
        else
            let mutable linkCount = 0
            store.WithTransaction("순서 기반 Work 체인 동기화", fun () ->
                linkCount <- DsStoreArrowsExtensions.TrackSyncOrderedWorkChain(store, orderedIds, arrowType))
            store.EmitConnectionsChangedAndHistory()
            linkCount

    /// Undo/History 없이 순서 기반 Work 체인만 직접 동기화.
    [<Extension>]
    static member SyncOrderedWorkChainDirect(store: DsStore, orderedWorkIds: seq<Guid>, arrowType: ArrowType) : int =
        let orderedIds = orderedWorkIds |> Seq.distinct |> Seq.toList
        let arrowIdsToRemove =
            orderedIds
            |> Seq.choose (fun workId -> Queries.trySystemIdOfWork workId store |> Option.map (fun systemId -> systemId, workId))
            |> Seq.groupBy fst
            |> Seq.collect (fun (systemId, pairs) ->
                let systemWorkIds = pairs |> Seq.map snd |> Set.ofSeq
                Queries.arrowWorksOf systemId store
                |> Seq.filter (fun arrow -> Set.contains arrow.SourceId systemWorkIds && Set.contains arrow.TargetId systemWorkIds)
                |> Seq.map _.Id)
            |> Seq.distinct
            |> Seq.toList
        let links = DirectArrowOps.orderedWorkChainLinks store orderedIds
        for arrowId in arrowIdsToRemove do
            store.ArrowWorks.Remove(arrowId) |> ignore
        for (systemId, sourceId, targetId) in links do
            let arrow = ArrowBetweenWorks(systemId, sourceId, targetId, arrowType)
            store.ArrowWorks[arrow.Id] <- arrow
        links.Length

    /// 지정된 Work ID 목록에서 같은 System에 속한 Work들끼리 ResetReset 화살표를 전체 쌍으로 연결.
    /// 쌍 (A, B)에 A→B 또는 B→A ResetReset 이 이미 있으면 스킵 → 중복 방지.
    /// N개 Work → 최대 N*(N-1)/2 화살표 (단일 트랜잭션)
    [<Extension>]
    static member ConnectWorksWithMutualReset(store: DsStore, workIds: seq<Guid>) : int =
        let bySystem =
            workIds
            |> Seq.distinct
            |> Seq.choose (fun id ->
                Queries.trySystemIdOfWork id store
                |> Option.map (fun sysId -> sysId, id))
            |> Seq.groupBy fst
            |> Seq.map (fun (sysId, pairs) -> sysId, pairs |> Seq.map snd |> Seq.toList)
            |> Seq.filter (fun (_, works) -> List.length works >= 2)
            |> Seq.toList
        if bySystem.IsEmpty then 0
        else
            // 기존 ResetReset 화살표를 양방향 key 세트로 프리빌드
            let existing =
                Queries.allArrowWorks store
                |> List.filter (fun a -> a.ArrowType = ArrowType.ResetReset)
                |> List.collect (fun a ->
                    [ ConnectionQueries.arrowKey a.SourceId a.TargetId a.ArrowType
                      ConnectionQueries.arrowKey a.TargetId a.SourceId a.ArrowType ])
                |> System.Collections.Generic.HashSet
            let links =
                bySystem
                |> List.collect (fun (sysId, works) ->
                    [ for i in 0 .. works.Length - 2 do
                        for j in i + 1 .. works.Length - 1 do
                            let key = ConnectionQueries.arrowKey works.[i] works.[j] ArrowType.ResetReset
                            if not (existing.Contains(key)) then
                                yield (sysId, works.[i], works.[j]) ])
            if links.IsEmpty then 0
            else
                StoreLog.debug($"상호리셋 arrows={links.Length}")
                store.WithTransaction("상호리셋 연결", fun () ->
                    for (sysId, srcId, tgtId) in links do
                        store.TrackAdd(store.ArrowWorks, ArrowBetweenWorks(sysId, srcId, tgtId, ArrowType.ResetReset)))
                store.EmitRefreshAndHistory()
                links.Length
