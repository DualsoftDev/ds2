namespace Ds2.UI.Core

open System
open System.Globalization
open System.Runtime.CompilerServices
open Ds2.Core

// =============================================================================
// 내부 헬퍼 — 패널 읽기/쓰기
// =============================================================================

module internal DirectPanelOps =
    let private resolveApiDefDisplay (store: DsStore) (apiDefIdOpt: Guid option) : Guid * bool * string =
        match apiDefIdOpt |> Option.bind (fun id -> DsQuery.getApiDef id store) with
        | Some apiDef ->
            let dev =
                DsQuery.getSystem apiDef.ParentId store
                |> Option.map (fun s -> s.Name)
                |> Option.defaultValue "UnknownDevice"
            apiDef.Id, true, $"{dev}.{apiDef.Name}"
        | None -> Guid.Empty, false, "(unlinked)"

    let private tagAddress (tagOpt: IOTag option) =
        tagOpt |> Option.map (fun t -> t.Address) |> Option.defaultValue ""

    let toCallApiCallPanelItem (store: DsStore) (apiCall: ApiCall) : CallApiCallPanelItem =
        let apiDefId, hasApiDef, apiDefDisplayName = resolveApiDefDisplay store apiCall.ApiDefId
        CallApiCallPanelItem(
            apiCall.Id, apiCall.Name, apiDefId, hasApiDef, apiDefDisplayName,
            tagAddress apiCall.OutTag, tagAddress apiCall.InTag,
            PropertyPanelValueSpec.format apiCall.OutputSpec,
            PropertyPanelValueSpec.format apiCall.InputSpec,
            PropertyPanelValueSpec.dataTypeIndex apiCall.OutputSpec,
            PropertyPanelValueSpec.dataTypeIndex apiCall.InputSpec)

    let toConditionApiCallItem (store: DsStore) (ac: ApiCall) : CallConditionApiCallItem =
        let _, _, displayName = resolveApiDefDisplay store ac.ApiDefId
        CallConditionApiCallItem(
            ac.Id, ac.Name, displayName,
            PropertyPanelValueSpec.format ac.OutputSpec,
            PropertyPanelValueSpec.dataTypeIndex ac.OutputSpec)

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
        apiCall.InputSpec <- inputSpec
        apiCall.OutputSpec <- outputSpec
        apiCall

    // ─── 반복 패턴 헬퍼 ──────────────────────────────────────────────

    /// WithTransaction + EmitAndHistory(CallPropsChanged) 쌍
    let withTransactionCallProps (store: DsStore) callId label (action: unit -> unit) =
        store.WithTransaction(label, action)
        store.EmitAndHistory(CallPropsChanged callId)

    /// Call의 ApiCall 등록 (store 컬렉션 + call.ApiCalls 양쪽)
    let addApiCallToStore (store: DsStore) (call: Call) (apiCall: ApiCall) =
        store.TrackAdd(store.ApiCalls, apiCall)
        store.TrackMutate(store.Calls, call.Id, fun c -> c.ApiCalls.Add(apiCall))

    /// Call의 ApiCall 제거 (call.ApiCalls + store 컬렉션 양쪽)
    let removeApiCallFromStore (store: DsStore) (call: Call) (apiCallId: Guid) =
        store.TrackMutate(store.Calls, call.Id, fun c -> c.ApiCalls.RemoveAll(fun ac -> ac.Id = apiCallId) |> ignore)
        store.TrackRemove(store.ApiCalls, apiCallId)

    /// TrackMutate 내부에서 조건(CallCondition) 탐색
    let findCondition (c: Call) (condId: Guid) =
        c.CallConditions |> Seq.find (fun cc -> cc.Id = condId)

    let toApiDefPanelItem (apiDef: ApiDef) =
        ApiDefPanelItem(
            apiDef.Id, apiDef.Name, apiDef.Properties.IsPush,
            apiDef.Properties.TxGuid, apiDef.Properties.RxGuid,
            apiDef.Properties.Period,
            apiDef.Properties.Description |> Option.defaultValue "")

// =============================================================================
// DsStore 패널 확장 — 속성 패널 읽기/쓰기
// =============================================================================

