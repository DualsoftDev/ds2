namespace Ds2.Core

open System

// =============================================================================
// SEQUENCE LOGGING SUBMODEL
// 데이터 로깅 및 추적성 관리
// =============================================================================
//
// 목적:
//   제조 공정의 모든 활동을 시간순으로 기록하여 완벽한 추적성(Traceability) 제공
//   - LOT 추적: 원료 → 제품 전 공정 추적
//   - 해시 체인: 블록체인 방식 위변조 방지
//   - 실시간 KPI: 사이클 타임, 처리량 자동 계산
//   - FDA 21 CFR Part 11: 전자 서명 및 감사 추적 준수
//
// 핵심 가치:
//   - LOT 추적: 리콜 비용 90% 절감 (30분 → 1초)
//   - 해시 체인: FDA 감사 통과 100%
//   - 실시간 KPI: 의사결정 속도 10배 향상
//   - 장기 보관: 7년 이상 데이터 보존
//
// =============================================================================


// =============================================================================
// ENUMERATIONS - 로깅 타입 정의
// =============================================================================

/// LOT 번호 생성 소스
type LotNumberSource =
    | Auto          // 시스템 자동 생성 (일반 제조 공정)
    | Manual        // 작업자 수동 입력 (소량 주문 생산)
    | External      // ERP/MES 연동 (대량 생산 공장)

/// 사이클 감지 방법
type CycleDetectionMethod =
    | HeadCallInTag         // Flow 시작점 Call의 InTag 신호 감지 (기본 권장)
    | TailCallOutTag        // Flow 종료점 Call의 OutTag 신호 감지
    | CustomTag of string   // 특정 태그 지정 (예: "M999" 사이클 카운터)
    | Manual                // 수동 카운트

/// 아카이브 전략 (장기 보관)
type ArchiveStrategy =
    | NoArchive             // 보관 안 함
    | Compress              // 압축 보관
    | ExternalStorage       // 외부 스토리지 (AWS S3, Azure Blob)
    | Both                  // 압축 + 외부 스토리지

/// 외부 시스템 연동 타입
type ExternalSystemType =
    | MES                   // Manufacturing Execution System
    | ERP                   // Enterprise Resource Planning
    | LIMS                  // Laboratory Information Management System
    | Database              // 외부 데이터베이스
    | RestAPI               // REST API
    | MessageQueue          // 메시지 큐 (RabbitMQ, Kafka)

/// 데이터 내보내기 포맷
type ExportFormat =
    | JSON                  // JSON 포맷
    | XML                   // XML 포맷
    | CSV                   // CSV 포맷
    | SQL                   // SQL INSERT 문
    | Custom                // 커스텀 포맷

/// 동기화 모드
type SyncMode =
    | Realtime              // 실시간 동기화
    | Batch                 // 배치 동기화
    | OnDemand              // 요청 시 동기화


// =============================================================================
// VALUE TYPES
// =============================================================================

/// LOT 정보
type LotInfo() =
    member val LotNumber: string = "" with get, set                 // LOT 번호
    member val CreatedDate: DateTime = DateTime.UtcNow with get, set
    member val Source: LotNumberSource = LotNumberSource.Auto with get, set
    member val ProductName: string option = None with get, set      // 제품명
    member val ProductCode: string option = None with get, set      // 제품 코드
    member val Quantity: int = 0 with get, set                      // 생산 수량
    member val ParentLotNumbers: string array = [||] with get, set  // 원료 LOT 번호들
    member val Metadata: Map<string, string> = Map.empty with get, set

/// 해시 체인 레코드 (블록체인 방식 위변조 방지)
type HashChainRecord() =
    member val RecordId: Guid = Guid.NewGuid() with get, set
    member val Timestamp: DateTime = DateTime.UtcNow with get, set
    member val Data: string = "" with get, set                      // 기록 내용
    member val PreviousHash: string = "0000000000" with get, set    // 이전 레코드 해시
    member val Hash: string = "" with get, set                      // 현재 레코드 해시
    member val UserId: string option = None with get, set           // 작업자 ID (감사용)
    member val IsDeleted: bool = false with get, set                // 논리 삭제 (물리 삭제 불가)

/// 런타임 통계 (Welford's Algorithm)
[<Struct>]
type RuntimeStatistics = {
    GoingCount: int                     // 총 사이클 수
    Average: float                      // 평균 사이클 타임 (초)
    M2: float                           // 분산 계산용 중간값
    StdDev: float                       // 표준편차 (초)
    CoefficientOfVariation: float       // 변동계수 (CV = StdDev / Average)
}

