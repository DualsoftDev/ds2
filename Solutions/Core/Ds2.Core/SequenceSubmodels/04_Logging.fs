namespace Ds2.Core

open System

// =============================================================================
// SEQUENCE LOGGING SUBMODEL
// =============================================================================
//
// 역할: 실행 이력 기록 및 통계 분석 (Historical data logging and analysis)
//
// 핵심 기능:
//   - Call/Work 실행 이력 기록
//   - Welford's Algorithm 기반 O(1) 증분 통계
//   - 병목 구간 탐지 (CriticalPath, LongDuration, FrequentExecution)
//   - 성능 메트릭 계산 (평균, 표준편차, CV)
//   - Gantt/Heatmap 데이터 생성
//
// 다른 모듈과의 관계:
//   - 03_Monitoring.fs: Logging은 과거(PAST), Monitoring은 현재(NOW)
//   - 05_Maintenance.fs: Logging은 정상 실행 통계, Maintenance는 에러 이력
//
// =============================================================================


// =============================================================================
// TYPE DEFINITIONS: Enumerations
// =============================================================================

/// 병목 타입 (Bottleneck detection)
type BottleneckType =
    | CriticalPath          // 임계 경로 (순차 실행 체인이 긴 경우)
    | LongDuration          // 긴 실행 시간
    | FrequentExecution     // 빈번한 실행 (높은 빈도)

/// 사이클 감지 방법
type CycleDetectionMethod =
    | HeadCallInTag         // Flow 시작점 Call의 InTag 신호
    | TailCallOutTag        // Flow 종료점 Call의 OutTag 신호
    | CustomTag of string   // 사용자 지정 태그

/// Heatmap 메트릭
type HeatmapMetric =
    | AverageTime               // 평균 시간
    | StdDeviation              // 표준편차
    | CoefficientOfVariation    // 변동계수 (CV)

/// 성능 색상 등급 (CV 기반)
type ColorClass =
    | Excellent     // 우수 (CV <= 5%)
    | Good          // 양호 (CV <= 10%)
    | Fair          // 보통 (CV <= 20%)
    | Poor          // 나쁨 (CV <= 30%)
    | Critical      // 심각 (CV > 30%)

/// 시간 버킷 크기 (시계열 집계 단위)
type BucketSize =
    | Min5      // 5분 버킷
    | Min10     // 10분 버킷
    | Hour1     // 1시간 버킷


// =============================================================================
// TYPE DEFINITIONS: Statistics
// =============================================================================

/// 증분 통계 결과 (Welford's Algorithm - O(1) time complexity)
[<CLIMutable>]
type IncrementalStatsResult = {
    Count: int              // 샘플 개수
    Mean: float             // 평균
    Variance: float         // 분산
    StdDev: float           // 표준편차
    Min: float option       // 최솟값
    Max: float option       // 최댓값
    M2: float               // Welford 중간값 (분산 계산용)
}

/// 성능 메트릭
type PerformanceMetrics = {
    AverageTime: float
    StdDev: float
    CoefficientOfVariation: float
}

/// Per-Call 실시간 통계 추적 상태 (Welford 기반)
type CallStatsState = {
    Stats: IncrementalStatsResult
    LastStartAt: DateTime option
}

/// Call 실행 세션 추적 상태 (이동평균 기반)
type CallExecutionState = {
    StartTime: DateTime option
    History: int list       // 최근 100개 실행 시간(ms)
    SessionCount: int
    BaseCount: int
}

/// Runtime 통계 결과 (세션 기반)
[<CLIMutable>]
type RuntimeStatistics = {
    GoingTime: int
    Average: float
    StdDev: float
    SessionCount: int
    BaseCount: int
    TotalCount: int
}


// =============================================================================
// TYPE DEFINITIONS: Historical Records
// =============================================================================

/// 실행 이력 레코드
type ExecutionHistoryRecord = {
    Id: Guid
    CallName: string option
    WorkName: string option
    FlowName: string option
    StartedAt: DateTime
    FinishedAt: DateTime option
    DurationMs: float option
    State: string
    CycleNo: int option
}

/// 사이클 타임 분석 결과
type CycleTimeAnalysis = {
    FlowName: string
    TotalCycles: int
    AverageCycleTime: float
    MinCycleTime: float
    MaxCycleTime: float
    StdDevCycleTime: float
    CoefficientOfVariation: float
}

