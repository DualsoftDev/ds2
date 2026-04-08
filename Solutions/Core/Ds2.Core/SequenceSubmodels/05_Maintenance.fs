namespace Ds2.Core

open System

// =============================================================================
// SEQUENCE MAINTENANCE SUBMODEL
// =============================================================================
//
// 역할: 에러 관리 및 설비 유지보수 (Error management and device maintenance)
//
// 핵심 기능:
//   - 에러 발생/확인/해결 추적 (Active → Acknowledged → Resolved)
//   - 설비별 에러 이력 관리
//   - 에러 알람 및 통보
//   - 설비 통계 (에러율, 실행 이력)
//   - 에러 차트 데이터 생성
//
// 다른 모듈과의 관계:
//   - 03_Monitoring.fs: Maintenance는 에러 이력, Monitoring은 실시간 알람
//   - 04_Logging.fs: Maintenance는 에러 발생 통계, Logging은 정상 실행 통계
//
// =============================================================================


// =============================================================================
// TYPE DEFINITIONS: Enumerations
// =============================================================================

/// 에러 심각도
type ErrorSeverity =
    | Info = 0
    | Warning = 1
    | Error = 2
    | Critical = 3

/// 에러 상태 (Lifecycle: Active → Acknowledged → Resolved)
type ErrorState =
    | Active        // 현재 발생 중
    | Acknowledged  // 운영자가 확인함
    | Resolved      // 해결됨
    | Ignored       // 무시됨

/// 설비 상태
type DeviceState =
    | DeviceIdle          // 유휴
    | DeviceRunning       // 실행 중
    | DeviceError         // 에러 발생
    | DeviceMaintenance   // 정비 중
    | DeviceStopped       // 정지


// =============================================================================
// TYPE DEFINITIONS: Error Events
// =============================================================================

/// 에러 로그 이벤트 (실시간 발생)
type ErrorLogEvent = {
    Timestamp: DateTime
    CallName: string option
    WorkName: string option
    DeviceName: string option
    ErrorCode: string
    ErrorText: string
    Severity: ErrorSeverity
    TagAddress: string option
    TagValue: obj option
}

/// 에러 이력 레코드 (해결 추적용)
type ErrorHistoryRecord = {
    Id: Guid
    ErrorCode: string
    ErrorText: string
    Severity: ErrorSeverity
    State: ErrorState
    CallName: string option
    WorkName: string option
    DeviceName: string option
    OccurredAt: DateTime
    AcknowledgedAt: DateTime option
    AcknowledgedBy: string option
    ResolvedAt: DateTime option
    ResolvedBy: string option
    DurationMs: float option
}


// =============================================================================
// TYPE DEFINITIONS: Device Management
// =============================================================================

/// 설비 실행 이력
type DeviceExecutionRecord = {
    DeviceName: string
    CallName: string
    WorkName: string option
    StartedAt: DateTime
    FinishedAt: DateTime option
    DurationMs: float option
    State: string
    ErrorText: string option
    CycleCount: int
}

/// 설비 통계
type DeviceStatistics = {
    DeviceName: string
    TotalExecutions: int
    TotalErrors: int
    ErrorRate: float                // % (0.0 ~ 100.0)
    AverageDurationMs: float
    LastExecutedAt: DateTime option
    LastErrorAt: DateTime option
}


// =============================================================================
// TYPE DEFINITIONS: UI Data Models
// =============================================================================

/// 에러 차트 데이터 포인트 (DSPilot.Winform Chart)
type ErrorChartPoint = {
    Timestamp: DateTime
    ErrorCount: int
    ErrorSeverity: ErrorSeverity
    TagName: string option
    TagAddress: string option
}

/// 에러 요약 (시간별/일별/월별 집계)
type ErrorSummary = {
    Period: string                      // "2025-04-08 10:00" | "2025-04-08" | "2025-04"
    TotalErrors: int
    CriticalErrors: int
    ErrorRate: float                    // % (Critical / Total * 100)
    MostFrequentError: string option
    DevicesAffected: int
}


