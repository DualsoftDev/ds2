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

/// 사용자 태그 매칭 조건 — "값이 어떻게 되면 알림으로 기록할 것인가"
/// Bit/비-Bit 모두에 대해 통일된 의미를 가지지만, 기본값과 적용 가능성은 ValueType 에 따라 다름.
[<RequireQualifiedAccess>]
type UserTagMatchOp =
    | Eq                    // 직전 값 != X, 새 값 == X 일 때 1건 기록 ("X 와 같아질 때")
    | Neq                   // 직전 값 == X, 새 값 != X 일 때 1건 기록
    | Gt                    // 직전 값 <= X, 새 값 > X 일 때 1건 기록 (수치형)
    | Gte                   // 직전 값 <  X, 새 값 >= X 일 때 1건 기록 (수치형)
    | Lt                    // 직전 값 >= X, 새 값 < X 일 때 1건 기록 (수치형)
    | Lte                   // 직전 값 >  X, 새 값 <= X 일 때 1건 기록 (수치형)
    | RisingEdge            // Bit 0 → 1 전이 (기본값)
    | FallingEdge           // Bit 1 → 0 전이
    | Changed               // 직전 값과 다르면 항상 (비-Bit 기본값)


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
/// MatchOp / MatchValue 는 v2 확장 — 기본값(레거시 4필드 호환)은 Bit→RisingEdge / 그 외→Changed.
type UserTag = {
    Name: string                  // 태그 이름 (예: "Motor_Overload")
    LogLevel: UserTagLogLevel     // 로그 레벨 (Info / Warning / Error)
    TagAddress: string            // PLC 태그 주소 (예: "M901")
    ValueType: PlcValueType       // 값 타입 (예: Bit)
    MatchOp: UserTagMatchOp       // 매칭 조건
    MatchValue: string            // 비교 기준값 (Eq/Gt 등에서 사용, edge/Changed 에서는 무시 가능)
}


// =============================================================================
// TYPE DEFINITIONS: Signal Collection Policy (Phase 0 · AAS × OPC UA)
// =============================================================================
//
// AID InteractionMetadata 신호별 수집 정책. 원래 별도 DualSoft SM 이었으나,
// "이력 로깅"과 "샘플링·retention·deadband"가 동일한 관찰 계층에 속하므로
// SequenceLogging 서브모델의 System-level 속성으로 흡수한다.
//
// - UserTag       = 값 변화 이벤트 → 로그 기록  (WHAT to record on change)
// - SignalPolicy  = 신호별 샘플링·필터 규칙        (HOW to sample continuously)
//
// 두 개념은 상호 보완적이며 같은 SequenceLogging AAS Submodel 로 emit 된다.

/// 어댑터가 신호 값을 획득하는 방식.
type AcquisitionMode =
    /// 시간 간격 폴링. SamplingIntervalMs 필수.
    | Sampled
    /// Deadband 구독. Publishing/Sampling 간격 옵션, Deadband 권장.
    | ChangeOfValue
    /// 이벤트 트리거 (OPC UA AutoID 등). 폴링/deadband 무의미.
    | EventDriven

/// 한 신호의 런타임 수집 정책 (ADR-002 SignalId 로 키).
type SignalPolicy = {
    SignalId: SignalId
    AcquisitionMode: AcquisitionMode
    SamplingIntervalMs: int option
    PublishingIntervalMs: int option
    DeadbandAbsolute: float option
    DeadbandPercent: float option
    /// Percent deadband 계산 기준 engineering range. 둘 다 있거나 둘 다 없어야 한다.
    EngineeringRangeLow: float option
    EngineeringRangeHigh: float option
    QueueSize: int option
    /// ISO-8601 duration (예: `P90D`).
    Retention: string
}

/// Agent OPC UA 서버와 Collector 사이에서 CollectionPolicy를 전달하는 UA Property 계약.
/// 각 signal Variable의 HasProperty 자식 BrowseName으로 사용한다.
[<RequireQualifiedAccess>]
module SignalPolicyUaMetadata =
    [<Literal>]
    let AcquisitionMode = "AcquisitionMode"
    [<Literal>]
    let SamplingIntervalMs = "SamplingIntervalMs"
    [<Literal>]
    let PublishingIntervalMs = "PublishingIntervalMs"
    [<Literal>]
    let DeadbandAbsolute = "DeadbandAbsolute"
    [<Literal>]
    let DeadbandPercent = "DeadbandPercent"
    [<Literal>]
    let EngineeringRangeLow = "EngineeringRangeLow"
    [<Literal>]
    let EngineeringRangeHigh = "EngineeringRangeHigh"
    [<Literal>]
    let QueueSize = "QueueSize"
    [<Literal>]
    let Retention = "Retention"

