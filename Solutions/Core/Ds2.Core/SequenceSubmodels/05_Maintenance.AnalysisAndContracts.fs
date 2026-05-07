namespace Ds2.Core

open System

type AnalysisWindow = {
    From: DateTime
    To: DateTime
}

/// 신뢰성 분석 결과 — maintenance.analysis.reliability 도구의 반환 계약
type ReliabilityAnalysisResult() =
    member val EquipmentId: string = "" with get, set
    member val Window: AnalysisWindow = { From = DateTime.MinValue; To = DateTime.MinValue } with get, set
    member val TotalOperatingHours = 0.0 with get, set
    member val FailureCount = 0 with get, set
    member val TotalDowntimeHours = 0.0 with get, set
    member val Mtbf = 0.0 with get, set
    member val Mttr = 0.0 with get, set
    member val AvailabilityPercent = 100.0 with get, set
    member val FailureRatePerYear = 0.0 with get, set
    member val MeetsTarget = true with get, set
    member val Notes: string = "" with get, set

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
    member val Confidence = 0.0 with get, set
    member val Method: string = "Linear" with get, set

/// 이상 감지 요약 — maintenance.analysis.anomaly 도구의 반환 계약.
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
    member val FalsePositiveRatio = 0.0 with get, set
    member val SuggestedWarningValue: float option = None with get, set
    member val SuggestedCriticalValue: float option = None with get, set

/// 고장 예측 결과 — maintenance.analysis.failurepredict 도구의 반환 계약.
type FailurePredictionResult() =
    member val EquipmentId: string = "" with get, set
    member val Window: AnalysisWindow = { From = DateTime.MinValue; To = DateTime.MinValue } with get, set
    member val PredictedFailureAt: DateTime option = None with get, set
    member val ProbabilityWithin7Days = 0.0 with get, set
    member val ProbabilityWithin30Days = 0.0 with get, set
    member val Confidence = 0.0 with get, set
    member val Method: string = "Statistical" with get, set
    member val TopFeatures: string array = [||] with get, set
    member val SimilarPastFailureIds: string array = [||] with get, set
    member val RecommendedActions: MaintenanceActionKind array = [||] with get, set
    member val Rationale: string = "" with get, set

/// 교체 계획 제안 — maintenance.plan.replacement 도구가 생성하고
/// AI 가 ChangeRequestSpec 으로 변환해 승인 요청하는 계약.
type ReplacementPlanProposal() =
    inherit SpecBase<ReplacementPlanProposal>()

    member val EquipmentId: string = "" with get, set
    member val Priority = 3 with get, set
    member val PlannedDate: DateTime option = None with get, set
    member val DrivenBy: string = "EOL" with get, set
    member val EstimatedCost = 0.0 with get, set
    member val Currency: string = "USD" with get, set
    member val EstimatedDowntimeHours = 0.0 with get, set
    member val RequiredPartNumbers: string array = [||] with get, set
    member val Rationale: string = "" with get, set
    member val SourceAnalysisId: string = "" with get, set

/// 작업 지시 제안 — maintenance.plan.workorder 도구가 생성.
type WorkOrderProposal() =
    inherit SpecBase<WorkOrderProposal>()

    member val EquipmentId: string = "" with get, set
    member val TriggerRuleId: string = "" with get, set
    member val Strategy = Corrective with get, set
    member val Severity = Moderate with get, set
    member val Actions: MaintenanceActionKind array = [||] with get, set
    member val RequiredPartNumbers: string array = [||] with get, set
    member val EstimatedDurationHours = 0.0 with get, set
    member val Rationale: string = "" with get, set

/// MCP 도구 기준정보.
type McpToolSpec() =
    inherit SpecBase<McpToolSpec>()

    member val ToolName: string = "" with get, set
    member val Category: string = "" with get, set
    member val TargetSpecType: string = "" with get, set
    member val Operation = UpdateSpec with get, set
    member val Permission = ReadOnly with get, set
    member val InputSchemaJson: string = "" with get, set
    member val OutputSchemaJson: string = "" with get, set
    member val RequiresApproval = false with get, set
    member val Idempotent = true with get, set
    member val RateLimitPerMinute = 0 with get, set
    member val Description: string = "" with get, set

/// MCP 리소스 기준정보.
type McpResourceSpec() =
    inherit SpecBase<McpResourceSpec>()

    member val Uri: string = "" with get, set
    member val DisplayName: string = "" with get, set
    member val MimeType: string = "application/json" with get, set
    member val TargetSpecType: string = "" with get, set
    member val Description: string = "" with get, set

/// MCP 프롬프트 파라미터 정의
type McpPromptParameter() =
    member val Name: string = "" with get, set
    member val DataType: string = "string" with get, set
    member val Required = true with get, set
    member val Description: string = "" with get, set

/// MCP 프롬프트 기준정보.
type McpPromptSpec() =
    inherit SpecBase<McpPromptSpec>()

    member val PromptName: string = "" with get, set
    member val DisplayName: string = "" with get, set
    member val Template: string = "" with get, set
    member val Parameters: McpPromptParameter array = [||] with get, set
    member val Description: string = "" with get, set

/// 변경 요청 (AI 가 기준정보 수정 시 생성하는 제안/승인 단위)
type ChangeRequestSpec() =
    inherit SpecBase<ChangeRequestSpec>()

    member val RequestedBy: string = "" with get, set
    member val RequestedAt: DateTime = DateTime.UtcNow with get, set
    member val ToolName: string = "" with get, set
    member val TargetSpecType: string = "" with get, set
    member val TargetSpecId: string = "" with get, set
    member val Operation = UpdateSpec with get, set
    member val BeforeJson: string = "" with get, set
    member val AfterJson: string = "" with get, set
    member val Reason: string = "" with get, set
    member val Status = PendingApproval with get, set
    member val ReviewedBy: string = "" with get, set
    member val ReviewedAt: DateTime option = None with get, set
    member val ReviewComment: string = "" with get, set

/// 감사 로그 엔트리 (실제 반영된 CRUD 이력)
type AuditLogEntry() =
    inherit SpecBase<AuditLogEntry>()

    member val Actor: string = "" with get, set
    member val OccurredAt: DateTime = DateTime.UtcNow with get, set
    member val ToolName: string = "" with get, set
    member val TargetSpecType: string = "" with get, set
    member val TargetSpecId: string = "" with get, set
    member val Operation = UpdateSpec with get, set
    member val ChangeRequestId: string = "" with get, set
    member val Success = true with get, set
    member val ErrorMessage: string = "" with get, set
