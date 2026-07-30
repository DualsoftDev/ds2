module Ds2.Core.ConditionQueries

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store

// =============================================================================
// Condition 트리 평탄화 (Call/Work 공용)
// =============================================================================

let rec private allConditions (conditions: seq<Condition>) : seq<Condition> =
    seq {
        for condition in conditions do
            yield condition
            yield! allConditions condition.Children
    }

let private leafApiCallIds (conditions: seq<Condition>) : Guid seq =
    conditions
    |> allConditions
    |> Seq.collect (fun condition -> condition.ApiCalls |> Seq.map (fun apiCall -> apiCall.Id))

// =============================================================================
// Call 쪽 쿼리
// =============================================================================

let conditionTypesOfCall (call: Call) : ConditionType list =
    call.Conditions
    |> allConditions
    |> Seq.choose (fun condition -> condition.Type)
    |> Seq.distinct
    |> Seq.sort
    |> Seq.toList

let referencedApiCallIdsOfCall (call: Call) : Guid seq =
    seq {
        yield! call.ApiCalls |> Seq.map (fun apiCall -> apiCall.Id)
        yield! leafApiCallIds call.Conditions
    }

let containsApiCallReferenceInCall (apiCallId: Guid) (call: Call) : bool =
    referencedApiCallIdsOfCall call
    |> Seq.exists ((=) apiCallId)

[<CompiledName("GetCallConditionTypes")>]
let getCallConditionTypes (store: DsStore) (callId: Guid) : ConditionType list =
    match Queries.getCall callId store with
    | Some call -> conditionTypesOfCall call
    | None -> []

[<CompiledName("FindCallsByApiCallId")>]
let findCallsByApiCallId (store: DsStore) (apiCallId: Guid) : struct(Guid * string) list =
    store.CallsReadOnly.Values
    |> Seq.filter (containsApiCallReferenceInCall apiCallId)
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

// =============================================================================
// Work 쪽 쿼리 (Conditions 신규 추가에 대응)
// =============================================================================

let conditionTypesOfWork (work: Work) : ConditionType list =
    work.Conditions
    |> allConditions
    |> Seq.choose (fun condition -> condition.Type)
    |> Seq.distinct
    |> Seq.sort
    |> Seq.toList

let referencedApiCallIdsOfWork (work: Work) : Guid seq =
    leafApiCallIds work.Conditions

let containsApiCallReferenceInWork (apiCallId: Guid) (work: Work) : bool =
    referencedApiCallIdsOfWork work
    |> Seq.exists ((=) apiCallId)

[<CompiledName("GetWorkConditionTypes")>]
let getWorkConditionTypes (store: DsStore) (workId: Guid) : ConditionType list =
    match Queries.getWork workId store with
    | Some work -> conditionTypesOfWork work
    | None -> []

/// 캔버스 Work 노드의 조건 배지(SkipAction 등)용. Reference Work 는 원본의 조건을 따른다
/// — Properties 패널의 GetWorkConditionsForPanel 과 동일한 resolve 규칙이라야 패널 표시와 배지가 일치한다.
[<CompiledName("GetResolvedWorkConditionTypes")>]
let getResolvedWorkConditionTypes (store: DsStore) (workId: Guid) : ConditionType list =
    getWorkConditionTypes store (Queries.resolveOriginalWorkId workId store)

[<CompiledName("FindWorksByApiCallId")>]
let findWorksByApiCallId (store: DsStore) (apiCallId: Guid) : struct(Guid * string) list =
    store.WorksReadOnly.Values
    |> Seq.filter (containsApiCallReferenceInWork apiCallId)
    |> Seq.map (fun work -> struct(work.Id, work.Name))
    |> Seq.toList
