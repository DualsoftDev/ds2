namespace Ds2.UI.Core

open System
open System.Runtime.CompilerServices
open Ds2.Core

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

    // ─── ApiCall Panel ─────────────────────────────────────────────
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
        (store: DsStore, callId: Guid, apiDefId: Guid, apiCallName: string,
         outputAddress: string, inputAddress: string,
         outTypeIndex: int, outText: string, inTypeIndex: int, inText: string)
        : Guid =
        StoreLog.debug($"callId={callId}, apiDefId={apiDefId}, name={apiCallName}")
        let apiDef = StoreLog.requireApiDef(store, apiDefId)
        let call = StoreLog.requireCall(store, callId)
        let outputSpec = PropertyPanelValueSpec.parseFromPanel outTypeIndex outText
        let inputSpec = PropertyPanelValueSpec.parseFromPanel inTypeIndex inText
        let apiCall = DirectPanelOps.buildApiCall apiDef apiDef.Name apiCallName outputAddress inputAddress None inputSpec outputSpec
        DirectPanelOps.withTransactionCallProps store callId "ApiCall 추가" (fun () ->
            DirectPanelOps.addApiCallToStore store call apiCall)
        apiCall.Id

    [<Extension>]
    static member UpdateApiCallFromPanel
        (store: DsStore, callId: Guid, apiCallId: Guid, apiDefId: Guid, apiCallName: string,
         outputAddress: string, inputAddress: string,
         outTypeIndex: int, outText: string, inTypeIndex: int, inText: string)
        : bool =
        StoreLog.debug($"callId={callId}, apiCallId={apiCallId}, apiDefId={apiDefId}")
        let call = StoreLog.requireCall(store, callId)
        let newApiDef = StoreLog.requireApiDef(store, apiDefId)
        StoreLog.requireApiCallInCall(call, apiCallId)
        let outputSpec = PropertyPanelValueSpec.parseFromPanel outTypeIndex outText
        let inputSpec = PropertyPanelValueSpec.parseFromPanel inTypeIndex inText
        let updated = DirectPanelOps.buildApiCall newApiDef "" apiCallName outputAddress inputAddress (Some apiCallId) inputSpec outputSpec
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

    // ─── ApiDef (생성+속성 통합 / 편집 통합) ────────────────────────
