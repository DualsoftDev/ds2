namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store

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