// =============================================================================
// AAS PROPERTIES CLASSES
// =============================================================================

/// System-level 유지보수 속성 (AAS SubmodelElementCollection)
type MaintenanceSystemProperties() =
    inherit PropertiesBase<MaintenanceSystemProperties>()

    // 메타데이터
    member val EngineVersion: string option = None with get, set
    member val LangVersion: string option = None with get, set
    member val Author: string option = None with get, set
    member val DateTime: DateTimeOffset option = None with get, set
    member val IRI: string option = None with get, set
    member val SystemType: string option = None with get, set

    // 에러 로깅 설정
    member val EnableErrorLogging = true with get, set
    member val ErrorLogPath = "./logs/errors" with get, set
    member val ErrorRetentionDays = 90 with get, set
    member val AutoAcknowledgeErrors = false with get, set

    // 에러 알람 설정
    member val EnableErrorAlarm = true with get, set
    member val CriticalErrorAlarmSound = true with get, set
    member val ErrorAlarmThreshold = 3 with get, set

/// Flow-level 유지보수 속성 (AAS SubmodelElementCollection)
type MaintenanceFlowProperties() =
    inherit PropertiesBase<MaintenanceFlowProperties>()

    member val EnableDeviceTracking = false with get, set
    member val DeviceNames = ResizeArray<string>() with get, set

/// Work-level 유지보수 속성 (AAS SubmodelElementCollection)
type MaintenanceWorkProperties() =
    inherit PropertiesBase<MaintenanceWorkProperties>()

    // Work 정의
    member val Motion: string option = None with get, set
    member val Script: string option = None with get, set
    member val ExternalStart = false with get, set
    member val IsFinished = false with get, set
    member val NumRepeat = 0 with get, set
    member val Duration: TimeSpan option = None with get, set
    member val SequenceOrder = 0 with get, set
    member val OperationCode: string option = None with get, set

    // 설비 연결
    member val DeviceName: string option = None with get, set
    member val ErrorText: string option = None with get, set

/// Call-level 유지보수 속성 (AAS SubmodelElementCollection)
type MaintenanceCallProperties() =
    inherit PropertiesBase<MaintenanceCallProperties>()

    // Call 정의
    member val ObjectName: string = "" with get, set
    member val ActionName: string = "" with get, set
    member val RobotExecutable: string option = None with get, set
    member val Timeout: TimeSpan option = None with get, set
    member val CallDirection: string option = None with get, set

    // 설비 연결
    member val DeviceName: string option = None with get, set
    member val ErrorText: string option = None with get, set

    // 에러 재시도 설정
    member val EnableErrorRetry = false with get, set
    member val MaxRetryCount = 3 with get, set
    member val RetryDelayMs = 1000 with get, set


// =============================================================================
// HELPER FUNCTIONS
// =============================================================================