[<Extension>]
type DsStorePanelExtensions =

    // ─── Work Period (ms) ───────────────────────────────────────
    [<Extension>]
    static member GetWorkPeriodMs(store: DsStore, workId: Guid) : int option =
        match DsQuery.getWork workId store with
        | Some work -> work.Properties.Period |> Option.map (fun p -> int p.TotalMilliseconds)
        | None ->
            StoreLog.warn($"Work not found. id={workId}")
            None

    [<Extension>]
    static member UpdateWorkPeriodMs(store: DsStore, workId: Guid, periodMs: int option) =
        StoreLog.debug($"workId={workId}, periodMs={periodMs}")
        let work = StoreLog.requireWork(store, workId)
        let period = periodMs |> Option.map (fun ms -> TimeSpan.FromMilliseconds(float ms))
        if work.Properties.Period <> period then
            store.WithTransaction("Work 속성 변경", fun () ->
                store.TrackMutate(store.Works, workId, fun w -> w.Properties.Period <- period))
            store.EmitAndHistory(WorkPropsChanged workId)

    // ─── Call Timeout (ms) ───────────────────────────────────────
    [<Extension>]
    static member GetCallTimeoutMs(store: DsStore, callId: Guid) : int option =
        match DsQuery.getCall callId store with
        | Some call -> call.Properties.Timeout |> Option.map (fun t -> int t.TotalMilliseconds)
        | None ->
            StoreLog.warn($"Call not found. id={callId}")
            None

    [<Extension>]
    static member UpdateCallTimeoutMs(store: DsStore, callId: Guid, timeoutMs: int option) =
        StoreLog.debug($"callId={callId}, timeoutMs={timeoutMs}")
        let call = StoreLog.requireCall(store, callId)
        let timeout = timeoutMs |> Option.map (fun ms -> TimeSpan.FromMilliseconds(float ms))
        if call.Properties.Timeout <> timeout then
            store.WithTransaction("Call 속성 변경", fun () ->
                store.TrackMutate(store.Calls, callId, fun c -> c.Properties.Timeout <- timeout))
            store.EmitAndHistory(CallPropsChanged callId)

    // ─── ApiDef / System Query ─────────────────────────────────────
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
    static member GetDeviceApiDefOptionsForCall(store: DsStore, callId: Guid) : DeviceApiDefOption list =
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

    // ─── ApiCall Panel ─────────────────────────────────────────────
    [<Extension>]
    static member GetCallApiCallsForPanel(store: DsStore, callId: Guid) : CallApiCallPanelItem list =
        match DsQuery.getCall callId store with
        | Some call ->
            call.ApiCalls |> Seq.map (DirectPanelOps.toCallApiCallPanelItem store) |> Seq.toList
        | None ->
            StoreLog.warn($"Call not found. id={callId}")
            []

    [<Extension>]
    static member GetAllApiCallsForPanel(store: DsStore) : CallApiCallPanelItem list =
        DsQuery.allApiCalls store
        |> List.map (DirectPanelOps.toCallApiCallPanelItem store)
        |> List.sortBy (fun item -> item.ApiDefDisplayName, item.Name)

    [<Extension>]
    static member AddApiCallFromPanel
        (store: DsStore, callId: Guid, apiDefId: Guid, apiCallName: string,
         outputAddress: string, inputAddress: string,
         outTypeIndex: int, outText: string, inTypeIndex: int, inText: string)
        : Guid option =
        StoreLog.debug($"callId={callId}, apiDefId={apiDefId}, name={apiCallName}")
        let apiDef = StoreLog.requireApiDef(store, apiDefId)
        let call = StoreLog.requireCall(store, callId)
        let outputSpec = PropertyPanelValueSpec.parseFromPanel outTypeIndex outText
        let inputSpec = PropertyPanelValueSpec.parseFromPanel inTypeIndex inText
        let apiCall = DirectPanelOps.buildApiCall apiDef apiDef.Name apiCallName outputAddress inputAddress None inputSpec outputSpec
        DirectPanelOps.withTransactionCallProps store callId "ApiCall 추가" (fun () ->
            DirectPanelOps.addApiCallToStore store call apiCall)
        Some apiCall.Id

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
    [<Extension>]
    static member GetCallConditionsForPanelUi(store: DsStore, callId: Guid) : UiCallConditionPanelItem list =
        match DsQuery.getCall callId store with
        | Some call ->
            call.CallConditions
            |> Seq.map (fun cond ->
                let items = cond.Conditions |> Seq.map (DirectPanelOps.toConditionApiCallItem store) |> Seq.toList
                UiCallConditionPanelItem(
                    cond.Id,
                    UiCallConditionType.ofCore (cond.Type |> Option.defaultValue CallConditionType.Auto),
                    cond.IsOR, cond.IsRising, items))
            |> Seq.toList
        | None ->
            StoreLog.warn($"Call not found. id={callId}")
            []

    [<Extension>]
    static member AddCallConditionUi(store: DsStore, callId: Guid, condType: UiCallConditionType) : bool =
        StoreLog.debug($"callId={callId}, condType={condType}")
        let _call = StoreLog.requireCall(store, callId)
        let cond = CallCondition(Type = Some (UiCallConditionType.toCore condType))
        DirectPanelOps.withTransactionCallProps store callId "조건 추가" (fun () ->
            store.TrackMutate(store.Calls, callId, fun c -> c.CallConditions.Add(cond)))
        true

    [<Extension>]
    static member RemoveCallCondition(store: DsStore, callId: Guid, conditionId: Guid) : bool =
        StoreLog.debug($"callId={callId}, conditionId={conditionId}")
        StoreLog.requireCallCondition(store, callId, conditionId) |> ignore
        DirectPanelOps.withTransactionCallProps store callId "조건 삭제" (fun () ->
            store.TrackMutate(store.Calls, callId, fun c ->
                c.CallConditions.RemoveAll(fun cc -> cc.Id = conditionId) |> ignore))
        true

    [<Extension>]
    static member UpdateCallConditionSettings(store: DsStore, callId: Guid, condId: Guid, isOR: bool, isRising: bool) : bool =
        StoreLog.debug($"callId={callId}, condId={condId}, isOR={isOR}, isRising={isRising}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        if cond.IsOR <> isOR || cond.IsRising <> isRising then
            DirectPanelOps.withTransactionCallProps store callId "조건 설정 변경" (fun () ->
                store.TrackMutate(store.Calls, callId, fun _ ->
                    cond.IsOR <- isOR
                    cond.IsRising <- isRising))
            true
        else false

    [<Extension>]
    static member AddApiCallsToConditionBatch(store: DsStore, callId: Guid, condId: Guid, sourceApiCallIds: Guid seq) : int =
        let sources = sourceApiCallIds |> Seq.choose (fun id -> DsQuery.getApiCall id store) |> Seq.toList
        if sources.IsEmpty then 0
        else
            StoreLog.debug($"callId={callId}, condId={condId}, count={sources.Length}")
            let _cond = StoreLog.requireCallCondition(store, callId, condId)
            DirectPanelOps.withTransactionCallProps store callId "조건에 ApiCall 추가" (fun () ->
                store.TrackMutate(store.Calls, callId, fun c ->
                    let cond = DirectPanelOps.findCondition c condId
                    for src in sources do
                        let copy = src.DeepCopy()
                        copy.Id <- src.Id
                        cond.Conditions.Add(copy)))
            sources.Length

    [<Extension>]
    static member RemoveApiCallFromCondition(store: DsStore, callId: Guid, condId: Guid, apiCallId: Guid) : bool =
        StoreLog.debug($"callId={callId}, condId={condId}, apiCallId={apiCallId}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        StoreLog.requireApiCallInCondition(cond, apiCallId) |> ignore
        DirectPanelOps.withTransactionCallProps store callId "조건에서 ApiCall 제거" (fun () ->
            store.TrackMutate(store.Calls, callId, fun c ->
                (DirectPanelOps.findCondition c condId).Conditions.RemoveAll(fun ac -> ac.Id = apiCallId) |> ignore))
        true

    [<Extension>]
    static member UpdateConditionApiCallOutputSpec(store: DsStore, callId: Guid, condId: Guid, apiCallId: Guid, outTypeIndex: int, outText: string) : bool =
        StoreLog.debug($"callId={callId}, condId={condId}, apiCallId={apiCallId}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        let ac = StoreLog.requireApiCallInCondition(cond, apiCallId)
        let newSpec = PropertyPanelValueSpec.parseFromPanel outTypeIndex outText
        if ac.OutputSpec <> newSpec then
            DirectPanelOps.withTransactionCallProps store callId "조건 OutputSpec 변경" (fun () ->
                store.TrackMutate(store.Calls, callId, fun c ->
                    let ac = (DirectPanelOps.findCondition c condId).Conditions |> Seq.find (fun a -> a.Id = apiCallId)
                    ac.OutputSpec <- newSpec))
            true
        else false
