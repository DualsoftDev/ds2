namespace Ds2.UI.Core

open System
open System.Runtime.CompilerServices
open Ds2.Core

// ─── ApiDef helpers ──────────────────────────────────────────────────

module internal PanelApiDefOps =
    let toApiDefPanelItem (apiDef: ApiDef) =
        ApiDefPanelItem(
            apiDef.Id, apiDef.Name, apiDef.Properties.IsPush,
            apiDef.Properties.TxGuid, apiDef.Properties.RxGuid,
            apiDef.Properties.Period,
            apiDef.Properties.Description |> Option.defaultValue "")

// ─── ApiDef extensions ───────────────────────────────────────────────

[<Extension>]
type DsStorePanelApiDefExtensions =
    [<Extension>]
    static member GetApiDefsForSystem(store: DsStore, systemId: Guid) : ApiDefPanelItem list =
        DsQuery.apiDefsOf systemId store
        |> List.map PanelApiDefOps.toApiDefPanelItem

    [<Extension>]
    static member GetWorksForSystem(store: DsStore, systemId: Guid) : WorkDropdownItem list =
        DsQuery.flowsOf systemId store
        |> List.collect (fun flow -> DsQuery.worksOf flow.Id store)
        |> List.map (fun work -> WorkDropdownItem(work.Id, work.Name))

    [<Extension>]
    static member TryGetApiDefForEdit(store: DsStore, apiDefId: Guid) : (Guid * ApiDefPanelItem) option =
        DsQuery.getApiDef apiDefId store
        |> Option.map (fun apiDef -> apiDef.ParentId, PanelApiDefOps.toApiDefPanelItem apiDef)

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

// ─── ApiCall extensions ──────────────────────────────────────────────

[<Extension>]
type DsStorePanelApiCallExtensions =
    [<Extension>]
    static member GetDeviceApiDefOptionsForCall(store: DsStore, callId: Guid) : DeviceApiDefOption list =
        let systems =
            match EntityHierarchyQueries.tryFindProjectIdForEntity store EntityKind.Call callId with
            | Some projectId -> DsQuery.passiveSystemsOf projectId store
            | None -> DsQuery.allProjects store |> List.collect (fun p -> DsQuery.passiveSystemsOf p.Id store)
        systems
        |> List.distinctBy (fun s -> s.Id)
        |> List.collect (fun system ->
            DsQuery.apiDefsOf system.Id store
            |> List.map (fun apiDef -> DeviceApiDefOption(apiDef.Id, system.Name, apiDef.Name)))
        |> List.sortBy (fun item -> item.DisplayName)

    [<Extension>]
    static member GetCallApiCallsForPanel(store: DsStore, callId: Guid) : CallApiCallPanelItem list =
        DirectPanelOps.withCallOrEmpty store callId (fun call ->
            call.ApiCalls |> Seq.map (DirectPanelOps.toCallApiCallPanelItem store) |> Seq.toList)

    [<Extension>]
    static member TryGetCallApiCallForPanel(store: DsStore, callId: Guid, apiCallId: Guid) : CallApiCallPanelItem option =
        match DsQuery.getCall callId store with
        | Some call ->
            call.ApiCalls
            |> Seq.tryFind (fun apiCall -> apiCall.Id = apiCallId)
            |> Option.map (DirectPanelOps.toCallApiCallPanelItem store)
        | None ->
            StoreLog.warn($"Call not found. id={callId}")
            None

    [<Extension>]
    static member TryGetCallApiCallForPanelOrNull(store: DsStore, callId: Guid, apiCallId: Guid) : CallApiCallPanelItem =
        DsStorePanelApiCallExtensions.TryGetCallApiCallForPanel(store, callId, apiCallId)
        |> Option.toObj

    [<Extension>]
    static member AddApiCallFromPanel
        (store: DsStore, callId: Guid, apiDefId: Guid,
         outputTagName: string, outputAddress: string,
         inputTagName: string, inputAddress: string,
         outTypeIndex: int, outText: string, inTypeIndex: int, inText: string)
        : Guid =
        StoreLog.debug($"callId={callId}, apiDefId={apiDefId}")
        let apiDef = StoreLog.requireApiDef(store, apiDefId)
        let call = StoreLog.requireCall(store, callId)
        let outputSpec = PropertyPanelValueSpec.parseFromPanel outTypeIndex outText
        let inputSpec = PropertyPanelValueSpec.parseFromPanel inTypeIndex inText
        let apiCall = DirectPanelOps.buildApiCall apiDef apiDef.Name None outputTagName outputAddress inputTagName inputAddress None inputSpec outputSpec
        DirectPanelOps.withTransactionCallProps store callId "ApiCall 추가" (fun () ->
            DirectPanelOps.addApiCallToStore store call apiCall)
        apiCall.Id

    [<Extension>]
    static member UpdateApiCallFromPanel
        (store: DsStore, callId: Guid, apiCallId: Guid, apiDefId: Guid, apiCallName: string,
         outputTagName: string, outputAddress: string,
         inputTagName: string, inputAddress: string,
         outTypeIndex: int, outText: string, inTypeIndex: int, inText: string)
        : bool =
        StoreLog.debug($"callId={callId}, apiCallId={apiCallId}, apiDefId={apiDefId}")
        let call = StoreLog.requireCall(store, callId)
        let newApiDef = StoreLog.requireApiDef(store, apiDefId)
        StoreLog.requireApiCallInCall(call, apiCallId)
        let outputSpec = PropertyPanelValueSpec.parseFromPanel outTypeIndex outText
        let inputSpec = PropertyPanelValueSpec.parseFromPanel inTypeIndex inText
        let updated = DirectPanelOps.buildApiCall newApiDef "" (Some apiCallName) outputTagName outputAddress inputTagName inputAddress (Some apiCallId) inputSpec outputSpec
        DirectPanelOps.withTransactionCallProps store callId "Update ApiCall" (fun () ->
            DirectPanelOps.removeApiCallFromStore store call apiCallId
            DirectPanelOps.addApiCallToStore store call updated)
        true

    [<Extension>]
    static member RemoveApiCallFromCall(store: DsStore, callId: Guid, apiCallId: Guid) =
        StoreLog.debug($"callId={callId}, apiCallId={apiCallId}")
        let call = StoreLog.requireCall(store, callId)
        StoreLog.requireApiCallInCall(call, apiCallId)
        DirectPanelOps.withTransactionCallProps store callId "ApiCall 제거" (fun () ->
            DirectPanelOps.removeApiCallFromStore store call apiCallId)
