namespace Ds2.Core

open System
open System.Security.Cryptography
open System.Text

// =============================================================================
// SEQUENCE SIMULATION SUBMODEL
// Phase 4, Q4 - 생산 능력 시뮬레이션 및 디지털 트윈
// =============================================================================


// =============================================================================
// ENUMERATIONS - 시뮬레이션 타입 정의
// =============================================================================

/// 시뮬레이션 모드
type SimulationMode =
    | EventDriven    // 이벤트 기반 (고속, 권장) - 실시간의 100-1000배
    | TimeStep       // 시간 단계 (물리 시뮬레이션용)
    | Hybrid         // 혼합 모드

/// Capacity Planning 기간
type CapacityHorizon =
    | ShortTerm      // 1-3개월
    | MediumTerm     // 3-12개월
    | LongTerm       // 1년 이상

/// Capacity Planning 전략
type CapacityStrategy =
    | LevelStrategy  // 평준화 전략 (일정 생산량 유지)
    | ChaseStrategy  // 추격 전략 (수요 추종)
    | MixedStrategy  // 혼합 전략 (권장)

/// TOC (Theory of Constraints) 제약 유형
type ConstraintType =
    | TimeConstraint        // 시간 제약: CycleTime > TaktTime
    | QuantityConstraint    // 수량 제약: 자원 수량 부족
    | QualityConstraint     // 품질 제약: 불량률 목표 초과
    | CapabilityConstraint  // 역량 제약: 설비 능력 부족

/// 병목 심각도 (5단계 자동 분류)
type BottleneckSeverity =
    | NoBottleneck          // < 70% 가동률, 여유 충분
    | MinorBottleneck       // 70-80% 가동률, 모니터링 권장
    | ModerateBottleneck    // 80-90% 가동률, 개선 검토 필요
    | MajorBottleneck       // 90-95% 가동률, 즉시 개선 필요
    | CriticalBottleneck    // ≥ 95% 가동률, 긴급 조치 필요

/// TOC 5단계 프로세스
type TocStep =
    | Identify      // 제약 식별
    | Exploit       // 제약 최대 활용
    | Subordinate   // 전체 동기화
    | Elevate       // 제약 능력 향상
    | Repeat        // 반복 (새 제약 탐지)

/// 작업자 숙련도 (Cost Simulation용)
type SimSkillLevel =
    | Novice        // 초보자 (0-6개월)
    | Intermediate  // 중급자 (6-18개월)
    | Advanced      // 숙련자 (18-36개월)
    | Expert        // 전문가 (3년 이상)


// =============================================================================
// VALUE TYPES - Capacity Analysis
// =============================================================================

/// 3단계 Capacity 분석 결과 - Simulation 전용
[<Struct>]
type SimCapacityAnalysis = {
    AnalysisId: Guid
    AnalysisDate: DateTime

    // 3가지 Capacity 유형
    DesignCapacity: float           // 설계 능력 (이론 최대, 24시간 무정지)
    EffectiveCapacity: float        // 실효 능력 (현실 최대, 예방보전 반영)
    ActualCapacity: float           // 실제 능력 (달성 생산량)
    PlannedCapacity: float          // 계획 능력 (목표)

    // 활용률
    DesignUtilization: float        // 실제/설계 (%)
    EffectiveUtilization: float     // 실제/실효 (%)
    CapacityUtilization: float      // 실제/계획 (%)

    // 분석 결과
    CapacityGap: float              // 부족 능력 (Planned - Actual)
    Bottlenecks: string array       // 병목 공정 목록
    RecommendedActions: string array // 개선 권장 사항

    // Planning 정보
    CapacityHorizon: CapacityHorizon
    CapacityStrategy: CapacityStrategy
}

/// Throughput 분석 결과 - Simulation 전용
[<Struct>]
type SimThroughputResult = {
    StartTime: DateTime
    EndTime: DateTime
    ElapsedTime: TimeSpan

    // 생산 실적
    TotalUnitsProduced: int
    ThroughputPerHour: float        // 시간당 처리량
    ThroughputPerDay: float         // 일일 처리량
    ThroughputPerWeek: float        // 주간 처리량
    ThroughputPerMonth: float       // 월간 처리량

    // Takt Time vs Cycle Time
    TaktTime: float                 // 고객 수요 기준 (초/대)
    AverageCycleTime: float         // 실제 작업 시간 (초/대)
    CycleTimeMargin: float          // 여유율 (%)

    // 효율
    TargetThroughput: float         // 목표 처리량
    AchievementRate: float          // 목표 달성률 (%)
    EfficiencyRate: float           // 효율 (%)

    // 시간대별 분석
    HourlyThroughput: (int * float) array // (시간, 처리량) 쌍
}

/// Cycle Time 정밀 분석 (Work 단위) - Simulation 전용
[<Struct>]
type SimCycleTimeAnalysis = {
    WorkId: Guid
    WorkName: string

    // Cycle Time 통계
    DesignCycleTime: float          // 설계 사이클 타임 (초)
    ActualCycleTime: float          // 실제 평균 사이클 타임 (초)
    MinCycleTime: float             // 최소 사이클 타임 (초)
    MaxCycleTime: float             // 최대 사이클 타임 (초)
    StandardDeviation: float        // 표준편차
    VariationCoefficient: float     // 변동계수 (CV) = StdDev/Average

    // 분포 분석
    CycleCount: int                 // 총 사이클 수
    ConfidenceInterval95: float * float // 95% 신뢰구간 (하한, 상한)
    IsNormalDistribution: bool      // 정규분포 여부

    // 설계 대비 분석
    DeviationFromDesign: float      // 설계 대비 편차 (%)
    IsExceedingWarning: bool        // 경고 임계값 초과 여부

    // 병목 분석
    IsBottleneck: bool
    BottleneckSeverity: BottleneckSeverity
    UtilizationRate: float          // 가동률 (%)

    // 개선 가능성
    ImprovementPotential: float     // 개선 여지 (%, Actual→Min 달성 시)
    RecommendedTargetCT: float      // 권장 목표 사이클 타임 (초)
}

/// TOC (Theory of Constraints) 제약 분석 - Simulation 전용
[<Struct>]
type SimConstraintAnalysis = {
    ConstraintId: Guid
    AnalysisDate: DateTime

    // 제약 식별
    ResourceName: string
    ConstraintType: ConstraintType

    // 부하 분석
    CurrentLoad: float              // 현재 부하 (%)
    MaxCapacity: float              // 최대 능력
    RemainingCapacity: float        // 잔여 능력

    // 제약 여부
    IsConstraining: bool            // 제약 조건 여부 (> 90%)
    Severity: BottleneckSeverity    // 심각도

    // 처리량 영향
    ImpactOnThroughput: float       // 처리량 영향도 (%)
    EstimatedGainIfResolved: float  // 해소 시 예상 개선 (units/hour)

    // TOC 5단계 권장
    CurrentTocStep: TocStep         // 현재 적용 단계
    RecommendedActions: string array // 권장 조치
}

