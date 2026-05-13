namespace Ds2.Core

open System

// =============================================================================
// SEQUENCE MAINTENANCE SUBMODEL — Static Reference Schema / MCP Contract Layer
// =============================================================================
//
// 본 파일의 책임:
//   설비 유지보수 시스템이 필요로 하는 **정적 기준정보(Static Reference)** 의
//   스키마와, 그 기준정보를 AI(Claude) 가 MCP(Model Context Protocol) 로
//   조회·수정·승인·감사하기 위한 **계약(Contract)** 을 선언한다.
//
// 본 파일이 다루지 **않는 것**:
//   - 런타임 센서 값 수집, 규칙 평가, 알람 발생, EOL 예측 계산
//     → 상위 Runtime/Analytics 계층에서 수행
//   - MCP 서버/도구 핸들러 구현 → Ds2.Mcp 프로젝트에서 수행
//   - 영속화(DB/JSON) → Storage 계층에서 수행
//
// MCP 프로토콜 대응:
//   - Tools     → McpToolSpec       (AI 가 호출 가능한 동작)
//   - Resources → McpResourceSpec   (AI 가 읽을 수 있는 데이터 URI)
//   - Prompts   → McpPromptSpec     (유지보수 작업 프롬프트 템플릿)
//
// 모든 Spec 은 단일 기반 클래스 SpecBase 를 상속해
// Id / Version / Revision / UpdatedAt / UpdatedBy 감사 메타를 자동 보유한다.
//
// =============================================================================


// =============================================================================
// SECTION 1. ENUMERATIONS — 분류 체계
// =============================================================================

// ---------- 1.1 수명/설비 분류 ----------

/// 설비 라이프사이클 단계
type LifecycleStage =
    | Planning
    | Procurement
    | Installation
    | Commissioning
    | Operation
    | Maintenance
    | Upgrade
    | Decommission
    | Retired

/// 설비 건강 등급 (잔여 수명 구간)
type EquipmentHealth =
    | Excellent
    | Good
    | Fair
    | Warning
    | Critical
    | Exceeded

/// 수명 지표 단위
type LifecycleIndicator =
    | OperatingHours
    | CycleCount
    | Distance
    | UsageDays
    | CustomSensor

/// 보전 전략
type MaintenanceType =
    | Preventive
    | Predictive
    | Corrective
    | ConditionBased

/// 고장 심각도
type FailureSeverity =
    | Minor
    | Moderate
    | Major
    | Catastrophic


// ---------- 1.2 센서/임계 분류 ----------

/// 센서 물리량 종류
type SensorKind =
    | Vibration
    | Temperature
    | Current
    | Voltage
    | Pressure
    | FlowRate
    | Speed
    | Torque
    | Counter
    | RunTime
    | ErrorTagRef          // Logging.ErrorLogTagSpec 참조 센서
    | UserDefined

/// 센서 데이터 타입
type SensorDataType =
    | BoolType
    | IntType
    | FloatType
    | StringType


// ---------- 1.3 규칙/액션 분류 ----------

/// 규칙 트리거 시 수행할 조치
type MaintenanceActionKind =
    | RaiseAlarm
    | CreateWorkOrder
    | NotifyOperator
    | RequestSparePart
    | ScheduleReplacement
    | TriggerDiagnostics
    | EscalateToAi


// ---------- 1.4 MCP/감사 분류 ----------

/// MCP 도구 권한 수준
type McpPermission =
    | ReadOnly
    | ReadWrite
    | ReadWriteDelete
    | Admin

/// MCP 계약 유형 (Tool / Resource / Prompt)
type McpContractKind =
    | ToolContract
    | ResourceContract
    | PromptContract

/// 변경 작업 종류 (감사/승인용)
type ChangeOperation =
    | CreateSpec
    | UpdateSpec
    | DeleteSpec

/// 변경 요청 승인 상태
type ApprovalStatus =
    | PendingApproval
    | Approved
    | Rejected
    | AutoApplied


