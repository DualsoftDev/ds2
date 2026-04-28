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


// =============================================================================
// SECTION 5.5 ANALYSIS / PLANNING CONTRACTS — AI 가 호출하는 분석·계획 I/O 계약
// =============================================================================
//
// 본 서브모델은 기준정보 선언 계층이므로 실제 MTBF/MTTR·교체계획·EOL 계산은
// Ds2.Runtime/Analytics 가 수행한다. 다만 "AI 가 이 기능을 MCP 로 호출할 때의
// 입출력 모양(Contract)" 은 여기서 정적으로 선언해, 스키마 드리프트를 막는다.
//
// 결과 타입은 전부 값 타입(record / class 계산 없음) 으로, 런타임이 채워 반환하고
// AI 는 이 결과를 읽어 ChangeRequestSpec / WorkOrder 제안에 활용한다.
// =============================================================================

// ---------- 5.5.1 공통 조회 윈도우 ----------

/// 분석 대상 시간 윈도우 (AI 호출 파라미터)
[<Struct>]
type AnalysisWindow = {
    From: DateTime
    To: DateTime
}


// ---------- 5.5.2 신뢰성 분석 결과 (MTBF / MTTR / Availability) ----------

/// 신뢰성 분석 결과 — maintenance.analysis.reliability 도구의 반환 계약
type ReliabilityAnalysisResult() =
    member val EquipmentId: string = "" with get, set
    member val Window: AnalysisWindow = { From = DateTime.MinValue; To = DateTime.MinValue } with get, set
    member val TotalOperatingHours = 0.0 with get, set
    member val FailureCount = 0 with get, set
    member val TotalDowntimeHours = 0.0 with get, set
    member val Mtbf = 0.0 with get, set                     // 평균 고장 간격 (h)
    member val Mttr = 0.0 with get, set                     // 평균 복구 시간 (h)
    member val AvailabilityPercent = 100.0 with get, set    // 가용률 (%)
    member val FailureRatePerYear = 0.0 with get, set       // 고장률 (회/년)
    member val MeetsTarget = true with get, set             // 목표 대비 달성 여부
    member val Notes: string = "" with get, set


// ---------- 5.5.3 EOL 예측 결과 ----------

/// EOL 예측 결과 — maintenance.analysis.eol 도구의 반환 계약
type EolPredictionResult() =
    member val EquipmentId: string = "" with get, set
    member val Indicator = OperatingHours with get, set
    member val CurrentValue = 0.0 with get, set
    member val MaxValue = 0.0 with get, set
    member val RemainingPercent = 100.0 with get, set
    member val Health = Excellent with get, set
    member val EstimatedEol: DateTime option = None with get, set
    member val DaysToEol: int option = None with get, set
    member val Confidence = 0.0 with get, set               // 0.0 ~ 1.0
    member val Method: string = "Linear" with get, set      // "Linear" | "Exponential" | "LSTM"


// ---------- 5.5.4 이상 감지 요약 ----------

/// 이상 감지 요약 — maintenance.analysis.anomaly 도구의 반환 계약.
/// 개별 이벤트 배열이 아니라 "기준정보 관점의 집계" 만 노출해 AI가 임계 튜닝에 사용.
type AnomalySummary() =
    member val SensorId: string = "" with get, set
    member val Window: AnalysisWindow = { From = DateTime.MinValue; To = DateTime.MinValue } with get, set
    member val AnomalyCount = 0 with get, set
    member val WarningCount = 0 with get, set
    member val CriticalCount = 0 with get, set
    member val MaxObservedValue: float option = None with get, set
    member val MinObservedValue: float option = None with get, set
    member val MeanValue: float option = None with get, set
    member val StdDev: float option = None with get, set
    member val FalsePositiveRatio = 0.0 with get, set       // 오알람 비율 (0~1)
    member val SuggestedWarningValue: float option = None with get, set
    member val SuggestedCriticalValue: float option = None with get, set


// ---------- 5.5.4b 고장 예측 결과 (플랫 이력 기반 AI 예측) ----------

