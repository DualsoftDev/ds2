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
    | SequenceCostAnalysis
    | SequenceQuality
    | SequenceHmi

    /// Submodel offset (byte) 반환
    member this.Offset =
        match this with
        | SequenceModel        -> 0uy
        | SequenceSimulation   -> 1uy
        | SequenceControl      -> 2uy
        | SequenceMonitoring   -> 3uy
        | SequenceLogging      -> 4uy
        | SequenceMaintenance  -> 5uy
        | SequenceCostAnalysis -> 6uy
        | SequenceQuality      -> 7uy
        | SequenceHmi          -> 8uy

    /// Submodel IdShort 반환
    member this.IdShort =
        match this with
        | SequenceModel        -> "SequenceModel"
        | SequenceSimulation   -> "SequenceSimulation"
        | SequenceControl      -> "SequenceControl"
        | SequenceMonitoring   -> "SequenceMonitoring"
        | SequenceLogging      -> "SequenceLogging"
        | SequenceMaintenance  -> "SequenceMaintenance"
        | SequenceCostAnalysis -> "SequenceCostAnalysis"
        | SequenceQuality      -> "SequenceQuality"
        | SequenceHmi          -> "SequenceHmi"

    /// Reference name 반환 (Entity에서 사용)
    member this.RefName =
        match this with
        | SequenceModel        -> "ModelRef"
        | SequenceSimulation   -> "SimulationRef"
        | SequenceControl      -> "ControlRef"
        | SequenceMonitoring   -> "MonitoringRef"
        | SequenceLogging      -> "LoggingRef"
        | SequenceMaintenance  -> "MaintenanceRef"
        | SequenceCostAnalysis -> "CostAnalysisRef"
        | SequenceQuality      -> "QualityRef"
        | SequenceHmi          -> "HmiRef"

    /// 모든 도메인 서브모델 반환 (Model 제외)
    static member AllDomains =
        [ SequenceSimulation
          SequenceControl
          SequenceMonitoring
          SequenceLogging
          SequenceMaintenance
          SequenceCostAnalysis
          SequenceQuality
          SequenceHmi ]

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
    | CostAnalysisSystem of CostAnalysisSystemProperties
    | QualitySystem of QualitySystemProperties
    | HmiSystem of HMISystemProperties

/// Flow-level 서브모델 속성 DU
type FlowSubmodelProperty =
    | SimulationFlow of SimulationFlowProperties
    | ControlFlow of ControlFlowProperties
    | MonitoringFlow of MonitoringFlowProperties
    | LoggingFlow of LoggingFlowProperties
    | MaintenanceFlow of MaintenanceFlowProperties
    | CostAnalysisFlow of CostAnalysisFlowProperties
    | QualityFlow of QualityFlowProperties
    | HmiFlow of HMIFlowProperties

/// Work-level 서브모델 속성 DU
type WorkSubmodelProperty =
    | SimulationWork of SimulationWorkProperties
    | ControlWork of ControlWorkProperties
    | MonitoringWork of MonitoringWorkProperties
    | LoggingWork of LoggingWorkProperties
    | MaintenanceWork of MaintenanceWorkProperties
    | CostAnalysisWork of CostAnalysisWorkProperties
    | QualityWork of QualityWorkProperties
    | HmiWork of HMIWorkProperties

/// Call-level 서브모델 속성 DU
type CallSubmodelProperty =
    | SimulationCall of SimulationCallProperties
    | ControlCall of ControlCallProperties
    | MonitoringCall of MonitoringCallProperties
    | LoggingCall of LoggingCallProperties
    | MaintenanceCall of MaintenanceCallProperties
    | CostAnalysisCall of CostAnalysisCallProperties
    | QualityCall of QualityCallProperties
    | HmiCall of HMICallProperties
