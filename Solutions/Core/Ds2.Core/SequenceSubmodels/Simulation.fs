namespace Ds2.Core

open System



/// 작업자 숙련도
type SimSkillLevel =
    | Beginner
    | Intermediate
    | Advanced
    | Expert

/// 교대 근무 패턴
type SimShiftPattern =
    | OneShift    // 1교대 (주간만)
    | TwoShift    // 2교대 (주간 + 야간)
    | ThreeShift  // 3교대 (24시간)
    | Custom      // 사용자 정의

/// 다운타임 사유
type SimDowntimeReason =
    | PlannedMaintenance    // 계획 정비
    | UnplannedMaintenance  // 돌발 정비
    | MaterialShortage      // 자재 부족
    | OperatorAbsence       // 작업자 부재
    | QualityIssue          // 품질 문제
    | EquipmentFailure      // 설비 고장
    | Changeover            // 제품 전환
    | OtherReason of string

/// 병목 공정 심각도
type SimBottleneckSeverity =
    | Low       // 가동률 70-80%
    | Medium    // 가동률 80-90%
    | High      // 가동률 90-95%
    | Critical  // 가동률 95%+

/// 작업 인력 이벤트 타입
type SimWorkforceEventType =
    | WorkerAssignment   // 작업자 투입
    | WorkerRemoval      // 작업자 이탈
    | ShiftChange        // 교대 변경
    | SkillUpgrade       // 숙련도 향상


    

// ================================
// Simulation Value Types (for MemberwiseClone)
// ================================


type Xywh(x: int, y: int, w: int, h: int) =
    member val X : int = x with get, set
    member val Y : int = y with get, set
    member val W : int = w with get, set
    member val H : int = h with get, set

/// 부품 원가 정보 (struct)
[<Struct>]
type SimPartCostInfo = {
    PartName: string
    PartNumber: string
    Quantity: int
    UnitCost: float
    Supplier: string
    LeadTimeDays: int
}

/// 다운타임 이벤트 (struct)
[<Struct>]
type SimDowntimeEvent = {
    StartTime: DateTime
    EndTime: DateTime
    Reason: SimDowntimeReason
    Description: string
}

/// 교대 근무 정보 (struct)
[<Struct>]
type SimShiftInfo = {
    ShiftNumber: int
    StartTime: TimeSpan
    EndTime: TimeSpan
    WorkerCount: int
    Efficiency: float
}

/// 생산 라인 설정 (struct)
[<Struct>]
type SimProductionLineConfig = {
    LineId: Guid
    LineName: string
    IsActive: bool
    Capacity: int
    WorkerCount: int
}

/// 작업 인력 이벤트 (struct)
[<Struct>]
type SimWorkforceEvent = {
    EventTime: DateTime
    EventType: SimWorkforceEventType
    WorkerCount: int
}



// =============================================================================
// 통합 속성 클래스 (All Properties in Simulation)
// =============================================================================


/// SimulationFlowProperties - Flow-level 모든 속성
type SimulationFlowProperties() =
    inherit PropertiesBase<SimulationFlowProperties>()

    // ========== Flow 레벨 시뮬레이션 설정 ==========
    member val FlowSimulationEnabled = true with get, set
    member val FlowSimulationMode = "" with get, set