/// 고장 예측 결과 — maintenance.analysis.failurepredict 도구의 반환 계약.
/// 입력: DeviceSamplingPolicy 로 수집된 플랫 이력 + FailureRecord / ReplacementRecord.
type FailurePredictionResult() =
    member val EquipmentId: string = "" with get, set
    member val Window: AnalysisWindow = { From = DateTime.MinValue; To = DateTime.MinValue } with get, set
    member val PredictedFailureAt: DateTime option = None with get, set
    member val ProbabilityWithin7Days = 0.0 with get, set   // 0.0 ~ 1.0
    member val ProbabilityWithin30Days = 0.0 with get, set
    member val Confidence = 0.0 with get, set
    member val Method: string = "Statistical" with get, set // "Rule" | "Statistical" | "ML"
    member val TopFeatures: string array = [||] with get, set  // 예: "actuator_duration_trend_up", "temp_drift_0.8C/d"
    member val SimilarPastFailureIds: string array = [||] with get, set // 과거 유사 FailureRecord.Id
    member val RecommendedActions: MaintenanceActionKind array = [||] with get, set
    member val Rationale: string = "" with get, set


// ---------- 5.5.5 교체 계획 제안 ----------

/// 교체 계획 제안 — maintenance.plan.replacement 도구가 생성하고
/// AI 가 ChangeRequestSpec 으로 변환해 승인 요청하는 계약.
type ReplacementPlanProposal() =
    inherit SpecBase<ReplacementPlanProposal>()

    member val EquipmentId: string = "" with get, set
    member val Priority = 3 with get, set                   // 1(low) ~ 5(critical)
    member val PlannedDate: DateTime option = None with get, set
    member val DrivenBy: string = "EOL" with get, set       // "EOL" | "Failure" | "Schedule" | "AI"
    member val EstimatedCost = 0.0 with get, set
    member val Currency: string = "USD" with get, set
    member val EstimatedDowntimeHours = 0.0 with get, set
    member val RequiredPartNumbers: string array = [||] with get, set
    member val Rationale: string = "" with get, set
    member val SourceAnalysisId: string = "" with get, set  // EolPrediction/Reliability 분석 Id


// ---------- 5.5.6 작업 지시 제안 ----------

/// 작업 지시 제안 — maintenance.plan.workorder 도구가 생성.
/// 실제 WorkOrder 발행은 상위 ERP/CMMS 계층이 수행.
type WorkOrderProposal() =
    inherit SpecBase<WorkOrderProposal>()

    member val EquipmentId: string = "" with get, set
    member val TriggerRuleId: string = "" with get, set     // MaintenanceRuleSpec.Id
    member val Strategy = Corrective with get, set
    member val Severity = Moderate with get, set
    member val Actions: MaintenanceActionKind array = [||] with get, set
    member val RequiredPartNumbers: string array = [||] with get, set
    member val EstimatedDurationHours = 0.0 with get, set
    member val Rationale: string = "" with get, set


// =============================================================================
// SECTION 6. MCP CONTRACT SPECS — AI 가 기준정보를 다루는 3대 계약
// =============================================================================

// ---------- 6.1 MCP Tool (AI 호출 가능 동작) ----------

/// MCP 도구 기준정보.
/// Ds2.Mcp 서버가 구현해야 하는 도구의 계약(이름/스키마/권한/승인정책)을 선언한다.
type McpToolSpec() =
    inherit SpecBase<McpToolSpec>()

    member val ToolName: string = "" with get, set                     // 예: "maintenance.sensor.upsert"
    member val Category: string = "" with get, set                     // "Sensor" | "Threshold" | "Rule" | ...
    member val TargetSpecType: string = "" with get, set               // 대상 Spec CLR FullName
    member val Operation = UpdateSpec with get, set
    member val Permission = ReadOnly with get, set
    member val InputSchemaJson: string = "" with get, set              // JSON Schema
    member val OutputSchemaJson: string = "" with get, set             // JSON Schema
    member val RequiresApproval = false with get, set
    member val Idempotent = true with get, set
    member val RateLimitPerMinute = 0 with get, set                    // 0 = 무제한
    member val Description: string = "" with get, set


// ---------- 6.2 MCP Resource (AI 가 읽는 데이터 URI) ----------

/// MCP 리소스 기준정보.
/// AI 가 MCP 를 통해 읽을 수 있는 읽기 전용 데이터 소스의 URI/MIME 계약.
type McpResourceSpec() =
    inherit SpecBase<McpResourceSpec>()

    member val Uri: string = "" with get, set                          // 예: "ds2://maintenance/sensors"
    member val DisplayName: string = "" with get, set
    member val MimeType: string = "application/json" with get, set
    member val TargetSpecType: string = "" with get, set               // 어떤 Spec 을 투영하는가
    member val Description: string = "" with get, set


