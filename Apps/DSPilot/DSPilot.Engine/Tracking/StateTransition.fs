namespace DSPilot.Engine.Tracking

open System
open System.Collections.Generic
open Microsoft.Data.Sqlite
open Dapper
open Ds2.Core
open Ds2.Core.LoggingHelpers

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
      /// PLC 제어기 관점: 장비에서 PLC로 입력(DI)되는 신호 (응답) 존재 여부
      HasInTag: bool
      /// PLC 제어기 관점: PLC가 장비로 출력(DO)하는 신호 (명령) 존재 여부
      HasOutTag: bool }

// CallDirection은 Ds2.Core.CallDirection을 사용 (InOut=0, InOnly=1, OutOnly=2, None=3)


// -------------------------------------------------------------------------
// Session Statistics Tracker (Moving Average, BaseCount aware)
// Ds2.Core.CallExecutionState + LoggingHelpers.calculateWindowStatistics 사용
// -------------------------------------------------------------------------

module SessionStatsTracker =

    let empty (baseCount: int) : CallExecutionState =
        { StartTime = None; History = []; SessionCount = 0; BaseCount = baseCount }

    let recordStart (startTime: DateTime) (state: CallExecutionState) : CallExecutionState =
        { state with StartTime = Some startTime }

    let recordFinish (finishTime: DateTime) (state: CallExecutionState) : CallExecutionState * RuntimeStatistics option =
        match state.StartTime with
        | None -> (state, None)
        | Some start ->
            let goingTime = int (finishTime - start).TotalMilliseconds
            let (average, stdDev, _, updatedSamples) = calculateWindowStatistics state.History goingTime
            let newSessionCount = state.SessionCount + 1
            let newState = { StartTime = None; History = updatedSamples
                             SessionCount = newSessionCount; BaseCount = state.BaseCount }
            let stats = { GoingTime = goingTime; Average = average; StdDev = stdDev
                          SessionCount = newSessionCount; BaseCount = state.BaseCount
                          TotalCount = state.BaseCount + newSessionCount }
            (newState, Some stats)

    let resetSession (state: CallExecutionState) : CallExecutionState =
        { StartTime = None; History = []; SessionCount = 0; BaseCount = state.BaseCount }

    let getBaseCount (state: CallExecutionState) = state.BaseCount
    let getSessionCount (state: CallExecutionState) = state.SessionCount
    let getTotalCount (state: CallExecutionState) = state.BaseCount + state.SessionCount


// -------------------------------------------------------------------------
// Cycle Analysis (Bucket-based time series)
// Ds2.Core.BucketSize, TrendPoint 사용
// -------------------------------------------------------------------------

module CycleAnalysisHelpers =

    let bucketSizeToTimeSpan (size: BucketSize) : TimeSpan =
        match size with
        | Min5  -> TimeSpan.FromMinutes(5.0)
        | Min10 -> TimeSpan.FromMinutes(10.0)
        | Hour1 -> TimeSpan.FromHours(1.0)

    let getBucketKey (time: DateTime) (bucketSize: BucketSize) : DateTime =
        let span = bucketSizeToTimeSpan bucketSize
        let ticks = time.Ticks / span.Ticks
        DateTime(ticks * span.Ticks)

    let calculateBucketStats (executionTimes: int list) : float * float * int =
        if executionTimes.IsEmpty then (0.0, 0.0, 0)
        else
            let count = executionTimes.Length
            let avg = executionTimes |> List.averageBy float
            let variance =
                if count > 1 then
                    executionTimes |> List.map (fun x -> let d = float x - avg in d*d) |> List.average
                else 0.0
            (avg, sqrt variance, count)

    let createTrendPoint (bucketTime: DateTime) (executionTimes: int list) : TrendPoint =
        let (avg, stdDev, count) = calculateBucketStats executionTimes
        { Time = bucketTime; Average = avg; StdDev = stdDev; SampleCount = count }

    let calculatePercentiles (values: int list) : float * float * float =
        if values.IsEmpty then (0.0, 0.0, 0.0)
        else
            let sorted = values |> List.sort |> List.toArray
            let count = sorted.Length
            let getPercentile p =
                let index = int (float count * p / 100.0)
                float sorted.[max 0 (min (count - 1) index)]
            (getPercentile 50.0, getPercentile 95.0, getPercentile 99.0)

    let calculateDistribution (values: int list) (binCount: int) : (float * float * int) list =
        if values.IsEmpty || binCount <= 0 then []
        else
            let minVal = values |> List.min |> float
            let maxVal = values |> List.max |> float
            let binWidth = (maxVal - minVal) / float binCount
            if binWidth = 0.0 then [(minVal, maxVal, values.Length)]
            else
                [0 .. binCount - 1]
                |> List.map (fun i ->
                    let binStart = minVal + float i * binWidth
                    let binEnd   = binStart + binWidth
                    let cnt =
                        values
                        |> List.filter (fun v ->
                            let fv = float v
                            fv >= binStart && (i = binCount - 1 || fv < binEnd))
                        |> List.length
                    (binStart, binEnd, cnt))


