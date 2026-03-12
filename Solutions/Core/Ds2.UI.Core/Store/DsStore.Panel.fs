namespace Ds2.UI.Core

open System
open Ds2.Core

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
