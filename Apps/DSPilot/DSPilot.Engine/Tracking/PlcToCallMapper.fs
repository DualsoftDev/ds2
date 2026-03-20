namespace DSPilot.Engine

open System
open System.Collections.Generic
open Microsoft.Extensions.Logging
open Ds2.Core
open Ds2.UI.Core

/// PLC 태그와 Call 매핑 정보
type CallMappingInfo = {
    Call: Call
    ApiCall: ApiCall
    IsInTag: bool
    FlowName: string
}

/// 태그 매칭 모드
type TagMatchMode =
    | ByAddress
    | ByName

/// PLC 태그와 Call 매핑 서비스
module PlcToCallMapper =

    /// 태그 키 추출 (Address 또는 Name)
    let private getTagKey (mode: TagMatchMode) (tag: IOTag) : string option =
        match mode with
        | ByAddress ->
            if String.IsNullOrEmpty(tag.Address) then None else Some tag.Address
        | ByName ->
            if String.IsNullOrEmpty(tag.Name) then None else Some tag.Name

    /// AASX에서 모든 Flow/Work/Call/ApiCall을 순회하여 태그 매핑 구축
    let initialize (store: DsStore) (tagMatchMode: TagMatchMode) (logger: ILogger) : Map<string, CallMappingInfo> * Map<Guid, string option * string option> =
        logger.LogInformation("Initializing PlcToCallMapper with TagMatchMode: {Mode}", tagMatchMode)

        let mutable tagToCallMap = Map.empty<string, CallMappingInfo>
        let mutable callTagMap = Map.empty<Guid, string option * string option>

        // DsQuery를 사용하여 모든 Flow 조회
        let allFlows = DsQuery.allFlows store

        for flow in allFlows do
            // DsQuery를 사용하여 Work 조회
            let works = DsQuery.worksOf flow.Id store

            for work in works do
                // DsQuery를 사용하여 Call 조회
                let calls = DsQuery.callsOf work.Id store

                for call in calls do
                    let mutable inTagKey: string option = None
                    let mutable outTagKey: string option = None

                    // Call의 모든 ApiCall 순회
                    for apiCall in call.ApiCalls do
                        // InTag 처리
                        match apiCall.InTag with
                        | Some inTag ->
                            match getTagKey tagMatchMode inTag with
                            | Some tagKey ->
                                inTagKey <- Some tagKey
                                let mapping = {
                                    Call = call
                                    ApiCall = apiCall
                                    IsInTag = true
                                    FlowName = flow.Name
                                }
                                tagToCallMap <- tagToCallMap.Add(tagKey, mapping)
                                logger.LogDebug("Mapped InTag '{TagKey}' → Call '{CallName}' (Flow: {FlowName})",
                                    tagKey, call.Name, flow.Name)
                            | None -> ()
                        | None -> ()

                        // OutTag 처리
                        match apiCall.OutTag with
                        | Some outTag ->
                            match getTagKey tagMatchMode outTag with
                            | Some tagKey ->
                                outTagKey <- Some tagKey
                                let mapping = {
                                    Call = call
                                    ApiCall = apiCall
                                    IsInTag = false
                                    FlowName = flow.Name
                                }
                                tagToCallMap <- tagToCallMap.Add(tagKey, mapping)
                                logger.LogDebug("Mapped OutTag '{TagKey}' → Call '{CallName}' (Flow: {FlowName})",
                                    tagKey, call.Name, flow.Name)
                            | None -> ()
                        | None -> ()

                    // call.Id (Guid)를 키로 사용
                    callTagMap <- callTagMap.Add(call.Id, (inTagKey, outTagKey))

        logger.LogInformation(
            "PlcToCallMapper initialized: {FlowCount} flows, {TagCount} tags mapped",
            allFlows.Length, tagToCallMap.Count)

        if tagToCallMap.Count > 0 then
            logger.LogDebug("Mapped {Count} tags", tagToCallMap.Count)
        else
            logger.LogWarning("No tags were mapped! Please check that AASX ApiCall InTag/OutTag names match PLC tag names.")

        (tagToCallMap, callTagMap)

    /// PLC 태그로 Call 찾기
    let findCallByTag (tagMatchMode: TagMatchMode) (tagName: string) (tagAddress: string)
                      (tagToCallMap: Map<string, CallMappingInfo>) : CallMappingInfo option =
        let tagKey =
            match tagMatchMode with
            | ByAddress -> tagAddress
            | ByName -> tagName

        tagToCallMap.TryFind(tagKey)

    /// Call의 InTag/OutTag 조회 (Call.Id 기반)
    let getCallTags (callId: Guid) (callTagMap: Map<Guid, string option * string option>) : (string option * string option) option =
        callTagMap.TryFind(callId)

    /// PLC 태그 목록으로 유효성 검증
    let validateWithPlcTags (plcTagKeys: HashSet<string>)
                            (logger: ILogger)
                            (tagToCallMap: Map<string, CallMappingInfo>)
                            (callTagMap: Map<Guid, string option * string option>)
                            : Map<string, CallMappingInfo> * Map<Guid, string option * string option> =
        let mutable newTagToCallMap = tagToCallMap
        let mutable newCallTagMap = callTagMap
        let mutable invalidCount = 0

        for KeyValue(callId, (inTag, outTag)) in callTagMap do
            match inTag with
            | Some inTagStr when not (plcTagKeys.Contains(inTagStr)) ->
                newCallTagMap <- newCallTagMap.Add(callId, (None, outTag))
                newTagToCallMap <- newTagToCallMap.Remove(inTagStr)
                invalidCount <- invalidCount + 1
                logger.LogWarning(
                    "Call ID '{CallId}': InTag '{InTag}' not found in PLC tags. Removed (OutTag Falling will trigger Finish).",
                    callId, inTagStr)
            | _ -> ()

        if invalidCount > 0 then
            logger.LogInformation(
                "Validated tag mappings: {InvalidCount} InTag(s) removed (not in PLC), {RemainingTags} tags remain",
                invalidCount, newTagToCallMap.Count)

        (newTagToCallMap, newCallTagMap)

    /// Call 상태 전이 결정
    let determineCallState (tagMatchMode: TagMatchMode)
                          (tagName: string)
                          (tagAddress: string)
                          (edgeState: TagEdgeState)
                          (currentCallState: string)
                          (tagToCallMap: Map<string, CallMappingInfo>)
                          (callTagMap: Map<Guid, string option * string option>)
                          (logger: ILogger)
                          : string * bool =
        let tagKey =
            match tagMatchMode with
            | ByAddress -> tagAddress
            | ByName -> tagName

        match tagToCallMap.TryFind(tagKey) with
        | Some mapping ->
            let call = mapping.Call
            let isInTag = mapping.IsInTag

            // call.Id로 조회
            let hasInTag =
                match callTagMap.TryFind(call.Id) with
                | Some (Some _, _) -> true
                | _ -> false

            // StateTransition 사용
            let (newState, stateChanged) =
                StateTransition.tryTransition currentCallState isInTag hasInTag edgeState.EdgeType

            if stateChanged then
                let newStateStr = StateTransition.stateToString newState
                logger.LogInformation(
                    "Call '{CallName}' (Flow: {FlowName}): State transition {OldState} → {NewState} (Tag: {TagName}, Edge: {EdgeType})",
                    call.Name, mapping.FlowName, currentCallState, newStateStr, tagName, edgeState.EdgeType)
                (newStateStr, true)
            else
                (currentCallState, false)
        | None ->
            (currentCallState, false)