// -------------------------------------------------------------------------
// Gantt Lane Assignment — Ds2.Core.TimelineItem 사용
// -------------------------------------------------------------------------

module GanttLayoutHelpers =

    let private tlRelEnd   (t: TimelineItem) = t.RelativeEnd
    let private tlRelStart (t: TimelineItem) = t.RelativeStart
    let private tlLane     (t: TimelineItem) = t.Lane

    let isOverlapping (item1: TimelineItem) (item2: TimelineItem) : bool =
        match tlRelEnd item1, tlRelEnd item2 with
        | Some end1, Some end2 -> not (end1 <= tlRelStart item2 || end2 <= tlRelStart item1)
        | Some end1, None      -> end1 > tlRelStart item2
        | None, Some end2      -> end2 > tlRelStart item1
        | None, None           -> true

    let assignLanes (timelines: TimelineItem list) : TimelineItem list =
        let sorted = timelines |> List.sortBy tlRelStart
        sorted
        |> List.fold (fun (assigned: TimelineItem list) (item: TimelineItem) ->
            let usedLanes =
                assigned
                |> List.filter (fun prev -> isOverlapping prev item)
                |> List.map tlLane
                |> Set.ofList
            let availableLane = Seq.initInfinite id |> Seq.find (fun lane -> not (Set.contains lane usedLanes))
            assigned @ [{ item with Lane = availableLane }]
        ) []


/// C# 호환 mutable Welford 통계 tracker (multi-call)
type RuntimeStatsCollectorMutable() =
    let mutable stateMap : Map<string, CallStatsState> = Map.empty

    member _.RecordStart(callName: string, timestamp: DateTime) : unit =
        let current = stateMap |> Map.tryFind callName |> Option.defaultValue CallStatsCollector.empty
        stateMap <- Map.add callName (CallStatsCollector.recordStart timestamp current) stateMap

    member _.RecordFinish(callName: string, timestamp: DateTime) : float option =
        match Map.tryFind callName stateMap with
        | None -> None
        | Some state ->
            let (newState, duration) = CallStatsCollector.recordFinish timestamp state
            stateMap <- Map.add callName newState stateMap
            duration

    member _.GetStats(callName: string) : IncrementalStatsResult option =
        stateMap |> Map.tryFind callName |> Option.map CallStatsCollector.getStats

    member _.GetTrackedCalls() : string list =
        Map.toList stateMap |> List.map fst

    member _.Clear(callName: string) : unit = stateMap <- Map.remove callName stateMap
    member _.ClearAll() : unit = stateMap <- Map.empty
    member _.Count : int = Map.count stateMap


/// C# 호환 mutable 세션 통계 tracker (Moving Average 기반)
type RuntimeStatisticsTrackerMutable() =
    let mutable stateMap : Map<string, CallExecutionState> = Map.empty

    member _.RecordStart(callName: string, baseCount: int) : unit =
        let current =
            stateMap |> Map.tryFind callName
            |> Option.defaultValue (SessionStatsTracker.empty baseCount)
        stateMap <- Map.add callName (SessionStatsTracker.recordStart DateTime.Now current) stateMap

    member _.RecordFinish(callName: string) : RuntimeStatistics option =
        match Map.tryFind callName stateMap with
        | None -> None
        | Some state ->
            let (newState, stats) = SessionStatsTracker.recordFinish DateTime.Now state
            stateMap <- Map.add callName newState stateMap
            stats

    member _.ResetSession(callName: string) : unit =
        match Map.tryFind callName stateMap with
        | Some state -> stateMap <- Map.add callName (SessionStatsTracker.resetSession state) stateMap
        | None -> ()

    member _.ResetAllSessions() : unit =
        stateMap <- stateMap |> Map.map (fun _ state -> SessionStatsTracker.resetSession state)

    member _.TrackedCallCount : int = Map.count stateMap

    member _.GetTotalCount(callName: string) : int =
        stateMap |> Map.tryFind callName
        |> Option.map SessionStatsTracker.getTotalCount
        |> Option.defaultValue 0

    member _.GetSessionCount(callName: string) : int =
        stateMap |> Map.tryFind callName
        |> Option.map SessionStatsTracker.getSessionCount
        |> Option.defaultValue 0