/// DBR (Drum-Buffer-Rope) 시스템 상태 - Simulation 전용
[<Struct>]
type SimDbrSystemState = {
    // Drum (제약 공정)
    DrumWorkId: Guid
    DrumWorkName: string
    DrumUtilizationRate: float      // 제약 공정 가동률 (%)

    // Buffer (보호 재고)
    BufferType: string              // "Time" | "Quantity"
    BufferSize: float               // 버퍼 크기 (시간 또는 수량)
    BufferLocation: string          // 버퍼 위치 (제약 직전)
    CurrentBufferLevel: float       // 현재 버퍼 수준
    BufferZone: string              // "Green" | "Yellow" | "Red"

    // Rope (투입 통제)
    RopeReleaseRate: float          // 투입 속도 (units/hour)
    IsRopeSynchronized: bool        // Drum과 동기화 여부

    // 효과
    WipReduction: float             // WIP 감소율 (%)
    LeadTimeReduction: float        // 리드타임 단축 (%)
}

/// Resource Utilization (자원 활용도) - Simulation 전용
[<Struct>]
type SimResourceUtilization = {
    ResourceId: Guid
    ResourceName: string
    ResourceType: string            // "Equipment" | "Worker" | "Material"

    // 시간 분류
    AvailableTime: TimeSpan         // 가용 시간 (계획 가동 시간)
    UsedTime: TimeSpan              // 사용 시간 (생산 + 준비)
    ProductionTime: TimeSpan        // 생산 시간
    ChangeoverTime: TimeSpan        // 준비 교체 시간
    IdleTime: TimeSpan              // 유휴 시간 (대기)
    DownTime: TimeSpan              // 정지 시간 (고장)

    // 활용률 계산
    UtilizationRate: float          // 활용률 = Used/Available (%)
    ProductiveRate: float           // 생산률 = Production/Available (%)
    AvailabilityRate: float         // 가동률 = (Available-Down)/Available (%)
    IdleRate: float                 // 유휴율 = Idle/Available (%)
    DownRate: float                 // 고장률 = Down/Available (%)

    // 벤치마크
    IndustryBenchmark: float        // 산업 벤치마크 활용률 (%)
    TargetUtilization: float        // 목표 활용률 (%)
    PerformanceGap: float           // 목표 대비 갭 (%)

    // 시간대별 추이
    HourlyUtilization: (int * float) array // (시간, 활용률) 쌍
}

/// OEE (Overall Equipment Effectiveness) 추적 - Simulation 전용
[<Struct>]
type SimOeeTracking = {
    ResourceId: Guid
    ResourceName: string
    CalculationDate: DateTime
    CalculationPeriod: TimeSpan

    // OEE 3요소
    Availability: float             // 가동률 = 실제 가동 / 계획 가동
    Performance: float              // 성능률 = (생산량 × 표준CT) / 실제 가동
    Quality: float                  // 양품률 = 양품 / 총 생산
    OEE: float                      // 종합 = 가동률 × 성능률 × 양품률

    // 시간 정보
    PlannedOperatingTime: TimeSpan  // 계획 가동 시간
    ActualOperatingTime: TimeSpan   // 실제 가동 시간

    // 생산 정보
    PlannedProductionQty: int       // 계획 생산량
    ActualProductionQty: int        // 실제 생산량
    GoodProductQty: int             // 양품 수량
    DefectQty: int                  // 불량 수량

    // 손실 분석
    TimeLoss: float                 // 시간 손실 (%)
    SpeedLoss: float                // 속도 손실 (%)
    QualityLoss: float              // 품질 손실 (%)

    // 벤치마크
    TargetOEE: float                // 목표 OEE (일반적으로 85% World Class)
    OeeGap: float                   // 목표 대비 갭 (%)
    OeeClass: string                // "World Class" | "Excellent" | "Good" | "Fair" | "Poor"
}


// =============================================================================
// PROPERTIES - 시뮬레이션 속성 클래스
// =============================================================================

/// System-level 시뮬레이션 속성
type SimulationSystemProperties() =
    inherit PropertiesBase<SimulationSystemProperties>()

    // ========== 시뮬레이션 모드 설정 ==========
    member val SimulationMode = "EventDriven" with get, set
    member val EnablePhysicsSimulation = false with get, set
    member val TimeStepMs = 100 with get, set               // Time-Step 모드 시간 간격 (ms)
    member val EnableBreakpoints = false with get, set

    // ========== 난수 생성 (Random Variation) ==========
    member val RandomSeed: int option = None with get, set  // 재현성 확보용
    member val UseRandomVariation = true with get, set      // 현실적 시뮬레이션
    member val VariationPercentage = 10.0 with get, set     // 변동률 (±%)

    // ========== 시뮬레이션 반복 (몬테카를로) ==========
    member val SimulationRepetitions = 100 with get, set    // 권장: 100회 이상
    member val ConfidenceLevel = 0.99 with get, set         // 신뢰수준 (99%)

    // ========== Capacity Analysis (생산 능력 분석) ==========
    member val EnableCapacityAnalysis = true with get, set
    member val CapacityHorizon = "MediumTerm" with get, set
    member val CapacityStrategy = "MixedStrategy" with get, set

    // Design Capacity (설계 능력)
    member val DesignCapacityPerHour = 60.0 with get, set   // 이론 최대 (units/hour)
    member val DesignHoursPerDay = 24.0 with get, set       // 24시간 무정지 가정

    // Effective Capacity (실효 능력)
    member val EffectiveCapacityPerHour = 50.0 with get, set // 현실 최대 (units/hour)
    member val OperatingHoursPerDay = 16.0 with get, set    // 실제 가동 시간 (2교대)

    // Planned Capacity (계획 능력)
    member val PlannedCapacityPerHour = 50.0 with get, set  // 목표 (units/hour)
    member val OperatingDaysPerWeek = 5.0 with get, set
    member val OperatingWeeksPerMonth = 4.0 with get, set

    // ========== Throughput Analysis (처리량 분석) ==========
    member val EnableThroughputTracking = true with get, set
    member val ThroughputCalculationInterval = TimeSpan.FromHours(1.0) with get, set
    member val TargetThroughputPerHour = 50.0 with get, set

    // Takt Time (고객 수요 기준)
    member val CustomerDemandPerDay = 800.0 with get, set   // 일일 수요 (units)
    member val TaktTime = 72.0 with get, set                // 초/대 (계산값)

    // ========== C/TIME Optimization (사이클 타임 최적화) ==========
    member val EnableCycleTimeAnalysis = true with get, set
    member val CycleTimeWarningThreshold = 10.0 with get, set // 설계 대비 +10% 경고
    member val EnableNormalDistributionTest = true with get, set // 정규분포 검증

    // ========== TOC (Theory of Constraints) ==========
    member val EnableTocAnalysis = true with get, set
    member val EnableBottleneckDetection = true with get, set
    member val BottleneckThreshold = 0.90 with get, set     // 90% 이상 시 병목
    member val CriticalThreshold = 0.95 with get, set       // 95% 이상 시 긴급

    // ========== DBR (Drum-Buffer-Rope) System ==========
    member val EnableDbrSystem = false with get, set
    member val BufferType = "Time" with get, set            // "Time" | "Quantity"
    member val TimeBufferHours = 2.0 with get, set          // 시간 버퍼 (시간)
    member val BufferGreenZone = 1.5 with get, set          // Green: > 1.5시간
    member val BufferYellowZone = 1.0 with get, set         // Yellow: 1.0-1.5시간
    member val BufferRedZone = 1.0 with get, set            // Red: < 1.0시간

    // ========== Resource Utilization (자원 활용도) ==========
    member val EnableResourceUtilization = true with get, set
    member val IndustryType = "Automotive" with get, set    // 산업별 벤치마크용
    member val TargetUtilizationRate = 85.0 with get, set   // 목표 활용률 (%)
    member val EnableLineBalancing = true with get, set     // 라인 균형화 분석

    // ========== OEE (Overall Equipment Effectiveness) ==========
    member val EnableOeeTracking = true with get, set
    member val OeeCalculationInterval = TimeSpan.FromHours(1.0) with get, set
    member val TargetOEE = 85.0 with get, set               // World Class 목표 (%)
    member val TargetAvailability = 90.0 with get, set      // 목표 가동률 (%)
    member val TargetPerformance = 95.0 with get, set       // 목표 성능률 (%)
    member val TargetQuality = 98.0 with get, set           // 목표 양품률 (%)

    // ========== What-If Scenario (시나리오 분석) ==========
    member val EnableScenarioComparison = true with get, set
    member val ScenarioCount = 3 with get, set              // 최소 3개 시나리오 권장
    member val EnableRoiCalculation = true with get, set    // ROI 자동 계산


