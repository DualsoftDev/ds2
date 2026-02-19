module Ds2.UI.Core.PanelOps

open System
open System.Globalization
open Ds2.Core

/// ApiCall 객체 생성 (property panel에서 사용)
let buildApiCall
    (apiDef: ApiDef) (fallbackName: string) (apiCallName: string)
    (outputAddress: string) (inputAddress: string) (apiCallId: Guid option)
    (inputValueSpec: ValueSpec)
    : ApiCall =
    let resolvedName =
        if String.IsNullOrWhiteSpace(apiCallName) then fallbackName
        else apiCallName.Trim()
    let apiCall = ApiCall(resolvedName)
    apiCall.ApiDefId <- Some apiDef.Id
    match apiCallId with Some id -> apiCall.Id <- id | None -> ()
    if not (String.IsNullOrWhiteSpace(outputAddress)) then
        apiCall.OutTag <- Some(IOTag("Out", outputAddress.Trim(), ""))
    if not (String.IsNullOrWhiteSpace(inputAddress)) then
        apiCall.InTag <- Some(IOTag("In", inputAddress.Trim(), ""))
    apiCall.InputValueSpec <- inputValueSpec
    apiCall

let getWorkDurationText (store: DsStore) (workId: Guid) : string =
    match DsQuery.getWork workId store with
    | Some work ->
        match work.Properties.Duration with
        | Some duration -> duration.ToString("c", CultureInfo.InvariantCulture)
        | None -> ""
    | None -> ""

/// parse 실패 → (false, None) / 변경 없음 → (true, None) / 변경 → (true, Some cmd)
let tryBuildUpdateWorkDurationCmd (store: DsStore) (workId: Guid) (durationText: string) : bool * EditorCommand option =
    let parsedOpt =
        if String.IsNullOrWhiteSpace(durationText) then Some None
        else
            match TimeSpan.TryParse(durationText.Trim(), CultureInfo.InvariantCulture) with
            | true, d -> Some(Some d)
            | _ -> None
    match parsedOpt with
    | None -> false, None
    | Some parsedDuration ->
        match DsQuery.getWork workId store with
        | None -> false, None
        | Some work ->
            if work.Properties.Duration = parsedDuration then true, None
            else
                let oldProps = work.Properties.DeepCopy()
                let next = work.Properties.DeepCopy()
                next.Duration <- parsedDuration
                true, Some(UpdateWorkProps(workId, oldProps, next))

let getCallTimeoutText (store: DsStore) (callId: Guid) : string =
    match DsQuery.getCall callId store with
    | Some call ->
        match call.Properties.Timeout with
        | Some t -> string (int t.TotalMilliseconds)
        | None -> ""
    | None -> ""