// ---------- 6.3 MCP Prompt (유지보수 작업 프롬프트 템플릿) ----------

/// MCP 프롬프트 파라미터 정의
type McpPromptParameter() =
    member val Name: string = "" with get, set
    member val DataType: string = "string" with get, set               // "string" | "number" | "boolean"
    member val Required = true with get, set
    member val Description: string = "" with get, set


/// MCP 프롬프트 기준정보.
/// 정비 진단/리포트/규칙 제안 등 반복적으로 사용되는 프롬프트 템플릿을 선언.
type McpPromptSpec() =
    inherit SpecBase<McpPromptSpec>()

    member val PromptName: string = "" with get, set                   // 예: "diagnose-equipment"
    member val DisplayName: string = "" with get, set
    member val Template: string = "" with get, set                     // 변수 치환용 본문
    member val Parameters: McpPromptParameter array = [||] with get, set
    member val Description: string = "" with get, set


// =============================================================================
// SECTION 7. CHANGE / AUDIT SPECS — AI 변경 요청/감사 계약
// =============================================================================

/// 변경 요청 (AI 가 기준정보 수정 시 생성하는 제안/승인 단위)
type ChangeRequestSpec() =
    inherit SpecBase<ChangeRequestSpec>()

    member val RequestedBy: string = "" with get, set                  // AI agent id / user
    member val RequestedAt: DateTime = DateTime.UtcNow with get, set
    member val ToolName: string = "" with get, set                     // 트리거한 McpToolSpec.ToolName
    member val TargetSpecType: string = "" with get, set
    member val TargetSpecId: string = "" with get, set
    member val Operation = UpdateSpec with get, set
    member val BeforeJson: string = "" with get, set                   // 변경 전 스냅샷
    member val AfterJson: string = "" with get, set                    // 변경 후 스냅샷
    member val Reason: string = "" with get, set                       // AI 가 제시한 변경 사유
    member val Status = PendingApproval with get, set
    member val ReviewedBy: string = "" with get, set
    member val ReviewedAt: DateTime option = None with get, set
    member val ReviewComment: string = "" with get, set


/// 감사 로그 엔트리 (실제 반영된 CRUD 이력)
type AuditLogEntry() =
    inherit SpecBase<AuditLogEntry>()

    member val Actor: string = "" with get, set                        // 실제 실행 주체
    member val OccurredAt: DateTime = DateTime.UtcNow with get, set
    member val ToolName: string = "" with get, set
    member val TargetSpecType: string = "" with get, set
    member val TargetSpecId: string = "" with get, set
    member val Operation = UpdateSpec with get, set
    member val ChangeRequestId: string = "" with get, set              // 연결된 ChangeRequestSpec.Id
    member val Success = true with get, set
    member val ErrorMessage: string = "" with get, set


// =============================================================================
// SECTION 8. HIERARCHY PROPERTIES — 계층별 유지보수 바인딩
// =============================================================================