/// Flow-level 시뮬레이션 속성
type SimulationFlowProperties() =
    inherit PropertiesBase<SimulationFlowProperties>()

    member val FlowSimulationEnabled = true with get, set
    member val FlowSimulationMode = "EventDriven" with get, set

/// Work-level 시뮬레이션 속성
type SimulationWorkProperties() =
    inherit PropertiesBase<SimulationWorkProperties>()

    // ========== 기본 Work 속성 ==========
    member val Motion: string option = None with get, set
    member val Script: string option = None with get, set
    member val ExternalStart = false with get, set
    member val IsFinished = false with get, set
    member val NumRepeat = 0 with get, set
    member val SequenceOrder = 0 with get, set
    member val OperationCode: string option = None with get, set

    // ========== Cycle Time 분석 ==========
    member val DesignCycleTime = 0.0 with get, set          // 설계 사이클 타임 (초)
    member val ActualCycleTime = 0.0 with get, set          // 실제 평균 (초)
    member val MinCycleTime = Double.MaxValue with get, set // 최소 (초)
    member val MaxCycleTime = 0.0 with get, set             // 최대 (초)
    member val CycleTimeSum = 0.0 with get, set             // 합계 (Welford 알고리즘용)
    member val CycleTimeSumOfSquares = 0.0 with get, set    // 제곱합
    member val CycleTimeStdDev = 0.0 with get, set          // 표준편차
    member val CycleCount = 0 with get, set                 // 사이클 카운트

    // ========== 병목 분석 ==========
    member val IsBottleneck = false with get, set
    member val BottleneckSeverity = "NoBottleneck" with get, set
    member val UtilizationRate = 0.0 with get, set          // 가동률 (%)

    // ========== 생산 능력 ==========
    member val ProcessingTimePerUnit = 0.0 with get, set    // 단위당 처리 시간 (초)
    member val SetupTime = 0.0 with get, set                // 준비 시간 (초)
    member val TeardownTime = 0.0 with get, set             // 정리 시간 (초)
    member val BatchSize = 1 with get, set                  // 배치 크기
    member val MaxParallelExecutions = 1 with get, set      // 최대 병렬 실행 수

    // ========== 처리량 ==========
    member val TotalUnitsProcessed = 0 with get, set
    member val ThroughputPerHour: float option = None with get, set
    member val ThroughputPerDay: float option = None with get, set

    // ========== Resource Utilization (자원 활용도) ==========
    member val AvailableTime = TimeSpan.Zero with get, set
    member val UsedTime = TimeSpan.Zero with get, set
    member val ProductionTime = TimeSpan.Zero with get, set
    member val ChangeoverTime = TimeSpan.Zero with get, set
    member val IdleTime = TimeSpan.Zero with get, set
    member val DownTime = TimeSpan.Zero with get, set

    // ========== OEE 추적 ==========
    member val PlannedOperatingTime = TimeSpan.Zero with get, set
    member val ActualOperatingTime = TimeSpan.Zero with get, set
    member val PlannedProductionQty = 0 with get, set
    member val ActualProductionQty = 0 with get, set
    member val GoodProductQty = 0 with get, set
    member val DefectQty = 0 with get, set
    member val Availability: float option = None with get, set
    member val Performance: float option = None with get, set
    member val Quality: float option = None with get, set
    member val OEE: float option = None with get, set

    // ========== TOC 제약 분석 ==========
    member val ConstraintType: string option = None with get, set
    member val IsConstraining = false with get, set
    member val CurrentTocStep = "Identify" with get, set

    // ========== DBR System (Drum인 경우) ==========
    member val IsDrum = false with get, set                 // Drum (제약 공정) 여부
    member val HasBuffer = false with get, set              // Buffer 보유 여부
    member val BufferSize = 0.0 with get, set               // 버퍼 크기
    member val CurrentBufferLevel = 0.0 with get, set       // 현재 버퍼 수준


/// Call-level 시뮬레이션 속성
type SimulationCallProperties() =
    inherit PropertiesBase<SimulationCallProperties>()

    // ========== 기본 Call 속성 ==========
    member val CallType = CallType.WaitForCompletion with get, set
    member val Timeout: TimeSpan option = None with get, set
    member val SensorDelay: int option = None with get, set

    // ========== Call 실행 시간 추적 ==========
    member val StandardExecutionTime = 0.0 with get, set    // 표준 실행 시간 (초)
    member val ActualExecutionTime: float option = None with get, set
    member val MinExecutionTime = Double.MaxValue with get, set
    member val MaxExecutionTime = 0.0 with get, set

    // ========== API 시뮬레이션 ==========
    member val SimulateApiCall = false with get, set
    member val MockApiResponse = "" with get, set
    member val ApiResponseCode = 200 with get, set
    member val ApiLatencyMs = 100 with get, set


