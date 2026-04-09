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
// -------------------------------------------------------------------------

module SessionStatsTracker =

    /// 빈 실행 상태 생성
    let empty (baseCount: int) : CallExecutionState =
        { StartTime = None; History = []; SessionCount = 0; BaseCount = baseCount }

    /// Going 시작 기록
    let recordStart (startTime: DateTime) (state: CallExecutionState) : CallExecutionState =
        { state with StartTime = Some startTime }

    /// Going 종료 및 통계 계산
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

    /// 세션 데이터만 클리어 (BaseCount 유지)
    let resetSession (state: CallExecutionState) : CallExecutionState =
        { StartTime = None; History = []; SessionCount = 0; BaseCount = state.BaseCount }

    let getBaseCount (state: CallExecutionState) = state.BaseCount
    let getSessionCount (state: CallExecutionState) = state.SessionCount
    let getTotalCount (state: CallExecutionState) = state.BaseCount + state.SessionCount


// -------------------------------------------------------------------------
// Cycle Analysis (Bucket-based time series)
// -------------------------------------------------------------------------

module CycleAnalysisHelpers =

    /// 버킷 크기를 TimeSpan으로 변환
    let bucketSizeToTimeSpan (size: BucketSize) : TimeSpan =
        match size with
        | Min5  -> TimeSpan.FromMinutes(5.0)
        | Min10 -> TimeSpan.FromMinutes(10.0)
        | Hour1 -> TimeSpan.FromHours(1.0)

    /// 시간을 버킷 시작 시간으로 정규화
    let getBucketKey (time: DateTime) (bucketSize: BucketSize) : DateTime =
        let span = bucketSizeToTimeSpan bucketSize
        let ticks = time.Ticks / span.Ticks
        DateTime(ticks * span.Ticks)

    /// 실행 시간 목록에서 버킷 통계 계산 (평균, StdDev, 개수)
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

    /// Trend 포인트 생성
    let createTrendPoint (bucketTime: DateTime) (executionTimes: int list) : TrendPoint =
        let (avg, stdDev, count) = calculateBucketStats executionTimes
        { Time = bucketTime; Average = avg; StdDev = stdDev; SampleCount = count }

    /// 백분위수 계산 (p50, p95, p99)
    let calculatePercentiles (values: int list) : float * float * float =
        if values.IsEmpty then (0.0, 0.0, 0.0)
        else
            let sorted = values |> List.sort |> List.toArray
            let count = sorted.Length
            let getPercentile p =
                let index = int (float count * p / 100.0)
                float sorted.[max 0 (min (count - 1) index)]
            (getPercentile 50.0, getPercentile 95.0, getPercentile 99.0)

    /// 실행 시간 분포 계산 (히스토그램)
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
// Gantt Lane Assignment
// -------------------------------------------------------------------------

module GanttLayoutHelpers =

    // private 헬퍼 함수로 TimelineItem 필드 접근 (GanttItem과의 CallName 충돌 해소)
    let private tlRelEnd   (t: TimelineItem) = t.RelativeEnd
    let private tlRelStart (t: TimelineItem) = t.RelativeStart
    let private tlLane     (t: TimelineItem) = t.Lane

    /// 두 타임라인 항목의 시간적 겹침 여부
    let isOverlapping (item1: TimelineItem) (item2: TimelineItem) : bool =
        match tlRelEnd item1, tlRelEnd item2 with
        | Some end1, Some end2 -> not (end1 <= tlRelStart item2 || end2 <= tlRelStart item1)
        | Some end1, None      -> end1 > tlRelStart item2
        | None, Some end2      -> end2 > tlRelStart item1
        | None, None           -> true  // 둘 다 진행 중 = 겹침

    /// 레인 할당 (시작 시간 순으로 처리, 겹침 없는 가장 작은 레인 번호 배정)
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
        let current = stateMap |> Map.tryFind callName |> Option.defaultValue LoggingHelpers.CallStatsCollector.empty
        stateMap <- Map.add callName (LoggingHelpers.CallStatsCollector.recordStart timestamp current) stateMap

    member _.RecordFinish(callName: string, timestamp: DateTime) : float option =
        match Map.tryFind callName stateMap with
        | None -> None
        | Some state ->
            let (newState, duration) = LoggingHelpers.CallStatsCollector.recordFinish timestamp state
            stateMap <- Map.add callName newState stateMap
            duration

    member _.GetStats(callName: string) : IncrementalStatsResult option =
        stateMap |> Map.tryFind callName |> Option.map LoggingHelpers.CallStatsCollector.getStats

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

    // Global statistics collector (shared across all calls) - Ds2.Core.RuntimeStatsCollectorMutable
    let private statsCollector = RuntimeStatsCollectorMutable()

    /// Get statistics for a specific Call
    let getCallStats (callName: string) : IncrementalStatsResult option =
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

    /// Handle InTag Rising Edge (PLC 제어기 관점: 장비 → PLC 입력 신호 ON)
    /// - InOut: Going -> Finish (응답 수신 → 실행 완료)
    /// - InOnly: Ready -> Finish (instant, 응답만으로 완료 처리)
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

    /// Handle InTag Falling Edge (PLC 제어기 관점: 장비 → PLC 입력 신호 OFF)
    /// - InOut: Finish -> Ready (응답 해제 → 대기 복귀)
    /// - InOnly: Finish -> Ready (응답 해제 → 대기 복귀)
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

    /// Handle OutTag Rising Edge (PLC 제어기 관점: PLC → 장비 출력 신호 ON)
    /// - InOut: Ready -> Going (명령 송신 → 실행 시작)
    /// - OutOnly: Ready -> Going (명령 송신 → 실행 시작)
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

    /// Handle OutTag Falling Edge (PLC 제어기 관점: PLC → 장비 출력 신호 OFF)
    /// - OutOnly: Going -> Finish -> Ready (명령 해제 → 자동 완료)
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
    /// isInTag: PLC 제어기 관점 — true = PLC 입력(DI, 장비 응답), false = PLC 출력(DO, 장비 명령)
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