/// System-level — 유지보수 기준정보 레지스트리(단일 진실원)
type MaintenanceSystemProperties() =
    inherit PropertiesBase<MaintenanceSystemProperties>()

    // ---- 8.1 기준정보 레지스트리 ----
    member val Sensors = ResizeArray<SensorSpec>() with get, set
    member val Thresholds = ResizeArray<SensorThresholdSpec>() with get, set
    member val Equipments = ResizeArray<EquipmentSpec>() with get, set
    member val Lifecycles = ResizeArray<LifecycleSpec>() with get, set
    member val SpareParts = ResizeArray<SparePartSpec>() with get, set
    member val Rules = ResizeArray<MaintenanceRuleSpec>() with get, set
    member val Schedules = ResizeArray<MaintenanceScheduleSpec>() with get, set
    member val ErrorTrackings = ResizeArray<ErrorTrackingConfig>() with get, set

    // ---- 8.2 MCP 계약 카탈로그 ----
    member val McpTools = ResizeArray<McpToolSpec>() with get, set
    member val McpResources = ResizeArray<McpResourceSpec>() with get, set
    member val McpPrompts = ResizeArray<McpPromptSpec>() with get, set

    // ---- 8.3 MCP 서버 운영 설정 ----
    member val McpServerName: string = "ds2-maintenance" with get, set
    member val McpEndpoint: string = "stdio" with get, set             // "stdio" | "http://..."
    member val McpProtocolVersion: string = "2025-06-18" with get, set
    member val RequireAiApprovalForWrite = true with get, set
    member val AuditLogEnabled = true with get, set

    // ---- 8.4 변경/감사 저장 ----
    member val ChangeRequests = ResizeArray<ChangeRequestSpec>() with get, set
    member val AuditLog = ResizeArray<AuditLogEntry>() with get, set

    // ---- 8.4b AI 분석/계획 제안 캐시 (런타임이 채우고 AI 가 소비) ----
    member val ReplacementProposals = ResizeArray<ReplacementPlanProposal>() with get, set
    member val WorkOrderProposals = ResizeArray<WorkOrderProposal>() with get, set

    // ---- 8.4c 플랫 이력 저장 (디바이스별 샘플링 + 사용자 사건 입력) ----
    member val SamplingPolicies = ResizeArray<DeviceSamplingPolicy>() with get, set
    member val ActuatorRunLog = ResizeArray<ActuatorRunRecord>() with get, set
    member val AnalogSampleLog = ResizeArray<AnalogSampleRecord>() with get, set
    member val FailureLog = ResizeArray<FailureRecord>() with get, set
    member val ReplacementLog = ResizeArray<ReplacementRecord>() with get, set

    // ---- 8.5 기본 정책 기준값 ----
    member val DefaultTargetAvailabilityPercent = 95.0 with get, set
    member val DefaultTargetMtbfHours = 720.0 with get, set
    member val DefaultTargetMttrHours = 4.0 with get, set
    member val DefaultReplacementLeadTimeDays = 30 with get, set


/// Flow-level — Flow 가 참조하는 설비/규칙 ID 바인딩
type MaintenanceFlowProperties() =
    inherit PropertiesBase<MaintenanceFlowProperties>()

    member val EnableFlowMaintenance = false with get, set
    member val AssociatedEquipmentIds: string array = [||] with get, set
    member val AssociatedRuleIds: string array = [||] with get, set


/// Work-level — Work 가 참조하는 스펙 ID 바인딩 (값 저장 아님)
type MaintenanceWorkProperties() =
    inherit PropertiesBase<MaintenanceWorkProperties>()

    // ---- Work 기본 속성 ----
    member val Motion: string option = None with get, set
    member val Script: string option = None with get, set
    member val ExternalStart = false with get, set
    member val IsFinished = false with get, set
    member val NumRepeat = 0 with get, set
    member val Duration: TimeSpan option = None with get, set
    member val SequenceOrder = 0 with get, set
    member val OperationCode: string option = None with get, set

    // ---- 스펙 참조 (ID 만) ----
    member val EquipmentId: string option = None with get, set
    member val LifecycleSpecId: string option = None with get, set
    member val SensorIds: string array = [||] with get, set
    member val RuleIds: string array = [||] with get, set
    member val ErrorTrackingIds: string array = [||] with get, set


/// Call-level — Call 단위 스펙 ID 바인딩
type MaintenanceCallProperties() =
    inherit PropertiesBase<MaintenanceCallProperties>()

    // ---- Call 기본 속성 ----
    member val ObjectName: string = "" with get, set
    member val ActionName: string = "" with get, set
    member val RobotExecutable: string option = None with get, set
    member val Timeout: TimeSpan option = None with get, set
    member val CallDirection: string option = None with get, set

    // ---- 스펙 참조 ----
    member val EnableCallMaintenance = false with get, set
    member val SensorIds: string array = [||] with get, set
    member val RuleIds: string array = [||] with get, set


// =============================================================================
// SECTION 9. STANDARD MCP TOOL / RESOURCE / PROMPT NAMES
// =============================================================================
//
// 문자열 오타로 인한 계약 파손을 막기 위해 well-known 이름을 모듈 상수로 고정.
// Ds2.Mcp 서버 구현은 이 상수 값을 도구/리소스/프롬프트 이름으로 사용해야 한다.
//
// =============================================================================

