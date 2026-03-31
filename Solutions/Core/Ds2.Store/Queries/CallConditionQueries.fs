module Ds2.Store.CallConditionQueries

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Store.DsQuery

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
