namespace Ds2.Core

open System

/// Anomaly detection algorithm
type AnomalyAlgorithm =
    | StatisticalZScore
    | IsolationForest
    | LSTM

    
// ================================
// Maintenance domain value types
// ================================

/// Spare part information for inventory management
[<Struct>]
type SparePartInfo = {
    PartNumber: string
    Description: string
    CurrentStock: int
    MinimumStock: int
    LeadTimeDays: int
    UnitCost: float
    Supplier: string option
}



// ================================
// Maintenance Properties Classes
// ================================

/// MaintenanceFlowProperties - Flow-level 보전 속성
type MaintenanceFlowProperties() =
    inherit PropertiesBase<MaintenanceFlowProperties>()

    // ========== Flow 레벨 보전 설정 ==========
    member val FlowMaintenanceEnabled = false with get, set

/// MaintenanceSystemProperties - 예지 보전 및 이상 감지
type MaintenanceSystemProperties() =
    inherit PropertiesBase<MaintenanceSystemProperties>()

    // ========== 이상 감지 ==========
    member val EnableAnomalyDetection = false with get, set
    member val AnomalyThreshold = 2.0 with get, set  // std dev multiplier
    member val AnomalyWindowSize = 100 with get, set
    member val AnomalyAlgorithm = AnomalyAlgorithm.StatisticalZScore with get, set

    // ========== 예지 보전 ==========
    member val EnablePredictiveMaintenance = false with get, set
    member val PredictionHorizonDays = 30 with get, set
    member val MachineLearningModel: string option = None with get, set
    member val UpdateModelInterval = TimeSpan.FromDays(7.0) with get, set

    // ========== 진동 모니터링 ==========
    member val EnableVibrationMonitoring = false with get, set
    member val VibrationSensorTag: string option = None with get, set
    member val VibrationThreshold = 7.1 with get, set  // mm/s RMS
    member val VibrationSamplingRate = 1000 with get, set  // Hz

    // ========== 임계값 설정 ==========
    member val WarningThreshold = 70.0 with get, set
    member val CriticalThreshold = 90.0 with get, set

/// MaintenanceWorkProperties - Work-level 보전 속성
type MaintenanceWorkProperties() =
    inherit PropertiesBase<MaintenanceWorkProperties>()

    // ========== 사이클 카운터 ==========
    member val TotalCycles = 0L with get, set
    member val MaxCycles: int64 option = None with get, set
    member val CyclesPerDay = 0.0 with get, set
    member val EstimatedEOL: DateTime option = None with get, set

    // ========== 건강 점수 ==========
    member val HealthScore = 100.0 with get, set
    member val DegradationRate = 0.0 with get, set  // %/day
    member val LastHealthCheck: DateTime option = None with get, set

    // ========== 센서 데이터 ==========
    member val CurrentVibration = 0.0 with get, set
    member val CurrentTemperature = 0.0 with get, set
    member val CurrentPower = 0.0 with get, set
    member val CurrentPressure = 0.0 with get, set

    // ========== 이상 감지 ==========
    member val AnomalyDetected = false with get, set
    member val AnomalyScore = 0.0 with get, set
    member val AnomalyTimestamp: DateTime option = None with get, set
    member val AnomalyDescription = "" with get, set

    // ========== 보전 ==========
    member val MaintenanceInterval: TimeSpan option = None with get, set
    // NOTE: LinkedPlcTags 제거됨 (array는 MemberwiseClone shallow copy 위험)
    // → 필요시 별도 컬렉션으로 관리 (예: Work.PlcTagLinks: ResizeArray<string>)

/// MaintenanceCallProperties - Call-level 신뢰성 메트릭
type MaintenanceCallProperties() =
    inherit PropertiesBase<MaintenanceCallProperties>()

    // ========== 신뢰성 메트릭 ==========
    member val MTBF: TimeSpan option = None with get, set
    member val MTTR: TimeSpan option = None with get, set
    member val Availability = 100.0 with get, set
    member val SuccessRate = 100.0 with get, set
    member val FailureCount = 0 with get, set

    // ========== 고장 확률 ==========
    member val FailureProbability = 0.0 with get, set
