namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Store
open Ds2.Store.DsQuery

// ─── Internal helpers shared by all Panel extensions ─────────────────

module internal DirectPanelOps =
    let private tagName (tagOpt: IOTag option) =
        tagOpt |> Option.map (fun tag -> tag.Name) |> Option.defaultValue ""

    let private tagAddress (tagOpt: IOTag option) =
        tagOpt |> Option.map (fun tag -> tag.Address) |> Option.defaultValue ""

    let resolveApiDefDisplay (store: DsStore) (apiDefIdOpt: Guid option) : Guid option * string =
        match apiDefIdOpt |> Option.bind (fun id -> Queries.getApiDef id store) with
        | Some apiDef ->
            let deviceName =
                Queries.getSystem apiDef.ParentId store
                |> Option.map (fun system -> system.Name)
                |> Option.defaultValue "UnknownDevice"
            Some apiDef.Id, $"{deviceName}.{apiDef.Name}"
        | None ->
            None, "(unlinked)"

    let toCallApiCallPanelItem (store: DsStore) (apiCall: ApiCall) : CallApiCallPanelItem =
        let apiDefId, apiDefDisplayName = resolveApiDefDisplay store apiCall.ApiDefId
        CallApiCallPanelItem(
            apiCall.Id, apiCall.Name, apiDefId, apiDefDisplayName,
            tagName apiCall.OutTag, tagAddress apiCall.OutTag,
            tagName apiCall.InTag, tagAddress apiCall.InTag,
            PropertyPanelValueSpec.format apiCall.OutputSpec,
            PropertyPanelValueSpec.format apiCall.InputSpec,
            PropertyPanelValueSpec.dataTypeIndex apiCall.OutputSpec,
            PropertyPanelValueSpec.dataTypeIndex apiCall.InputSpec)

    let toConditionApiCallItem (store: DsStore) (apiCall: ApiCall) : CallConditionApiCallItem =
        let _, displayName = resolveApiDefDisplay store apiCall.ApiDefId
        CallConditionApiCallItem(
            apiCall.Id, apiCall.Name, displayName,
            PropertyPanelValueSpec.format apiCall.OutputSpec,
            PropertyPanelValueSpec.dataTypeIndex apiCall.OutputSpec,
            PropertyPanelValueSpec.format apiCall.InputSpec,
            PropertyPanelValueSpec.dataTypeIndex apiCall.InputSpec)

    let buildApiCall
        (apiDef: ApiDef) (fallbackName: string) (apiCallNameOpt: string option)
        (outputTagName: string) (outputAddress: string)
        (inputTagName: string) (inputAddress: string)
        (apiCallId: Guid option)
        (inputSpec: ValueSpec) (outputSpec: ValueSpec)
        : ApiCall =
        let resolvedName =
            match apiCallNameOpt with
            | Some apiCallName when not (String.IsNullOrWhiteSpace(apiCallName)) -> apiCallName.Trim()
            | _ -> fallbackName
        let apiCall = ApiCall(resolvedName)
        apiCall.ApiDefId <- Some apiDef.Id
        match apiCallId with
        | Some id -> apiCall.Id <- id
        | None -> ()
        let hasOut = not (String.IsNullOrWhiteSpace(outputAddress)) || not (String.IsNullOrWhiteSpace(outputTagName))
        let hasIn  = not (String.IsNullOrWhiteSpace(inputAddress))  || not (String.IsNullOrWhiteSpace(inputTagName))
        if hasOut then
            apiCall.OutTag <- Some(IOTag((if isNull outputTagName then "" else outputTagName.Trim()), (if isNull outputAddress then "" else outputAddress.Trim()), ""))
        if hasIn then
            apiCall.InTag <- Some(IOTag((if isNull inputTagName then "" else inputTagName.Trim()), (if isNull inputAddress then "" else inputAddress.Trim()), ""))
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

    let tryFindConditionRec (conditions: ResizeArray<CallCondition>) (condId: Guid) : CallCondition option =
        Queries.tryFindConditionRec conditions condId

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
        match Queries.getCall callId store with
        | Some call -> mapCall call
        | None ->
            StoreLog.warn($"Call not found. id={callId}")
            []

    let mutateCallProps (store: DsStore) callId label (mutate: Call -> unit) =
        withTransactionCallProps store callId label (fun () ->
            store.TrackMutate(store.Calls, callId, mutate))

    let toOpt (s: string) =
        if System.String.IsNullOrEmpty(s) then None else Some s

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