// =============================================================================
// SECTION 2. BASE — 모든 Spec 공통 메타 (감사/버전)
// =============================================================================

/// 기준정보 공통 메타 베이스.
/// 모든 Spec 은 본 타입을 상속해 식별/버전/감사 필드를 갖는다.
[<AbstractClass>]
type SpecBase<'T when 'T :> SpecBase<'T>>() =
    inherit PropertiesBase<'T>()

    /// 스펙 고유 식별자 (URI 안전: [a-zA-Z0-9._-])
    member val Id: string = "" with get, set
    /// 스펙 스키마 버전 (semver)
    member val SchemaVersion: string = "1.0.0" with get, set
    /// 리비전 번호 (변경 시마다 +1)
    member val Revision = 1 with get, set
    /// 최종 수정 시각 (UTC)
    member val UpdatedAt: DateTime = DateTime.UtcNow with get, set
    /// 최종 수정 주체 (AI agent id / human user id)
    member val UpdatedBy: string = "" with get, set
    /// 사용 여부
    member val Enabled = true with get, set
    /// 태그 (검색/필터링)
    member val Tags: string array = [||] with get, set


// =============================================================================
// SECTION 3. SENSOR SPECS — 모든 유지보수 판단의 최소 단위
// =============================================================================

/// 센서 기준정보 — 측정 대상을 추상화
type SensorSpec() =
    inherit SpecBase<SensorSpec>()

    member val DisplayName: string = "" with get, set
    member val Kind = UserDefined with get, set
    member val DataType = FloatType with get, set
    member val Unit: string = "" with get, set
    member val Address: string option = None with get, set             // PLC 주소
    member val ErrorLogTagName: string option = None with get, set     // Logging 연동
    member val NominalValue: float option = None with get, set
    member val NominalRangeLow: float option = None with get, set
    member val NominalRangeHigh: float option = None with get, set
    member val SamplingIntervalMs = 1000 with get, set
    member val Precision = 2 with get, set                             // 소수점 자릿수
    member val EquipmentId: string option = None with get, set         // 소속 설비
    member val Description: string = "" with get, set


/// 임계값 기준정보 — 한 센서의 경고/위험 구간 선언.
/// 평가 순서: RangeLow/High 가 설정돼 있으면 범위 기반,
///           아니면 WarningValue/CriticalValue 상한 기반으로 판정.
type SensorThresholdSpec() =
    inherit SpecBase<SensorThresholdSpec>()

    member val SensorId: string = "" with get, set                     // SensorSpec.Id 참조
    member val WarningValue: float option = None with get, set
    member val CriticalValue: float option = None with get, set
    member val RangeLow: float option = None with get, set
    member val RangeHigh: float option = None with get, set
    member val HysteresisPercent = 0.0 with get, set
    member val SustainedSeconds = 0 with get, set                      // 유지 시간 조건
    member val StandardRef: string = "" with get, set                  // "ISO 10816" 등


// =============================================================================
// SECTION 4. EQUIPMENT / LIFECYCLE / SPARE PART SPECS
// =============================================================================

/// 설비 기준정보
type EquipmentSpec() =
    inherit SpecBase<EquipmentSpec>()

    member val DisplayName: string = "" with get, set
    member val Manufacturer: string = "" with get, set
    member val Model: string = "" with get, set
    member val SerialNumber: string = "" with get, set
    member val Location: string = "" with get, set
    member val InstalledAt: DateTime option = None with get, set
    member val Criticality = 3 with get, set                           // 1(low) ~ 5(critical)
    member val CurrentStage = Operation with get, set
    member val AssociatedSensorIds: string array = [||] with get, set
    member val Description: string = "" with get, set