/// SimulationSystemProperties - System-level 모든 속성
type SimulationSystemProperties() =
    inherit PropertiesBase<SimulationSystemProperties>()

    // ========== 기본 속성 (구 SystemProperties) ==========
    member val EngineVersion : string option         = None with get, set
    member val LangVersion   : string option         = None with get, set
    member val Author        : string option         = None with get, set
    member val DateTime      : DateTimeOffset option = None with get, set
    member val IRI           : string option         = None with get, set
    member val SystemType    : string option         = None with get, set

    // ========== 기존 속성 (호환성 유지) ==========
    member val SimulationMode = "EventDriven" with get, set
    member val EnablePhysicsSimulation = false with get, set
    member val EnableCollisionDetection = false with get, set
    member val EnableBreakpoints = false with get, set
    // NOTE: BreakpointWorkIds 제거됨 (array는 MemberwiseClone shallow copy 위험)

    // ========== 난수 생성 ==========
    member val RandomSeed: int option = None with get, set
    member val UseRandomVariation = false with get, set
    member val VariationPercentage = 10.0 with get, set

    // ========== 원가 산정 설정 (Activity-Based Costing) ==========
    member val EnableCostSimulation = false with get, set
    member val DefaultCurrency = "KRW" with get, set

    // ========== 설비 효율 추적 ==========
    member val EnableOEETracking = false with get, set
    member val OEECalculationInterval = TimeSpan.FromHours(1.0) with get, set
    member val TargetOEE = 0.85 with get, set

    // ========== 생산 능력 시뮬레이션 ==========
    member val EnableCapacitySimulation = true with get, set
    member val ProductionLineCount = 1 with get, set
    member val ShiftPattern = SimShiftPattern.OneShift with get, set
    member val ShiftDuration = TimeSpan.FromHours(8.0) with get, set
    // NOTE: Shifts, Lines 제거됨 (array는 MemberwiseClone shallow copy 위험)
    // → 필요시 별도 컬렉션으로 관리 (예: DsSystem.ShiftConfigs: ResizeArray<SimShiftInfo>)

    // ========== BOM 및 재고 시뮬레이션 (JIT 기준) ==========
    member val EnableBOMTracking = false with get, set
    member val EnableInventorySimulation = false with get, set

/// SimulationWorkProperties - Work-level 모든 속성
type SimulationWorkProperties() =
    inherit PropertiesBase<SimulationWorkProperties>()

    // ========== 기본 속성 (구 WorkProperties) ==========
    member val Motion        : string option   = None  with get, set
    member val Script        : string option   = None  with get, set
    member val ExternalStart : bool            = false with get, set
    member val IsFinished    : bool            = false with get, set
    member val NumRepeat     : int             = 0     with get, set
    member val Duration      : TimeSpan option = None  with get, set

    // ========== 기본 시뮬레이션 속성 ==========
    member val EstimatedDuration = TimeSpan.Zero with get, set
    member val RecordStateChanges = true with get, set
    member val EnableResourceContention = false with get, set
    member val OperationCode: string option = None with get, set
    // NOTE: RequiredResourceIds 제거됨 (array는 MemberwiseClone shallow copy 위험)
    // → 필요시 별도 컬렉션으로 관리 (예: Work.RequiredResources: ResizeArray<Guid>)
    member val ResourceLockDuration: TimeSpan option = None with get, set

    // ========== 원가 속성 - 인건비 ==========
    member val LaborCostPerHour = 0.0 with get, set
    member val WorkerCount = 1 with get, set
    member val SkillLevel = SimSkillLevel.Intermediate with get, set

    // ========== 원가 속성 - 설비 및 간접비 (Activity 기준) ==========
    member val EquipmentCostPerHour = 0.0 with get, set
    member val OverheadCostPerHour = 0.0 with get, set
    member val UtilityCostPerHour = 0.0 with get, set

    // ========== BOM 및 재료비 ==========
    // NOTE: Parts 제거됨 (array는 MemberwiseClone shallow copy 위험)
    // → 필요시 별도 컬렉션으로 관리 (예: Work.BomParts: ResizeArray<SimPartCostInfo>)

    // ========== 수율 및 불량률 ==========
    member val YieldRate = 1.0 with get, set
    member val DefectRate = 0.0 with get, set
    member val ReworkRate = 0.0 with get, set

    // ========== 원가 계산 결과 ==========
    member val TotalMaterialCost: float option = None with get, set
    member val TotalLaborCost: float option = None with get, set
    member val TotalEquipmentCost: float option = None with get, set
    member val TotalOverheadCost: float option = None with get, set
    member val TotalCost: float option = None with get, set
    member val UnitCost: float option = None with get, set

    // ========== OEE 추적 속성 - 가동률 관련 ==========
    member val PlannedOperatingTime = TimeSpan.Zero with get, set
    member val ActualOperatingTime = TimeSpan.Zero with get, set

    // ========== OEE 추적 속성 - 성능률 관련 ==========
    member val StandardCycleTime = 0.0 with get, set
    member val ActualCycleTime = 0.0 with get, set
    member val PlannedProductionQty = 0 with get, set
    member val ActualProductionQty = 0 with get, set

    // ========== OEE 추적 속성 - 양품률 관련 ==========
    member val GoodProductQty = 0 with get, set
    member val DefectQty = 0 with get, set
    member val ReworkQty = 0 with get, set

    // ========== OEE 계산 결과 ==========
    member val Availability: float option = None with get, set
    member val Performance: float option = None with get, set
    member val Quality: float option = None with get, set
    member val OEE: float option = None with get, set

