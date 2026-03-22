namespace DSPilot.Engine.Tracking

open System
open System.Collections.Generic
open Microsoft.Data.Sqlite
open Dapper
open DSPilot.Engine.Core

[<CLIMutable>]
type CallInfo =
    { FlowName: string
      CallName: string
      State: string
      LastStartAt: string
      Direction: string }

[<CLIMutable>]
type CallDirectionInfo =
    { CallName: string
      HasInTag: bool
      HasOutTag: bool }

/// Direction enum matching the C# implementation
type CallDirection =
    | InOut = 0
    | InOnly = 1
    | OutOnly = 2
    | None = 3

/// State Transition Handler
module StateTransition =

    // Global statistics collector (shared across all calls)
    let private statsCollector = DSPilot.Engine.Stats.RuntimeStatsCollectorMutable()

    /// Get statistics for a specific Call
    let getCallStats (callName: string) : DSPilot.Engine.Stats.IncrementalStatsResult option =
        statsCollector.GetStats(callName)

    /// Get all tracked Call names
    let getTrackedCalls () : string list =
        statsCollector.GetTrackedCalls()

    /// Determine Call Direction based on tag configuration
    let determineDirection (hasInTag: bool) (hasOutTag: bool) : CallDirection =
        match (hasInTag, hasOutTag) with
        | (true, true) -> CallDirection.InOut
        | (true, false) -> CallDirection.InOnly
        | (false, true) -> CallDirection.OutOnly
        | (false, false) -> CallDirection.None

    /// Parse Direction string to enum
    let parseDirection (directionStr: string) : CallDirection =
        match directionStr with
        | "InOut" -> CallDirection.InOut
        | "InOnly" -> CallDirection.InOnly
        | "OutOnly" -> CallDirection.OutOnly
        | _ -> CallDirection.None

    /// Handle InTag Rising Edge
    /// - InOut: Ready -> Going (Out signal starts work)
    /// - InOnly: Ready -> Going -> Finish (instant)
    let handleInTagRisingEdge (dbPath: string) (callName: string) (timestamp: DateTime) : Async<unit> =
        async {
            use conn = new SqliteConnection($"Data Source={dbPath}")
            do! conn.OpenAsync() |> Async.AwaitTask

            // Get Call info
            let sql = "SELECT FlowName, CallName, State, LastStartAt, Direction FROM dspCall WHERE CallName = @CallName"
            let! call = conn.QueryFirstOrDefaultAsync<CallInfo>(sql, dict ["CallName", box callName]) |> Async.AwaitTask

            if not (isNull (box call)) then
                let direction = parseDirection call.Direction

                match direction with
                | CallDirection.InOut ->
                    // InOut: In ON means Going -> Finish
                    if call.State = "Going" then
                        // Record finish time in statistics
                        let statsDuration = statsCollector.RecordFinish(callName, timestamp)

                        // Calculate duration
                        let durationMs =
                            match statsDuration with
                            | Some duration -> Some duration
                            | None ->
                                if String.IsNullOrEmpty(call.LastStartAt) then
                                    None
                                else
                                    try
                                        let startTime = DateTime.Parse(call.LastStartAt)
                                        Some ((timestamp - startTime).TotalMilliseconds)
                                    with
                                    | _ -> None

                        // Update Call: State = Finish, increment CycleCount
                        let updateCallSql = """
                            UPDATE dspCall
                            SET State = @State,
                                LastFinishAt = @LastFinishAt,
                                LastDurationMs = @LastDurationMs,
                                CycleCount = CycleCount + 1,
                                UpdatedAt = @UpdatedAt
                            WHERE CallName = @CallName
                        """

                        let callParams = dict [
                            "State", box "Finish"
                            "LastFinishAt", box (timestamp.ToString("o"))
                            "LastDurationMs", box durationMs
                            "UpdatedAt", box (DateTime.UtcNow.ToString("o"))
                            "CallName", box callName
                        ]

                        let! _ = conn.ExecuteAsync(updateCallSql, callParams) |> Async.AwaitTask
                        ()

                | CallDirection.InOnly ->
                    // InOnly: In ON means Ready -> Going -> Finish (instant)
                    if call.State = "Ready" then
                        // Record start and finish
                        statsCollector.RecordStart(callName, timestamp)
                        let statsDuration = statsCollector.RecordFinish(callName, timestamp)

                        // Update Call: State = Finish (skip Going visually, but honor state sequence)
                        let updateCallSql = """
                            UPDATE dspCall
                            SET State = @State,
                                LastStartAt = @LastStartAt,
                                LastFinishAt = @LastFinishAt,
                                LastDurationMs = @LastDurationMs,
                                CycleCount = CycleCount + 1,
                                UpdatedAt = @UpdatedAt
                            WHERE CallName = @CallName
                        """

                        let callParams = dict [
                            "State", box "Finish"
                            "LastStartAt", box (timestamp.ToString("o"))
                            "LastFinishAt", box (timestamp.ToString("o"))
                            "LastDurationMs", box 0.0  // InOnly has no duration
                            "UpdatedAt", box (DateTime.UtcNow.ToString("o"))
                            "CallName", box callName
                        ]

                        let! _ = conn.ExecuteAsync(updateCallSql, callParams) |> Async.AwaitTask

                        // Update Flow: increment ActiveCallCount temporarily (will decrement on In OFF)
                        let updateFlowSql = """
                            UPDATE dspFlow
                            SET ActiveCallCount = ActiveCallCount + 1,
                                State = 'Going',
                                UpdatedAt = @UpdatedAt
                            WHERE FlowName = @FlowName
                        """

                        let flowParams = dict [
                            "UpdatedAt", box (DateTime.UtcNow.ToString("o"))
                            "FlowName", box call.FlowName
                        ]

                        let! _ = conn.ExecuteAsync(updateFlowSql, flowParams) |> Async.AwaitTask
                        ()

                | _ ->
                    printfn "[StateTransition] Warning: InTag Rising for Call with invalid Direction: %s (%A)" callName direction
        }

    /// Handle InTag Falling Edge
    /// - InOut: Finish -> Ready (In OFF resets)
    /// - InOnly: Finish -> Ready (In OFF resets)
    let handleInTagFallingEdge (dbPath: string) (callName: string) (timestamp: DateTime) : Async<unit> =
        async {
            use conn = new SqliteConnection($"Data Source={dbPath}")
            do! conn.OpenAsync() |> Async.AwaitTask

            // Get Call info
            let sql = "SELECT FlowName, State, Direction FROM dspCall WHERE CallName = @CallName"
            let! call = conn.QueryFirstOrDefaultAsync<CallInfo>(sql, dict ["CallName", box callName]) |> Async.AwaitTask

            if not (isNull (box call)) then
                let direction = parseDirection call.Direction

                match direction with
                | CallDirection.InOut
                | CallDirection.InOnly ->
                    // In OFF: Finish -> Ready
                    if call.State = "Finish" then
                        // Update Call: State = Ready
                        let updateCallSql = """
                            UPDATE dspCall
                            SET State = @State,
                                UpdatedAt = @UpdatedAt
                            WHERE CallName = @CallName
                        """

                        let callParams = dict [
                            "State", box "Ready"
                            "UpdatedAt", box (DateTime.UtcNow.ToString("o"))
                            "CallName", box callName
                        ]

                        let! _ = conn.ExecuteAsync(updateCallSql, callParams) |> Async.AwaitTask

                        // Update Flow: decrement ActiveCallCount
                        let updateFlowSql = """
                            UPDATE dspFlow
                            SET ActiveCallCount = MAX(0, ActiveCallCount - 1),
                                UpdatedAt = @UpdatedAt
                            WHERE FlowName = @FlowName
                        """

                        let flowParams = dict [
                            "UpdatedAt", box (DateTime.UtcNow.ToString("o"))
                            "FlowName", box call.FlowName
                        ]

                        let! _ = conn.ExecuteAsync(updateFlowSql, flowParams) |> Async.AwaitTask

                        // Recalculate Flow State
                        let countSql = "SELECT ActiveCallCount FROM dspFlow WHERE FlowName = @FlowName"
                        let! activeCount = conn.ExecuteScalarAsync<int>(countSql, dict ["FlowName", box call.FlowName]) |> Async.AwaitTask

                        let newFlowState = if activeCount > 0 then "Going" else "Ready"

                        let updateStateSql = """
                            UPDATE dspFlow
                            SET State = @State,
                                UpdatedAt = @UpdatedAt
                            WHERE FlowName = @FlowName
                        """

                        let stateParams = dict [
                            "State", box newFlowState
                            "UpdatedAt", box (DateTime.UtcNow.ToString("o"))
                            "FlowName", box call.FlowName
                        ]

                        let! _ = conn.ExecuteAsync(updateStateSql, stateParams) |> Async.AwaitTask
                        ()

                | _ ->
                    ()
        }

    /// Handle OutTag Rising Edge
    /// - InOut: Ready -> Going (Out signal starts work)
    /// - OutOnly: Ready -> Going (Out ON starts)
    let handleOutTagRisingEdge (dbPath: string) (callName: string) (timestamp: DateTime) : Async<unit> =
        async {
            use conn = new SqliteConnection($"Data Source={dbPath}")
            do! conn.OpenAsync() |> Async.AwaitTask

            // Get Call info
            let sql = "SELECT FlowName, State, Direction FROM dspCall WHERE CallName = @CallName"
            let! call = conn.QueryFirstOrDefaultAsync<CallInfo>(sql, dict ["CallName", box callName]) |> Async.AwaitTask

            if not (isNull (box call)) then
                let direction = parseDirection call.Direction

                match direction with
                | CallDirection.InOut
                | CallDirection.OutOnly ->
                    // Out ON: Ready -> Going
                    if call.State = "Ready" then
                        // Record start time
                        statsCollector.RecordStart(callName, timestamp)

                        // Update Call: State = Going
                        let updateCallSql = """
                            UPDATE dspCall
                            SET State = @State,
                                LastStartAt = @LastStartAt,
                                UpdatedAt = @UpdatedAt
                            WHERE CallName = @CallName
                        """

                        let callParams = dict [
                            "State", box "Going"
                            "LastStartAt", box (timestamp.ToString("o"))
                            "UpdatedAt", box (DateTime.UtcNow.ToString("o"))
                            "CallName", box callName
                        ]

                        let! _ = conn.ExecuteAsync(updateCallSql, callParams) |> Async.AwaitTask

                        // Update Flow: increment ActiveCallCount
                        let updateFlowSql = """
                            UPDATE dspFlow
                            SET ActiveCallCount = ActiveCallCount + 1,
                                State = 'Going',
                                UpdatedAt = @UpdatedAt
                            WHERE FlowName = @FlowName
                        """

                        let flowParams = dict [
                            "UpdatedAt", box (DateTime.UtcNow.ToString("o"))
                            "FlowName", box call.FlowName
                        ]

                        let! _ = conn.ExecuteAsync(updateFlowSql, flowParams) |> Async.AwaitTask
                        ()

                | _ ->
                    printfn "[StateTransition] Warning: OutTag Rising for Call with invalid Direction: %s (%A)" callName direction
        }

    /// Handle OutTag Falling Edge
    /// - OutOnly: Going -> Finish -> Ready (auto)
    let handleOutTagFallingEdge (dbPath: string) (callName: string) (timestamp: DateTime) : Async<unit> =
        async {
            use conn = new SqliteConnection($"Data Source={dbPath}")
            do! conn.OpenAsync() |> Async.AwaitTask

            // Get Call info
            let sql = "SELECT FlowName, CallName, State, LastStartAt, Direction FROM dspCall WHERE CallName = @CallName"
            let! call = conn.QueryFirstOrDefaultAsync<CallInfo>(sql, dict ["CallName", box callName]) |> Async.AwaitTask

            if not (isNull (box call)) then
                let direction = parseDirection call.Direction

                match direction with
                | CallDirection.OutOnly ->
                    // Out OFF: Going -> Finish -> Ready (auto)
                    if call.State = "Going" then
                        // Record finish time
                        let statsDuration = statsCollector.RecordFinish(callName, timestamp)

                        // Calculate duration
                        let durationMs =
                            match statsDuration with
                            | Some duration -> Some duration
                            | None ->
                                if String.IsNullOrEmpty(call.LastStartAt) then
                                    None
                                else
                                    try
                                        let startTime = DateTime.Parse(call.LastStartAt)
                                        Some ((timestamp - startTime).TotalMilliseconds)
                                    with
                                    | _ -> None

                        // Update Call: State = Ready (skip Finish, auto return)
                        let updateCallSql = """
                            UPDATE dspCall
                            SET State = @State,
                                LastFinishAt = @LastFinishAt,
                                LastDurationMs = @LastDurationMs,
                                CycleCount = CycleCount + 1,
                                UpdatedAt = @UpdatedAt
                            WHERE CallName = @CallName
                        """

                        let callParams = dict [
                            "State", box "Ready"
                            "LastFinishAt", box (timestamp.ToString("o"))
                            "LastDurationMs", box durationMs
                            "UpdatedAt", box (DateTime.UtcNow.ToString("o"))
                            "CallName", box callName
                        ]

                        let! _ = conn.ExecuteAsync(updateCallSql, callParams) |> Async.AwaitTask

                        // Update Flow: decrement ActiveCallCount
                        let updateFlowSql = """
                            UPDATE dspFlow
                            SET ActiveCallCount = MAX(0, ActiveCallCount - 1),
                                UpdatedAt = @UpdatedAt
                            WHERE FlowName = @FlowName
                        """

                        let flowParams = dict [
                            "UpdatedAt", box (DateTime.UtcNow.ToString("o"))
                            "FlowName", box call.FlowName
                        ]

                        let! _ = conn.ExecuteAsync(updateFlowSql, flowParams) |> Async.AwaitTask

                        // Recalculate Flow State
                        let countSql = "SELECT ActiveCallCount FROM dspFlow WHERE FlowName = @FlowName"
                        let! activeCount = conn.ExecuteScalarAsync<int>(countSql, dict ["FlowName", box call.FlowName]) |> Async.AwaitTask

                        let newFlowState = if activeCount > 0 then "Going" else "Ready"

                        let updateStateSql = """
                            UPDATE dspFlow
                            SET State = @State,
                                UpdatedAt = @UpdatedAt
                            WHERE FlowName = @FlowName
                        """

                        let stateParams = dict [
                            "State", box newFlowState
                            "UpdatedAt", box (DateTime.UtcNow.ToString("o"))
                            "FlowName", box call.FlowName
                        ]

                        let! _ = conn.ExecuteAsync(updateStateSql, stateParams) |> Async.AwaitTask
                        ()

                | _ ->
                    ()
        }

    /// Process Edge Event (main entry point)
    let processEdgeEvent (dbPath: string) (tagAddress: string) (isInTag: bool) (edgeType: EdgeType) (timestamp: DateTime) (callName: string) : Async<unit> =
        async {
            match (isInTag, edgeType) with
            | (true, EdgeType.RisingEdge) ->
                do! handleInTagRisingEdge dbPath callName timestamp
            | (true, EdgeType.FallingEdge) ->
                do! handleInTagFallingEdge dbPath callName timestamp
            | (false, EdgeType.RisingEdge) ->
                do! handleOutTagRisingEdge dbPath callName timestamp
            | (false, EdgeType.FallingEdge) ->
                do! handleOutTagFallingEdge dbPath callName timestamp
            | _ ->
                // Ignore unknown edge types
                ()
        }