module StandardMcpNames =

    // ---- Tools: Sensor ----
    let [<Literal>] ToolSensorList       = "maintenance.sensor.list"
    let [<Literal>] ToolSensorGet        = "maintenance.sensor.get"
    let [<Literal>] ToolSensorUpsert     = "maintenance.sensor.upsert"
    let [<Literal>] ToolSensorDelete     = "maintenance.sensor.delete"

    // ---- Tools: Threshold ----
    let [<Literal>] ToolThresholdList    = "maintenance.threshold.list"
    let [<Literal>] ToolThresholdUpsert  = "maintenance.threshold.upsert"
    let [<Literal>] ToolThresholdDelete  = "maintenance.threshold.delete"

    // ---- Tools: Equipment / Lifecycle / SparePart ----
    let [<Literal>] ToolEquipmentList    = "maintenance.equipment.list"
    let [<Literal>] ToolEquipmentUpsert  = "maintenance.equipment.upsert"
    let [<Literal>] ToolLifecycleUpsert  = "maintenance.lifecycle.upsert"
    let [<Literal>] ToolSparePartUpsert  = "maintenance.sparepart.upsert"

    // ---- Tools: Rule / Schedule / ErrorTracking ----
    let [<Literal>] ToolRuleList         = "maintenance.rule.list"
    let [<Literal>] ToolRuleUpsert       = "maintenance.rule.upsert"
    let [<Literal>] ToolRuleDelete       = "maintenance.rule.delete"
    let [<Literal>] ToolScheduleUpsert   = "maintenance.schedule.upsert"
    let [<Literal>] ToolErrorTrackUpsert = "maintenance.errortrack.upsert"

    // ---- Tools: Sampling Policy & Flat History Logging ----
    let [<Literal>] ToolSamplingPolicyUpsert = "maintenance.sampling.upsert"
    let [<Literal>] ToolActuatorLogAppend    = "maintenance.log.actuator.append"
    let [<Literal>] ToolActuatorLogQuery     = "maintenance.log.actuator.query"
    let [<Literal>] ToolAnalogLogAppend      = "maintenance.log.analog.append"
    let [<Literal>] ToolAnalogLogQuery       = "maintenance.log.analog.query"
    let [<Literal>] ToolFailureReport        = "maintenance.event.failure.report"    // 사용자 고장 입력
    let [<Literal>] ToolFailureUpdate        = "maintenance.event.failure.update"    // 근본원인 사후 입력
    let [<Literal>] ToolReplacementReport    = "maintenance.event.replacement.report" // 사용자 교체 입력

    // ---- Tools: Analysis (AI 호출, 런타임 계산) ----
    let [<Literal>] ToolAnalysisReliability    = "maintenance.analysis.reliability"   // MTBF/MTTR/Availability
    let [<Literal>] ToolAnalysisEol            = "maintenance.analysis.eol"           // EOL 예측
    let [<Literal>] ToolAnalysisAnomaly        = "maintenance.analysis.anomaly"       // 이상 감지 요약
    let [<Literal>] ToolAnalysisFailureTrend   = "maintenance.analysis.failuretrend"  // 고장 추세
    let [<Literal>] ToolAnalysisFailurePredict = "maintenance.analysis.failurepredict" // 플랫 이력 기반 고장 예측
    let [<Literal>] ToolAnalysisSpareStockRisk = "maintenance.analysis.sparestockrisk" // 재고 리스크

    // ---- Tools: Planning (AI 주도 계획 생성) ----
    let [<Literal>] ToolPlanReplacement     = "maintenance.plan.replacement"      // 교체 계획 생성
    let [<Literal>] ToolPlanWorkOrder       = "maintenance.plan.workorder"        // 작업 지시 초안
    let [<Literal>] ToolPlanScheduleOptimize = "maintenance.plan.scheduleoptimize" // 정기점검 최적화
    let [<Literal>] ToolPlanThresholdTune   = "maintenance.plan.thresholdtune"    // 임계값 튜닝 제안

    // ---- Tools: Governance ----
    let [<Literal>] ToolRegistryValidate = "maintenance.registry.validate"
    let [<Literal>] ToolChangeRequestSubmit = "maintenance.change.submit"
    let [<Literal>] ToolChangeRequestApprove = "maintenance.change.approve"
    let [<Literal>] ToolAuditList        = "maintenance.audit.list"

    // ---- Resources ----
    let [<Literal>] ResSensors           = "ds2://maintenance/sensors"
    let [<Literal>] ResThresholds        = "ds2://maintenance/thresholds"
    let [<Literal>] ResEquipments        = "ds2://maintenance/equipments"
    let [<Literal>] ResLifecycles        = "ds2://maintenance/lifecycles"
    let [<Literal>] ResSpareParts        = "ds2://maintenance/spareparts"
    let [<Literal>] ResRules             = "ds2://maintenance/rules"
    let [<Literal>] ResSchedules         = "ds2://maintenance/schedules"
    let [<Literal>] ResErrorTrackings    = "ds2://maintenance/errortrackings"
    let [<Literal>] ResChangeRequests    = "ds2://maintenance/changerequests"
    let [<Literal>] ResAuditLog          = "ds2://maintenance/auditlog"
    let [<Literal>] ResReplacementProposals = "ds2://maintenance/replacementproposals"
    let [<Literal>] ResWorkOrderProposals   = "ds2://maintenance/workorderproposals"
    let [<Literal>] ResSamplingPolicies     = "ds2://maintenance/samplingpolicies"
    let [<Literal>] ResActuatorLog          = "ds2://maintenance/log/actuator"
    let [<Literal>] ResAnalogLog            = "ds2://maintenance/log/analog"
    let [<Literal>] ResFailureLog           = "ds2://maintenance/log/failure"
    let [<Literal>] ResReplacementLog       = "ds2://maintenance/log/replacement"

    // ---- Prompts ----
    let [<Literal>] PromptDiagnoseEquipment   = "diagnose-equipment"
    let [<Literal>] PromptSuggestRule         = "suggest-maintenance-rule"
    let [<Literal>] PromptExplainThreshold    = "explain-threshold"
    let [<Literal>] PromptReviewChangeRequest = "review-change-request"
    let [<Literal>] PromptGenerateReport      = "generate-maintenance-report"
    let [<Literal>] PromptAnalyzeReliability  = "analyze-reliability"
    let [<Literal>] PromptPredictEol          = "predict-eol"
    let [<Literal>] PromptProposeReplacement  = "propose-replacement-plan"
    let [<Literal>] PromptTuneThreshold       = "tune-threshold"
    let [<Literal>] PromptReportFailure       = "report-failure-intake"           // 사용자 고장 입력 가이드
    let [<Literal>] PromptReportReplacement   = "report-replacement-intake"       // 사용자 교체 입력 가이드
    let [<Literal>] PromptPredictFailure      = "predict-failure-from-history"    // 플랫 이력 기반 예측


