namespace Ds2.Core

open System
open System.Text.Json.Serialization

// =============================================================================
// Submodel Type (AASX Submodel ID 생성용)
// =============================================================================

/// AASX 서브모델 타입 (타입 안전성 보장)
type SubmodelType =
    | SequenceModel
    | SequenceSimulation
    | SequenceControl
    | SequenceMonitoring
    | SequenceLogging
    | SequenceMaintenance
    | SequenceHmi
    | SequenceCostAnalysis
    | SequenceQuality

    /// Submodel offset (byte) 반환
    member this.Offset =
        match this with
        | SequenceModel        -> 0uy
        | SequenceSimulation   -> 1uy
        | SequenceControl      -> 2uy
        | SequenceMonitoring   -> 3uy
        | SequenceLogging      -> 4uy
        | SequenceMaintenance  -> 5uy
        | SequenceHmi          -> 6uy
        | SequenceQuality      -> 7uy
        | SequenceCostAnalysis -> 8uy

    /// Submodel IdShort 반환
    member this.IdShort =
        match this with
        | SequenceModel        -> "SequenceModel"
        | SequenceSimulation   -> "SequenceSimulation"
        | SequenceControl      -> "SequenceControl"
        | SequenceMonitoring   -> "SequenceMonitoring"
        | SequenceLogging      -> "SequenceLogging"
        | SequenceMaintenance  -> "SequenceMaintenance"
        | SequenceHmi          -> "SequenceHmi"
        | SequenceQuality      -> "SequenceQuality"
        | SequenceCostAnalysis -> "SequenceCostAnalysis"

    /// Reference name 반환 (Entity에서 사용)
    member this.RefName =
        match this with
        | SequenceModel        -> "ModelRef"
        | SequenceSimulation   -> "SimulationRef"
        | SequenceControl      -> "ControlRef"
        | SequenceMonitoring   -> "MonitoringRef"
        | SequenceLogging      -> "LoggingRef"
        | SequenceMaintenance  -> "MaintenanceRef"
        | SequenceHmi          -> "HmiRef"
        | SequenceQuality      -> "QualityRef"
        | SequenceCostAnalysis -> "CostAnalysisRef"

    /// 모든 도메인 서브모델 반환 (Model 제외)
    static member AllDomains =
        [ SequenceSimulation
          SequenceControl
          SequenceMonitoring
          SequenceLogging
          SequenceMaintenance
          SequenceHmi
          SequenceQuality
          SequenceCostAnalysis
           ]

// =============================================================================
// Discriminated Unions for Submodel Properties
// =============================================================================

/// System-level 서브모델 속성 DU
type SystemSubmodelProperty =
    | SimulationSystem of SimulationSystemProperties
    | ControlSystem of ControlSystemProperties
    | MonitoringSystem of MonitoringSystemProperties
    | LoggingSystem of LoggingSystemProperties
    | MaintenanceSystem of MaintenanceSystemProperties
    | HmiSystem of HMISystemProperties
    | QualitySystem of QualitySystemProperties
    | CostAnalysisSystem of CostAnalysisSystemProperties

/// Flow-level 서브모델 속성 DU
type FlowSubmodelProperty =
    | SimulationFlow of SimulationFlowProperties
    | ControlFlow of ControlFlowProperties
    | MonitoringFlow of MonitoringFlowProperties
    | LoggingFlow of LoggingFlowProperties
    | MaintenanceFlow of MaintenanceFlowProperties
    | HmiFlow of HMIFlowProperties
    | QualityFlow of QualityFlowProperties
    | CostAnalysisFlow of CostAnalysisFlowProperties

/// Work-level 서브모델 속성 DU
type WorkSubmodelProperty =
    | SimulationWork of SimulationWorkProperties
    | ControlWork of ControlWorkProperties
    | MonitoringWork of MonitoringWorkProperties
    | LoggingWork of LoggingWorkProperties
    | MaintenanceWork of MaintenanceWorkProperties
    | HmiWork of HMIWorkProperties
    | QualityWork of QualityWorkProperties
    | CostAnalysisWork of CostAnalysisWorkProperties

/// Call-level 서브모델 속성 DU
type CallSubmodelProperty =
    | SimulationCall of SimulationCallProperties
    | ControlCall of ControlCallProperties
    | MonitoringCall of MonitoringCallProperties
    | LoggingCall of LoggingCallProperties
    | MaintenanceCall of MaintenanceCallProperties
    | HmiCall of HMICallProperties
    | QualityCall of QualityCallProperties
    | CostAnalysisCall of CostAnalysisCallProperties
