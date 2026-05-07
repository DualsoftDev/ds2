namespace Ds2.Core

open System

/// System-level — 유지보수 기준정보 레지스트리(단일 진실원)
type MaintenanceSystemProperties() =
    inherit PropertiesBase<MaintenanceSystemProperties>()

    member val Sensors = ResizeArray<SensorSpec>() with get, set
    member val Thresholds = ResizeArray<SensorThresholdSpec>() with get, set
    member val Equipments = ResizeArray<EquipmentSpec>() with get, set
    member val Lifecycles = ResizeArray<LifecycleSpec>() with get, set
    member val SpareParts = ResizeArray<SparePartSpec>() with get, set
    member val Rules = ResizeArray<MaintenanceRuleSpec>() with get, set
    member val Schedules = ResizeArray<MaintenanceScheduleSpec>() with get, set
    member val ErrorTrackings = ResizeArray<ErrorTrackingConfig>() with get, set
    member val McpTools = ResizeArray<McpToolSpec>() with get, set
    member val McpResources = ResizeArray<McpResourceSpec>() with get, set
    member val McpPrompts = ResizeArray<McpPromptSpec>() with get, set
    member val McpServerName: string = "ds2-maintenance" with get, set
    member val McpEndpoint: string = "stdio" with get, set
    member val McpProtocolVersion: string = "2025-06-18" with get, set
    member val RequireAiApprovalForWrite = true with get, set
    member val AuditLogEnabled = true with get, set
    member val ChangeRequests = ResizeArray<ChangeRequestSpec>() with get, set
    member val AuditLog = ResizeArray<AuditLogEntry>() with get, set
    member val ReplacementProposals = ResizeArray<ReplacementPlanProposal>() with get, set
    member val WorkOrderProposals = ResizeArray<WorkOrderProposal>() with get, set
    member val SamplingPolicies = ResizeArray<DeviceSamplingPolicy>() with get, set
    member val ActuatorRunLog = ResizeArray<ActuatorRunRecord>() with get, set
    member val AnalogSampleLog = ResizeArray<AnalogSampleRecord>() with get, set
    member val FailureLog = ResizeArray<FailureRecord>() with get, set
    member val ReplacementLog = ResizeArray<ReplacementRecord>() with get, set
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
    inherit CommonWorkProperties<MaintenanceWorkProperties>()

    member val Duration: TimeSpan option = None with get, set
    member val EquipmentId: string option = None with get, set
    member val LifecycleSpecId: string option = None with get, set
    member val SensorIds: string array = [||] with get, set
    member val RuleIds: string array = [||] with get, set
    member val ErrorTrackingIds: string array = [||] with get, set

/// Call-level — Call 단위 스펙 ID 바인딩
type MaintenanceCallProperties() =
    inherit CommonCallProperties<MaintenanceCallProperties>()

    member val EnableCallMaintenance = false with get, set
    member val SensorIds: string array = [||] with get, set
    member val RuleIds: string array = [||] with get, set