module MaintenanceHelpers =

    // -------------------------------------------------------------------------
    // Type Conversions
    // -------------------------------------------------------------------------

    /// ErrorSeverity → String
    let errorSeverityToString = function
        | ErrorSeverity.Info -> "Info"
        | ErrorSeverity.Warning -> "Warning"
        | ErrorSeverity.Error -> "Error"
        | ErrorSeverity.Critical -> "Critical"
        | _ -> "Unknown"

    /// String → ErrorSeverity
    let parseErrorSeverity (str: string) =
        match str with
        | "Info" -> ErrorSeverity.Info
        | "Warning" -> ErrorSeverity.Warning
        | "Error" -> ErrorSeverity.Error
        | "Critical" -> ErrorSeverity.Critical
        | _ -> ErrorSeverity.Error

    /// ErrorSeverity → Int (정렬용)
    let errorSeverityToInt = function
        | ErrorSeverity.Info -> 0
        | ErrorSeverity.Warning -> 1
        | ErrorSeverity.Error -> 2
        | ErrorSeverity.Critical -> 3
        | _ -> 2

    /// ErrorState → String
    let errorStateToString = function
        | Active -> "Active"
        | Acknowledged -> "Acknowledged"
        | Resolved -> "Resolved"
        | Ignored -> "Ignored"

    /// String → ErrorState
    let parseErrorState (str: string) =
        match str with
        | "Active" -> Active
        | "Acknowledged" -> Acknowledged
        | "Resolved" -> Resolved
        | "Ignored" -> Ignored
        | _ -> Active

    /// DeviceState → String
    let deviceStateToString = function
        | DeviceIdle -> "Idle"
        | DeviceRunning -> "Running"
        | DeviceError -> "Error"
        | DeviceMaintenance -> "Maintenance"
        | DeviceStopped -> "Stopped"

    /// String → DeviceState
    let parseDeviceState (str: string) =
        match str with
        | "Idle" -> DeviceIdle
        | "Running" -> DeviceRunning
        | "Error" -> DeviceError
        | "Maintenance" -> DeviceMaintenance
        | "Stopped" -> DeviceStopped
        | _ -> DeviceIdle


    // -------------------------------------------------------------------------
    // Error Event Creation
    // -------------------------------------------------------------------------

    /// 에러 로그 이벤트 생성
    let createErrorLogEvent
        (errorCode: string)
        (errorText: string)
        (severity: ErrorSeverity)
        (callName: string option)
        (workName: string option)
        (deviceName: string option) : ErrorLogEvent =
        {
            Timestamp = DateTime.UtcNow
            CallName = callName
            WorkName = workName
            DeviceName = deviceName
            ErrorCode = errorCode
            ErrorText = errorText
            Severity = severity
            TagAddress = None
            TagValue = None
        }

    /// 에러 이력 레코드 생성
    let createErrorHistoryRecord (errorEvent: ErrorLogEvent) : ErrorHistoryRecord =
        {
            Id = Guid.NewGuid()
            ErrorCode = errorEvent.ErrorCode
            ErrorText = errorEvent.ErrorText
            Severity = errorEvent.Severity
            State = Active
            CallName = errorEvent.CallName
            WorkName = errorEvent.WorkName
            DeviceName = errorEvent.DeviceName
            OccurredAt = errorEvent.Timestamp
            AcknowledgedAt = None
            AcknowledgedBy = None
            ResolvedAt = None
            ResolvedBy = None
            DurationMs = None
        }


    // -------------------------------------------------------------------------
    // Error Lifecycle Management
    // -------------------------------------------------------------------------

    /// 에러 확인 처리 (Active → Acknowledged)
    let acknowledgeError (record: ErrorHistoryRecord) (userId: string) : ErrorHistoryRecord =
        {
            record with
                State = Acknowledged
                AcknowledgedAt = Some DateTime.UtcNow
                AcknowledgedBy = Some userId
        }

    /// 에러 해결 처리 (Acknowledged → Resolved)
    let resolveError (record: ErrorHistoryRecord) (userId: string) : ErrorHistoryRecord =
        let now = DateTime.UtcNow
        let duration = (now - record.OccurredAt).TotalMilliseconds
        {
            record with
                State = Resolved
                ResolvedAt = Some now
                ResolvedBy = Some userId
                DurationMs = Some duration
        }


    // -------------------------------------------------------------------------
    // Device Statistics
    // -------------------------------------------------------------------------

    /// 에러율 계산 (%)
    let calculateErrorRate (totalExecutions: int) (totalErrors: int) : float =
        if totalExecutions > 0 then
            (float totalErrors / float totalExecutions) * 100.0
        else
            0.0

    /// 설비 통계 생성
    let createDeviceStatistics
        (deviceName: string)
        (executions: DeviceExecutionRecord list) : DeviceStatistics =

        let totalExecutions = executions.Length
        let errorExecutions = executions |> List.filter (fun e -> e.ErrorText.IsSome)
        let totalErrors = errorExecutions.Length

        let avgDuration =
            executions
            |> List.choose (fun e -> e.DurationMs)
            |> fun durations ->
                if durations.IsEmpty then 0.0 else List.average durations

        let lastExecuted =
            executions
            |> List.sortByDescending (fun e -> e.StartedAt)
            |> List.tryHead
            |> Option.map (fun e -> e.StartedAt)

        let lastError =
            errorExecutions
            |> List.sortByDescending (fun e -> e.StartedAt)
            |> List.tryHead
            |> Option.map (fun e -> e.StartedAt)

        {
            DeviceName = deviceName
            TotalExecutions = totalExecutions
            TotalErrors = totalErrors
            ErrorRate = calculateErrorRate totalExecutions totalErrors
            AverageDurationMs = avgDuration
            LastExecutedAt = lastExecuted
            LastErrorAt = lastError
        }


    // -------------------------------------------------------------------------
    // Alarm Detection
    // -------------------------------------------------------------------------

    /// 알람 발생 여부 판단
    let shouldTriggerAlarm (severity: ErrorSeverity) (enableAlarm: bool) : bool =
        enableAlarm && (severity = ErrorSeverity.Critical || severity = ErrorSeverity.Error)

    /// 연속 에러 감지
    let detectConsecutiveErrors (errors: ErrorHistoryRecord list) (threshold: int) : bool =
        errors
        |> List.filter (fun e -> e.State = Active)
        |> List.sortByDescending (fun e -> e.OccurredAt)
        |> List.truncate threshold
        |> fun recentErrors -> recentErrors.Length >= threshold


    // -------------------------------------------------------------------------
    // UI Data Helpers (Chart, Summary)
    // -------------------------------------------------------------------------

    /// 에러 차트 데이터 포인트 생성
    let createErrorChartPoint
        (timestamp: DateTime)
        (errorCount: int)
        (severity: ErrorSeverity)
        (tagName: string option)
        (tagAddress: string option) : ErrorChartPoint =
        {
            Timestamp = timestamp
            ErrorCount = errorCount
            ErrorSeverity = severity
            TagName = tagName
            TagAddress = tagAddress
        }

    /// 에러 요약 생성 (시간별/일별/월별 집계)
    let createErrorSummary
        (period: string)
        (errors: ErrorHistoryRecord list) : ErrorSummary =

        let totalErrors = errors.Length
        let criticalErrors =
            errors
            |> List.filter (fun e -> e.Severity = ErrorSeverity.Critical)
            |> List.length

        let errorRate =
            if totalErrors > 0 then
                (float criticalErrors / float totalErrors) * 100.0
            else
                0.0

        let mostFrequentError =
            errors
            |> List.groupBy (fun e -> e.ErrorCode)
            |> List.sortByDescending (fun (_, group) -> group.Length)
            |> List.tryHead
            |> Option.map fst

        let devicesAffected =
            errors
            |> List.choose (fun e -> e.DeviceName)
            |> List.distinct
            |> List.length

        {
            Period = period
            TotalErrors = totalErrors
            CriticalErrors = criticalErrors
            ErrorRate = errorRate
            MostFrequentError = mostFrequentError
            DevicesAffected = devicesAffected
        }

    /// 시간별 에러 집계
    let aggregateErrorsByHour (errors: ErrorHistoryRecord list) : ErrorSummary list =
        errors
        |> List.groupBy (fun e -> e.OccurredAt.ToString("yyyy-MM-dd HH:00"))
        |> List.map (fun (period, group) -> createErrorSummary period group)
        |> List.sortBy (fun s -> s.Period)

    /// 일별 에러 집계
    let aggregateErrorsByDay (errors: ErrorHistoryRecord list) : ErrorSummary list =
        errors
        |> List.groupBy (fun e -> e.OccurredAt.ToString("yyyy-MM-dd"))
        |> List.map (fun (period, group) -> createErrorSummary period group)
        |> List.sortBy (fun s -> s.Period)