// =============================================================================
// SECTION 10. REGISTRY LOOKUP & VALIDATION (조회/정적검증 전용, 계산 없음)
// =============================================================================

module MaintenanceRegistry =

    let inline private findFirst (pred: 'T -> bool) (items: ResizeArray<'T>) =
        let mutable result: 'T option = None
        let mutable i = 0
        while result.IsNone && i < items.Count do
            let x = items.[i]
            if pred x then result <- Some x
            i <- i + 1
        result

    // ---- 기본 조회 ----
    let findSensor (sys: MaintenanceSystemProperties) id =
        findFirst (fun (s: SensorSpec) -> s.Id = id) sys.Sensors

    let findEquipment (sys: MaintenanceSystemProperties) id =
        findFirst (fun (e: EquipmentSpec) -> e.Id = id) sys.Equipments

    let findLifecycleByEquipment (sys: MaintenanceSystemProperties) equipmentId =
        findFirst (fun (l: LifecycleSpec) -> l.EquipmentId = equipmentId) sys.Lifecycles

    let findThreshold (sys: MaintenanceSystemProperties) id =
        findFirst (fun (t: SensorThresholdSpec) -> t.Id = id) sys.Thresholds

    let findThresholdsForSensor (sys: MaintenanceSystemProperties) sensorId =
        sys.Thresholds
        |> Seq.filter (fun (t: SensorThresholdSpec) -> t.SensorId = sensorId)
        |> Seq.toArray

    let findRule (sys: MaintenanceSystemProperties) id =
        findFirst (fun (r: MaintenanceRuleSpec) -> r.Id = id) sys.Rules

    let findActiveRulesForEquipment (sys: MaintenanceSystemProperties) equipmentId =
        sys.Rules
        |> Seq.filter (fun (r: MaintenanceRuleSpec) -> r.Enabled && r.EquipmentId = equipmentId)
        |> Seq.toArray

    let findSparePartsForEquipment (sys: MaintenanceSystemProperties) equipmentId =
        sys.SpareParts
        |> Seq.filter (fun (p: SparePartSpec) ->
            p.CompatibleEquipmentIds |> Array.contains equipmentId)
        |> Seq.toArray

    // ---- MCP 계약 조회 ----
    let findMcpTool (sys: MaintenanceSystemProperties) toolName =
        findFirst (fun (t: McpToolSpec) -> t.ToolName = toolName) sys.McpTools

    let findMcpResource (sys: MaintenanceSystemProperties) uri =
        findFirst (fun (r: McpResourceSpec) -> r.Uri = uri) sys.McpResources

    let findMcpPrompt (sys: MaintenanceSystemProperties) name =
        findFirst (fun (p: McpPromptSpec) -> p.PromptName = name) sys.McpPrompts

    // ---- 변경/감사 조회 ----
    let findPendingChangeRequests (sys: MaintenanceSystemProperties) =
        sys.ChangeRequests
        |> Seq.filter (fun (c: ChangeRequestSpec) -> c.Status = PendingApproval)
        |> Seq.toArray

    // ---- 정적 무결성 검증 ----
    /// 기준정보 간 참조 무결성 검증. 반환: 위반 사유 배열 (빈 배열이면 무결).
    let validate (sys: MaintenanceSystemProperties) =
        let errors = ResizeArray<string>()

        // Threshold → Sensor
        for t in sys.Thresholds do
            if (findSensor sys t.SensorId).IsNone then
                errors.Add(sprintf "Threshold '%s' references unknown SensorId '%s'" t.Id t.SensorId)

        // Lifecycle → Equipment / Sensor
        for l in sys.Lifecycles do
            if (findEquipment sys l.EquipmentId).IsNone then
                errors.Add(sprintf "Lifecycle '%s' references unknown EquipmentId '%s'" l.Id l.EquipmentId)
            match l.IndicatorSensorId with
            | Some sid when (findSensor sys sid).IsNone ->
                errors.Add(sprintf "Lifecycle '%s' indicator sensor '%s' not found" l.Id sid)
            | _ -> ()

        // Rule → Equipment / Sensor / Threshold
        for r in sys.Rules do
            if (findEquipment sys r.EquipmentId).IsNone then
                errors.Add(sprintf "Rule '%s' references unknown EquipmentId '%s'" r.Id r.EquipmentId)
            if (findSensor sys r.SensorId).IsNone then
                errors.Add(sprintf "Rule '%s' references unknown SensorId '%s'" r.Id r.SensorId)
            if r.ThresholdSpecId <> "" && (findThreshold sys r.ThresholdSpecId).IsNone then
                errors.Add(sprintf "Rule '%s' references unknown ThresholdSpecId '%s'" r.Id r.ThresholdSpecId)

        // Schedule → Equipment
        for s in sys.Schedules do
            if (findEquipment sys s.EquipmentId).IsNone then
                errors.Add(sprintf "Schedule '%s' references unknown EquipmentId '%s'" s.Id s.EquipmentId)

        // ErrorTracking → Equipment
        for e in sys.ErrorTrackings do
            if e.EquipmentId <> "" && (findEquipment sys e.EquipmentId).IsNone then
                errors.Add(sprintf "ErrorTracking '%s' references unknown EquipmentId '%s'" e.Id e.EquipmentId)

        // Id 중복 검사 (각 레지스트리 내)
        let checkDup name (ids: seq<string>) =
            ids
            |> Seq.groupBy id
            |> Seq.filter (fun (_, g) -> Seq.length g > 1)
            |> Seq.iter (fun (k, _) -> errors.Add(sprintf "Duplicate %s Id '%s'" name k))

        checkDup "Sensor"        (sys.Sensors       |> Seq.map (fun x -> x.Id))
        checkDup "Threshold"     (sys.Thresholds    |> Seq.map (fun x -> x.Id))
        checkDup "Equipment"     (sys.Equipments    |> Seq.map (fun x -> x.Id))
        checkDup "Lifecycle"     (sys.Lifecycles    |> Seq.map (fun x -> x.Id))
        checkDup "SparePart"     (sys.SpareParts    |> Seq.map (fun x -> x.Id))
        checkDup "Rule"          (sys.Rules         |> Seq.map (fun x -> x.Id))
        checkDup "Schedule"      (sys.Schedules     |> Seq.map (fun x -> x.Id))
        checkDup "ErrorTracking" (sys.ErrorTrackings |> Seq.map (fun x -> x.Id))
        checkDup "McpTool"       (sys.McpTools      |> Seq.map (fun x -> x.ToolName))
        checkDup "McpResource"   (sys.McpResources  |> Seq.map (fun x -> x.Uri))
        checkDup "McpPrompt"     (sys.McpPrompts    |> Seq.map (fun x -> x.PromptName))

        errors.ToArray()
