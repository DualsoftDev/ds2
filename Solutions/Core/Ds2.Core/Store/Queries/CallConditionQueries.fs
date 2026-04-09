module Ds2.Core.CallConditionQueries

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store

let rec private allConditions (conditions: seq<CallCondition>) : seq<CallCondition> =
    seq {
        for condition in conditions do
            yield condition
            yield! allConditions condition.Children
    }

let conditionTypes (call: Call) : CallConditionType list =
    call.CallConditions
    |> allConditions
    |> Seq.choose (fun condition -> condition.Type)
    |> Seq.distinct
    |> Seq.sort
    |> Seq.toList

let referencedApiCallIds (call: Call) : Guid seq =
    seq {
        yield! call.ApiCalls |> Seq.map (fun apiCall -> apiCall.Id)
        yield!
            call.CallConditions
            |> allConditions
            |> Seq.collect (fun condition -> condition.Conditions |> Seq.map (fun apiCall -> apiCall.Id))
    }

let containsApiCallReference (apiCallId: Guid) (call: Call) : bool =
    referencedApiCallIds call
    |> Seq.exists ((=) apiCallId)

[<CompiledName("GetCallConditionTypes")>]
let getCallConditionTypes (store: DsStore) (callId: Guid) : CallConditionType list =
    match Queries.getCall callId store with
    | Some call -> conditionTypes call
    | None -> []

[<CompiledName("FindCallsByApiCallId")>]
let findCallsByApiCallId (store: DsStore) (apiCallId: Guid) : struct(Guid * string) list =
    store.CallsReadOnly.Values
    |> Seq.filter (containsApiCallReference apiCallId)
    |> Seq.map (fun call -> struct(call.Id, call.Name))
    |> Seq.toList

/// ApiCall을 직접 소유한 Call(=ApiCalls 리스트에 들어있는 Call)을 찾아 반환.
/// ApiCall은 정확히 1개의 Call에 소속되므로 보통 0 또는 1개의 결과.
[<CompiledName("FindOwnerCallByApiCallId")>]
let findOwnerCallByApiCallId (store: DsStore) (apiCallId: Guid) : struct(Guid * string) list =
    store.CallsReadOnly.Values
    |> Seq.filter (fun call -> call.ApiCalls |> Seq.exists (fun ac -> ac.Id = apiCallId))
    |> Seq.map (fun call -> struct(call.Id, call.Name))
    |> Seq.toList
