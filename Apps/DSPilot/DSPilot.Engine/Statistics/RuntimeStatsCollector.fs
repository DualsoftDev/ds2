namespace DSPilot.Engine.Stats

open System
open System.Collections.Generic
open DSPilot.Engine.Stats.IncrementalStats

/// Runtime Statistics Collector using Welford's Incremental Stats
/// Tracks Call execution statistics in real-time with O(1) updates
module RuntimeStatsCollector =

    /// Per-Call statistics state
    type CallStatsState =
        { Stats: IncrementalStatsResult
          LastStartAt: DateTime option }

    /// Empty state for a new Call
    let empty : CallStatsState =
        { Stats = IncrementalStats.empty
          LastStartAt = None }

    /// Record Call start (Ready → Going)
    let recordStart (state: CallStatsState) (timestamp: DateTime) : CallStatsState =
        { state with LastStartAt = Some timestamp }

    /// Record Call finish (Going → Done) and update statistics
    let recordFinish (state: CallStatsState) (timestamp: DateTime) : CallStatsState * float option =
        match state.LastStartAt with
        | None ->
            // No start time, can't calculate duration
            (state, None)
        | Some startTime ->
            // Calculate duration in milliseconds
            let durationMs = (timestamp - startTime).TotalMilliseconds

            // Update incremental statistics
            let newStats =
                IncrementalStats.update
                    state.Stats.Count
                    state.Stats.Mean
                    state.Stats.M2
                    state.Stats.Min
                    state.Stats.Max
                    durationMs

            // New state with updated stats and cleared start time
            let newState =
                { Stats = newStats
                  LastStartAt = None }

            (newState, Some durationMs)

    /// Get current statistics for a Call
    let getStats (state: CallStatsState) : IncrementalStatsResult =
        state.Stats


/// Mutable wrapper for C# compatibility
/// Manages statistics for multiple Calls concurrently
type RuntimeStatsCollectorMutable() =
    let mutable stateMap : Map<string, RuntimeStatsCollector.CallStatsState> = Map.empty

    /// Record Going start for a Call
    member this.RecordStart(callName: string, timestamp: DateTime) : unit =
        let currentState =
            match Map.tryFind callName stateMap with
            | Some state -> state
            | None -> RuntimeStatsCollector.empty

        let newState = RuntimeStatsCollector.recordStart currentState timestamp
        stateMap <- Map.add callName newState stateMap

    /// Record Going finish for a Call, returns duration in ms
    member this.RecordFinish(callName: string, timestamp: DateTime) : float option =
        match Map.tryFind callName stateMap with
        | None -> None
        | Some state ->
            let (newState, durationMs) = RuntimeStatsCollector.recordFinish state timestamp
            stateMap <- Map.add callName newState stateMap
            durationMs

    /// Get current statistics for a Call
    member this.GetStats(callName: string) : IncrementalStatsResult option =
        match Map.tryFind callName stateMap with
        | Some state -> Some (RuntimeStatsCollector.getStats state)
        | None -> None

    /// Get all Call names being tracked
    member this.GetTrackedCalls() : string list =
        Map.toList stateMap |> List.map fst

    /// Clear statistics for a specific Call
    member this.Clear(callName: string) : unit =
        stateMap <- Map.remove callName stateMap

    /// Clear all statistics
    member this.ClearAll() : unit =
        stateMap <- Map.empty

    /// Number of Calls being tracked
    member this.Count : int =
        Map.count stateMap
