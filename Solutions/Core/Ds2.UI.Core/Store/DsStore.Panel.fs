namespace Ds2.UI.Core

open System
open System.Globalization
open System.Runtime.CompilerServices
open Ds2.Core

// =============================================================================
// 내부 헬퍼 — 패널 읽기/쓰기
// =============================================================================

module internal DirectPanelOps =
    let private resolveApiDefDisplay (store: DsStore) (apiDefIdOpt: Guid option) : Guid option * string =
        match apiDefIdOpt |> Option.bind (fun id -> DsQuery.getApiDef id store) with
        | Some apiDef ->
            let dev =
                DsQuery.getSystem apiDef.ParentId store
                |> Option.map (fun s -> s.Name)
                |> Option.defaultValue "UnknownDevice"
            Some apiDef.Id, $"{dev}.{apiDef.Name}"
        | None -> None, "(unlinked)"

    let private tagAddress (tagOpt: IOTag option) =
        tagOpt |> Option.map (fun t -> t.Address) |> Option.defaultValue ""

    let toCallApiCallPanelItem (store: DsStore) (apiCall: ApiCall) : CallApiCallPanelItem =
        let apiDefId, apiDefDisplayName = resolveApiDefDisplay store apiCall.ApiDefId
        CallApiCallPanelItem(
            apiCall.Id, apiCall.Name, apiDefId, apiDefDisplayName,
            tagAddress apiCall.OutTag, tagAddress apiCall.InTag,
            PropertyPanelValueSpec.format apiCall.OutputSpec,
            PropertyPanelValueSpec.format apiCall.InputSpec,
            PropertyPanelValueSpec.dataTypeIndex apiCall.OutputSpec,
            PropertyPanelValueSpec.dataTypeIndex apiCall.InputSpec)

    let toConditionApiCallItem (store: DsStore) (ac: ApiCall) : CallConditionApiCallItem =
        let _, displayName = resolveApiDefDisplay store ac.ApiDefId
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

    /// Call 조회 → 매핑 or warn + 빈 리스트
    let withCallOrEmpty (store: DsStore) (callId: Guid) (f: Call -> 'T list) : 'T list =
        match DsQuery.getCall callId store with
        | Some call -> f call
        | None -> StoreLog.warn($"Call not found. id={callId}"); []

    /// withTransactionCallProps + TrackMutate(Calls) 조합 (조건 CRUD용)
    let mutateCallProps (store: DsStore) callId label (mutate: Call -> unit) =
        withTransactionCallProps store callId label (fun () ->
            store.TrackMutate(store.Calls, callId, mutate))

// =============================================================================
// DsStore 패널 확장 — 속성 패널 읽기/쓰기
// =============================================================================

module internal TimeSpanMsHelper =
    let getMs (tsOpt: TimeSpan option) : int option =
        tsOpt |> Option.map (fun t -> int t.TotalMilliseconds)

    let fromMs (ms: int option) : TimeSpan option =
        ms |> Option.map (fun m -> TimeSpan.FromMilliseconds(float m))

    /// 엔티티 조회 → TimeSpan 속성 → ms 변환, 없으면 warn + None
    let readMs (query: Guid -> DsStore -> 'T option) (getProp: 'T -> TimeSpan option) (entityKind: EntityKind) (store: DsStore) (id: Guid) : int option =
        query id store
        |> Option.bind (fun e -> getProp e |> getMs)
        |> Option.orElseWith (fun () -> StoreLog.warn($"{entityKind} not found. id={id}"); None)

[<Extension>]
type DsStorePanelExtensions =

    // ─── Work Period / Call Timeout (ms) ─────────────────────────
    [<Extension>]
    static member GetWorkPeriodMs(store: DsStore, workId: Guid) : int option =
        TimeSpanMsHelper.readMs DsQuery.getWork (fun w -> w.Properties.Period) EntityKind.Work store workId

    [<Extension>]
    static member GetWorkPeriodMsOrNull(store: DsStore, workId: Guid) : Nullable<int> =
        DsStorePanelExtensions.GetWorkPeriodMs(store, workId)
        |> Option.toNullable

    [<Extension>]
    static member GetCallTimeoutMs(store: DsStore, callId: Guid) : int option =
        TimeSpanMsHelper.readMs DsQuery.getCall (fun c -> c.Properties.Timeout) EntityKind.Call store callId

    [<Extension>]
    static member GetCallTimeoutMsOrNull(store: DsStore, callId: Guid) : Nullable<int> =
        DsStorePanelExtensions.GetCallTimeoutMs(store, callId)
        |> Option.toNullable

    [<Extension>]
    static member UpdateWorkPeriodMs(store: DsStore, workId: Guid, periodMs: int option) =
        StoreLog.debug($"workId={workId}, periodMs={periodMs}")
        let work = StoreLog.requireWork(store, workId)
        let period = TimeSpanMsHelper.fromMs periodMs
        if work.Properties.Period <> period then
            store.WithTransaction("Work 속성 변경", fun () ->
                store.TrackMutate(store.Works, workId, fun w -> w.Properties.Period <- period))
            store.EmitAndHistory(WorkPropsChanged workId)

    [<Extension>]
    static member UpdateWorkPeriodMs(store: DsStore, workId: Guid, periodMs: Nullable<int>) =
        DsStorePanelExtensions.UpdateWorkPeriodMs(store, workId, Option.ofNullable periodMs)

    [<Extension>]
    static member UpdateCallTimeoutMs(store: DsStore, callId: Guid, timeoutMs: int option) =
        StoreLog.debug($"callId={callId}, timeoutMs={timeoutMs}")
        let call = StoreLog.requireCall(store, callId)
        let timeout = TimeSpanMsHelper.fromMs timeoutMs
        if call.Properties.Timeout <> timeout then
            store.WithTransaction("Call 속성 변경", fun () ->
                store.TrackMutate(store.Calls, callId, fun c -> c.Properties.Timeout <- timeout))
            store.EmitAndHistory(CallPropsChanged callId)

    [<Extension>]
    static member UpdateCallTimeoutMs(store: DsStore, callId: Guid, timeoutMs: Nullable<int>) =
        DsStorePanelExtensions.UpdateCallTimeoutMs(store, callId, Option.ofNullable timeoutMs)

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
    static member TryGetApiDefForEditOrNull(store: DsStore, apiDefId: Guid) : ApiDefEditInfo =
        DsStorePanelExtensions.TryGetApiDefForEdit(store, apiDefId)
        |> Option.map (fun (systemId, item) -> ApiDefEditInfo(systemId, item))
        |> Option.toObj

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
        DsStorePanelExtensions.TryGetCallApiCallForPanel(store, callId, apiCallId)
        |> Option.toObj

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
    static member GetCallConditionsForPanel(store: DsStore, callId: Guid) : CallConditionPanelItem list =
        DirectPanelOps.withCallOrEmpty store callId (fun call ->
            call.CallConditions
            |> Seq.map (fun cond ->
                let items = cond.Conditions |> Seq.map (DirectPanelOps.toConditionApiCallItem store) |> Seq.toList
                CallConditionPanelItem(
                    cond.Id,
                    (cond.Type |> Option.defaultValue CallConditionType.Auto),
                    cond.IsOR, cond.IsRising, items))
            |> Seq.toList)

    [<Extension>]
    static member AddCallCondition(store: DsStore, callId: Guid, condType: CallConditionType) =
        StoreLog.debug($"callId={callId}, condType={condType}")
        StoreLog.requireCall(store, callId) |> ignore
        let cond = CallCondition(Type = Some condType)
        DirectPanelOps.mutateCallProps store callId "조건 추가" (fun c -> c.CallConditions.Add(cond))

    [<Extension>]
    static member RemoveCallCondition(store: DsStore, callId: Guid, conditionId: Guid) =
        StoreLog.debug($"callId={callId}, conditionId={conditionId}")
        StoreLog.requireCallCondition(store, callId, conditionId) |> ignore
        DirectPanelOps.mutateCallProps store callId "조건 삭제" (fun c ->
            c.CallConditions.RemoveAll(fun cc -> cc.Id = conditionId) |> ignore)

    [<Extension>]
    static member UpdateCallConditionSettings(store: DsStore, callId: Guid, condId: Guid, isOR: bool, isRising: bool) : bool =
        StoreLog.debug($"callId={callId}, condId={condId}, isOR={isOR}, isRising={isRising}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        if cond.IsOR <> isOR || cond.IsRising <> isRising then
            DirectPanelOps.mutateCallProps store callId "조건 설정 변경" (fun _ ->
                cond.IsOR <- isOR
                cond.IsRising <- isRising)
            true
        else false

    [<Extension>]
    static member AddApiCallsToConditionBatch(store: DsStore, callId: Guid, condId: Guid, sourceApiCallIds: Guid seq) : int =
        let sources = sourceApiCallIds |> Seq.choose (fun id -> DsQuery.getApiCall id store) |> Seq.toList
        if sources.IsEmpty then 0
        else
            StoreLog.debug($"callId={callId}, condId={condId}, count={sources.Length}")
            StoreLog.requireCallCondition(store, callId, condId) |> ignore
            DirectPanelOps.mutateCallProps store callId "조건에 ApiCall 추가" (fun c ->
                let cond = DirectPanelOps.findCondition c condId
                for src in sources do
                    let copy = src.DeepCopy()
                    copy.Id <- src.Id
                    cond.Conditions.Add(copy))
            sources.Length

    [<Extension>]
    static member RemoveApiCallFromCondition(store: DsStore, callId: Guid, condId: Guid, apiCallId: Guid) =
        StoreLog.debug($"callId={callId}, condId={condId}, apiCallId={apiCallId}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        StoreLog.requireApiCallInCondition(cond, apiCallId) |> ignore
        DirectPanelOps.mutateCallProps store callId "조건에서 ApiCall 제거" (fun c ->
            (DirectPanelOps.findCondition c condId).Conditions.RemoveAll(fun ac -> ac.Id = apiCallId) |> ignore)

    [<Extension>]
    static member UpdateConditionApiCallOutputSpec(store: DsStore, callId: Guid, condId: Guid, apiCallId: Guid, outTypeIndex: int, outText: string) : bool =
        StoreLog.debug($"callId={callId}, condId={condId}, apiCallId={apiCallId}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        let ac = StoreLog.requireApiCallInCondition(cond, apiCallId)
        let newSpec = PropertyPanelValueSpec.parseFromPanel outTypeIndex outText
        if ac.OutputSpec <> newSpec then
            DirectPanelOps.mutateCallProps store callId "조건 OutputSpec 변경" (fun c ->
                let ac = (DirectPanelOps.findCondition c condId).Conditions |> Seq.find (fun a -> a.Id = apiCallId)
                ac.OutputSpec <- newSpec)
            true
        else false
