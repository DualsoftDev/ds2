namespace Ds2.UI.Core

open System
open System.Runtime.CompilerServices
open Ds2.Core

[<Extension>]
type DsStorePanelApiDefExtensions =
    [<Extension>]
    static member GetApiDefsForSystem(store: DsStore, systemId: Guid) : ApiDefPanelItem list =
        DsQuery.apiDefsOf systemId store
        |> List.map DirectPanelOps.toApiDefPanelItem

    [<Extension>]
    static member GetWorksForSystem(store: DsStore, systemId: Guid) : WorkDropdownItem list =
        DsQuery.flowsOf systemId store
        |> List.collect (fun flow -> DsQuery.worksOf flow.Id store)
        |> List.map (fun work -> WorkDropdownItem(work.Id, work.Name))

    [<Extension>]
    static member TryGetApiDefForEdit(store: DsStore, apiDefId: Guid) : (Guid * ApiDefPanelItem) option =
        DsQuery.getApiDef apiDefId store
        |> Option.map (fun apiDef -> apiDef.ParentId, DirectPanelOps.toApiDefPanelItem apiDef)

    [<Extension>]
    static member TryGetApiDefForEditOrNull(store: DsStore, apiDefId: Guid) : ApiDefEditInfo =
        DsStorePanelApiDefExtensions.TryGetApiDefForEdit(store, apiDefId)
        |> Option.map (fun (systemId, item) -> ApiDefEditInfo(systemId, item))
        |> Option.toObj


    [<Extension>]
    static member AddApiDefWithProperties
        (store: DsStore, name: string, systemId: Guid, isPush: bool, txGuid: Guid option, rxGuid: Guid option,
         period: int, description: string option) : Guid =
        StoreLog.debug($"name={name}, systemId={systemId}, isPush={isPush}")
        StoreLog.requireSystem(store, systemId) |> ignore
        let apiDef = ApiDef(name, systemId)
        apiDef.Properties.IsPush <- isPush
        apiDef.Properties.TxGuid <- txGuid
        apiDef.Properties.RxGuid <- rxGuid
        apiDef.Properties.Period <- period
        apiDef.Properties.Description <- description
        store.WithTransaction($"ApiDef 추가 \"{name}\"", fun () ->
            store.TrackAdd(store.ApiDefs, apiDef))
        store.EmitAndHistory(ApiDefAdded apiDef)
        apiDef.Id

    [<Extension>]
    static member UpdateApiDef
        (store: DsStore, apiDefId: Guid, newName: string, isPush: bool, txGuid: Guid option, rxGuid: Guid option,
         period: int, description: string option) =
        StoreLog.debug($"apiDefId={apiDefId}, newName={newName}, isPush={isPush}")
        StoreLog.requireApiDef(store, apiDefId) |> ignore
        store.WithTransaction("ApiDef 편집", fun () ->
            store.TrackMutate(store.ApiDefs, apiDefId, fun d ->
                d.Name <- newName
                d.Properties.IsPush <- isPush
                d.Properties.TxGuid <- txGuid
                d.Properties.RxGuid <- rxGuid
                d.Properties.Period <- period
                d.Properties.Description <- description))
        store.EmitRefreshAndHistory()

    // ─── Call Conditions ───────────────────────────────────────────