/// parse 실패 → (false, None) / 변경 없음 → (true, None) / 변경 → (true, Some cmd)
let tryBuildUpdateCallTimeoutCmd (store: DsStore) (callId: Guid) (msText: string) : bool * EditorCommand option =
    let parsedOpt =
        if String.IsNullOrWhiteSpace(msText) then Some None
        else
            match Int32.TryParse(msText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, ms when ms >= 0 -> Some(Some(TimeSpan.FromMilliseconds(float ms)))
            | _ -> None
    match parsedOpt with
    | None -> false, None
    | Some parsedTimeout ->
        match DsQuery.getCall callId store with
        | None -> false, None
        | Some call ->
            if call.Properties.Timeout = parsedTimeout then true, None
            else
                let oldProps = call.Properties.DeepCopy()
                let next = call.Properties.DeepCopy()
                next.Timeout <- parsedTimeout
                true, Some(UpdateCallProps(callId, oldProps, next))

let getApiDefsForSystem (store: DsStore) (systemId: Guid) : ApiDefPanelItem list =
    DsQuery.apiDefsOf systemId store
    |> List.map (fun apiDef ->
        ApiDefPanelItem(
            apiDef.Id,
            apiDef.Name,
            apiDef.Properties.IsPush,
            apiDef.Properties.TxGuid,
            apiDef.Properties.RxGuid,
            apiDef.Properties.Duration,
            apiDef.Properties.Memo |> Option.defaultValue ""))

let getWorksForSystem (store: DsStore) (systemId: Guid) : WorkDropdownItem list =
    DsQuery.flowsOf systemId store
    |> List.collect (fun flow -> DsQuery.worksOf flow.Id store)
    |> List.map (fun work -> WorkDropdownItem(work.Id, work.Name))

let getApiDefParentSystemId (store: DsStore) (apiDefId: Guid) : Guid option =
    DsQuery.getApiDef apiDefId store |> Option.map (fun d -> d.ParentId)

let getDeviceApiDefOptionsForCall (store: DsStore) (callId: Guid) : DeviceApiDefOption list =
    let systems =
        match EntityHierarchyQueries.tryFindProjectIdForEntity store "Call" callId with
        | Some projectId -> DsQuery.passiveSystemsOf projectId store
        | None -> DsQuery.allProjects store |> List.collect (fun p -> DsQuery.passiveSystemsOf p.Id store)
    systems
    |> List.distinctBy (fun s -> s.Id)
    |> List.collect (fun system ->
        DsQuery.apiDefsOf system.Id store
        |> List.map (fun apiDef -> DeviceApiDefOption(apiDef.Id, system.Name, apiDef.Name)))
    |> List.sortBy (fun item -> item.DisplayName)

let getCallApiCallsForPanel (store: DsStore) (callId: Guid) : CallApiCallPanelItem list =
    match DsQuery.getCall callId store with
    | None -> []
    | Some call ->
        call.ApiCalls
        |> Seq.map (fun (apiCall, valueSpec) ->
            let apiDefId, hasApiDef, apiDefDisplayName =
                match apiCall.ApiDefId |> Option.bind (fun id -> DsQuery.getApiDef id store) with
                | Some apiDef ->
                    let deviceName =
                        DsQuery.getSystem apiDef.ParentId store
                        |> Option.map (fun s -> s.Name)
                        |> Option.defaultValue "UnknownDevice"
                    apiDef.Id, true, $"{deviceName}.{apiDef.Name}"
                | None -> Guid.Empty, false, "(unlinked)"
            let outputAddress = apiCall.OutTag |> Option.map (fun t -> t.Address) |> Option.defaultValue ""
            let inputAddress  = apiCall.InTag  |> Option.map (fun t -> t.Address) |> Option.defaultValue ""
            CallApiCallPanelItem(
                apiCall.Id, apiCall.Name, apiDefId, hasApiDef, apiDefDisplayName,
                outputAddress, inputAddress,
                PropertyPanelValueSpec.format valueSpec,
                PropertyPanelValueSpec.format apiCall.InputValueSpec))
        |> Seq.toList

/// AddApiCall 커맨드 빌드 → Some (newApiCallId, cmd) or None
let buildAddApiCallCmd (store: DsStore) (callId: Guid) (apiDefId: Guid) (apiCallName: string) (outputAddress: string) (inputAddress: string) (valueSpecText: string) (inputValueSpecText: string) : (Guid * EditorCommand) option =
    match DsQuery.getApiDef apiDefId store, DsQuery.getCall callId store, PropertyPanelValueSpec.tryParseSingle valueSpecText, PropertyPanelValueSpec.tryParseSingle inputValueSpecText with
    | Some apiDef, Some _, Some valueSpec, Some inputValueSpec ->
        let apiCall = buildApiCall apiDef apiDef.Name apiCallName outputAddress inputAddress None inputValueSpec
        Some(apiCall.Id, AddApiCallToCall(callId, apiCall, valueSpec))
    | _ -> None

/// UpdateApiCall 커맨드 빌드 → Some cmd or None
let buildUpdateApiCallCmd (store: DsStore) (callId: Guid) (apiCallId: Guid) (apiDefId: Guid) (apiCallName: string) (outputAddress: string) (inputAddress: string) (valueSpecText: string) (inputValueSpecText: string) : EditorCommand option =
    match DsQuery.getCall callId store with
    | None -> None
    | Some call ->
        let existing = call.ApiCalls |> Seq.tryFind (fun (ac, _) -> ac.Id = apiCallId)
        match existing, DsQuery.getApiDef apiDefId store, PropertyPanelValueSpec.tryParseSingle valueSpecText, PropertyPanelValueSpec.tryParseSingle inputValueSpecText with
        | Some(oldApiCall, oldValueSpec), Some newApiDef, Some newValueSpec, Some newInputValueSpec ->
            let updatedApiCall = buildApiCall newApiDef oldApiCall.Name apiCallName outputAddress inputAddress (Some oldApiCall.Id) newInputValueSpec
            Some(Composite("Update ApiCall", [
                RemoveApiCallFromCall(callId, oldApiCall, oldValueSpec)
                AddApiCallToCall(callId, updatedApiCall, newValueSpec)
            ]))
        | _ -> None