// =============================================================================
// HELPER FUNCTIONS - 계산 함수
// =============================================================================

module SimulationHelpers =

    // ========== Capacity Analysis 함수 ==========

    /// Design Capacity 계산 (일일)
    let calculateDesignCapacityPerDay (capacityPerHour: float) (hoursPerDay: float) =
        capacityPerHour * hoursPerDay

    /// Effective Capacity 계산 (일일)
    let calculateEffectiveCapacityPerDay (capacityPerHour: float) (operatingHours: float) =
        capacityPerHour * operatingHours

    /// Planned Capacity 계산 (월간)
    let calculatePlannedCapacityPerMonth
        (capacityPerHour: float)
        (hoursPerDay: float)
        (daysPerWeek: float)
        (weeksPerMonth: float) =
        capacityPerHour * hoursPerDay * daysPerWeek * weeksPerMonth

    /// Design Utilization 계산 (실제/설계)
    let calculateDesignUtilization (actualCapacity: float) (designCapacity: float) =
        if designCapacity > 0.0 then
            (actualCapacity / designCapacity) * 100.0
        else
            0.0

    /// Effective Utilization 계산 (실제/실효)
    let calculateEffectiveUtilization (actualCapacity: float) (effectiveCapacity: float) =
        if effectiveCapacity > 0.0 then
            (actualCapacity / effectiveCapacity) * 100.0
        else
            0.0

    /// Capacity Utilization 계산 (실제/계획)
    let calculateCapacityUtilization (actualCapacity: float) (plannedCapacity: float) =
        if plannedCapacity > 0.0 then
            (actualCapacity / plannedCapacity) * 100.0
        else
            0.0

    /// Capacity Gap 계산 (부족분)
    let calculateCapacityGap (plannedCapacity: float) (actualCapacity: float) =
        plannedCapacity - actualCapacity


    // ========== Throughput Analysis 함수 ==========

    /// Throughput Per Hour 계산
    let calculateThroughputPerHour (totalUnits: int) (elapsedHours: float) =
        if elapsedHours > 0.0 then
            float totalUnits / elapsedHours
        else
            0.0

    /// Throughput Per Day 계산
    let calculateThroughputPerDay (throughputPerHour: float) (operatingHoursPerDay: float) =
        throughputPerHour * operatingHoursPerDay

    /// Throughput Per Week 계산
    let calculateThroughputPerWeek (throughputPerDay: float) (operatingDaysPerWeek: float) =
        throughputPerDay * operatingDaysPerWeek

    /// Throughput Per Month 계산
    let calculateThroughputPerMonth (throughputPerWeek: float) (operatingWeeksPerMonth: float) =
        throughputPerWeek * operatingWeeksPerMonth

    /// Takt Time 계산 (고객 수요 기준)
    let calculateTaktTime (availableTimeSeconds: float) (customerDemand: float) =
        if customerDemand > 0.0 then
            availableTimeSeconds / customerDemand
        else
            0.0

    /// Cycle Time Margin 계산 (여유율)
    let calculateCycleTimeMargin (taktTime: float) (cycleTime: float) =
        if taktTime > 0.0 then
            ((taktTime - cycleTime) / taktTime) * 100.0
        else
            0.0

    /// Achievement Rate 계산 (목표 달성률)
    let calculateAchievementRate (actualThroughput: float) (targetThroughput: float) =
        if targetThroughput > 0.0 then
            (actualThroughput / targetThroughput) * 100.0
        else
            0.0


    // ========== Cycle Time Analysis 함수 ==========

    /// Cycle Time Variation Coefficient (CV) 계산
    let calculateCycleTimeCV (stdDev: float) (average: float) =
        if average > 0.0 then
            stdDev / average
        else
            0.0

    /// 설계 대비 편차 계산
    let calculateDeviationFromDesign (actualCT: float) (designCT: float) =
        if designCT > 0.0 then
            ((actualCT - designCT) / designCT) * 100.0
        else
            0.0

    /// Cycle Time 경고 임계값 초과 여부
    let isCycleTimeExceedingWarning (actualCT: float) (designCT: float) (warningThreshold: float) =
        let deviation = calculateDeviationFromDesign actualCT designCT
        deviation > warningThreshold

    /// 95% 신뢰구간 계산
    let calculateConfidenceInterval95 (average: float) (stdDev: float) (count: int) =
        if count > 1 then
            let z = 1.96 // 95% 신뢰수준
            let sem = stdDev / sqrt (float count) // Standard Error of Mean
            let margin = z * sem
            (average - margin, average + margin)
        else
            (average, average)

    /// 개선 가능성 계산 (%)
    let calculateImprovementPotential (actualCT: float) (minCT: float) =
        if actualCT > 0.0 && minCT > 0.0 && actualCT > minCT then
            ((actualCT - minCT) / actualCT) * 100.0
        else
            0.0

    /// 권장 목표 Cycle Time 계산
    let calculateRecommendedTargetCT (minCT: float) (avgCT: float) =
        // 최소값과 평균값 사이의 80% 지점을 권장
        minCT + (avgCT - minCT) * 0.2


    // ========== TOC (Theory of Constraints) 함수 ==========

    /// 병목 심각도 탐지
    let detectBottleneckSeverity (utilizationRate: float) =
        if utilizationRate >= 0.95 then "CriticalBottleneck"
        elif utilizationRate >= 0.90 then "MajorBottleneck"
        elif utilizationRate >= 0.80 then "ModerateBottleneck"
        elif utilizationRate >= 0.70 then "MinorBottleneck"
        else "NoBottleneck"

    /// 제약 여부 판단
    let isConstraining (utilizationRate: float) (threshold: float) =
        utilizationRate >= threshold

    /// 제약 유형 자동 탐지
    let detectConstraintType
        (cycleTime: float)
        (taktTime: float)
        (utilization: float)
        (defectRate: float)
        (targetQuality: float) =

        if cycleTime > taktTime then
            Some "TimeConstraint"
        elif utilization > 0.90 then
            Some "QuantityConstraint"
        elif defectRate > targetQuality then
            Some "QualityConstraint"
        else
            None

    /// 처리량 영향도 계산 (제약 해소 시 개선 예상)
    let calculateConstraintImpact
        (currentLoad: float)
        (maxCapacity: float)
        (systemThroughput: float) =

        if maxCapacity > 0.0 && currentLoad > maxCapacity * 0.90 then
            let constraintRate = currentLoad / maxCapacity
            let excessRate = constraintRate - 0.90
            excessRate * systemThroughput * 10.0 // 영향도 추정
        else
            0.0

    /// 제약 해소 시 예상 개선량 계산
    let estimateGainIfResolved
        (currentThroughput: float)
        (utilizationRate: float) =

        if utilizationRate >= 0.90 then
            // 가동률을 80%로 낮추면 얻을 수 있는 개선량
            let targetUtilization = 0.80
            let capacityIncrease = (utilizationRate - targetUtilization) / utilizationRate
            currentThroughput * capacityIncrease / utilizationRate
        else
            0.0


    // ========== DBR (Drum-Buffer-Rope) 함수 ==========

    /// 버퍼 존 판단 (Green/Yellow/Red)
    let determineBufferZone
        (currentLevel: float)
        (greenThreshold: float)
        (yellowThreshold: float) =

        if currentLevel >= greenThreshold then "Green"
        elif currentLevel >= yellowThreshold then "Yellow"
        else "Red"

    /// Rope 동기화 여부 확인
    let isRopeSynchronized
        (releaseRate: float)
        (drumRate: float)
        (tolerance: float) =

        let difference = abs (releaseRate - drumRate)
        difference <= (drumRate * tolerance)

    /// WIP 감소율 계산
    let calculateWipReduction (beforeWip: float) (afterWip: float) =
        if beforeWip > 0.0 then
            ((beforeWip - afterWip) / beforeWip) * 100.0
        else
            0.0

    /// 리드타임 단축 계산
    let calculateLeadTimeReduction (beforeLeadTime: float) (afterLeadTime: float) =
        if beforeLeadTime > 0.0 then
            ((beforeLeadTime - afterLeadTime) / beforeLeadTime) * 100.0
        else
            0.0


    // ========== Resource Utilization 함수 ==========

    /// Utilization Rate 계산 (활용률)
    let calculateUtilizationRate (usedTime: TimeSpan) (availableTime: TimeSpan) =
        if availableTime.TotalSeconds > 0.0 then
            (usedTime.TotalSeconds / availableTime.TotalSeconds) * 100.0
        else
            0.0

    /// Productive Rate 계산 (생산률)
    let calculateProductiveRate (productionTime: TimeSpan) (availableTime: TimeSpan) =
        if availableTime.TotalSeconds > 0.0 then
            (productionTime.TotalSeconds / availableTime.TotalSeconds) * 100.0
        else
            0.0

    /// Availability Rate 계산 (가동률)
    let calculateAvailabilityRate (availableTime: TimeSpan) (downTime: TimeSpan) =
        if availableTime.TotalSeconds > 0.0 then
            let upTime = availableTime.TotalSeconds - downTime.TotalSeconds
            (upTime / availableTime.TotalSeconds) * 100.0
        else
            0.0

    /// Idle Rate 계산 (유휴율)
    let calculateIdleRate (idleTime: TimeSpan) (availableTime: TimeSpan) =
        if availableTime.TotalSeconds > 0.0 then
            (idleTime.TotalSeconds / availableTime.TotalSeconds) * 100.0
        else
            0.0

    /// Down Rate 계산 (고장률)
    let calculateDownRate (downTime: TimeSpan) (availableTime: TimeSpan) =
        if availableTime.TotalSeconds > 0.0 then
            (downTime.TotalSeconds / availableTime.TotalSeconds) * 100.0
        else
            0.0

    /// Performance Gap 계산 (목표 대비 갭)
    let calculatePerformanceGap (actualUtilization: float) (targetUtilization: float) =
        targetUtilization - actualUtilization

    /// 산업별 벤치마크 조회
    let getIndustryBenchmark (industryType: string) =
        match industryType.ToLower() with
        | "automotive" -> 87.5          // 자동차: 85-90% (World Class ≥92%)
        | "electronics" | "semiconductor" -> 92.5 // 전자/반도체: 90-95% (WC ≥97%)
        | "food" | "beverage" -> 80.0   // 식품/음료: 75-85% (WC ≥87%)
        | "pharmaceutical" -> 75.0      // 제약: 70-80% (WC ≥85%)
        | "metal" | "steel" -> 85.0     // 금속/철강: 80-90% (WC ≥92%)
        | "plastic" -> 80.0             // 플라스틱: 75-85% (WC ≥88%)
        | "logistics" | "packaging" -> 77.5 // 물류/포장: 70-85% (WC ≥87%)
        | _ -> 80.0                     // 기본값


    // ========== OEE (Overall Equipment Effectiveness) 함수 ==========

    /// OEE - Availability (가동률) 계산
    let calculateOeeAvailability (actualTime: TimeSpan) (plannedTime: TimeSpan) =
        if plannedTime.TotalSeconds > 0.0 then
            actualTime.TotalSeconds / plannedTime.TotalSeconds
        else
            0.0

    /// OEE - Performance (성능률) 계산
    let calculateOeePerformance
        (productionQty: int)
        (standardCycleTime: float)
        (actualTime: TimeSpan) =

        if actualTime.TotalSeconds > 0.0 then
            (float productionQty * standardCycleTime) / actualTime.TotalSeconds
        else
            0.0

    /// OEE - Quality (양품률) 계산
    let calculateOeeQuality (goodQty: int) (totalQty: int) =
        if totalQty > 0 then
            float goodQty / float totalQty
        else
            0.0

    /// OEE 종합 계산
    let calculateOEE (availability: float) (performance: float) (quality: float) =
        availability * performance * quality

    /// 시간 손실 계산
    let calculateTimeLoss (plannedTime: TimeSpan) (actualTime: TimeSpan) =
        if plannedTime.TotalSeconds > 0.0 then
            ((plannedTime.TotalSeconds - actualTime.TotalSeconds) / plannedTime.TotalSeconds) * 100.0
        else
            0.0

    /// 속도 손실 계산
    let calculateSpeedLoss (idealCycleTime: float) (actualCycleTime: float) =
        if actualCycleTime > 0.0 then
            ((actualCycleTime - idealCycleTime) / actualCycleTime) * 100.0
        else
            0.0

    /// 품질 손실 계산
    let calculateQualityLoss (totalQty: int) (goodQty: int) =
        if totalQty > 0 then
            (float (totalQty - goodQty) / float totalQty) * 100.0
        else
            0.0

    /// OEE Gap 계산 (목표 대비)
    let calculateOeeGap (actualOee: float) (targetOee: float) =
        (targetOee - actualOee * 100.0)

    /// OEE Class 판정
    let classifyOee (oee: float) =
        let oeePercent = oee * 100.0
        if oeePercent >= 85.0 then "World Class"
        elif oeePercent >= 75.0 then "Excellent"
        elif oeePercent >= 65.0 then "Good"
        elif oeePercent >= 50.0 then "Fair"
        else "Poor"


    // ========== What-If Scenario 함수 ==========

    /// ROI 계산 (투자 회수 기간, 개월)
    let calculateRoiMonths (investmentCost: float) (monthlyGain: float) =
        if monthlyGain > 0.0 then
            investmentCost / monthlyGain
        else
            Double.PositiveInfinity

    /// 시나리오 비교 점수 (높을수록 좋음)
    let calculateScenarioScore
        (throughputGain: float)
        (investment: float)
        (roiMonths: float)
        (oeeGain: float) =

        // 가중치: 처리량 40%, ROI 30%, 투자비용 20%, OEE 10%
        let throughputScore = throughputGain * 0.4
        let roiScore = (if roiMonths < 12.0 then (12.0 - roiMonths) / 12.0 else 0.0) * 0.3
        let costScore = (if investment < 1000000.0 then 1.0 - (investment / 1000000.0) else 0.0) * 0.2
        let oeeScore = oeeGain * 0.1

        throughputScore + roiScore + costScore + oeeScore


    // ========== Welford's Algorithm (온라인 통계) ==========

    /// Welford's 알고리즘으로 평균과 분산 업데이트
    let updateWelfordStats (count: int) (mean: float) (m2: float) (newValue: float) =
        let newCount = count + 1
        let delta = newValue - mean
        let newMean = mean + delta / float newCount
        let delta2 = newValue - newMean
        let newM2 = m2 + delta * delta2
        (newCount, newMean, newM2)

    /// Welford's 알고리즘으로 표준편차 계산
    let calculateWelfordStdDev (count: int) (m2: float) =
        if count > 1 then
            sqrt (m2 / float (count - 1))
        else
            0.0


    // ========== 난수 생성 (Random Variation) ==========

    /// 난수 변동 적용 (정규분포)
    let applyRandomVariation (baseValue: float) (variationPercent: float) (random: Random) =
        let stdDev = baseValue * (variationPercent / 100.0)
        let u1 = random.NextDouble()
        let u2 = random.NextDouble()
        let z0 = sqrt (-2.0 * log u1) * cos (2.0 * Math.PI * u2) // Box-Muller 변환
        baseValue + z0 * stdDev

    /// 시드 기반 Random 생성
    let createRandom (seed: int option) =
        match seed with
        | Some s -> new Random(s)
        | None -> new Random()


