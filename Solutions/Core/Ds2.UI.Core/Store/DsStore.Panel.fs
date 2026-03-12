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

    /// 재귀적 조건 탐색 (중첩 Children 지원)
    let rec tryFindConditionRec (conditions: ResizeArray<CallCondition>) (condId: Guid) : CallCondition option =
        conditions |> Seq.tryPick (fun cc ->
            if cc.Id = condId then Some cc
            else tryFindConditionRec cc.Children condId)

    let tryFindCondition (c: Call) (condId: Guid) =
        tryFindConditionRec c.CallConditions condId

    let requireCondition (callId: Guid) (c: Call) (condId: Guid) =
        match tryFindCondition c condId with
        | Some cond -> cond
        | None -> invalidOp $"CallCondition not found. callId={callId}, condId={condId}"

    let requireApiCallInCondition (callId: Guid) (condId: Guid) (cond: CallCondition) (apiCallId: Guid) =
        match cond.Conditions |> Seq.tryFind (fun ac -> ac.Id = apiCallId) with
        | Some apiCall -> apiCall
        | None -> invalidOp $"ApiCall not found in condition. callId={callId}, condId={condId}, apiCallId={apiCallId}"

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
