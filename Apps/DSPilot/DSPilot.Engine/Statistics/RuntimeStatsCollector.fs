namespace DSPilot.Engine.Stats

open System
open Ds2.Core
open Ds2.Core.LoggingHelpers

/// Runtime Statistics Collector using Welford's Incremental Stats
/// Delegates to Ds2.Core.LoggingHelpers.CallStatsCollector
module RuntimeStatsCollector =

    /// Per-Call statistics state (Ds2.Core.CallStatsState 사용)
    type CallStatsState = Ds2.Core.CallStatsState

    let empty = CallStatsCollector.empty

    let recordStart (state: CallStatsState) (timestamp: DateTime) : CallStatsState =
        CallStatsCollector.recordStart timestamp state

    let recordFinish (state: CallStatsState) (timestamp: DateTime) : CallStatsState * float option =
        CallStatsCollector.recordFinish timestamp state

    let getStats (state: CallStatsState) : IncrementalStatsResult =
        CallStatsCollector.getStats state


/// Mutable wrapper for C# compatibility
type RuntimeStatsCollectorMutable() =
    let mutable stateMap : Map<string, Ds2.Core.CallStatsState> = Map.empty

    member this.RecordStart(callName: string, timestamp: DateTime) : unit =
        let currentState =
            match Map.tryFind callName stateMap with
            | Some state -> state
            | None -> RuntimeStatsCollector.empty

        let newState = RuntimeStatsCollector.recordStart currentState timestamp
        stateMap <- Map.add callName newState stateMap

    member this.RecordFinish(callName: string, timestamp: DateTime) : float option =
        match Map.tryFind callName stateMap with
        | None -> None
        | Some state ->
            let (newState, durationMs) = RuntimeStatsCollector.recordFinish state timestamp
            stateMap <- Map.add callName newState stateMap
            durationMs

    member this.GetStats(callName: string) : IncrementalStatsResult option =
        match Map.tryFind callName stateMap with
        | Some state -> Some (RuntimeStatsCollector.getStats state)
        | None -> None

    member this.GetTrackedCalls() : string list =
        Map.toList stateMap |> List.map fst

    member this.Clear(callName: string) : unit =
        stateMap <- Map.remove callName stateMap

    member this.ClearAll() : unit =
        stateMap <- Map.empty

    member this.Count : int =
        Map.count stateMap