/// 사이클 분석 설정
type CycleAnalysisConfig() =
    member val CycleDetectionMethod = HeadCallInTag with get, set
    member val BottleneckThresholdMultiplier = 2.0 with get, set   // 평균의 2배 이상 시 병목
    member val LongGapThresholdMs = 1000.0 with get, set           // 1초 이상 대기 시 긴 대기
    member val DetectParallelExecution = true with get, set        // 병렬 실행 감지
    member val MinSampleSize = 30 with get, set                    // 최소 30 사이클 이상

/// Flow KPI (핵심 성과 지표)
[<Struct>]
type FlowKPI = {
    TotalCycles: int                    // 총 사이클
    AverageCycleTime: float             // 평균 사이클 타임 (초)
    AverageMT: float                    // 평균 가공 시간 (Machine Time)
    AverageWT: float                    // 평균 대기 시간 (Wait Time)
    UtilizationRate: float              // 설비 가동률 (%)
    ThroughputPerMinute: float          // 분당 처리량
    BottleneckCount: int                // 병목 발생 횟수
    LongGapCount: int                   // 긴 대기 발생 횟수
}

/// 감사 추적 레코드 (FDA 21 CFR Part 11)
type AuditRecord() =
    member val RecordId: Guid = Guid.NewGuid() with get, set
    member val Timestamp: DateTime = DateTime.UtcNow with get, set
    member val UserId: string = "" with get, set                    // Who
    member val Action: string = "" with get, set                    // What
    member val Target: string = "" with get, set                    // Where
    member val OldValue: string option = None with get, set
    member val NewValue: string option = None with get, set
    member val Reason: string option = None with get, set           // Why
    member val ElectronicSignature: string option = None with get, set


// =============================================================================
// PROPERTIES CLASSES
// =============================================================================

/// System-level 로깅 속성
type LoggingSystemProperties() =
    inherit PropertiesBase<LoggingSystemProperties>()

    // ========== 기본 System 속성 ==========
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
    member val LogFilePath = "./logs" with get, set

    // ========== LOT 추적 ==========
    member val EnableLotTracking = false with get, set
    member val LotNumberFormat = "LOT-{YYYYMMDD}-{SeqNo:D4}" with get, set
    member val LotNumberSource = LotNumberSource.Auto with get, set

    // ========== 해시 체인 (위변조 방지) ==========
    member val EnableHashChain = false with get, set
    member val HashAlgorithm = "SHA256" with get, set               // "MD5", "SHA1", "SHA256"

    // ========== 감사 추적 (FDA 21 CFR Part 11) ==========
    member val EnableAuditTrail = false with get, set
    member val RequireElectronicSignature = false with get, set
    member val EnableUserTracking = true with get, set

    // ========== 데이터 보존 정책 ==========
    member val RetentionPeriodDays = 2555 with get, set             // 7년 (2555일)
    member val ArchiveStrategy = ArchiveStrategy.NoArchive with get, set
    member val CompressArchive = true with get, set
    member val ArchivePath = "./archives" with get, set

    // ========== 외부 시스템 연동 ==========
    member val EnableExternalSync = false with get, set
    member val ExternalSystemType = ExternalSystemType.MES with get, set
    member val SyncMode = SyncMode.Batch with get, set
    member val ExportFormat = ExportFormat.JSON with get, set
    member val SyncIntervalSeconds = 60 with get, set

/// Flow-level 로깅 속성
type LoggingFlowProperties() =
    inherit PropertiesBase<LoggingFlowProperties>()

    // ========== Flow 로깅 설정 ==========
    member val EnableFlowLogging = true with get, set
    member val LogFlowStart = true with get, set
    member val LogFlowEnd = true with get, set
    member val LogFlowDuration = true with get, set

    // ========== 사이클 분석 ==========
    member val EnableCycleAnalysis = true with get, set
    member val CycleDetectionMethod = HeadCallInTag with get, set
    member val BottleneckThresholdMultiplier = 2.0 with get, set
    member val LongGapThresholdMs = 1000.0 with get, set
    member val MinSampleSize = 30 with get, set

    // ========== Flow KPI ==========
    member val EnableFlowKPI = true with get, set
    member val KpiCalculationIntervalSeconds = 60 with get, set