/// Trend 데이터 포인트 (시계열 통계)
type TrendPoint = {
    Time: DateTime
    Average: float
    StdDev: float
    SampleCount: int
}


// =============================================================================
// TYPE DEFINITIONS: Bottleneck Analysis
// =============================================================================

/// 병목 정보
type BottleneckInfo = {
    CallName: string
    BottleneckType: BottleneckType
    Value: float            // 메트릭 값 (ms 또는 실행 횟수)
    Impact: float           // 영향도 (0.0 ~ 1.0)
}

/// Gantt 레인 할당을 위한 타임라인 항목 (상대 오프셋 기반)
type TimelineItem = {
    CallName: string
    RelativeStart: int      // 사이클 시작으로부터 ms
    RelativeEnd: int option // None = 아직 실행 중
    Lane: int               // 레인 번호 (0-based)
}


// =============================================================================
// TYPE DEFINITIONS: UI Data Models
// =============================================================================

/// 간트 차트 아이템 (DSPilot.Winform Gantt)
type GanttItem = {
    CallName: string
    FlowName: string
    StartTime: DateTime
    EndTime: DateTime option
    DurationMs: float option
    State: string
    ColorClass: ColorClass
}

/// 히트맵 셀 데이터 (DSPilot.Winform Heatmap)
type HeatmapCell = {
    RowLabel: string            // Call 이름
    ColumnLabel: string         // 시간 버킷
    Value: float                // CV 값
    ColorClass: ColorClass
    Tooltip: string
}


// =============================================================================
// AAS PROPERTIES CLASSES
// =============================================================================

/// System-level 로깅 속성 (AAS SubmodelElementCollection)
type LoggingSystemProperties() =
    inherit PropertiesBase<LoggingSystemProperties>()

    // 메타데이터
    member val EngineVersion: string option = None with get, set
    member val LangVersion: string option = None with get, set
    member val Author: string option = None with get, set
    member val DateTime: DateTimeOffset option = None with get, set
    member val IRI: string option = None with get, set
    member val SystemType: string option = None with get, set

    // 로깅 설정
    member val EnableLogging = true with get, set
    member val LogToFile = true with get, set
    member val LogToDatabase = false with get, set
    member val LogFilePath = "./logs/history" with get, set
    member val RetentionDays = 90 with get, set

/// Flow-level 로깅 속성 (AAS SubmodelElementCollection)
type LoggingFlowProperties() =
    inherit PropertiesBase<LoggingFlowProperties>()

    member val CycleDetectionMethod = HeadCallInTag with get, set
    member val BottleneckThresholdMultiplier = 2.0 with get, set
    member val LongGapThresholdMs = 1000.0 with get, set
    member val MinSampleSize = 30 with get, set

/// Work-level 로깅 속성 (AAS SubmodelElementCollection)
type LoggingWorkProperties() =
    inherit PropertiesBase<LoggingWorkProperties>()

    // Work 정의
    member val Motion: string option = None with get, set
    member val Script: string option = None with get, set
    member val ExternalStart = false with get, set
    member val IsFinished = false with get, set
    member val NumRepeat = 0 with get, set
    member val Duration: TimeSpan option = None with get, set
    member val SequenceOrder = 0 with get, set
    member val OperationCode: string option = None with get, set

    // 런타임 증분 통계 (Welford)
    member val GoingCount = 0 with get, set
    member val AverageDuration = 0.0 with get, set
    member val M2 = 0.0 with get, set
    member val StdDevDuration = 0.0 with get, set

/// Call-level 로깅 속성 (AAS SubmodelElementCollection)
type LoggingCallProperties() =
    inherit PropertiesBase<LoggingCallProperties>()

    member val ObjectName: string = "" with get, set
    member val ActionName: string = "" with get, set
    member val RobotExecutable: string option = None with get, set
    member val Timeout: TimeSpan option = None with get, set
    member val CallDirection: string option = None with get, set


// =============================================================================
// HELPER FUNCTIONS
// =============================================================================