/// State Transition Handler
module StateTransition =

    let private statsCollector = RuntimeStatsCollectorMutable()

    let getCallStats (callName: string) : IncrementalStatsResult option =
        statsCollector.GetStats(callName)

    let getTrackedCalls () : string list =
        statsCollector.GetTrackedCalls()

    let determineDirection (hasInTag: bool) (hasOutTag: bool) : CallDirection =
        match (hasInTag, hasOutTag) with
        | (true, true) -> CallDirection.InOut
        | (true, false) -> CallDirection.InOnly
        | (false, true) -> CallDirection.OutOnly
        | (false, false) -> CallDirection.None

    let parseDirection (directionStr: string) : CallDirection =
        match directionStr with
        | "InOut" -> CallDirection.InOut
        | "InOnly" -> CallDirection.InOnly
        | "OutOnly" -> CallDirection.OutOnly
        | _ -> CallDirection.None

    let handleInTagRisingEdge (dbPath: string) (callName: string) (timestamp: DateTime) : Async<unit> =
        async {
            use conn = new SqliteConnection($"Data Source={dbPath}")
            do! conn.OpenAsync() |> Async.AwaitTask

            let sql = "SELECT FlowName, CallName, State, LastStartAt, Direction FROM dspCall WHERE CallName = @CallName"
            let! call = conn.QueryFirstOrDefaultAsync<CallInfo>(sql, dict ["CallName", box callName]) |> Async.AwaitTask

            if not (isNull (box call)) then
                let direction = parseDirection call.Direction

                match direction with
                | CallDirection.InOut ->
                    if call.State = "Going" then
                        let statsDuration = statsCollector.RecordFinish(callName, timestamp)

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
                    if call.State = "Ready" then
                        statsCollector.RecordStart(callName, timestamp)
                        let statsDuration = statsCollector.RecordFinish(callName, timestamp)

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
                            "LastDurationMs", box 0.0
                            "UpdatedAt", box (DateTime.UtcNow.ToString("o"))
                            "CallName", box callName
                        ]

                        let! _ = conn.ExecuteAsync(updateCallSql, callParams) |> Async.AwaitTask

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

    let handleInTagFallingEdge (dbPath: string) (callName: string) (timestamp: DateTime) : Async<unit> =
        async {
            use conn = new SqliteConnection($"Data Source={dbPath}")
            do! conn.OpenAsync() |> Async.AwaitTask

            let sql = "SELECT FlowName, State, Direction FROM dspCall WHERE CallName = @CallName"
            let! call = conn.QueryFirstOrDefaultAsync<CallInfo>(sql, dict ["CallName", box callName]) |> Async.AwaitTask

            if not (isNull (box call)) then
                let direction = parseDirection call.Direction

                match direction with
                | CallDirection.InOut
                | CallDirection.InOnly ->
                    if call.State = "Finish" then
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

    let handleOutTagRisingEdge (dbPath: string) (callName: string) (timestamp: DateTime) : Async<unit> =
        async {
            use conn = new SqliteConnection($"Data Source={dbPath}")
            do! conn.OpenAsync() |> Async.AwaitTask

            let sql = "SELECT FlowName, State, Direction FROM dspCall WHERE CallName = @CallName"
            let! call = conn.QueryFirstOrDefaultAsync<CallInfo>(sql, dict ["CallName", box callName]) |> Async.AwaitTask

            if not (isNull (box call)) then
                let direction = parseDirection call.Direction

                match direction with
                | CallDirection.InOut
                | CallDirection.OutOnly ->
                    if call.State = "Ready" then
                        statsCollector.RecordStart(callName, timestamp)

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

    let handleOutTagFallingEdge (dbPath: string) (callName: string) (timestamp: DateTime) : Async<unit> =
        async {
            use conn = new SqliteConnection($"Data Source={dbPath}")
            do! conn.OpenAsync() |> Async.AwaitTask

            let sql = "SELECT FlowName, CallName, State, LastStartAt, Direction FROM dspCall WHERE CallName = @CallName"
            let! call = conn.QueryFirstOrDefaultAsync<CallInfo>(sql, dict ["CallName", box callName]) |> Async.AwaitTask

            if not (isNull (box call)) then
                let direction = parseDirection call.Direction

                match direction with
                | CallDirection.OutOnly ->
                    if call.State = "Going" then
                        let statsDuration = statsCollector.RecordFinish(callName, timestamp)

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
                ()
        }
