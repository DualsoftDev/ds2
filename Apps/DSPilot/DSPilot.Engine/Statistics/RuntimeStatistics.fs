namespace DSPilot.Engine

open System
open Ds2.Core
open Ds2.Core.LoggingHelpers

/// Runtime 통계 추적 모듈 — Ds2.Core 타입 사용
module RuntimeStatisticsTracker =

    let empty (baseCount: int) : CallExecutionState =
        { StartTime = None; History = []; SessionCount = 0; BaseCount = baseCount }

    let recordStart (state: CallExecutionState) (startTime: DateTime) : CallExecutionState =
        { state with StartTime = Some startTime }

    let recordFinish (state: CallExecutionState) (finishTime: DateTime) : (CallExecutionState * RuntimeStatistics option) =
        match state.StartTime with
        | None -> (state, None)
        | Some start ->
            let goingTime = int (finishTime - start).TotalMilliseconds
            let (average, stdDev, _, updatedSamples) = calculateWindowStatistics state.History goingTime
            let newSessionCount = state.SessionCount + 1

            let newState = {
                StartTime = None
                History = updatedSamples
                SessionCount = newSessionCount
                BaseCount = state.BaseCount
            }

            let stats = {
                GoingTime = goingTime
                Average = average
                StdDev = stdDev
                SessionCount = newSessionCount
                BaseCount = state.BaseCount
                TotalCount = state.BaseCount + newSessionCount
            }

            (newState, Some stats)

    let getBaseCount (state: CallExecutionState) : int = state.BaseCount
    let getSessionCount (state: CallExecutionState) : int = state.SessionCount
    let getTotalCount (state: CallExecutionState) : int = state.BaseCount + state.SessionCount
    let getHistory (state: CallExecutionState) : int list = state.History

    let resetSession (state: CallExecutionState) : CallExecutionState =
        { StartTime = None; History = []; SessionCount = 0; BaseCount = state.BaseCount }


/// C# 호환 mutable wrapper
type RuntimeStatisticsTrackerMutable() =
    let mutable stateMap : Map<string, CallExecutionState> = Map.empty

    member this.RecordStart(callName: string, baseCount: int) : unit =
        let currentState =
            match Map.tryFind callName stateMap with
            | Some state -> state
            | None -> RuntimeStatisticsTracker.empty baseCount
        let newState = RuntimeStatisticsTracker.recordStart currentState DateTime.Now
        stateMap <- Map.add callName newState stateMap

    member this.RecordFinish(callName: string) : RuntimeStatistics option =
        match Map.tryFind callName stateMap with
        | None -> None
        | Some state ->
            let (newState, stats) = RuntimeStatisticsTracker.recordFinish state DateTime.Now
            stateMap <- Map.add callName newState stateMap
            stats

    member this.ResetSession(callName: string) : unit =
        match Map.tryFind callName stateMap with
        | Some state ->
            let newState = RuntimeStatisticsTracker.resetSession state
            stateMap <- Map.add callName newState stateMap
        | None -> ()

    member this.ResetAllSessions() : unit =
        stateMap <- stateMap |> Map.map (fun _ state -> RuntimeStatisticsTracker.resetSession state)

    member this.TrackedCallCount : int = Map.count stateMap

    member this.GetTotalCount(callName: string) : int =
        stateMap |> Map.tryFind callName
        |> Option.map RuntimeStatisticsTracker.getTotalCount
        |> Option.defaultValue 0

    member this.GetSessionCount(callName: string) : int =
        stateMap |> Map.tryFind callName
        |> Option.map RuntimeStatisticsTracker.getSessionCount
        |> Option.defaultValue 0
