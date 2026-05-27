namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store

/// Flow 간 Call 이동의 사전 검증 결과.
/// Movable 외의 케이스는 모두 차단 사유 — UI 는 사유를 사용자에게 보여주고 작업을 취소.
type CrossFlowMoveValidation =
    /// (resolvedCallIds, targetFlowId, sourceFlowIds)
    | Movable of (Guid list * Guid * Guid list)
    /// callIds 중 일부가 store 에서 resolve 안 됨 (이미 삭제된 ID 등).
    | NotResolvable of Guid list
    /// 모든 source Call 의 ParentId == targetWorkId — 의미상 no-op.
    | SameWorkMove
    /// (sourceCallId, conflictingName) — target work 에 이미 존재하는 이름과 충돌.
    | DuplicateNamesInTarget of (Guid * string) list
    /// (devAlias, conflict) — RenameSourceSystem 모드의 device 충돌.
    | RenameModeConflicts of (string * RenameDeviceConflict) list
    /// targetWorkId 가 store 에 없거나 그 Flow 를 못 찾음.
    | InvalidTarget

/// Move 실행 결과.
type CrossFlowMoveResult =
    /// 정상 이동 — 새로 생성된 Call ID 리스트 (target work 안의).
    | Moved of newCallIds: Guid list
    /// Validation 실패 — 사유 포함.
    | Blocked of CrossFlowMoveValidation