// =============================================================================
// SIMULATION RESULT SNAPSHOT (AAS TechnicalData 박제용 POCO + 변환)
// 이전: StandardSubmodels/SimulationResultSnapshot.fs — 통합됨
// =============================================================================

/// 시뮬레이션 결과 스냅샷 (TechnicalData 서브모델에 박제되는 표준화된 KPI 컨테이너)
[<AutoOpen>]
module SimulationResultSnapshotTypes =

    /// 시뮬 실행 메타데이터 (검증가능한 박제의 핵심)
    type SimulationMeta() =
        member val SimulatorName    = "Ds2.Runtime.Simulator" with get, set
        member val SimulatorVersion = "" with get, set
        /// ds2 모델 식별자 (디지털 스레드 키 - 같은 모델에서 나온 시뮬결과끼리 묶기 위함)
        member val Ds2ModelHash     = "" with get, set
        member val ScenarioId       = "" with get, set
        member val ScenarioName     = "" with get, set
        member val RunDate          = DateTime.UtcNow with get, set
        member val RunDuration_s    = 0.0 with get, set
        member val Seed             : int option = None with get, set
        member val SignedBy         = "" with get, set

    /// SimCycleTimeAnalysis → AAS 매핑용 POCO
    type KpiCycleTime() =
        member val WorkName                       = "" with get, set
        member val DesignCycleTime_s              = 0.0 with get, set
        member val ActualCycleTime_s              = 0.0 with get, set
        member val MinCycleTime_s                 = 0.0 with get, set
        member val MaxCycleTime_s                 = 0.0 with get, set
        member val StandardDeviation_s            = 0.0 with get, set
        member val VariationCoefficient           = 0.0 with get, set
        member val CycleCount                     = 0 with get, set
        member val ConfidenceInterval95_Lower_s   = 0.0 with get, set
        member val ConfidenceInterval95_Upper_s   = 0.0 with get, set
        member val IsNormalDistribution           = false with get, set
        member val DeviationFromDesign_pct        = 0.0 with get, set
        member val IsExceedingWarning             = false with get, set
        member val IsBottleneck                   = false with get, set
        member val BottleneckSeverity             = "" with get, set
        member val UtilizationRate_pct            = 0.0 with get, set
        member val ImprovementPotential_pct       = 0.0 with get, set
        member val RecommendedTargetCT_s          = 0.0 with get, set

    /// SimThroughputResult → AAS 매핑용 POCO
    type KpiThroughput() =
        member val StartTime              = DateTime.MinValue with get, set
        member val EndTime                = DateTime.MinValue with get, set
        member val ElapsedTime_s          = 0.0 with get, set
        member val TotalUnitsProduced     = 0 with get, set
        member val ThroughputPerHour      = 0.0 with get, set
        member val ThroughputPerDay       = 0.0 with get, set
        member val ThroughputPerWeek      = 0.0 with get, set
        member val ThroughputPerMonth     = 0.0 with get, set
        member val TaktTime_s             = 0.0 with get, set
        member val AverageCycleTime_s     = 0.0 with get, set
        member val CycleTimeMargin_pct    = 0.0 with get, set
        member val TargetThroughput       = 0.0 with get, set
        member val AchievementRate_pct    = 0.0 with get, set
        member val EfficiencyRate_pct     = 0.0 with get, set
        member val HourlyThroughputJson   = "" with get, set

    /// SimCapacityAnalysis → AAS 매핑용 POCO
    type KpiCapacity() =
        member val AnalysisDate                = DateTime.MinValue with get, set
        member val DesignCapacity              = 0.0 with get, set
        member val EffectiveCapacity           = 0.0 with get, set
        member val ActualCapacity              = 0.0 with get, set
        member val PlannedCapacity             = 0.0 with get, set
        member val DesignUtilization_pct       = 0.0 with get, set
        member val EffectiveUtilization_pct    = 0.0 with get, set
        member val CapacityUtilization_pct     = 0.0 with get, set
        member val CapacityGap                 = 0.0 with get, set
        member val Bottlenecks                 = ResizeArray<string>() with get, set
        member val RecommendedActions          = ResizeArray<string>() with get, set
        member val CapacityHorizon             = "" with get, set
        member val CapacityStrategy            = "" with get, set

    /// SimConstraintAnalysis 단일 항목 → AAS 매핑용 POCO
    type KpiConstraintItem() =
        member val ResourceName                = "" with get, set
        member val ConstraintType              = "" with get, set
        member val CurrentLoad                 = 0.0 with get, set
        member val MaxCapacity                 = 0.0 with get, set
        member val RemainingCapacity           = 0.0 with get, set
        member val IsConstraining              = false with get, set
        member val Severity                    = "" with get, set
        member val ImpactOnThroughput          = 0.0 with get, set
        member val EstimatedGainIfResolved     = 0.0 with get, set
        member val CurrentTocStep              = "" with get, set
        member val RecommendedActions          = ResizeArray<string>() with get, set

    /// SimResourceUtilization 단일 항목 → AAS 매핑용 POCO
    type KpiResourceItem() =
        member val ResourceName              = "" with get, set
        member val ResourceType              = "" with get, set
        member val AvailableTime_s           = 0.0 with get, set
        member val UsedTime_s                = 0.0 with get, set
        member val ProductionTime_s          = 0.0 with get, set
        member val ChangeoverTime_s          = 0.0 with get, set
        member val IdleTime_s                = 0.0 with get, set
        member val DownTime_s                = 0.0 with get, set
        member val UtilizationRate_pct       = 0.0 with get, set
        member val ProductiveRate_pct        = 0.0 with get, set
        member val AvailabilityRate_pct      = 0.0 with get, set
        member val IdleRate_pct              = 0.0 with get, set
        member val DownRate_pct              = 0.0 with get, set
        member val IndustryBenchmark         = 0.0 with get, set
        member val TargetUtilization         = 0.0 with get, set
        member val PerformanceGap            = 0.0 with get, set

    /// SimOeeTracking 단일 항목 → AAS 매핑용 POCO
    type KpiOeeItem() =
        member val ResourceName              = "" with get, set
        member val CalculationDate           = DateTime.MinValue with get, set
        member val CalculationPeriod_s       = 0.0 with get, set
        member val Availability              = 0.0 with get, set
        member val Performance               = 0.0 with get, set
        member val Quality                   = 0.0 with get, set
        member val OEE                       = 0.0 with get, set
        member val PlannedOperatingTime_s    = 0.0 with get, set
        member val ActualOperatingTime_s     = 0.0 with get, set
        member val PlannedProductionQty     = 0 with get, set
        member val ActualProductionQty      = 0 with get, set
        member val GoodProductQty           = 0 with get, set
        member val DefectQty                = 0 with get, set
        member val TimeLoss_pct              = 0.0 with get, set
        member val SpeedLoss_pct             = 0.0 with get, set
        member val QualityLoss_pct           = 0.0 with get, set
        member val TargetOEE                 = 0.0 with get, set
        member val OeeGap                    = 0.0 with get, set
        member val OeeClass                  = "" with get, set

    /// 토큰 유형별 Work breakdown
    type KpiPerTokenWorkBreakdown() =
        member val WorkName              = "" with get, set
        member val VisitCount            = 0 with get, set
        member val AvgGoingTime_s        = 0.0 with get, set
        member val AvgFinishTime_s       = 0.0 with get, set

    /// 토큰 유형(혼류 환경에서 originName 기준 그룹) 별 KPI
    type KpiPerToken() =
        member val OriginName            = "" with get, set
        member val SpecLabel             = "" with get, set
        member val InstanceCount         = 0 with get, set
        member val CompletedCount        = 0 with get, set
        member val AvgTraversalTime_s    = 0.0 with get, set
        member val MinTraversalTime_s    = 0.0 with get, set
        member val MaxTraversalTime_s    = 0.0 with get, set
        member val StdDevTraversalTime_s = 0.0 with get, set
        member val ThroughputPerHour     = 0.0 with get, set
        member val FirstSeed             = DateTime.MinValue with get, set
        member val LastComplete          = DateTime.MinValue with get, set
        member val WorkBreakdown         = ResizeArray<KpiPerTokenWorkBreakdown>() with get, set

    /// 시뮬레이션 시나리오 1건의 박제 (Meta + 6 KPI 그룹 + Per-Token)
    type SimulationScenario() =
        member val Meta                  = SimulationMeta() with get, set
        member val CycleTimes            = ResizeArray<KpiCycleTime>() with get, set
        member val Throughput            : KpiThroughput option = None with get, set
        member val Capacity              : KpiCapacity option = None with get, set
        member val Constraints           = ResizeArray<KpiConstraintItem>() with get, set
        member val ResourceUtilizations  = ResizeArray<KpiResourceItem>() with get, set
        member val OeeItems              = ResizeArray<KpiOeeItem>() with get, set
        member val PerTokenKpis          = ResizeArray<KpiPerToken>() with get, set


