module Ds2.UI.Core.PanelOps

open System
open System.Globalization
open Ds2.Core

/// ApiCall 객체 생성 (property panel에서 사용)
let buildApiCall
    (apiDef: ApiDef) (fallbackName: string) (apiCallName: string)
    (outputAddress: string) (inputAddress: string) (apiCallId: Guid option)
    (inputSpec: ValueSpec) (outputSpec: ValueSpec)
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
    apiCall.InputSpec  <- inputSpec
    apiCall.OutputSpec <- outputSpec
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
            apiDef.Properties.Description |> Option.defaultValue ""))

let getWorksForSystem (store: DsStore) (systemId: Guid) : WorkDropdownItem list =
    DsQuery.flowsOf systemId store
    |> List.collect (fun flow -> DsQuery.worksOf flow.Id store)
    |> List.map (fun work -> WorkDropdownItem(work.Id, work.Name))

let getApiDefParentSystemId (store: DsStore) (apiDefId: Guid) : Guid option =
    DsQuery.getApiDef apiDefId store |> Option.map (fun d -> d.ParentId)

let getDeviceApiDefOptionsForCall (store: DsStore) (callId: Guid) : DeviceApiDefOption list =
    let systems =
        match EntityHierarchyQueries.tryFindProjectIdForEntity store EntityTypeNames.Call callId with
        | Some projectId -> DsQuery.passiveSystemsOf projectId store
        | None -> DsQuery.allProjects store |> List.collect (fun p -> DsQuery.passiveSystemsOf p.Id store)
    systems
    |> List.distinctBy (fun s -> s.Id)
    |> List.collect (fun system ->
        DsQuery.apiDefsOf system.Id store
        |> List.map (fun apiDef -> DeviceApiDefOption(apiDef.Id, system.Name, apiDef.Name)))
    |> List.sortBy (fun item -> item.DisplayName)

let private resolveApiDefDisplay (store: DsStore) (apiDefIdOpt: Guid option) : Guid * bool * string =
    match apiDefIdOpt |> Option.bind (fun id -> DsQuery.getApiDef id store) with
    | Some apiDef ->
        let dev =
            DsQuery.getSystem apiDef.ParentId store
            |> Option.map (fun s -> s.Name)
            |> Option.defaultValue "UnknownDevice"
        apiDef.Id, true, $"{dev}.{apiDef.Name}"
    | None -> Guid.Empty, false, "(unlinked)"

let private toCallApiCallPanelItem (store: DsStore) (apiCall: ApiCall) : CallApiCallPanelItem =
    let apiDefId, hasApiDef, apiDefDisplayName = resolveApiDefDisplay store apiCall.ApiDefId
    let outputAddress = apiCall.OutTag |> Option.map (fun t -> t.Address) |> Option.defaultValue ""
    let inputAddress  = apiCall.InTag  |> Option.map (fun t -> t.Address) |> Option.defaultValue ""
    CallApiCallPanelItem(
        apiCall.Id, apiCall.Name, apiDefId, hasApiDef, apiDefDisplayName,
        outputAddress, inputAddress,
        PropertyPanelValueSpec.format apiCall.OutputSpec,
        PropertyPanelValueSpec.format apiCall.InputSpec,
        PropertyPanelValueSpec.dataTypeIndex apiCall.OutputSpec,
        PropertyPanelValueSpec.dataTypeIndex apiCall.InputSpec)

let getCallApiCallsForPanel (store: DsStore) (callId: Guid) : CallApiCallPanelItem list =
    match DsQuery.getCall callId store with
    | None -> []
    | Some call ->
        call.ApiCalls
        |> Seq.map (toCallApiCallPanelItem store)
        |> Seq.toList