module internal PanelMutationOps =
    let updateWorkIfChanged
        (store: DsStore)
        workId
        label
        (changedEvent: Guid -> EditorEvent)
        (getCurrent: Work -> 'T)
        nextValue
        (applyValue: Work -> 'T -> unit)
        =
        let work = StoreLog.requireWork(store, workId)
        if getCurrent work <> nextValue then
            store.WithTransaction(label, fun () ->
                store.TrackMutate(store.Works, workId, fun current -> applyValue current nextValue))
            store.EmitAndHistory(changedEvent workId)

    let updateCallIfChanged
        (store: DsStore)
        callId
        label
        (changedEvent: Guid -> EditorEvent)
        (getCurrent: Call -> 'T)
        nextValue
        (applyValue: Call -> 'T -> unit)
        =
        let call = StoreLog.requireCall(store, callId)
        if getCurrent call <> nextValue then
            store.WithTransaction(label, fun () ->
                store.TrackMutate(store.Calls, callId, fun current -> applyValue current nextValue))
            store.EmitAndHistory(changedEvent callId)

[<Extension>]
type DsStorePanelTimeExtensions =
    [<Extension>]
    static member GetWorkPeriodMs(store: DsStore, workId: Guid) : int option =
        let resolvedId = Queries.resolveOriginalWorkId workId store
        PanelTimeOps.readMs Queries.getWork (fun w -> w.Properties.Duration) EntityKind.Work store resolvedId

    [<Extension>]
    static member GetWorkPeriodMsOrNull(store: DsStore, workId: Guid) : Nullable<int> =
        DsStorePanelTimeExtensions.GetWorkPeriodMs(store, workId)
        |> Option.toNullable

    [<Extension>]
    static member GetCallTimeoutMs(store: DsStore, callId: Guid) : int option =
        PanelTimeOps.readMs Queries.getCall (fun c -> c.Properties.Timeout) EntityKind.Call store callId

    [<Extension>]
    static member GetCallTimeoutMsOrNull(store: DsStore, callId: Guid) : Nullable<int> =
        DsStorePanelTimeExtensions.GetCallTimeoutMs(store, callId)
        |> Option.toNullable

    [<Extension>]
    static member UpdateWorkPeriodMs(store: DsStore, workId: Guid, periodMs: int option) =
        let resolvedId = Queries.resolveOriginalWorkId workId store
        StoreLog.debug($"workId={workId}, resolvedId={resolvedId}, periodMs={periodMs}")
        let period = PanelTimeOps.fromMs periodMs
        PanelMutationOps.updateWorkIfChanged
            store
            resolvedId
            "Work 속성 변경"
            WorkPropsChanged
            (fun work -> work.Properties.Duration)
            period
            (fun work value -> work.Properties.Duration <- value)

    [<Extension>]
    static member UpdateWorkPeriodMs(store: DsStore, workId: Guid, periodMs: Nullable<int>) =
        DsStorePanelTimeExtensions.UpdateWorkPeriodMs(store, workId, Option.ofNullable periodMs)

    [<Extension>]
    static member GetWorkIsFinished(store: DsStore, workId: Guid) : bool =
        let resolvedId = Queries.resolveOriginalWorkId workId store
        match Queries.getWork resolvedId store with
        | Some work -> work.Properties.IsFinished
        | None -> false

    [<Extension>]
    static member UpdateWorkIsFinished(store: DsStore, workId: Guid, isFinished: bool) =
        let resolvedId = Queries.resolveOriginalWorkId workId store
        PanelMutationOps.updateWorkIfChanged
            store
            resolvedId
            "Work IsFinished 변경"
            WorkPropsChanged
            (fun work -> work.Properties.IsFinished)
            isFinished
            (fun work value -> work.Properties.IsFinished <- value)

    [<Extension>]
    static member UpdateWorkTokenRole(store: DsStore, workId: Guid, role: TokenRole) =
        StoreLog.debug($"workId={workId}, role={role}")
        PanelMutationOps.updateWorkIfChanged
            store
            workId
            "Work TokenRole 변경"
            WorkPropsChanged
            (fun work -> work.TokenRole)
            role
            (fun work value -> work.TokenRole <- value)

    [<Extension>]
    static member UpdateCallTimeoutMs(store: DsStore, callId: Guid, timeoutMs: int option) =
        StoreLog.debug($"callId={callId}, timeoutMs={timeoutMs}")
        let timeout = PanelTimeOps.fromMs timeoutMs
        PanelMutationOps.updateCallIfChanged
            store
            callId
            "Call 속성 변경"
            CallPropsChanged
            (fun call -> call.Properties.Timeout)
            timeout
            (fun call value -> call.Properties.Timeout <- value)

    [<Extension>]
    static member UpdateCallTimeoutMs(store: DsStore, callId: Guid, timeoutMs: Nullable<int>) =
        DsStorePanelTimeExtensions.UpdateCallTimeoutMs(store, callId, Option.ofNullable timeoutMs)

// ─── TokenSpec ───────────────────────────────────────────────────────

[<Extension>]
type DsStorePanelTokenSpecExtensions =
    [<Extension>]
    static member UpdateTokenSpecs(store: DsStore, specs: TokenSpec seq) =
        let specList = specs |> Seq.toList
        StoreLog.debug($"count={specList.Length}")
        let project = Queries.allProjects store |> List.head
        store.WithTransaction("TokenSpec 변경", fun () ->
            store.TrackMutate(store.Projects, project.Id, fun p ->
                p.TokenSpecs.Clear()
                for spec in specList do p.TokenSpecs.Add(spec)))
        store.EmitAndHistory(ProjectPropsChanged project.Id)

// ─── Conditions & Properties: see Panel.Condition.fs, Panel.Properties.fs ───