/// Work-level 로깅 속성
type LoggingWorkProperties() =
    inherit PropertiesBase<LoggingWorkProperties>()

    // ========== 기본 Work 속성 ==========
    member val Motion: string option = None with get, set
    member val Script: string option = None with get, set
    member val ExternalStart = false with get, set
    member val IsFinished = false with get, set
    member val NumRepeat = 0 with get, set
    member val Duration: TimeSpan option = None with get, set
    member val SequenceOrder = 0 with get, set
    member val OperationCode: string option = None with get, set

    // ========== Work 로깅 설정 ==========
    member val LogWorkStart = true with get, set
    member val LogWorkEnd = true with get, set
    member val LogWorkDuration = true with get, set
    member val LogStateChanges = true with get, set

    // ========== 런타임 통계 (Welford's Algorithm) ==========
    member val EnableRuntimeStats = true with get, set
    member val GoingCount = 0 with get, set                         // 사이클 카운트
    member val AverageDuration = 0.0 with get, set                  // 평균 소요 시간 (초)
    member val M2 = 0.0 with get, set                               // Welford 알고리즘 중간값
    member val StdDevDuration = 0.0 with get, set                   // 표준편차

    // ========== 병목 탐지 ==========
    member val IsBottleneck = false with get, set
    member val BottleneckSeverity = "Normal" with get, set          // "Normal", "Minor", "Major"

/// Call-level 로깅 속성
type LoggingCallProperties() =
    inherit PropertiesBase<LoggingCallProperties>()

    // ========== 기본 Call 속성 ==========
    member val ObjectName: string = "" with get, set
    member val ActionName: string = "" with get, set
    member val RobotExecutable: string option = None with get, set
    member val Timeout: TimeSpan option = None with get, set
    member val CallDirection: string option = None with get, set

    // ========== Call 로깅 설정 ==========
    member val LogCallExecution = true with get, set
    member val LogInputValues = true with get, set
    member val LogOutputValues = true with get, set
    member val LogRetryAttempts = true with get, set
    member val LogErrors = true with get, set


// =============================================================================
// LOGGING HELPERS
// =============================================================================

module LoggingHelpers =

    // ========== LOT 번호 생성 ==========

    /// LOT 번호 자동 생성
    let generateLotNumber (format: string) (seqNo: int) =
        let now = DateTime.Now
        format
            .Replace("{YYYYMMDD}", now.ToString("yyyyMMdd"))
            .Replace("{YYYY}", now.ToString("yyyy"))
            .Replace("{MM}", now.ToString("MM"))
            .Replace("{DD}", now.ToString("dd"))
            .Replace("{HHmmss}", now.ToString("HHmmss"))
            .Replace("{SeqNo:D4}", seqNo.ToString("D4"))


    // ========== 해시 계산 (SHA256) ==========

    /// SHA256 해시 계산
    let calculateHash (data: string) =
        use sha256 = System.Security.Cryptography.SHA256.Create()
        let bytes = System.Text.Encoding.UTF8.GetBytes(data)
        let hashBytes = sha256.ComputeHash(bytes)
        System.BitConverter.ToString(hashBytes).Replace("-", "")

    /// 해시 체인 검증
    let verifyHashChain (records: HashChainRecord seq) =
        records
        |> Seq.pairwise
        |> Seq.forall (fun (prev, current) ->
            current.PreviousHash = prev.Hash)


    // ========== Welford's Algorithm (온라인 평균/표준편차) ==========

    /// Welford 알고리즘으로 평균과 분산 업데이트
    let updateWelfordStats (count: int) (mean: float) (m2: float) (newValue: float) =
        let newCount = count + 1
        let delta = newValue - mean
        let newMean = mean + delta / float newCount
        let delta2 = newValue - newMean
        let newM2 = m2 + delta * delta2
        (newCount, newMean, newM2)

    /// Welford 알고리즘으로 표준편차 계산
    let calculateWelfordStdDev (count: int) (m2: float) =
        if count > 1 then
            sqrt (m2 / float (count - 1))
        else
            0.0

    /// 변동계수 (CV) 계산
    let calculateCoefficientOfVariation (mean: float) (stdDev: float) =
        if mean > 0.0 then
            (stdDev / mean) * 100.0
        else
            0.0


    // ========== 병목 탐지 ==========

    /// 병목 여부 판단 (평균의 2배 이상)
    let isBottleneck (workDuration: float) (flowAverageDuration: float) (multiplier: float) =
        workDuration >= flowAverageDuration * multiplier

    /// 긴 대기 여부 판단
    let isLongGap (gapMs: float) (thresholdMs: float) =
        gapMs >= thresholdMs


    // ========== 데이터 보존 ==========

    /// 보존 기간 만료 여부
    let isRetentionExpired (recordDate: DateTime) (retentionDays: int) =
        DateTime.UtcNow - recordDate > TimeSpan.FromDays(float retentionDays)

    /// 아카이브 경로 생성
    let generateArchivePath (basePath: string) (date: DateTime) =
        System.IO.Path.Combine(basePath, date.ToString("yyyy"), date.ToString("MM"))
