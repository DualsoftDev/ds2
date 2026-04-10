namespace Ds2.Core

open System

// =============================================================================
// SEQUENCE COST ANALYSIS SUBMODEL
// Phase 4, Q4 - 원가 분석 및 생산 비용 시뮬레이션
// =============================================================================
//
// 목적:
//   생산 공정의 원가를 정밀 분석하여 투자 수익성 사전 검증
//   - Cost Breakdown: 4대 원가 항목 (인건비/설비비/간접비/재료비)
//   - Cost Simulation: 공정별 원가 시뮬레이션
//   - OEE Tracking: 설비 종합 효율 (가동률/성능률/양품률)
//   - Resource Planning: 자원 계획 및 최적화
//
// 핵심 가치:
//   - 투자 의사결정 지원: 원가 기반 ROI 분석
//   - 원가 절감 목표 설정: 공정별 원가 가시화
//   - 최적 자원 배치: 인력/설비 최적화
//   - 생산 효율 개선: OEE 기반 효율 향상
//
// =============================================================================


// =============================================================================
// PROPERTIES - 원가 분석 속성 클래스
// =============================================================================

/// System-level 원가 분석 속성
type CostAnalysisSystemProperties() =
    inherit PropertiesBase<CostAnalysisSystemProperties>()

    // ========== 기본 원가 분석 설정 ==========
    member val EnableCostAnalysis = false with get, set
    member val EnableCostSimulation = false with get, set
    member val DefaultCurrency = "KRW" with get, set

    // ========== OEE 추적 설정 ==========
    member val EnableOEETracking = false with get, set
    member val OEECalculationInterval = TimeSpan.FromMinutes(30.0) with get, set
    member val TargetOEE = 85.0 with get, set               // World Class 목표 (%)
    member val TargetAvailability = 90.0 with get, set      // 목표 가동률 (%)
    member val TargetPerformance = 95.0 with get, set       // 목표 성능률 (%)
    member val TargetQuality = 98.0 with get, set           // 목표 양품률 (%)

    // ========== 생산 능력 설정 ==========
    member val EnableCapacitySimulation = false with get, set
    member val ProductionLineCount = 1 with get, set
    member val ShiftPattern = "TwoShift" with get, set      // OneShift/TwoShift/ThreeShift/Continuous
    member val ShiftDuration = TimeSpan.FromHours(8.0) with get, set

    // ========== 자재 및 재고 관리 ==========
    member val EnableBOMTracking = false with get, set      // BOM(자재 명세서) 추적
    member val EnableInventorySimulation = false with get, set

    // ========== 품질 관리 ==========
    member val EnableCollisionDetection = false with get, set
    member val EnableQualityTracking = true with get, set
    member val TargetYieldRate = 1.0 with get, set          // 목표 수율 (기본 100%)
    member val TargetDefectRate = 0.0 with get, set         // 목표 불량률 (기본 0%)


/// Flow-level 원가 분석 속성
type CostAnalysisFlowProperties() =
    inherit PropertiesBase<CostAnalysisFlowProperties>()

/// Work-level 원가 분석 속성
type CostAnalysisWorkProperties() =
    inherit PropertiesBase<CostAnalysisWorkProperties>()

    // ========== 기본 작업 속성 ==========
    member val Motion: string option = None with get, set
    member val Script: string option = None with get, set
    member val ExternalStart = false with get, set
    member val IsFinished = false with get, set
    member val NumRepeat = 0 with get, set
    member val Duration: TimeSpan option = None with get, set
    member val SequenceOrder = 0 with get, set
    member val OperationCode: string option = None with get, set

    // ========== 작업 시간 관련 ==========
    member val EstimatedDuration: TimeSpan option = None with get, set
    member val StandardCycleTime = 0.0 with get, set        // 표준 사이클 타임 (초)
    member val ActualCycleTime = 0.0 with get, set          // 실제 사이클 타임 (초)

    // ========== 자원 관리 ==========
    member val RecordStateChanges = false with get, set
    member val EnableResourceContention = false with get, set
    member val ResourceLockDuration: TimeSpan option = None with get, set
    member val WorkerCount = 0 with get, set
    member val SkillLevel = "Intermediate" with get, set    // Novice/Intermediate/Advanced/Expert

    // ========== OEE 추적 데이터 ==========
    member val PlannedOperatingTime = TimeSpan.Zero with get, set
    member val ActualOperatingTime = TimeSpan.Zero with get, set
    member val PlannedProductionQty = 0 with get, set
    member val ActualProductionQty = 0 with get, set
    member val GoodProductQty = 0 with get, set
    member val DefectQty = 0 with get, set
    member val ReworkQty = 0 with get, set
    member val ReworkRate = 0.0 with get, set

    // ========== OEE 계산 결과 ==========
    member val Availability: float option = None with get, set  // 가동률
    member val Performance: float option = None with get, set   // 성능률
    member val Quality: float option = None with get, set       // 양품률
    member val OEE: float option = None with get, set          // 종합 효율

    // ========== 원가 항목 (시간당) ==========
    member val LaborCostPerHour = 0.0 with get, set         // 시간당 인건비
    member val EquipmentCostPerHour = 0.0 with get, set     // 시간당 설비비
    member val OverheadCostPerHour = 0.0 with get, set      // 시간당 간접비
    member val UtilityCostPerHour = 0.0 with get, set       // 시간당 유틸리티 비용
    member val YieldRate = 1.0 with get, set                // 수율 (기본 100%)
    member val DefectRate = 0.0 with get, set               // 불량률 (기본 0%)

    // ========== 원가 계산 결과 ==========
    member val TotalMaterialCost: float option = None with get, set
    member val TotalLaborCost: float option = None with get, set
    member val TotalEquipmentCost: float option = None with get, set
    member val TotalOverheadCost: float option = None with get, set
    member val TotalCost: float option = None with get, set
    member val UnitCost: float option = None with get, set

    /// Call-level 원가 분석 속성
