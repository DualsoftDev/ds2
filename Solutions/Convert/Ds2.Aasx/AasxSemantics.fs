module Ds2.Aasx.AasxSemantics

let [<Literal>] SubmodelIdShort      = "SequenceControlSubmodel"
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
let [<Literal>] Properties_          = "Properties"
let [<Literal>] ActiveSystems_       = "ActiveSystems"
let [<Literal>] IRI_                 = "IRI"
let [<Literal>] Flows_               = "Flows"
let [<Literal>] Works_               = "Works"
let [<Literal>] Arrows_              = "Arrows"
let [<Literal>] Calls_               = "Calls"
let [<Literal>] ApiDefs_             = "ApiDefs"
let [<Literal>] ApiCalls_            = "ApiCalls"
let [<Literal>] ReferencedApiDefs_   = "ReferencedApiDefs"
let [<Literal>] DeviceReferences_    = "DeviceReferences"
let [<Literal>] PassiveSystems_      = "PassiveSystems"    // 구버전 호환 폴백용
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
