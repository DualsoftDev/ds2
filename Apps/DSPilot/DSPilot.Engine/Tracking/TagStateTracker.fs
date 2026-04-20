namespace DSPilot.Engine

open System
open DSPilot.Engine.Core

/// 태그 엣지 상태 (immutable)
[<CLIMutable>]
type TagEdgeState = {
    TagName: string
    PreviousValue: string
    CurrentValue: string
    LastUpdateTime: DateTime
    EdgeType: EdgeType
}

/// 태그 상태 추적기 모듈
module TagStateTracker =

    /// Edge detection helper
    let private detectEdge (prevValue: string option) (newValue: string) : EdgeType =
        match prevValue with
        | None -> EdgeType.NoChange  // First value, just initialize state
        | Some prev ->
            if prev = "0" && newValue = "1" then
                EdgeType.RisingEdge
            elif prev = "1" && newValue = "0" then
                EdgeType.FallingEdge
            else
                EdgeType.NoChange

    /// 빈 상태 맵
    let empty : Map<string, TagEdgeState> = Map.empty

    /// 태그 값 업데이트 및 새로운 상태 맵 반환 (immutable)
    let updateTagValue (tagName: string) (newValue: string) (stateMap: Map<string, TagEdgeState>) : Map<string, TagEdgeState> * TagEdgeState =
        match Map.tryFind tagName stateMap with
        | None ->
            // 첫 번째 업데이트
            let edgeType = detectEdge None newValue
            let newState = {
                TagName = tagName
                PreviousValue = "0"
                CurrentValue = newValue
                LastUpdateTime = DateTime.Now
                EdgeType = edgeType
            }
            (Map.add tagName newState stateMap, newState)

        | Some prevState ->
            // 기존 상태 업데이트
            let edgeType = detectEdge (Some prevState.CurrentValue) newValue
            let newState = {
                TagName = tagName
                PreviousValue = prevState.CurrentValue
                CurrentValue = newValue
                LastUpdateTime = DateTime.Now
                EdgeType = edgeType
            }
            (Map.add tagName newState stateMap, newState)

    /// 태그 상태 조회
    let getState (tagName: string) (stateMap: Map<string, TagEdgeState>) : TagEdgeState option =
        Map.tryFind tagName stateMap

    /// 추적 중인 태그 개수
    let count (stateMap: Map<string, TagEdgeState>) : int =
        Map.count stateMap

    /// 모든 태그 상태 목록
    let getAllStates (stateMap: Map<string, TagEdgeState>) : TagEdgeState list =
        stateMap |> Map.toList |> List.map snd

    /// Rising edge 태그만 필터링
    let getRisingEdgeTags (stateMap: Map<string, TagEdgeState>) : string list =
        stateMap
        |> Map.filter (fun _ state -> state.EdgeType = EdgeType.RisingEdge)
        |> Map.toList
        |> List.map fst

    /// Falling edge 태그만 필터링
    let getFallingEdgeTags (stateMap: Map<string, TagEdgeState>) : string list =
        stateMap
        |> Map.filter (fun _ state -> state.EdgeType = EdgeType.FallingEdge)
        |> Map.toList
        |> List.map fst


/// C# 호환 mutable wrapper
type TagStateTrackerMutable() =
    let mutable stateMap = TagStateTracker.empty

    /// 태그 값 업데이트 및 엣지 상태 반환
    member this.UpdateTagValue(tagName: string, newValue: string) : TagEdgeState =
        let (newMap, newState) = TagStateTracker.updateTagValue tagName newValue stateMap
        stateMap <- newMap
        newState

    /// 태그 상태 조회
    member this.GetState(tagName: string) : TagEdgeState option =
        TagStateTracker.getState tagName stateMap

    /// 모든 태그 상태 초기화
    member this.Reset() : unit =
        stateMap <- TagStateTracker.empty

    /// 추적 중인 태그 개수
    member this.TrackedTagCount : int =
        TagStateTracker.count stateMap

    /// 모든 태그 상태 목록
    member this.GetAllStates() : TagEdgeState list =
        TagStateTracker.getAllStates stateMap

    /// Rising edge 태그만 반환
    member this.GetRisingEdgeTags() : string list =
        TagStateTracker.getRisingEdgeTags stateMap

    /// Falling edge 태그만 반환
    member this.GetFallingEdgeTags() : string list =
        TagStateTracker.getFallingEdgeTags stateMap