[<Extension>]
type DsStoreCallMoveExtensions =

    static member private TryGetMoveCandidate(store: DsStore, callId: Guid, targetWorkId: Guid) =
        match Queries.getCall callId store, Queries.getWork targetWorkId store with
        | Some call, Some targetWork ->
            match Queries.getWork call.ParentId store with
            | Some sourceWork ->
                Some(call, sourceWork, targetWork)
            | None -> None
        | _ -> None

    [<Extension>]
    static member CanMoveCallToWork(store: DsStore, callId: Guid, targetWorkId: Guid) : bool =
        match DsStoreCallMoveExtensions.TryGetMoveCandidate(store, callId, targetWorkId) with
        | Some(call, sourceWork, targetWork) ->
            sourceWork.Id <> targetWork.Id
            && sourceWork.ParentId = targetWork.ParentId
            && Queries.isCallNameUniqueInWork targetWork.Id call.Name (Some call.Id) store
        | None -> false

    [<Extension>]
    static member MoveCallToWork(store: DsStore, callId: Guid, targetWorkId: Guid) : bool =
        match DsStoreCallMoveExtensions.TryGetMoveCandidate(store, callId, targetWorkId) with
        | Some(call, sourceWork, targetWork)
            when sourceWork.Id <> targetWork.Id
                 && sourceWork.ParentId = targetWork.ParentId
                 && Queries.isCallNameUniqueInWork targetWork.Id call.Name (Some call.Id) store ->
            let attachedArrowIds =
                store.ArrowCallsReadOnly.Values
                |> Seq.filter (fun arrow -> arrow.SourceId = call.Id || arrow.TargetId = call.Id)
                |> Seq.map (fun arrow -> arrow.Id)
                |> Seq.toList

            store.WithTransaction("Move Call To Work", fun () ->
                for arrowId in attachedArrowIds do
                    store.TrackRemove(store.ArrowCalls, arrowId)
                store.TrackMutate(store.Calls, call.Id, fun current ->
                    current.ParentId <- targetWork.Id))
            store.EmitRefreshAndHistory()
            true
        | _ -> false

    /// RenameSourceSystem 모드 가능 여부를 사전 검사. (devAlias * conflict) list 반환.
    /// 빈 리스트면 Rename 모드 안전. UI Dialog 가 라디오 disable 여부 판단용으로 호출.
    [<Extension>]
    static member GetRenameSourceSystemConflicts
        (store: DsStore, callIds: seq<Guid>, targetWorkId: Guid)
        : (string * RenameDeviceConflict) list =
        let resolved =
            callIds
            |> Seq.map (fun id -> Queries.resolveOriginalCallId id store)
            |> Seq.distinct
            |> Seq.choose (fun id -> Queries.getCall id store)
            |> Seq.toList
        if resolved.IsEmpty then []
        else
            match Queries.getWork targetWorkId store with
            | None -> []
            | Some targetWork ->
                match Queries.getFlow targetWork.ParentId store with
                | None -> []
                | Some targetFlow ->
                    let projectIdOpt =
                        Queries.getSystem targetFlow.ParentId store
                        |> Option.bind (fun s -> StoreHierarchyQueries.findProjectOfSystem store s.Id)
                    match projectIdOpt with
                    | None -> []
                    | Some pid ->
                        PasteDeviceOps.collectRenameConflicts store (resolved |> List.map (fun c -> c.Id)) targetFlow.Name pid

    [<Extension>]
    static member CanMoveCallsAcrossFlow
        (store: DsStore, callIds: seq<Guid>, targetWorkId: Guid, mode: CrossFlowDeviceMode)
        : CrossFlowMoveValidation =
        let requested =
            callIds
            |> Seq.map (fun id -> Queries.resolveOriginalCallId id store)
            |> Seq.distinct
            |> Seq.toList
        let resolved = requested |> List.choose (fun id -> Queries.getCall id store)
        let resolvedIds = resolved |> List.map (fun c -> c.Id) |> Set.ofList
        let missing = requested |> List.filter (fun id -> not (resolvedIds.Contains id))
        if not missing.IsEmpty then NotResolvable missing
        else
            match Queries.getWork targetWorkId store with
            | None -> InvalidTarget
            | Some targetWork ->
                let targetFlowId = targetWork.ParentId
                let sourceFlowIds =
                    resolved
                    |> List.choose (fun c -> Queries.getWork c.ParentId store |> Option.map (fun w -> w.ParentId))
                    |> List.distinct
                let allSameWork = resolved |> List.forall (fun c -> c.ParentId = targetWorkId)
                if allSameWork then SameWorkMove
                else
                    // Duplicate name check (target work 내 기존 Call 이름 + 우리가 옮길 Call 들끼리)
                    let existingNames =
                        Queries.originalCallsOf targetWorkId store
                        |> List.filter (fun c -> not (resolvedIds.Contains c.Id))
                        |> List.map (fun c -> c.Name)
                        |> Set.ofList
                    let dupes =
                        resolved
                        |> List.filter (fun c -> c.ParentId <> targetWorkId && existingNames.Contains c.Name)
                        |> List.map (fun c -> c.Id, c.Name)
                    if not dupes.IsEmpty then DuplicateNamesInTarget dupes
                    else
                        // Mode-specific guard
                        match mode with
                        | CrossFlowDeviceMode.RenameSourceSystem ->
                            match Queries.getFlow targetFlowId store with
                            | None -> InvalidTarget
                            | Some targetFlow ->
                                let projectIdOpt =
                                    Queries.getSystem targetFlow.ParentId store
                                    |> Option.bind (fun s -> StoreHierarchyQueries.findProjectOfSystem store s.Id)
                                match projectIdOpt with
                                | None -> InvalidTarget
                                | Some pid ->
                                    let conflicts =
                                        PasteDeviceOps.collectRenameConflicts store (resolved |> List.map (fun c -> c.Id)) targetFlow.Name pid
                                    if not conflicts.IsEmpty then RenameModeConflicts conflicts
                                    else Movable(resolved |> List.map (fun c -> c.Id), targetFlowId, sourceFlowIds)
                        | _ ->
                            Movable(resolved |> List.map (fun c -> c.Id), targetFlowId, sourceFlowIds)

    /// store 전체의 Call/Work Condition 안에서 *oldApiCall.Id -> newApiCall* 매핑에 따라
    /// ResizeArray<ApiCall> 안의 객체 reference 를 새 ApiCall 로 교체. 자기 자신 (movables 의 Condition)
    /// 은 어차피 cascade-remove 되므로 *target 외 모든 Call/Work* 만 walk 해도 안전하지만,
    /// 단순화 위해 *cascade-remove 이전에* 전체 store walk 한다 (movables 의 Condition 안 reference 가
    /// 새 Call 의 Condition 복사물에서도 동일 ApiCall 객체를 가리키는 경우 함께 갱신).
    static member private RewireConditionApiCallReferences
        (store: DsStore, apiCallMap: Map<Guid, ApiCall>) =
        if apiCallMap.IsEmpty then ()
        else
            let rec walk (conditions: ResizeArray<Condition>) (changed: bool ref) =
                for c in conditions do
                    let mutable i = 0
                    while i < c.ApiCalls.Count do
                        let ac = c.ApiCalls.[i]
                        match Map.tryFind ac.Id apiCallMap with
                        | Some newAc when not (obj.ReferenceEquals(ac, newAc)) ->
                            c.ApiCalls.[i] <- newAc
                            changed.Value <- true
                        | _ -> ()
                        i <- i + 1
                    walk c.Children changed
            for kvp in store.CallsReadOnly do
                let touched = ref false
                walk kvp.Value.Conditions touched
                if touched.Value then
                    store.TrackMutate(store.Calls, kvp.Key, fun _ -> ())
            for kvp in store.WorksReadOnly do
                let touched = ref false
                walk kvp.Value.Conditions touched
                if touched.Value then
                    store.TrackMutate(store.Works, kvp.Key, fun _ -> ())

    [<Extension>]
    static member MoveCallsAcrossFlow
        (store: DsStore, callIds: seq<Guid>, targetWorkId: Guid, mode: CrossFlowDeviceMode)
        : CrossFlowMoveResult =
        match store.CanMoveCallsAcrossFlow(callIds, targetWorkId, mode) with
        | Movable(resolvedIds, _targetFlowId, _sourceFlowIds) ->
            // SameWork 케이스는 자연 제외 (이미 같은 work).
            let sourceCalls = resolvedIds |> List.choose (fun id -> Queries.getCall id store)
            let sameFlowSameWorkSplit =
                sourceCalls
                |> List.partition (fun c -> c.ParentId = targetWorkId)
            let _alreadyInTargetWork, movables = sameFlowSameWorkSplit
            if movables.IsEmpty then Moved []
            else
                // 원본 ApiCall.Id 들을 미리 캡처 — paste 후 새 Call 의 ApiCalls 와 인덱스 zip 으로 매핑 구축.
                let oldApiCallSnapshots =
                    movables
                    |> List.map (fun sc ->
                        sc.Id, sc.ApiCalls |> Seq.map (fun ac -> ac.Id) |> Seq.toList)
                    |> Map.ofList

                let mutable pastedIds = []
                store.WithTransaction($"Move {movables.Length} Call(s) across Flow ({mode})", fun () ->
                    pastedIds <-
                        DirectPasteOps.pasteCallsToWorkBatchWithMode store movables targetWorkId 0 mode

                    // 새 Call 들이 paste 된 순서로 movables 와 1:1 매칭됨 (pasteCallsToWorkBatchWithMode 가
                    // sortByPositionAndName 으로 정렬 후 paste — movables 도 같은 정렬을 받아야 일치)
                    let sortedMovables =
                        movables
                        |> List.mapi (fun idx call -> idx, call)
                        |> List.sortBy (fun (idx, call) ->
                            match call.Position with
                            | Some pos -> 0, pos.Y, pos.X, call.Name, idx
                            | None -> 1, 0, 0, call.Name, idx)
                        |> List.map snd
                    // oldApiCallId -> newApiCall 매핑 구축 (모든 mode 에 대해 동일 — Clone/Rename/Keep 모두
                    // 새 ApiCall 객체를 만들고 원본 ApiCall.Id 는 cascade-remove 됨).
                    let apiCallMap =
                        List.zip sortedMovables pastedIds
                        |> List.collect (fun (oldCall, newCallId) ->
                            match Queries.getCall newCallId store with
                            | Some newCall ->
                                let oldAcIds = Map.find oldCall.Id oldApiCallSnapshots
                                let newAcs = newCall.ApiCalls |> Seq.toList
                                if oldAcIds.Length = newAcs.Length then
                                    List.zip oldAcIds newAcs
                                else []
                            | None -> [])
                        |> Map.ofList

                    // Cascade-remove 전에 다른 Call/Work Condition 의 ApiCall reference 자동 rewire
                    DsStoreCallMoveExtensions.RewireConditionApiCallReferences(store, apiCallMap)

                    // 원본 Call 들 cascade-remove
                    for sc in movables do
                        CascadeRemove.cascadeRemoveCall store sc.Id
                    CascadeRemove.removeOrphanApiCalls store)
                if not pastedIds.IsEmpty then store.EmitRefreshAndHistory()
                Moved pastedIds
        | other -> Blocked other
