namespace Ds2.Core

open System
open System.Text.Json.Serialization

// =============================================================================
// Submodel Offsets (AASX Submodel ID 생성용)
// =============================================================================

module SubmodelOffsets =
    [<Literal>]
    let Model        = 0uy  // SequenceModel
    [<Literal>]
    let Simulation   = 1uy  // SequenceSimulation
    [<Literal>]
    let Control      = 2uy  // SequenceControl
    [<Literal>]
    let Monitoring   = 3uy  // SequenceMonitoring
    [<Literal>]
    let Logging      = 4uy  // SequenceLogging
    [<Literal>]
    let Maintenance  = 5uy  // SequenceMaintenance
    [<Literal>]
    let CostAnalysis = 6uy  // SequenceCostAnalysis

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

/// Flow-level 서브모델 속성 DU
type FlowSubmodelProperty =
    | SimulationFlow of SimulationFlowProperties
    | ControlFlow of ControlFlowProperties
    | MonitoringFlow of MonitoringFlowProperties
    | LoggingFlow of LoggingFlowProperties
    | MaintenanceFlow of MaintenanceFlowProperties
    | CostAnalysisFlow of CostAnalysisFlowProperties

/// Work-level 서브모델 속성 DU
type WorkSubmodelProperty =
    | SimulationWork of SimulationWorkProperties
    | ControlWork of ControlWorkProperties
    | MonitoringWork of MonitoringWorkProperties
    | LoggingWork of LoggingWorkProperties
    | MaintenanceWork of MaintenanceWorkProperties
    | CostAnalysisWork of CostAnalysisWorkProperties

/// Call-level 서브모델 속성 DU
type CallSubmodelProperty =
    | SimulationCall of SimulationCallProperties
    | ControlCall of ControlCallProperties
    | MonitoringCall of MonitoringCallProperties
    | LoggingCall of LoggingCallProperties
    | MaintenanceCall of MaintenanceCallProperties
    | CostAnalysisCall of CostAnalysisCallProperties
