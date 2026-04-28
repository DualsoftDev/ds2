module Ds2.Aasx.AasxSemantics

// AASX Submodel 상수
// 참고: SubmodelType DU (Ds2.Core/SubmodelProperties.fs)에서 IdShort, Offset, RefName을 제공하므로
// 여기서는 SubmodelType에 의존하지 않는 공통 상수만 정의합니다.

// Submodel IdShort (하위 호환성을 위해 유지, SubmodelType.IdShort와 동일)
let [<Literal>] SubmodelModelIdShort        = "SequenceModel"
/// 구 버전 AASX 하위 호환용 (v1 포맷)
let [<Literal>] LegacySubmodelIdShort      = "SequenceControlSubmodel"
let [<Literal>] SubmodelSimulationIdShort   = "SequenceSimulation"
let [<Literal>] SubmodelControlIdShort      = "SequenceControl"
let [<Literal>] SubmodelMonitoringIdShort   = "SequenceMonitoring"
let [<Literal>] SubmodelLoggingIdShort      = "SequenceLogging"
let [<Literal>] SubmodelMaintenanceIdShort  = "SequenceMaintenance"
let [<Literal>] SubmodelCostAnalysisIdShort = "SequenceCostAnalysis"
let [<Literal>] SubmodelQualityIdShort      = "SequenceQuality"
let [<Literal>] SubmodelHmiIdShort          = "SequenceHmi"

let [<Literal>] SubmodelSemanticId   = "https://dualsoft.com/aas/submodel"

// Digital Nameplate (IDTA 02006-3-0) 상수
let [<Literal>] NameplateSubmodelIdShort  = "Nameplate"
let [<Literal>] NameplateSemanticId       = "https://admin-shell.io/zvei/nameplate/3/0/Nameplate"

// Handover Documentation (IDTA 02004-1-2) 상수
let [<Literal>] DocumentationSubmodelIdShort = "HandoverDocumentation"
let [<Literal>] DocumentationSemanticId      = "0173-1#01-AHF578#001"

// Technical Data (IDTA 02003 v1.2) 상수
let [<Literal>] TechnicalDataSubmodelIdShort = "TechnicalData"
let [<Literal>] TechnicalDataSemanticId      = "https://admin-shell.io/ZVEI/TechnicalData/Submodel/1/2"

// ProMaker 시뮬결과 박제용 자체 semanticId 네임스페이스 (IDTA 정식 spec 등장 시 교체 가능)
let [<Literal>] PromakerSimNamespace          = "https://dualsoft.com/semantics/promaker/sim"
let [<Literal>] SimulationResultSemanticId    = "https://dualsoft.com/semantics/promaker/sim/Result/1/0"
let [<Literal>] SimulationMetaSemanticId      = "https://dualsoft.com/semantics/promaker/sim/Meta/1/0"
let [<Literal>] SimKpiCycleTimeSemanticId     = "https://dualsoft.com/semantics/promaker/sim/Kpi/CycleTime/1/0"
let [<Literal>] SimKpiThroughputSemanticId    = "https://dualsoft.com/semantics/promaker/sim/Kpi/Throughput/1/0"
let [<Literal>] SimKpiCapacitySemanticId      = "https://dualsoft.com/semantics/promaker/sim/Kpi/Capacity/1/0"
let [<Literal>] SimKpiConstraintsSemanticId   = "https://dualsoft.com/semantics/promaker/sim/Kpi/Constraints/1/0"
let [<Literal>] SimKpiResourceUtilSemanticId  = "https://dualsoft.com/semantics/promaker/sim/Kpi/ResourceUtilization/1/0"
let [<Literal>] SimKpiOeeSemanticId           = "https://dualsoft.com/semantics/promaker/sim/Kpi/OEE/1/0"
let [<Literal>] SimKpiPerTokenSemanticId      = "https://dualsoft.com/semantics/promaker/sim/Kpi/PerToken/1/0"

// 출처(Provenance) Qualifier — 모든 KPI Property 에 부여하여 Simulation/Measurement/CatalogSpec/Estimate 구분
let [<Literal>] DataSourceQualifierType     = "DataSource"
let [<Literal>] DataSourceSimulation        = "Simulation"
let [<Literal>] DataSourceMeasurement       = "Measurement"
let [<Literal>] DataSourceCatalogSpec       = "CatalogSpec"
let [<Literal>] DataSourceEstimate          = "Estimate"

// IRI 기본값
let [<Literal>] DefaultIriPrefix = "http://your-company.com/"

// =============================================================================
// SubmodelProperty 컬렉션명 (도메인별)
// =============================================================================
let [<Literal>] SimulationProperties_   = "SimulationProperties"
let [<Literal>] CostAnalysisProperties_ = "CostAnalysisProperties"
let [<Literal>] ControlProperties_      = "ControlProperties"
let [<Literal>] MonitoringProperties_   = "MonitoringProperties"
let [<Literal>] LoggingProperties_      = "LoggingProperties"
let [<Literal>] MaintenanceProperties_  = "MaintenanceProperties"

// =============================================================================
// 계층 구조 및 컬렉션명
// =============================================================================
let [<Literal>] ActiveSystems_       = "ActiveSystems"
let [<Literal>] Flows_               = "Flows"
let [<Literal>] Works_               = "Works"
let [<Literal>] Arrows_              = "Arrows"
let [<Literal>] Calls_               = "Calls"
let [<Literal>] ApiDefs_             = "ApiDefs"
let [<Literal>] ApiCalls_            = "ApiCalls"
let [<Literal>] ReferencedApiDefs_   = "ReferencedApiDefs"
let [<Literal>] DeviceReferences_    = "DeviceReferences"

// =============================================================================
// Work 특수 필드 (생성자 파라미터)
// =============================================================================
let [<Literal>] FlowGuid_            = "FlowGuid"      // Work 생성자 parentId

// =============================================================================
// Device 분리 저장용
// =============================================================================
let [<Literal>] DeviceGuid_          = "DeviceGuid"
let [<Literal>] DeviceName_          = "DeviceName"
let [<Literal>] DeviceIRI_           = "DeviceIRI"
let [<Literal>] DeviceRelativePath_  = "DeviceRelativePath"
