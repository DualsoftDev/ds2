module Ds2.Store.CallConditionQueries

open System
open Ds2.Core

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