/// 수명 기준정보 — EOL 상한 / 건강 등급 컷오프 선언
type LifecycleSpec() =
    inherit SpecBase<LifecycleSpec>()

    member val EquipmentId: string = "" with get, set
    member val PrimaryIndicator = OperatingHours with get, set
    member val IndicatorSensorId: string option = None with get, set
    member val MaxValue = 0.0 with get, set
    member val CutoffExcellent = 70.0 with get, set
    member val CutoffGood = 50.0 with get, set
    member val CutoffFair = 30.0 with get, set
    member val CutoffWarning = 10.0 with get, set
    member val CutoffCritical = 0.0 with get, set
    member val WarnBeforeEolDays = 30 with get, set


/// 예비 부품 기준정보 (재고/조달)
type SparePartSpec() =
    inherit SpecBase<SparePartSpec>()

    member val PartNumber: string = "" with get, set
    member val Description: string = "" with get, set
    member val MinimumStock = 0 with get, set
    member val ReorderPoint = 0 with get, set
    member val LeadTimeDays = 0 with get, set
    member val UnitCost = 0.0 with get, set
    member val Currency: string = "USD" with get, set
    member val Supplier: string = "" with get, set
    member val CompatibleEquipmentIds: string array = [||] with get, set
    member val ReplacementHours = 0.0 with get, set


// =============================================================================
// SECTION 5. RULE / SCHEDULE / ERROR-TRACKING SPECS
// =============================================================================

/// 유지보수 규칙 — 센서 임계 만족 시 선언적 액션 지시
type MaintenanceRuleSpec() =
    inherit SpecBase<MaintenanceRuleSpec>()

    member val DisplayName: string = "" with get, set
    member val EquipmentId: string = "" with get, set
    member val SensorId: string = "" with get, set
    member val ThresholdSpecId: string = "" with get, set              // SensorThresholdSpec.Id
    member val Strategy = Predictive with get, set
    member val Severity = Moderate with get, set
    member val Actions: MaintenanceActionKind array = [||] with get, set
    member val CooldownMinutes = 60 with get, set
    member val Description: string = "" with get, set


/// 정기 유지보수 일정 기준정보
type MaintenanceScheduleSpec() =
    inherit SpecBase<MaintenanceScheduleSpec>()

    member val EquipmentId: string = "" with get, set
    member val Strategy = Preventive with get, set
    member val IntervalDays = 0 with get, set                          // 0 이면 Indicator 기반
    member val IntervalIndicator: LifecycleIndicator option = None with get, set
    member val IntervalValue = 0.0 with get, set
    member val EstimatedDowntimeHours = 0.0 with get, set
    member val RequiredPartNumbers: string array = [||] with get, set
    member val ChecklistItems: string array = [||] with get, set
    member val Description: string = "" with get, set


/// 에러 기반 추적 기준정보 (Logging.ErrorLogTagSpec.Name 참조)
type ErrorTrackingConfig() =
    inherit SpecBase<ErrorTrackingConfig>()

    member val ErrorLogTagName: string = "" with get, set
    member val EquipmentId: string = "" with get, set
    member val WarningCount = 5 with get, set
    member val CriticalCount = 10 with get, set
    member val AnalysisPeriodDays = 30 with get, set
    member val TrendWindowDays = 7 with get, set


// =============================================================================
// SECTION 5.4 DEVICE SAMPLING & FLAT HISTORY — 플랫 이력 저장 계약
// =============================================================================
//
// 운영 전략:
//   - 모든 센서 값을 **디바이스별 주기 설정**에 따라 플랫하게 저장한다.
//   - 단순 액추에이터: 매 동작마다 시작/종료/지속시간 기록 (ActuatorRunRecord)
//   - 아날로그 센서: 주기별 값 샘플 기록 (AnalogSampleRecord) — 압력/온도/토크/전류/진동 등
//   - 고장 및 교체 발생 시 사용자가 개입하여 사건 정보 추가 입력
//     (FailureRecord / ReplacementRecord)
//   - AI 는 위 네 가지 이력(Flat History) 을 학습·패턴 분석 하여 고장 예측 수행.
// =============================================================================

// ---------- 5.4.1 디바이스 샘플링 정책 ----------

