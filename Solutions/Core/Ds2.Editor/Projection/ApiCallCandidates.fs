namespace Ds2.Editor

open System
open System.Collections.Generic
open Ds2.Core.Store

/// <summary>
/// Call condition 편집용 ApiCall 후보 projection.
/// 현 Call 의 ApiCalls + 같은 Flow 내 다른 Call 의 ApiCalls 를 그룹 라벨과 함께 수집.
/// </summary>
module ApiCallCandidates =

    [<Sealed>]
    type Candidate(name: string, apiCallId: Guid, groupLabel: string) =
        member _.Name = name
        member _.ApiCallId = apiCallId
        member _.GroupLabel = groupLabel

    let private groupLabelCurrentCall = "현재 Call"
    let private groupLabelFlowOther = "Flow 내"

    /// 주어진 Call 의 ApiCalls + 같은 Flow 의 다른 Call 의 ApiCalls 후보 (dedup).
    /// Call 이 없으면 빈 list.
    [<CompiledName("Collect")>]
    let collect (store: DsStore) (callId: Guid) : Candidate list =
        match store.Calls.TryGetValue(callId) with
        | false, _ -> []
        | true, call ->
            let result = ResizeArray<Candidate>()

            for ac in call.ApiCalls do
                result.Add(Candidate(ac.Name, ac.Id, groupLabelCurrentCall))

            match store.Works.TryGetValue(call.ParentId) with
            | false, _ -> ()
            | true, work ->
                match store.Flows.TryGetValue(work.ParentId) with
                | false, _ -> ()
                | true, flow ->
                    let seen = HashSet<Guid>(call.ApiCalls |> Seq.map (fun a -> a.Id))
                    for w in store.Works.Values do
                        if w.ParentId = flow.Id then
                            for c in store.Calls.Values do
                                if c.ParentId = w.Id && c.Id <> callId then
                                    for ac in c.ApiCalls do
                                        if seen.Add(ac.Id) then
                                            result.Add(
                                                Candidate(
                                                    sprintf "%s  (%s)" ac.Name c.Name,
                                                    ac.Id,
                                                    groupLabelFlowOther))

            result |> List.ofSeq
