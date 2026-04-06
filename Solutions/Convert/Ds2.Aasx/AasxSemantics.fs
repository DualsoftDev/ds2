module Ds2.Aasx.AasxSemantics

// AASX Submodel 상수
// 참고: SubmodelType DU (Ds2.Core/SubmodelProperties.fs)에서 IdShort, Offset, RefName을 제공하므로
// 여기서는 SubmodelType에 의존하지 않는 공통 상수만 정의합니다.

// Submodel IdShort (하위 호환성을 위해 유지, SubmodelType.IdShort와 동일)
let [<Literal>] SubmodelModelIdShort        = "SequenceModel"
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

// IRI 기본값
let [<Literal>] DefaultIriPrefix = "http://your-company.com/"
let [<Literal>] Name_                = "Name"
let [<Literal>] Guid_                = "Guid"
let [<Literal>] SimulationProperties_   = "SimulationProperties"
let [<Literal>] CostAnalysisProperties_ = "CostAnalysisProperties"
let [<Literal>] ControlProperties_      = "ControlProperties"
let [<Literal>] MonitoringProperties_   = "MonitoringProperties"
let [<Literal>] LoggingProperties_      = "LoggingProperties"
let [<Literal>] MaintenanceProperties_  = "MaintenanceProperties"
let [<Literal>] ActiveSystems_       = "ActiveSystems"
let [<Literal>] IRI_                 = "IRI"
let [<Literal>] Flows_               = "Flows"
let [<Literal>] Works_               = "Works"
let [<Literal>] Arrows_              = "Arrows"
let [<Literal>] Calls_               = "Calls"
let [<Literal>] ApiDefs_             = "ApiDefs"
let [<Literal>] ApiCalls_            = "ApiCalls"
let [<Literal>] ApiDefId_            = "ApiDefId"
let [<Literal>] IsPush_              = "IsPush"
let [<Literal>] TxGuid_              = "TxGuid"
let [<Literal>] RxGuid_              = "RxGuid"
let [<Literal>] InTag_               = "InTag"
let [<Literal>] OutTag_              = "OutTag"
let [<Literal>] InputSpec_           = "InputSpec"
let [<Literal>] OutputSpec_          = "OutputSpec"
let [<Literal>] OriginFlowId_        = "OriginFlowId"
let [<Literal>] ReferencedApiDefs_   = "ReferencedApiDefs"
let [<Literal>] DeviceReferences_    = "DeviceReferences"
let [<Literal>] CallConditions_      = "CallConditions"
let [<Literal>] DevicesAlias_        = "DevicesAlias"
let [<Literal>] ApiName_             = "ApiName"
let [<Literal>] Position_            = "Position"
let [<Literal>] Status_              = "Status"
let [<Literal>] FlowGuid_            = "FlowGuid"
let [<Literal>] Source_              = "Source"
let [<Literal>] Target_              = "Target"
let [<Literal>] Type_                = "Type"
let [<Literal>] TokenRole_           = "TokenRole"
let [<Literal>] TokenSpecs_          = "TokenSpecs"

// Work 네이밍 확장
let [<Literal>] FlowPrefix_           = "FlowPrefix"
let [<Literal>] LocalName_            = "LocalName"
let [<Literal>] ReferenceOf_          = "ReferenceOf"

// Device 분리 저장용
let [<Literal>] DeviceGuid_          = "DeviceGuid"
let [<Literal>] DeviceName_          = "DeviceName"
let [<Literal>] DeviceIRI_           = "DeviceIRI"
let [<Literal>] DeviceRelativePath_  = "DeviceRelativePath"

// Project 메타데이터 (SequenceModel 서브모델에 저장)
let [<Literal>] Author_                     = "Author"
let [<Literal>] DateTime_                   = "DateTime"
let [<Literal>] Version_                    = "Version"