/// SimulationCallProperties - Call-level 모든 속성
type SimulationCallProperties() =
    inherit PropertiesBase<SimulationCallProperties>()

    // ========== 기본 속성 (구 CallProperties) ==========
    member val CallType    : CallType        = CallType.WaitForCompletion with get, set
    member val Timeout     : TimeSpan option = None with get, set
    member val SensorDelay : int option      = None with get, set

    // ========== 작업 메타데이터 ==========
    member val TaskSequence = 0 with get, set
    member val TaskDescription = "" with get, set
    member val StandardWorkTime = 0.0 with get, set
    member val Quantity = 1 with get, set

    // ========== 실행 정보 ==========
    member val ActualWorkTime: float option = None with get, set
    member val IsManualTask = false with get, set
    member val RequiresJig = false with get, set

    // ========== 품질 관리 ==========
    member val InspectionRequired = false with get, set
    member val InspectionType = "" with get, set
    member val DefectRate: float option = None with get, set

    // ========== API 시뮬레이션 ==========
    member val SimulateApiCall = false with get, set
    member val MockApiResponse = "" with get, set
    member val ApiResponseCode = 200 with get, set

// ================================
// Helper Functions
// ================================

module SimulationHelpers =

    /// 원가 계산 - 재료비
    let calculateMaterialCost (parts: SimPartCostInfo array) (defectRate: float) =
        parts
        |> Array.sumBy (fun p -> float p.Quantity * p.UnitCost)
        |> (*) (1.0 + defectRate)

    /// 원가 계산 - 인건비
    let calculateLaborCost (laborCostPerHour: float) (durationSeconds: float) (workerCount: int) =
        (laborCostPerHour / 3600.0) * durationSeconds * float workerCount

    /// 원가 계산 - 설비 감가상각비 (정액법)
    let calculateEquipmentCost (equipmentCost: float) (lifeYears: float) (durationSeconds: float) =
        let depreciationPerSecond = equipmentCost / (lifeYears * 365.0 * 24.0 * 3600.0)
        durationSeconds * depreciationPerSecond

    /// 원가 계산 - 간접비
    let calculateOverheadCost (directCost: float) (overheadRate: float) =
        directCost * overheadRate

    /// 원가 계산 - 유틸리티 비용
    let calculateUtilityCost (utilityCostPerHour: float) (durationSeconds: float) =
        (utilityCostPerHour / 3600.0) * durationSeconds

    /// OEE 계산 - 가동률
    let calculateAvailability (actualTime: TimeSpan) (plannedTime: TimeSpan) =
        if plannedTime.TotalSeconds > 0.0 then
            actualTime.TotalSeconds / plannedTime.TotalSeconds
        else
            0.0

    /// OEE 계산 - 성능률
    let calculatePerformance (productionQty: int) (standardCT: float) (actualTime: TimeSpan) =
        if actualTime.TotalSeconds > 0.0 then
            (float productionQty * standardCT) / actualTime.TotalSeconds
        else
            0.0

    /// OEE 계산 - 양품률
    let calculateQuality (goodQty: int) (totalQty: int) =
        if totalQty > 0 then
            float goodQty / float totalQty
        else
            0.0

    /// OEE 계산 - 종합
    let calculateOEE (availability: float) (performance: float) (quality: float) =
        availability * performance * quality

    /// 병목 공정 탐지
    let detectBottleneckSeverity (utilizationRate: float) =
        if utilizationRate > 0.95 then SimBottleneckSeverity.Critical
        elif utilizationRate > 0.90 then SimBottleneckSeverity.High
        elif utilizationRate > 0.80 then SimBottleneckSeverity.Medium
        else SimBottleneckSeverity.Low