/// Signal Variable 자체의 의미 메타데이터 계약.
[<RequireQualifiedAccess>]
module SignalUaMetadata =
    [<Literal>]
    let Unit = "Unit"

/// ISO-8601 duration 파서 · 검증.
///
/// 지원 형태:
///   - Weeks: `PnW`
///   - Date-only: `P[nY][nM][nD]` (최소 한 성분)
///   - Date + time: `P[nY][nM][nD]T[nH][nM][nS]`
///   - Time-only: `PT[nH][nM][nS]`
/// 성분 순서는 강제 (Y < M < D · H < M < S).
module Iso8601Duration =

    let private isPositiveInt (s: string) =
        s.Length > 0 && s |> Seq.forall Char.IsDigit

    let private scan (input: string) (units: char array) : bool =
        let mutable i = 0
        let mutable ok = true
        let mutable anyUsed = false
        let mutable unitIdx = 0
        while ok && i < input.Length do
            let start = i
            while i < input.Length && Char.IsDigit input.[i] do
                i <- i + 1
            if i = start then
                ok <- false
            elif i >= input.Length then
                ok <- false
            else
                let letter = input.[i]
                let mutable found = false
                while unitIdx < units.Length && not found do
                    if units.[unitIdx] = letter then
                        found <- true
                        unitIdx <- unitIdx + 1
                    else
                        unitIdx <- unitIdx + 1
                if found then
                    anyUsed <- true
                    i <- i + 1
                else
                    ok <- false
        ok && anyUsed && i = input.Length

    /// True iff `s` is a well-formed positive ISO-8601 duration.
    let isValid (s: string) : bool =
        if String.IsNullOrWhiteSpace s then false
        elif not (s.StartsWith "P") then false
        else
            let body = s.Substring 1
            if body = "" then false
            elif body.EndsWith "W" then
                isPositiveInt (body.Substring(0, body.Length - 1))
            else
                let tIdx = body.IndexOf 'T'
                let datePart, timePart =
                    if tIdx < 0 then body, ""
                    elif tIdx = 0 then "", body.Substring 1
                    else body.Substring(0, tIdx), body.Substring(tIdx + 1)

                let dateOk =
                    if datePart = "" then timePart <> ""
                    else scan datePart [| 'Y'; 'M'; 'D' |]

                let timeOk =
                    if timePart = "" then tIdx < 0
                    else scan timePart [| 'H'; 'M'; 'S' |]

                dateOk && timeOk

/// SignalPolicy 불변 조건 검증.
module SignalPolicy =

    let validate (p: SignalPolicy) : Result<unit, string> =
        if p.SignalId = SignalId.empty then Error "SignalPolicy.SignalId is empty"
        elif not (Iso8601Duration.isValid p.Retention) then
            Error (sprintf "Retention '%s' is not a valid ISO-8601 duration" p.Retention)
        elif p.AcquisitionMode = AcquisitionMode.Sampled && p.SamplingIntervalMs.IsNone then
            Error "Sampled acquisition requires SamplingIntervalMs"
        elif p.DeadbandAbsolute.IsSome && p.DeadbandPercent.IsSome then
            Error "DeadbandAbsolute and DeadbandPercent are mutually exclusive"
        elif p.DeadbandAbsolute |> Option.exists (fun value -> value < 0.0) then
            Error "DeadbandAbsolute must be non-negative"
        elif p.DeadbandPercent |> Option.exists (fun value -> value < 0.0 || value > 100.0) then
            Error "DeadbandPercent must be between 0 and 100"
        elif p.EngineeringRangeLow.IsSome <> p.EngineeringRangeHigh.IsSome then
            Error "EngineeringRangeLow and EngineeringRangeHigh must be specified together"
        elif Option.map2 (>=) p.EngineeringRangeLow p.EngineeringRangeHigh |> Option.defaultValue false then
            Error "EngineeringRangeLow must be less than EngineeringRangeHigh"
        elif p.DeadbandPercent.IsSome && p.EngineeringRangeLow.IsNone then
            Error "DeadbandPercent requires EngineeringRangeLow and EngineeringRangeHigh"
        else
            let posOk name v =
                match v with
                | Some n when n <= 0 -> Some (sprintf "%s must be positive" name)
                | _ -> None
            let errs = [
                posOk "SamplingIntervalMs" p.SamplingIntervalMs
                posOk "PublishingIntervalMs" p.PublishingIntervalMs
                posOk "QueueSize" p.QueueSize
            ]
            match errs |> List.choose id with
            | [] -> Ok ()
            | msg :: _ -> Error msg


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

    // Phase 0 · AID InteractionMetadata 신호별 수집 정책.
    // (원 별도 DualSoft "CollectionPolicy" SM 은 SequenceLogging SM 로 흡수됨.)
    member val SignalPolicies = ResizeArray<SignalPolicy>() with get, set

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