let getCallConditionsForPanel (store: DsStore) (callId: Guid) : CallConditionPanelItem list =
    match DsQuery.getCall callId store with
    | None -> []
    | Some call ->
        call.CallConditions
        |> Seq.map (fun cond ->
            let items =
                cond.Conditions
                |> Seq.map (fun ac ->
                    let _, _, displayName = resolveApiDefDisplay store ac.ApiDefId
                    CallConditionApiCallItem(
                        ac.Id, ac.Name, displayName,
                        PropertyPanelValueSpec.format ac.OutputSpec,
                        PropertyPanelValueSpec.dataTypeIndex ac.OutputSpec))
                |> Seq.toList
            CallConditionPanelItem(
                cond.Id,
                cond.Type |> Option.defaultValue CallConditionType.Auto,
                cond.IsOR, cond.IsRising, items))
        |> Seq.toList

let buildAddCallConditionCmd (store: DsStore) (callId: Guid) (conditionType: CallConditionType) : EditorCommand option =
    match DsQuery.getCall callId store with
    | None -> None
    | Some _ ->
        let cond = CallCondition(Type = Some conditionType)
        Some(AddCallCondition(callId, cond))

let buildRemoveCallConditionCmd (store: DsStore) (callId: Guid) (conditionId: Guid) : EditorCommand option =
    match DsQuery.getCall callId store with
    | None -> None
    | Some call ->
        call.CallConditions
        |> Seq.tryFind (fun c -> c.Id = conditionId)
        |> Option.map (fun cond -> RemoveCallCondition(callId, cond))

let buildUpdateCallConditionSettingsCmd
    (store: DsStore) (callId: Guid) (conditionId: Guid)
    (newIsOR: bool) (newIsRising: bool) : EditorCommand option =
    match DsQuery.getCall callId store with
    | None -> None
    | Some call ->
        call.CallConditions
        |> Seq.tryFind (fun c -> c.Id = conditionId)
        |> Option.bind (fun cond ->
            if cond.IsOR = newIsOR && cond.IsRising = newIsRising then None
            else
                Some(UpdateCallConditionSettings(
                    callId, conditionId,
                    cond.IsOR, newIsOR, cond.IsRising, newIsRising)))

/// store.ApiCalls 전체에서 ApiCall 목록 반환 (조건 ApiCall 선택 등 전역 목록 필요 시 사용)
let getAllApiCallsForPanel (store: DsStore) : CallApiCallPanelItem list =
    DsQuery.allApiCalls store
    |> List.map (toCallApiCallPanelItem store)
    |> List.sortBy (fun item -> item.ApiDefDisplayName, item.Name)

/// store.ApiCalls 전체에서 소스 ApiCall을 DeepCopy(Id 유지) 후 OutputSpec을 교체하여 조건에 추가
let buildAddApiCallToConditionCmd
    (store: DsStore) (callId: Guid) (conditionId: Guid)
    (sourceApiCallId: Guid) (outputSpecText: string) : EditorCommand option =
    match DsQuery.getCall callId store with
    | None -> None
    | Some _ ->
        match DsQuery.getApiCall sourceApiCallId store with
        | None -> None
        | Some src ->
            match PropertyPanelValueSpec.tryParseAs src.OutputSpec outputSpecText with
            | None -> None
            | Some newSpec ->
                let copy = src.DeepCopy()
                copy.Id <- src.Id   // DeepCopy는 새 Guid를 생성하므로 원본 Id 복원
                copy.OutputSpec <- newSpec
                Some(AddApiCallToCondition(callId, conditionId, copy))

let buildAddApiCallsToConditionBatchCmd
    (store: DsStore) (callId: Guid) (conditionId: Guid)
    (sourceApiCallIds: Guid list) : (EditorCommand * int) option =
    let cmds =
        sourceApiCallIds
        |> List.choose (fun srcId ->
            buildAddApiCallToConditionCmd store callId conditionId srcId "Undefined")
    match cmds with
    | []      -> None
    | [single] -> Some(single, 1)
    | many    -> Some(Composite("조건에 ApiCall 추가", many), many.Length)

