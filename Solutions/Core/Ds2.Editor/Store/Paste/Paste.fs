namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Store

// =============================================================================
// PasteResolvers — 붙여넣기 대상 해석 유틸리티 (internal)
// =============================================================================

module internal PasteResolvers =

    let isCopyableEntityKind (entityKind: EntityKind) : bool =
        match entityKind with
        | EntityKind.Flow | EntityKind.Work | EntityKind.Call -> true
        | _ -> false

// =============================================================================
// DsStore Paste 확장
// =============================================================================

[<Extension>]
type DsStorePasteExtensions =

    [<Extension>]
    static member PasteFlowWithRename
        (store: DsStore, sourceFlowId: Guid, targetSystemId: Guid, newFlowName: string) : Guid option =
        match DsQuery.getFlow sourceFlowId store with
        | None -> None
        | Some sourceFlow ->
            StoreLog.debug($"PasteFlowWithRename: {sourceFlow.Name} → {newFlowName}, targetSystem={targetSystemId}")
            let mutable pastedId = Guid.Empty
            store.WithTransaction($"Paste Flow '{newFlowName}'", fun () ->
                pastedId <- DirectPasteOps.pasteFlowToSystem store sourceFlow targetSystemId (Some newFlowName))
            if pastedId <> Guid.Empty then
                store.EmitRefreshAndHistory()
                Some pastedId
            else None

    [<Extension>]
    static member PasteEntities
        (store: DsStore, copiedEntityKind: EntityKind, copiedEntityIds: seq<Guid>,
         targetEntityKind: EntityKind, targetEntityId: Guid, pasteIndex: int) : Guid list =
        let ids = copiedEntityIds |> Seq.distinct |> Seq.toList
        if not (PasteResolvers.isCopyableEntityKind copiedEntityKind) || ids.IsEmpty then []
        else
            StoreLog.debug($"kind={copiedEntityKind}, count={ids.Length}, targetKind={targetEntityKind}, targetId={targetEntityId}")
            let mutable pastedIds = []
            store.WithTransaction($"Paste {copiedEntityKind}s", fun () ->
                pastedIds <- DirectPasteOps.dispatchPaste store copiedEntityKind ids targetEntityKind targetEntityId pasteIndex)
            if not pastedIds.IsEmpty then store.EmitRefreshAndHistory()
            pastedIds

    [<Extension>]
    static member ValidateCopySelection(store: DsStore, keys: seq<SelectionKey>) : CopyValidationResult =
        let filtered =
            keys
            |> Seq.filter (fun k -> PasteResolvers.isCopyableEntityKind k.EntityKind)
            |> Seq.distinctBy (fun k -> k.Id, k.EntityKind)
            |> Seq.toList
        if filtered.IsEmpty then CopyValidationResult.NothingToCopy
        else
            let kinds = filtered |> List.map (fun k -> k.EntityKind) |> List.distinct
            if kinds.Length > 1 then CopyValidationResult.MixedTypes
            elif filtered.Length > 1 then
                let parents =
                    filtered
                    |> List.map (fun k -> StoreHierarchyQueries.parentIdOf store k.EntityKind k.Id)
                    |> List.distinct
                if parents.Length > 1 then CopyValidationResult.MixedParents
                else CopyValidationResult.Ok filtered
            else CopyValidationResult.Ok filtered