/// 시뮬 결과 변환 유틸 (ds2 런타임 struct → AAS 박제용 POCO)
module SimulationResultSnapshot =

    /// SHA-256 해시 헬퍼
    let computeModelHash (canonicalRepresentation: string) : string =
        if String.IsNullOrEmpty canonicalRepresentation then ""
        else
            use sha = SHA256.Create()
            let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonicalRepresentation))
            "sha256:" + (bytes |> Array.map (sprintf "%02x") |> String.concat "")

    let private hourlyToJson (hourly: (int * float) array) : string =
        if isNull (box hourly) || hourly.Length = 0 then ""
        else
            hourly
            |> Array.map (fun (h, v) ->
                sprintf "[%d,%s]" h (v.ToString("G", System.Globalization.CultureInfo.InvariantCulture)))
            |> String.concat ","
            |> sprintf "[%s]"

    let fromCycleTime (a: SimCycleTimeAnalysis) : KpiCycleTime =
        let r = KpiCycleTime()
        r.WorkName                     <- a.WorkName
        r.DesignCycleTime_s            <- a.DesignCycleTime
        r.ActualCycleTime_s            <- a.ActualCycleTime
        r.MinCycleTime_s               <- a.MinCycleTime
        r.MaxCycleTime_s               <- a.MaxCycleTime
        r.StandardDeviation_s          <- a.StandardDeviation
        r.VariationCoefficient         <- a.VariationCoefficient
        r.CycleCount                   <- a.CycleCount
        let lo, hi                     = a.ConfidenceInterval95
        r.ConfidenceInterval95_Lower_s <- lo
        r.ConfidenceInterval95_Upper_s <- hi
        r.IsNormalDistribution         <- a.IsNormalDistribution
        r.DeviationFromDesign_pct      <- a.DeviationFromDesign
        r.IsExceedingWarning           <- a.IsExceedingWarning
        r.IsBottleneck                 <- a.IsBottleneck
        r.BottleneckSeverity           <- a.BottleneckSeverity.ToString()
        r.UtilizationRate_pct          <- a.UtilizationRate
        r.ImprovementPotential_pct     <- a.ImprovementPotential
        r.RecommendedTargetCT_s        <- a.RecommendedTargetCT
        r

    let fromThroughput (t: SimThroughputResult) : KpiThroughput =
        let r = KpiThroughput()
        r.StartTime           <- t.StartTime
        r.EndTime             <- t.EndTime
        r.ElapsedTime_s       <- t.ElapsedTime.TotalSeconds
        r.TotalUnitsProduced  <- t.TotalUnitsProduced
        r.ThroughputPerHour   <- t.ThroughputPerHour
        r.ThroughputPerDay    <- t.ThroughputPerDay
        r.ThroughputPerWeek   <- t.ThroughputPerWeek
        r.ThroughputPerMonth  <- t.ThroughputPerMonth
        r.TaktTime_s          <- t.TaktTime
        r.AverageCycleTime_s  <- t.AverageCycleTime
        r.CycleTimeMargin_pct <- t.CycleTimeMargin
        r.TargetThroughput    <- t.TargetThroughput
        r.AchievementRate_pct <- t.AchievementRate
        r.EfficiencyRate_pct  <- t.EfficiencyRate
        r.HourlyThroughputJson <- hourlyToJson t.HourlyThroughput
        r

    let fromCapacity (c: SimCapacityAnalysis) : KpiCapacity =
        let r = KpiCapacity()
        r.AnalysisDate              <- c.AnalysisDate
        r.DesignCapacity            <- c.DesignCapacity
        r.EffectiveCapacity         <- c.EffectiveCapacity
        r.ActualCapacity            <- c.ActualCapacity
        r.PlannedCapacity           <- c.PlannedCapacity
        r.DesignUtilization_pct     <- c.DesignUtilization
        r.EffectiveUtilization_pct  <- c.EffectiveUtilization
        r.CapacityUtilization_pct   <- c.CapacityUtilization
        r.CapacityGap               <- c.CapacityGap
        if not (isNull (box c.Bottlenecks)) then
            for b in c.Bottlenecks do r.Bottlenecks.Add(b)
        if not (isNull (box c.RecommendedActions)) then
            for a in c.RecommendedActions do r.RecommendedActions.Add(a)
        r.CapacityHorizon           <- c.CapacityHorizon.ToString()
        r.CapacityStrategy          <- c.CapacityStrategy.ToString()
        r

    let fromConstraint (k: SimConstraintAnalysis) : KpiConstraintItem =
        let r = KpiConstraintItem()
        r.ResourceName            <- k.ResourceName
        r.ConstraintType          <- k.ConstraintType.ToString()
        r.CurrentLoad             <- k.CurrentLoad
        r.MaxCapacity             <- k.MaxCapacity
        r.RemainingCapacity       <- k.RemainingCapacity
        r.IsConstraining          <- k.IsConstraining
        r.Severity                <- k.Severity.ToString()
        r.ImpactOnThroughput      <- k.ImpactOnThroughput
        r.EstimatedGainIfResolved <- k.EstimatedGainIfResolved
        r.CurrentTocStep          <- k.CurrentTocStep.ToString()
        if not (isNull (box k.RecommendedActions)) then
            for a in k.RecommendedActions do r.RecommendedActions.Add(a)
        r

    let fromResource (u: SimResourceUtilization) : KpiResourceItem =
        let r = KpiResourceItem()
        r.ResourceName          <- u.ResourceName
        r.ResourceType          <- u.ResourceType
        r.AvailableTime_s       <- u.AvailableTime.TotalSeconds
        r.UsedTime_s            <- u.UsedTime.TotalSeconds
        r.ProductionTime_s      <- u.ProductionTime.TotalSeconds
        r.ChangeoverTime_s      <- u.ChangeoverTime.TotalSeconds
        r.IdleTime_s            <- u.IdleTime.TotalSeconds
        r.DownTime_s            <- u.DownTime.TotalSeconds
        r.UtilizationRate_pct   <- u.UtilizationRate
        r.ProductiveRate_pct    <- u.ProductiveRate
        r.AvailabilityRate_pct  <- u.AvailabilityRate
        r.IdleRate_pct          <- u.IdleRate
        r.DownRate_pct          <- u.DownRate
        r.IndustryBenchmark     <- u.IndustryBenchmark
        r.TargetUtilization     <- u.TargetUtilization
        r.PerformanceGap        <- u.PerformanceGap
        r

    let fromOee (o: SimOeeTracking) : KpiOeeItem =
        let r = KpiOeeItem()
        r.ResourceName           <- o.ResourceName
        r.CalculationDate        <- o.CalculationDate
        r.CalculationPeriod_s    <- o.CalculationPeriod.TotalSeconds
        r.Availability           <- o.Availability
        r.Performance            <- o.Performance
        r.Quality                <- o.Quality
        r.OEE                    <- o.OEE
        r.PlannedOperatingTime_s <- o.PlannedOperatingTime.TotalSeconds
        r.ActualOperatingTime_s  <- o.ActualOperatingTime.TotalSeconds
        r.PlannedProductionQty   <- o.PlannedProductionQty
        r.ActualProductionQty    <- o.ActualProductionQty
        r.GoodProductQty         <- o.GoodProductQty
        r.DefectQty              <- o.DefectQty
        r.TimeLoss_pct           <- o.TimeLoss
        r.SpeedLoss_pct          <- o.SpeedLoss
        r.QualityLoss_pct        <- o.QualityLoss
        r.TargetOEE              <- o.TargetOEE
        r.OeeGap                 <- o.OeeGap
        r.OeeClass               <- o.OeeClass
        r

    /// 6종 KPI + 토큰별 KPI 를 단일 시나리오 스냅샷으로 합성
    let buildScenario
        (meta: SimulationMeta)
        (cycleTimes: SimCycleTimeAnalysis seq)
        (throughput: SimThroughputResult option)
        (capacity:   SimCapacityAnalysis option)
        (constraints: SimConstraintAnalysis seq)
        (resources:  SimResourceUtilization seq)
        (oeeItems:   SimOeeTracking seq)
        (perTokenKpis: KpiPerToken seq) : SimulationScenario =
        let s = SimulationScenario()
        s.Meta <- meta
        cycleTimes  |> Seq.iter (fun a -> s.CycleTimes.Add(fromCycleTime a))
        s.Throughput <- throughput |> Option.map fromThroughput
        s.Capacity   <- capacity   |> Option.map fromCapacity
        constraints |> Seq.iter (fun a -> s.Constraints.Add(fromConstraint a))
        resources   |> Seq.iter (fun a -> s.ResourceUtilizations.Add(fromResource a))
        oeeItems    |> Seq.iter (fun a -> s.OeeItems.Add(fromOee a))
        perTokenKpis|> Seq.iter (fun p -> s.PerTokenKpis.Add p)
        s