type CostAnalysisCallProperties() =
    inherit PropertiesBase<CostAnalysisCallProperties>()

// =============================================================================
// HELPER FUNCTIONS - 원가 및 OEE 계산 함수
// =============================================================================

module CostAnalysisHelpers =

    // ========== 원가 계산 함수 ==========

    /// 인건비 계산: (시간당 인건비 / 3600) × 작업 시간(초) × 작업자 수
    let calculateLaborCost (laborCostPerHour: float) (durationSeconds: float) (workerCount: int) : float =
        (laborCostPerHour / 3600.0) * durationSeconds * float workerCount

    /// 설비비 계산: (시간당 설비비 / 3600) × 작업 시간(초)
    let calculateEquipmentCost (equipmentCostPerHour: float) (durationSeconds: float) : float =
        (equipmentCostPerHour / 3600.0) * durationSeconds

    /// 간접비 계산: (시간당 간접비 / 3600) × 작업 시간(초)
    let calculateOverheadCost (overheadCostPerHour: float) (durationSeconds: float) : float =
        (overheadCostPerHour / 3600.0) * durationSeconds

    /// 유틸리티 비용 계산: (시간당 유틸리티 비용 / 3600) × 작업 시간(초)
    let calculateUtilityCost (utilityCostPerHour: float) (durationSeconds: float) : float =
        (utilityCostPerHour / 3600.0) * durationSeconds

    /// 총 원가 계산
    let calculateTotalCost (laborCost: float) (equipmentCost: float) (overheadCost: float) (utilityCost: float) : float =
        laborCost + equipmentCost + overheadCost + utilityCost

    /// 단위 원가 계산: 총 원가 / (수율 × (1 - 불량률))
    let calculateUnitCost (totalCost: float) (yieldRate: float) (defectRate: float) : float =
        let effectiveYield = Math.Max(0.01, yieldRate * (1.0 - defectRate))
        totalCost / effectiveYield

    /// 총 원가 구조체로 계산 (한 번에 모든 원가 계산)
    let calculateCostBreakdown
        (laborCostPerHour: float)
        (equipmentCostPerHour: float)
        (overheadCostPerHour: float)
        (utilityCostPerHour: float)
        (durationSeconds: float)
        (workerCount: int)
        (yieldRate: float)
        (defectRate: float) : float * float * float * float * float * float =

        let labor = calculateLaborCost laborCostPerHour durationSeconds workerCount
        let equipment = calculateEquipmentCost equipmentCostPerHour durationSeconds
        let overhead = calculateOverheadCost overheadCostPerHour durationSeconds
        let utility = calculateUtilityCost utilityCostPerHour durationSeconds
        let total = calculateTotalCost labor equipment overhead utility
        let unit = calculateUnitCost total yieldRate defectRate

        (labor, equipment, overhead, utility, total, unit)


    // ========== OEE 계산 함수 ==========

    /// 가동률 계산: 실제 가동 시간 / 계획 가동 시간
    let calculateAvailability (actualTime: TimeSpan) (plannedTime: TimeSpan) : float =
        if plannedTime.TotalSeconds <= 0.0 then 0.0
        else Math.Min(1.0, actualTime.TotalSeconds / plannedTime.TotalSeconds)

    /// 성능률 계산: (실제 생산량 × 표준 사이클 타임) / 실제 가동 시간
    let calculatePerformance (actualQty: int) (standardCycleTime: float) (actualTime: TimeSpan) : float =
        if actualTime.TotalSeconds <= 0.0 then 0.0
        else
            let idealTime = float actualQty * standardCycleTime
            Math.Min(1.0, idealTime / actualTime.TotalSeconds)

    /// 양품률 계산: 양품 수량 / 실제 생산량
    let calculateQuality (goodQty: int) (actualQty: int) : float =
        if actualQty <= 0 then 0.0
        else Math.Min(1.0, float goodQty / float actualQty)

    /// OEE 계산: 가동률 × 성능률 × 양품률
    let calculateOEE (availability: float) (performance: float) (quality: float) : float =
        availability * performance * quality

    /// OEE 구조체로 계산 (한 번에 모든 OEE 지표 계산)
    let calculateOEEMetrics
        (actualTime: TimeSpan)
        (plannedTime: TimeSpan)
        (actualQty: int)
        (standardCycleTime: float)
        (goodQty: int) : float * float * float * float =

        let availability = calculateAvailability actualTime plannedTime
        let performance = calculatePerformance actualQty standardCycleTime actualTime
        let quality = calculateQuality goodQty actualQty
        let oee = calculateOEE availability performance quality

        (availability, performance, quality, oee)

    /// 시간 손실률 계산: (계획 시간 - 실제 시간) / 계획 시간 × 100
    let calculateTimeLoss (plannedTime: TimeSpan) (actualTime: TimeSpan) : float =
        if plannedTime.TotalSeconds > 0.0 then
            ((plannedTime.TotalSeconds - actualTime.TotalSeconds) / plannedTime.TotalSeconds) * 100.0
        else
            0.0

    /// 속도 손실률 계산
    let calculateSpeedLoss (idealTime: float) (actualTime: float) : float =
        if actualTime > 0.0 then
            ((actualTime - idealTime) / actualTime) * 100.0
        else
            0.0

    /// 품질 손실률 계산: (총 생산량 - 양품) / 총 생산량 × 100
    let calculateQualityLoss (totalQty: int) (goodQty: int) : float =
        if totalQty > 0 then
            (float (totalQty - goodQty) / float totalQty) * 100.0
        else
            0.0
