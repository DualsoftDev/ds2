namespace Ds2.Core

open System

// =============================================================================
// SEQUENCE LOGGING SUBMODEL
// =============================================================================
//
// 역할: 실행 이력 기록, 통계 분석, 사용자 태그 정의 (Historical data logging, analysis, user tag definitions)
//
// 핵심 기능:
//   - Call/Work 실행 이력 기록
//   - Welford's Algorithm 기반 O(1) 증분 통계
//   - 병목 구간 탐지 (CriticalPath, LongDuration, FrequentExecution)
//   - 성능 메트릭 계산 (평균, 표준편차, CV)
//   - System 단위 사용자 태그 정의 (이름, 로그레벨, 태그, 값타입)
//   - Gantt/Heatmap 데이터 생성
//
// 다른 모듈과의 관계:
//   - 03_Monitoring.fs: Logging은 과거(PAST), Monitoring은 현재(NOW)
//   - 05_Maintenance.fs: Logging은 정상 실행 통계 + 사용자 태그 정의, Maintenance는 에러 추적/처리
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

/// PLC 데이터 타입
type PlcValueType =
    | Bit           // 1-bit (on/off)
    | Byte          // 8-bit unsigned
    | Word          // 16-bit unsigned
    | DWord         // 32-bit unsigned
    | Int16         // signed 16-bit
    | Int32         // signed 32-bit
    | Real          // 32-bit float
    | StringType    // 문자열

/// 사용자 태그 로그 레벨
[<RequireQualifiedAccess>]
type UserTagLogLevel =
    | Info          // 정보 (정상 이벤트)
    | Warning       // 경고
    | Error         // 에러


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
// TYPE DEFINITIONS: Historical Records & Analysis
// =============================================================================

/// Trend 데이터 포인트 (시계열 통계)
type TrendPoint = {
    Time: DateTime
    Average: float
    StdDev: float
    SampleCount: int
}

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

/// 간트 차트 아이템 (DSPilot Gantt)
type GanttItem = {
    CallName: string
    FlowName: string
    StartTime: DateTime
    EndTime: DateTime option
    DurationMs: float option
    State: string
    ColorClass: ColorClass
}

/// 히트맵 셀 데이터 (DSPilot Heatmap)
type HeatmapCell = {
    RowLabel: string            // Call 이름
    ColumnLabel: string         // 시간 버킷
    Value: float                // CV 값
    ColorClass: ColorClass
    Tooltip: string
}


// =============================================================================
// TYPE DEFINITIONS: User Tag Definitions
// =============================================================================

/// System 단위 사용자 태그 정의 (파싱된 구조체)
type UserTag = {
    Name: string                  // 태그 이름 (예: "Motor_Overload")
    LogLevel: UserTagLogLevel     // 로그 레벨 (Info / Warning / Error)
    TagAddress: string            // PLC 태그 주소 (예: "M901")
    ValueType: PlcValueType       // 값 타입 (예: Bit)
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

    // ========== 자동 로깅 설정 ==========
    member val EnableAutoLogging = true with get, set
    member val LogLevel = "Info" with get, set                      // "Debug", "Info", "Warning", "Error"
    member val LogToFile = true with get, set
    member val LogToDatabase = false with get, set
    member val LogFilePath = "./logs/history" with get, set
    member val RetentionDays = 90 with get, set

    // 사용자 태그 정의 (System 당 N개, 형식: "이름|로그레벨|태그주소|값타입")
    // 예: "Motor_Overload|Error|M901|Bit", "DoorOpen|Warning|M100|Bit", "CycleStart|Info|M200|Bit"
    member val UserTags = ResizeArray<string>() with get, set

/// Flow-level 로깅 속성 (AAS SubmodelElementCollection)
type LoggingFlowProperties() =
    inherit PropertiesBase<LoggingFlowProperties>()

    // 병목 분석 설정
    member val BottleneckThresholdMultiplier = 2.0 with get, set
    member val MinSampleSize = 30 with get, set

/// Work-level 로깅 속성 (AAS SubmodelElementCollection)
type LoggingWorkProperties() =
    inherit CommonWorkProperties<LoggingWorkProperties>()

    member val Duration: TimeSpan option = None with get, set

    // 런타임 증분 통계 (Welford)
    member val GoingCount = 0 with get, set
    member val AverageDuration = 0.0 with get, set
    member val M2 = 0.0 with get, set
    member val StdDevDuration = 0.0 with get, set

/// Call-level 로깅 속성 (AAS SubmodelElementCollection)
type LoggingCallProperties() =
    inherit CommonCallProperties<LoggingCallProperties>()
