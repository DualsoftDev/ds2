namespace DSPilot.Engine

open System

/// Runtime 통계 데이터
[<CLIMutable>]
type RuntimeStatistics = {
    GoingTime: int
    Average: double
    StdDev: double
    SessionCount: int
    BaseCount: int
    TotalCount: int
}

/// Call 실행 추적 상태
type CallExecutionState = {
    StartTime: DateTime option
    History: int list
    SessionCount: int
    BaseCount: int
}

/// Runtime 통계 추적 모듈
module RuntimeStatisticsTracker =

    /// 빈 실행 상태
    let empty (baseCount: int) : CallExecutionState =
        { StartTime = None
          History = []
          SessionCount = 0
          BaseCount = baseCount }

    /// Going 시작 기록
    let recordStart (state: CallExecutionState) (startTime: DateTime) : CallExecutionState =
        { state with StartTime = Some startTime }

    /// Going 종료 및 통계 계산
    let recordFinish (state: CallExecutionState) (finishTime: DateTime) : (CallExecutionState * RuntimeStatistics option) =
        match state.StartTime with
        | None ->
            // 시작 시간이 없으면 실패
            (state, None)
        | Some start ->
            // Going 시간 계산
            let goingTime = int (finishTime - start).TotalMilliseconds

            // F# Statistics 모듈 사용하여 통계 계산
            let (average, stdDev, cv, updatedSamples) = Statistics.calculateStatistics state.History goingTime

            // 세션 카운트 증가
            let newSessionCount = state.SessionCount + 1
            let newTotalCount = state.BaseCount + newSessionCount

            // 새로운 상태
            let newState = {
                StartTime = None
                History = updatedSamples
                SessionCount = newSessionCount
                BaseCount = state.BaseCount
            }

            // 통계 결과
            let stats = {
                GoingTime = goingTime
                Average = average
                StdDev = stdDev
                SessionCount = newSessionCount
                BaseCount = state.BaseCount
                TotalCount = newTotalCount
            }

            (newState, Some stats)

    /// Base count 조회
    let getBaseCount (state: CallExecutionState) : int =
        state.BaseCount

    /// Session count 조회
    let getSessionCount (state: CallExecutionState) : int =
        state.SessionCount

    /// Total count 조회
    let getTotalCount (state: CallExecutionState) : int =
        state.BaseCount + state.SessionCount

    /// 통계 히스토리 조회
    let getHistory (state: CallExecutionState) : int list =
        state.History

    /// 세션 데이터만 클리어 (base count는 유지)
    let resetSession (state: CallExecutionState) : CallExecutionState =
        { StartTime = None
          History = []
          SessionCount = 0
          BaseCount = state.BaseCount }


/// C# 호환 mutable wrapper
type RuntimeStatisticsTrackerMutable() =
    let mutable stateMap : Map<string, CallExecutionState> = Map.empty

    /// Going 시작 기록
    member this.RecordStart(callName: string, baseCount: int) : unit =
        let currentState =
            match Map.tryFind callName stateMap with
            | Some state -> state
            | None -> RuntimeStatisticsTracker.empty baseCount

        let newState = RuntimeStatisticsTracker.recordStart currentState DateTime.Now
        stateMap <- Map.add callName newState stateMap

    /// Going 종료 및 통계 반환
    member this.RecordFinish(callName: string) : RuntimeStatistics option =
        match Map.tryFind callName stateMap with
        | None -> None
        | Some state ->
            let (newState, stats) = RuntimeStatisticsTracker.recordFinish state DateTime.Now
            stateMap <- Map.add callName newState stateMap
            stats

    /// 세션 데이터만 클리어
    member this.ResetSession(callName: string) : unit =
        match Map.tryFind callName stateMap with
        | Some state ->
            let newState = RuntimeStatisticsTracker.resetSession state
            stateMap <- Map.add callName newState stateMap
        | None -> ()

    /// 모든 Call 세션 데이터 클리어
    member this.ResetAllSessions() : unit =
        stateMap <-
            stateMap
            |> Map.map (fun _ state -> RuntimeStatisticsTracker.resetSession state)

    /// 추적 중인 Call 개수
    member this.TrackedCallCount : int =
        Map.count stateMap

    /// 특정 Call의 total count 조회
    member this.GetTotalCount(callName: string) : int =
        match Map.tryFind callName stateMap with
        | Some state -> RuntimeStatisticsTracker.getTotalCount state
        | None -> 0

    /// 특정 Call의 session count 조회
    member this.GetSessionCount(callName: string) : int =
        match Map.tryFind callName stateMap with
        | Some state -> RuntimeStatisticsTracker.getSessionCount state
        | None -> 0