module LoggingHelpers =

    // -------------------------------------------------------------------------
    // Welford's Algorithm (Incremental Statistics - O(1))
    // -------------------------------------------------------------------------

    /// Welford 알고리즘으로 통계 업데이트 (O(1) 시간복잡도)
    let updateIncrementalStats
        (currentCount: int)
        (currentMean: float)
        (currentM2: float)
        (currentMin: float option)
        (currentMax: float option)
        (newValue: float)
        : IncrementalStatsResult =

        let newCount = currentCount + 1
        let delta = newValue - currentMean
        let newMean = currentMean + delta / float newCount
        let delta2 = newValue - newMean
        let newM2 = currentM2 + delta * delta2

        let newVariance = if newCount < 2 then 0.0 else newM2 / float newCount
        let newStdDev = sqrt newVariance

        let newMin =
            match currentMin with
            | None -> Some newValue
            | Some minVal -> Some (min minVal newValue)

        let newMax =
            match currentMax with
            | None -> Some newValue
            | Some maxVal -> Some (max maxVal newValue)

        {
            Count = newCount
            Mean = newMean
            Variance = newVariance
            StdDev = newStdDev
            Min = newMin
            Max = newMax
            M2 = newM2
        }

    /// 빈 통계 생성
    let emptyStats : IncrementalStatsResult =
        {
            Count = 0
            Mean = 0.0
            Variance = 0.0
            StdDev = 0.0
            Min = None
            Max = None
            M2 = 0.0
        }

    /// 변동계수 (CV) 계산: (StdDev / Mean) * 100
    let calculateCoefficientOfVariation (mean: float) (stdDev: float) =
        if mean > 0.0 then (stdDev / mean) * 100.0 else 0.0


    // -------------------------------------------------------------------------
    // Bottleneck Detection
    // -------------------------------------------------------------------------

    /// 병목 여부 판단 (평균의 multiplier배 이상)
    let isBottleneck (duration: float) (average: float) (multiplier: float) =
        duration >= average * multiplier

    /// 긴 대기 시간 여부 판단
    let isLongGap (gapMs: float) (thresholdMs: float) =
        gapMs >= thresholdMs


    // -------------------------------------------------------------------------
    // ColorClass Helpers
    // -------------------------------------------------------------------------

    /// ColorClass → CSS 클래스명
    let colorClassToString = function
        | Excellent -> "heatmap-excellent"
        | Good -> "heatmap-good"
        | Fair -> "heatmap-fair"
        | Poor -> "heatmap-poor"
        | Critical -> "heatmap-critical"

    /// CV 값으로 ColorClass 결정
    let classifyByCV (cv: float) : ColorClass =
        if cv <= 5.0 then Excellent
        elif cv <= 10.0 then Good
        elif cv <= 20.0 then Fair
        elif cv <= 30.0 then Poor
        else Critical


    // -------------------------------------------------------------------------
    // Historical Record Creation
    // -------------------------------------------------------------------------

    /// 실행 이력 레코드 생성
    let createExecutionHistory
        (callName: string option)
        (workName: string option)
        (flowName: string option)
        (startedAt: DateTime)
        (finishedAt: DateTime option)
        (state: string) : ExecutionHistoryRecord =

        let durationMs =
            match finishedAt with
            | Some ft -> Some ((ft - startedAt).TotalMilliseconds)
            | None -> None

        {
            Id = Guid.NewGuid()
            CallName = callName
            WorkName = workName
            FlowName = flowName
            StartedAt = startedAt
            FinishedAt = finishedAt
            DurationMs = durationMs
            State = state
            CycleNo = None
        }


    // -------------------------------------------------------------------------
    // Cycle Time Analysis
    // -------------------------------------------------------------------------

    /// 사이클 타임 분석 계산
    let analyzeCycleTime (flowName: string) (cycleTimes: float list) : CycleTimeAnalysis =
        let count = cycleTimes.Length

        if count = 0 then
            {
                FlowName = flowName
                TotalCycles = 0
                AverageCycleTime = 0.0
                MinCycleTime = 0.0
                MaxCycleTime = 0.0
                StdDevCycleTime = 0.0
                CoefficientOfVariation = 0.0
            }
        else
            let avg = List.average cycleTimes
            let minTime = List.min cycleTimes
            let maxTime = List.max cycleTimes

            let variance =
                if count > 1 then
                    cycleTimes
                    |> List.map (fun x -> (x - avg) ** 2.0)
                    |> List.average
                else
                    0.0

            let stdDev = sqrt variance
            let cv = calculateCoefficientOfVariation avg stdDev

            {
                FlowName = flowName
                TotalCycles = count
                AverageCycleTime = avg
                MinCycleTime = minTime
                MaxCycleTime = maxTime
                StdDevCycleTime = stdDev
                CoefficientOfVariation = cv
            }




    // -------------------------------------------------------------------------
    // Moving Average Statistics (Sample Window)
    // -------------------------------------------------------------------------

    /// 이동 평균 계산 (최대 100개 샘플)
    let calculateMovingAverage (samples: int list) (newValue: int) : float =
        let allSamples = newValue :: samples |> List.truncate 100
        float (List.sum allSamples) / float allSamples.Length

    /// 표준편차 계산 (샘플 목록 기반)
    let calculateStdDevFromSamples (samples: int list) (average: float) : float =
        if samples.IsEmpty then 0.0
        else
            let variance =
                samples
                |> List.map (fun x -> let diff = float x - average in diff * diff)
                |> List.average
            sqrt variance

    /// 샘플 목록 업데이트 (최대 100개 유지)
    let updateSamples (samples: int list) (newValue: int) : int list =
        newValue :: samples |> List.truncate 100

    /// 전체 통계 계산 (평균, 표준편차, CV, 갱신된 샘플)
    let calculateWindowStatistics (samples: int list) (newValue: int) : float * float * float * int list =
        let updatedSamples = updateSamples samples newValue
        let average = calculateMovingAverage samples newValue
        let stdDev = calculateStdDevFromSamples updatedSamples average
        let cv = calculateCoefficientOfVariation average stdDev
        (average, stdDev, cv, updatedSamples)


    // -------------------------------------------------------------------------
    // Performance Metric Classification
    // -------------------------------------------------------------------------

    /// 성능 메트릭 계산 (평균, StdDev → PerformanceMetrics)
    let calculatePerformanceMetrics (average: float) (stdDev: float) : PerformanceMetrics =
        let cv = calculateCoefficientOfVariation average stdDev
        { AverageTime = average; StdDev = stdDev; CoefficientOfVariation = cv }

    /// 값 정규화 (0.0 ~ 1.0)
    let normalizeValue (value: float) (minValue: float) (maxValue: float) : float =
        if maxValue > minValue then (value - minValue) / (maxValue - minValue) else 0.5

    /// HeatmapMetric + 값으로 ColorClass 결정 (메트릭별 임계값 기준)
    let determineColorClass (metric: HeatmapMetric) (value: float) : ColorClass =
        match metric with
        | AverageTime ->
            if value < 100.0 then Excellent
            elif value < 500.0 then Good
            elif value < 1000.0 then Fair
            elif value < 2000.0 then Poor
            else Critical
        | StdDeviation ->
            if value < 50.0 then Excellent
            elif value < 100.0 then Good
            elif value < 200.0 then Fair
            elif value < 400.0 then Poor
            else Critical
        | CoefficientOfVariation ->
            classifyByCV value


    // -------------------------------------------------------------------------
    // Welford Call Stats Collector (O(1) per update)
    // -------------------------------------------------------------------------

    module CallStatsCollector =

        /// 빈 상태 생성
        let empty : CallStatsState =
            { Stats = emptyStats; LastStartAt = None }

        /// Going 시작 기록
        let recordStart (timestamp: DateTime) (state: CallStatsState) : CallStatsState =
            { state with LastStartAt = Some timestamp }

        /// Going 완료 기록 및 통계 갱신
        let recordFinish (timestamp: DateTime) (state: CallStatsState) : CallStatsState * float option =
            match state.LastStartAt with
            | None -> (state, None)
            | Some startTime ->
                let durationMs = (timestamp - startTime).TotalMilliseconds
                let s = state.Stats
                let newStats = updateIncrementalStats s.Count s.Mean s.M2 s.Min s.Max durationMs
                ({ Stats = newStats; LastStartAt = None }, Some durationMs)

        /// 통계 조회
        let getStats (state: CallStatsState) : IncrementalStatsResult = state.Stats