/// 디바이스 단위 샘플링 정책 — 각 디바이스가 어떤 주기로 센서를 저장할지 선언
type DeviceSamplingPolicy() =
    inherit SpecBase<DeviceSamplingPolicy>()

    member val DeviceId: string = "" with get, set
    member val EquipmentId: string = "" with get, set
    member val SensorIds: string array = [||] with get, set   // 저장할 SensorSpec.Id 들
    member val AnalogSamplePeriodMs = 1000 with get, set      // 아날로그 주기 (ms)
    member val ActuatorLogEnabled = true with get, set        // 액추에이터 매 동작 기록 여부
    member val RetentionDays = 365 with get, set              // 이력 보관 일수
    member val StoreOnChangeOnly = false with get, set        // 변화 시만 저장 (아날로그 절약)
    member val Description: string = "" with get, set


// ---------- 5.4.2 액추에이터 동작 기록 ----------

/// 액추에이터 동작 1건 기록 (매회 동작마다 적재)
type ActuatorRunRecord() =
    member val RecordId: Guid = Guid.NewGuid() with get, set
    member val DeviceId: string = "" with get, set
    member val SensorId: string = "" with get, set            // RunTime/Counter 계열
    member val EquipmentId: string = "" with get, set
    member val StartedAt: DateTime = DateTime.UtcNow with get, set
    member val EndedAt: DateTime = DateTime.UtcNow with get, set
    member val DurationMs = 0.0 with get, set
    member val Result: string = "OK" with get, set           // "OK" | "Fault" | "Timeout"
    member val SequenceNumber = 0L with get, set              // 누적 카운터


// ---------- 5.4.3 아날로그 주기 샘플 기록 ----------

/// 아날로그 센서 주기 샘플 1건 (압력/온도/토크/진동/전류/전압 등)
[<Struct>]
type AnalogSampleRecord = {
    RecordId: Guid
    SensorId: string
    DeviceId: string
    Timestamp: DateTime
    Value: float
    Quality: string                                           // "Good" | "Bad" | "Uncertain"
}


// ---------- 5.4.4 사용자 입력 — 고장 기록 ----------

/// 고장 발생 사건 (사용자/AI 가 발생 시 입력)
type FailureRecord() =
    inherit SpecBase<FailureRecord>()

    member val EquipmentId: string = "" with get, set
    member val OccurredAt: DateTime = DateTime.UtcNow with get, set
    member val RecoveredAt: DateTime option = None with get, set
    member val DowntimeHours = 0.0 with get, set
    member val Severity = Moderate with get, set
    member val Symptom: string = "" with get, set             // 사용자 서술 (증상)
    member val RootCause: string = "" with get, set           // 원인 분석 (추후 입력 가능)
    member val RepairActions: string array = [||] with get, set
    member val ReportedBy: string = "" with get, set
    member val ReplacedPartNumbers: string array = [||] with get, set
    member val LinkedSensorIds: string array = [||] with get, set   // 관련 센서 (AI 학습 단서)
    member val Notes: string = "" with get, set


// ---------- 5.4.5 사용자 입력 — 교체 기록 ----------

/// 부품 교체 사건 (사용자/AI 가 교체 시 입력)
type ReplacementRecord() =
    inherit SpecBase<ReplacementRecord>()

    member val EquipmentId: string = "" with get, set
    member val PartNumber: string = "" with get, set
    member val ReplacedAt: DateTime = DateTime.UtcNow with get, set
    member val Reason: string = "" with get, set             // "EOL" | "Failure" | "Preventive" | 사용자 서술
    member val CumulativeIndicatorAtReplacement = 0.0 with get, set // 교체 시점 누적 수명 지표값
    member val IndicatorUnit: string = "" with get, set      // "h" | "cycles" | ...
    member val LinkedFailureRecordId: string = "" with get, set    // 연관 FailureRecord.Id
    member val Cost = 0.0 with get, set
    member val PerformedBy: string = "" with get, set
    member val Notes: string = "" with get, set
