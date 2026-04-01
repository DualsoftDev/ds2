namespace Ds2.Core

/// Archive strategy for long-term data retention
type ArchiveStrategy =
    | NoArchive
    | Compress
    | ExternalStorage
    | Both

/// LOT number generation source
type LotNumberSource =
    | Auto
    | Manual
    | External

/// External system integration type
type ExternalSystemType =
    | MES
    | ERP
    | LIMS
    | Database
    | RestAPI
    | MessageQueue

/// Data export format
type ExportFormat =
    | JSON
    | XML
    | CSV
    | SQL
    | Custom

/// Synchronization mode with external systems
type SyncMode =
    | Realtime
    | Batch
    | OnDemand

/// Cycle detection method for flow analysis
type CycleDetectionMethod =
    | HeadCallInTag       // Default
    | TailCallOutTag
    | CustomTag of string
    | Manual


    
// ================================
// Logging domain value types
// ================================

/// Runtime statistics using Welford's algorithm
[<Struct>]
type RuntimeStatistics = {
    GoingCount: int
    Average: float
    StdDev: float
    CoefficientOfVariation: float
    M2: float
}

/// Cycle analysis configuration
[<Struct>]
type CycleAnalysisConfig = {
    CycleDetectionMethod: CycleDetectionMethod
    BottleneckThresholdMultiplier: float  // Default: 2.0
    LongGapThresholdMs: int              // Default: 1000
    DetectParallelExecution: bool        // Default: true
    MinSampleSize: int                   // Default: 30
}

/// Flow KPI (Key Performance Indicators)
[<Struct>]
type FlowKPI = {
    TotalCycles: int
    AverageCycleTime: float
    AverageMT: float
    AverageWT: float
    UtilizationRate: float
    ThroughputPerMinute: float
    BottleneckCount: int
    LongGapCount: int
}


// ================================
// Logging Properties Classes
// ================================

/// LoggingFlowProperties - Flow-level 로깅 속성
type LoggingFlowProperties() =
    inherit PropertiesBase<LoggingFlowProperties>()

    // ========== Flow 레벨 로깅 설정 ==========
    member val FlowLoggingEnabled = false with get, set

/// LoggingSystemProperties - 데이터 로깅 및 추적성
type LoggingSystemProperties() =
    inherit PropertiesBase<LoggingSystemProperties>()

    // ========== 불변 레코드 (해시 체인) ==========
    member val EnableHashChain = true with get, set

    // ========== LOT 추적 ==========
    member val EnableLotTracking = false with get, set
    member val LotNumberFormat = "LOT-{YYYYMMDD}-{SeqNo:D4}" with get, set
    member val LotNumberSource = LotNumberSource.Auto with get, set

    // ========== 데이터 보존 ==========
    member val RetentionPeriodDays = 2555 with get, set  // 7 years
    member val ArchiveStrategy = ArchiveStrategy.Both with get, set
    member val AutoArchiveThresholdMB = 1024 with get, set
    member val ArchiveLocation = "" with get, set

    // ========== 외부 시스템 연동 ==========
    member val ExternalSystemType: ExternalSystemType option = None with get, set
    member val ExportFormat = ExportFormat.JSON with get, set
    member val SyncMode = SyncMode.Batch with get, set

    // ========== FDA 21 CFR Part 11 준수 ==========
    member val AccessControlEnabled = true with get, set
    member val BackupSchedule = "" with get, set
    member val ValidationEnabled = true with get, set
    member val TrainingRequired = false with get, set

/// LoggingWorkProperties - Work-level 로깅 설정
type LoggingWorkProperties() =
    inherit PropertiesBase<LoggingWorkProperties>()

    member val LogWorkStart = true with get, set
    member val LogWorkEnd = true with get, set
    member val LogDuration = true with get, set
    member val LogInputs = false with get, set
    member val LogOutputs = false with get, set
    member val LogStateChanges = true with get, set
    member val IncludeOperatorID = false with get, set

/// LoggingCallProperties - Call-level 로깅 설정
type LoggingCallProperties() =
    inherit PropertiesBase<LoggingCallProperties>()

    member val LogCallExecution = true with get, set
    member val LogConditionEvaluation = false with get, set
    member val LogRetryAttempts = true with get, set
    member val LogErrors = true with get, set
    member val LogApiCalls = false with get, set
    member val SensitiveDataMask = true with get, set
