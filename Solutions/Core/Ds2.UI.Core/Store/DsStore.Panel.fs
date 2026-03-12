namespace Ds2.UI.Core

open System
open System.Runtime.CompilerServices
open Ds2.Core

// ─── Internal helpers shared by all Panel extensions ─────────────────

module internal DirectPanelOps =
    let private tagAddress (tagOpt: IOTag option) =
        tagOpt |> Option.map (fun tag -> tag.Address) |> Option.defaultValue ""

    let resolveApiDefDisplay (store: DsStore) (apiDefIdOpt: Guid option) : Guid option * string =
        match apiDefIdOpt |> Option.bind (fun id -> DsQuery.getApiDef id store) with
        | Some apiDef ->
            let deviceName =
                DsQuery.getSystem apiDef.ParentId store
                |> Option.map (fun system -> system.Name)
                |> Option.defaultValue "UnknownDevice"
            Some apiDef.Id, $"{deviceName}.{apiDef.Name}"
        | None ->
            None, "(unlinked)"

    let toCallApiCallPanelItem (store: DsStore) (apiCall: ApiCall) : CallApiCallPanelItem =
        let apiDefId, apiDefDisplayName = resolveApiDefDisplay store apiCall.ApiDefId
        CallApiCallPanelItem(
            apiCall.Id, apiCall.Name, apiDefId, apiDefDisplayName,
            tagAddress apiCall.OutTag, tagAddress apiCall.InTag,
            PropertyPanelValueSpec.format apiCall.OutputSpec,
            PropertyPanelValueSpec.format apiCall.InputSpec,
            PropertyPanelValueSpec.dataTypeIndex apiCall.OutputSpec,
            PropertyPanelValueSpec.dataTypeIndex apiCall.InputSpec)

    let toConditionApiCallItem (store: DsStore) (apiCall: ApiCall) : CallConditionApiCallItem =
        let _, displayName = resolveApiDefDisplay store apiCall.ApiDefId
        CallConditionApiCallItem(
            apiCall.Id, apiCall.Name, displayName,
            PropertyPanelValueSpec.format apiCall.OutputSpec,
            PropertyPanelValueSpec.dataTypeIndex apiCall.OutputSpec)

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
        match apiCallId with
        | Some id -> apiCall.Id <- id
        | None -> ()
        if not (String.IsNullOrWhiteSpace(outputAddress)) then
            apiCall.OutTag <- Some(IOTag("Out", outputAddress.Trim(), ""))
        if not (String.IsNullOrWhiteSpace(inputAddress)) then
            apiCall.InTag <- Some(IOTag("In", inputAddress.Trim(), ""))
        apiCall.InputSpec <- inputSpec
        apiCall.OutputSpec <- outputSpec
        apiCall

    let withTransactionCallProps (store: DsStore) callId label (action: unit -> unit) =
        store.WithTransaction(label, action)
        store.EmitAndHistory(CallPropsChanged callId)

    let addApiCallToStore (store: DsStore) (call: Call) (apiCall: ApiCall) =
        store.TrackAdd(store.ApiCalls, apiCall)
        store.TrackMutate(store.Calls, call.Id, fun current -> current.ApiCalls.Add(apiCall))

    let removeApiCallFromStore (store: DsStore) (call: Call) (apiCallId: Guid) =
        store.TrackMutate(store.Calls, call.Id, fun current -> current.ApiCalls.RemoveAll(fun apiCall -> apiCall.Id = apiCallId) |> ignore)
        store.TrackRemove(store.ApiCalls, apiCallId)

    let rec tryFindConditionRec (conditions: ResizeArray<CallCondition>) (condId: Guid) : CallCondition option =
        conditions
        |> Seq.tryPick (fun condition ->
            if condition.Id = condId then Some condition
            else tryFindConditionRec condition.Children condId)

    let tryFindCondition (call: Call) (condId: Guid) =
        tryFindConditionRec call.CallConditions condId

    let requireCondition (callId: Guid) (call: Call) (condId: Guid) =
        match tryFindCondition call condId with
        | Some condition -> condition
        | None -> invalidOp $"CallCondition not found. callId={callId}, condId={condId}"

    let requireApiCallInCondition (callId: Guid) (condId: Guid) (condition: CallCondition) (apiCallId: Guid) =
        match condition.Conditions |> Seq.tryFind (fun apiCall -> apiCall.Id = apiCallId) with
        | Some apiCall -> apiCall
        | None -> invalidOp $"ApiCall not found in condition. callId={callId}, condId={condId}, apiCallId={apiCallId}"

    let withCallOrEmpty (store: DsStore) (callId: Guid) (mapCall: Call -> 'T list) : 'T list =
        match DsQuery.getCall callId store with
        | Some call -> mapCall call
        | None ->
            StoreLog.warn($"Call not found. id={callId}")
            []

    let mutateCallProps (store: DsStore) callId label (mutate: Call -> unit) =
        withTransactionCallProps store callId label (fun () ->
            store.TrackMutate(store.Calls, callId, mutate))

// ─── Time (Period / Timeout) ─────────────────────────────────────────

module internal PanelTimeOps =
    let getMs (tsOpt: TimeSpan option) : int option =
        tsOpt |> Option.map (fun t -> int t.TotalMilliseconds)

    let fromMs (ms: int option) : TimeSpan option =
        ms |> Option.map (fun m -> TimeSpan.FromMilliseconds(float m))

    let readMs (query: Guid -> DsStore -> 'T option) (getProp: 'T -> TimeSpan option) (entityKind: EntityKind) (store: DsStore) (id: Guid) : int option =
        query id store
        |> Option.bind (fun entity -> getProp entity |> getMs)
        |> Option.orElseWith (fun () -> StoreLog.warn($"{entityKind} not found. id={id}"); None)

[<Extension>]
type DsStorePanelTimeExtensions =
    [<Extension>]
    static member GetWorkPeriodMs(store: DsStore, workId: Guid) : int option =
        PanelTimeOps.readMs DsQuery.getWork (fun w -> w.Properties.Period) EntityKind.Work store workId

    [<Extension>]
    static member GetWorkPeriodMsOrNull(store: DsStore, workId: Guid) : Nullable<int> =
        DsStorePanelTimeExtensions.GetWorkPeriodMs(store, workId)
        |> Option.toNullable

    [<Extension>]
    static member GetCallTimeoutMs(store: DsStore, callId: Guid) : int option =
        PanelTimeOps.readMs DsQuery.getCall (fun c -> c.Properties.Timeout) EntityKind.Call store callId

    [<Extension>]
    static member GetCallTimeoutMsOrNull(store: DsStore, callId: Guid) : Nullable<int> =
        DsStorePanelTimeExtensions.GetCallTimeoutMs(store, callId)
        |> Option.toNullable

    [<Extension>]
    static member UpdateWorkPeriodMs(store: DsStore, workId: Guid, periodMs: int option) =
        StoreLog.debug($"workId={workId}, periodMs={periodMs}")
        let work = StoreLog.requireWork(store, workId)
        let period = PanelTimeOps.fromMs periodMs
        if work.Properties.Period <> period then
            store.WithTransaction("Work 속성 변경", fun () ->
                store.TrackMutate(store.Works, workId, fun w -> w.Properties.Period <- period))
            store.EmitAndHistory(WorkPropsChanged workId)

    [<Extension>]
    static member UpdateWorkPeriodMs(store: DsStore, workId: Guid, periodMs: Nullable<int>) =
        DsStorePanelTimeExtensions.UpdateWorkPeriodMs(store, workId, Option.ofNullable periodMs)

    [<Extension>]
    static member UpdateCallTimeoutMs(store: DsStore, callId: Guid, timeoutMs: int option) =
        StoreLog.debug($"callId={callId}, timeoutMs={timeoutMs}")
        let call = StoreLog.requireCall(store, callId)
        let timeout = PanelTimeOps.fromMs timeoutMs
        if call.Properties.Timeout <> timeout then
            store.WithTransaction("Call 속성 변경", fun () ->
                store.TrackMutate(store.Calls, callId, fun c -> c.Properties.Timeout <- timeout))
            store.EmitAndHistory(CallPropsChanged callId)

    [<Extension>]
    static member UpdateCallTimeoutMs(store: DsStore, callId: Guid, timeoutMs: Nullable<int>) =
        DsStorePanelTimeExtensions.UpdateCallTimeoutMs(store, callId, Option.ofNullable timeoutMs)

// ─── Conditions ──────────────────────────────────────────────────────

[<Extension>]
type DsStorePanelConditionExtensions =
    [<Extension>]
    static member GetCallConditionsForPanel(store: DsStore, callId: Guid) : CallConditionPanelItem list =
        let rec toPanel (cond: CallCondition) : CallConditionPanelItem =
            let items = cond.Conditions |> Seq.map (DirectPanelOps.toConditionApiCallItem store) |> Seq.toList
            let children = cond.Children |> Seq.map toPanel |> Seq.toList
            CallConditionPanelItem(
                cond.Id,
                (cond.Type |> Option.defaultValue CallConditionType.Auto),
                cond.IsOR, cond.IsRising, items, children)
        DirectPanelOps.withCallOrEmpty store callId (fun call ->
            call.CallConditions |> Seq.map toPanel |> Seq.toList)

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
        let rec removeRec (list: ResizeArray<CallCondition>) =
            if list.RemoveAll(fun cc -> cc.Id = conditionId) = 0 then
                list |> Seq.iter (fun cc -> removeRec cc.Children)
        DirectPanelOps.mutateCallProps store callId "조건 삭제" (fun c -> removeRec c.CallConditions)

    /// 기존 조건 안에 하위 조건 추가
    [<Extension>]
    static member AddChildCondition(store: DsStore, callId: Guid, parentCondId: Guid, isOR: bool) =
        StoreLog.debug($"callId={callId}, parentCondId={parentCondId}, isOR={isOR}")
        StoreLog.requireCallCondition(store, callId, parentCondId) |> ignore
        let child = CallCondition(IsOR = isOR)
        DirectPanelOps.mutateCallProps store callId "하위 조건 추가" (fun c ->
            let parent = DirectPanelOps.requireCondition callId c parentCondId
            parent.Children.Add(child))

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
                let cond = DirectPanelOps.requireCondition callId c condId
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
            let targetCond = DirectPanelOps.requireCondition callId c condId
            targetCond.Conditions.RemoveAll(fun ac -> ac.Id = apiCallId) |> ignore)

    [<Extension>]
    static member UpdateConditionApiCallOutputSpec(store: DsStore, callId: Guid, condId: Guid, apiCallId: Guid, outTypeIndex: int, outText: string) : bool =
        StoreLog.debug($"callId={callId}, condId={condId}, apiCallId={apiCallId}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        let ac = StoreLog.requireApiCallInCondition(cond, apiCallId)
        let newSpec = PropertyPanelValueSpec.parseFromPanel outTypeIndex outText
        if ac.OutputSpec <> newSpec then
            DirectPanelOps.mutateCallProps store callId "조건 OutputSpec 변경" (fun c ->
                let targetCond = DirectPanelOps.requireCondition callId c condId
                let targetApiCall = DirectPanelOps.requireApiCallInCondition callId condId targetCond apiCallId
                targetApiCall.OutputSpec <- newSpec)
            true
        else false