let buildRemoveApiCallFromConditionCmd
    (store: DsStore) (callId: Guid) (conditionId: Guid) (apiCallId: Guid) : EditorCommand option =
    match DsQuery.getCall callId store with
    | None -> None
    | Some call ->
        call.CallConditions
        |> Seq.tryFind (fun c -> c.Id = conditionId)
        |> Option.bind (fun cond ->
            cond.Conditions
            |> Seq.tryFind (fun ac -> ac.Id = apiCallId)
            |> Option.map (fun ac -> RemoveApiCallFromCondition(callId, conditionId, ac)))

let buildUpdateConditionApiCallOutputSpecCmd
    (store: DsStore) (callId: Guid) (conditionId: Guid)
    (apiCallId: Guid) (newSpecText: string) : EditorCommand option =
    match DsQuery.getCall callId store with
    | None -> None
    | Some call ->
        call.CallConditions
        |> Seq.tryFind (fun c -> c.Id = conditionId)
        |> Option.bind (fun cond ->
            cond.Conditions
            |> Seq.tryFind (fun ac -> ac.Id = apiCallId)
            |> Option.bind (fun ac ->
                match PropertyPanelValueSpec.tryParseAs ac.OutputSpec newSpecText with
                | None -> None
                | Some newSpec ->
                    if ac.OutputSpec = newSpec then None
                    else
                        Some(UpdateConditionApiCallOutputSpec(
                            callId, conditionId, apiCallId, ac.OutputSpec, newSpec))))

/// AddApiCall 커맨드 빌드 → Some (newApiCallId, cmd) or None
let buildAddApiCallCmd (store: DsStore) (callId: Guid) (apiDefId: Guid) (apiCallName: string) (outputAddress: string) (inputAddress: string) (valueSpecText: string) (inputValueSpecText: string) : (Guid * EditorCommand) option =
    match DsQuery.getApiDef apiDefId store, DsQuery.getCall callId store with
    | Some apiDef, Some _ ->
        // Add 시엔 기존 타입 없으므로 UndefinedValue 힌트로 텍스트에서 추론
        match PropertyPanelValueSpec.tryParseAs UndefinedValue valueSpecText,
              PropertyPanelValueSpec.tryParseAs UndefinedValue inputValueSpecText with
        | Some outputSpec, Some inputSpec ->
            let apiCall = buildApiCall apiDef apiDef.Name apiCallName outputAddress inputAddress None inputSpec outputSpec
            Some(apiCall.Id, AddApiCallToCall(callId, apiCall))
        | _ -> None
    | _ -> None

/// UpdateApiCall 커맨드 빌드 → Some cmd or None
let buildUpdateApiCallCmd (store: DsStore) (callId: Guid) (apiCallId: Guid) (apiDefId: Guid) (apiCallName: string) (outputAddress: string) (inputAddress: string) (outputTypeIndex: int) (valueSpecText: string) (inputTypeIndex: int) (inputValueSpecText: string) : EditorCommand option =
    match DsQuery.getCall callId store with
    | None -> None
    | Some call ->
        let existing = call.ApiCalls |> Seq.tryFind (fun ac -> ac.Id = apiCallId)
        match existing, DsQuery.getApiDef apiDefId store with
        | Some oldApiCall, Some newApiDef ->
            // 사용자가 선택한 타입 인덱스를 hint로 사용 (타입 변경 지원)
            let outputHint = PropertyPanelValueSpec.specFromTypeIndex outputTypeIndex
            let inputHint  = PropertyPanelValueSpec.specFromTypeIndex inputTypeIndex
            match PropertyPanelValueSpec.tryParseAs outputHint valueSpecText,
                  PropertyPanelValueSpec.tryParseAs inputHint  inputValueSpecText with
            | Some newOutputSpec, Some newInputSpec ->
                let updatedApiCall = buildApiCall newApiDef oldApiCall.Name apiCallName outputAddress inputAddress (Some oldApiCall.Id) newInputSpec newOutputSpec
                Some(Composite("Update ApiCall", [
                    RemoveApiCallFromCall(callId, oldApiCall)
                    AddApiCallToCall(callId, updatedApiCall)
                ]))
            | _ -> None
        | _ -> None
