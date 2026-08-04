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

// (deprecated: SubmodelSemanticId — 모든 SM SemanticId 는 CdBaseUrl 기반 상수로 일원화됨)

// Digital Nameplate (IDTA 02006-3-0) 상수
let [<Literal>] NameplateSubmodelIdShort  = "Nameplate"
let [<Literal>] NameplateSemanticId       = "https://admin-shell.io/zvei/nameplate/3/0/Nameplate"

// Handover Documentation (IDTA 02004-1-2) 상수
let [<Literal>] DocumentationSubmodelIdShort = "HandoverDocumentation"
let [<Literal>] DocumentationSemanticId      = "0173-1#01-AHF578#001"

// Technical Data (IDTA 02003 v1.2) 상수
let [<Literal>] TechnicalDataSubmodelIdShort = "TechnicalData"
let [<Literal>] TechnicalDataSemanticId      = "https://admin-shell.io/ZVEI/TechnicalData/Submodel/1/2"

// =============================================================================
// CD (ConceptDescription) 베이스 URL — 단일 진실 원천
// 향후 호스팅 위치 변경 시 이 한 줄만 수정하면 모든 자체 semanticId 가 일괄 전환됨.
//
// 후보:
//   "https://dualsoftdev.github.io/aas-semantics"   ← GitHub Pages (현재)
//   "https://semantics.dualsoft.com"                ← 커스텀 도메인 (장기)
//   "https://dualsoft.com/semantics"                ← 메인 도메인 직접 호스팅
// =============================================================================
let [<Literal>] CdBaseUrl = "https://dualsoftdev.github.io/aas-semantics"

// CD IRI 헬퍼 — IDTA / AAS 컨벤션에 맞춰 확장자 없는 깨끗한 identifier.
// (파일은 GitHub Pages 측에 <path>.json 으로 존재 — fetch URL 과 IRI 는 분리된 개념)
let private cdId (path: string) : string = CdBaseUrl + "/" + path

// ── Sequence Model 시뮬결과 CD 네임스페이스 ────────────────────────────────────
let SequenceModelSimNamespace     = CdBaseUrl + "/sim"
let SimulationResultSemanticId    = cdId "sim/Result/1/0"
let SimulationMetaSemanticId      = cdId "sim/Meta/1/0"
let SimKpiCycleTimeSemanticId     = cdId "sim/Kpi/CycleTime/1/0"
let SimKpiThroughputSemanticId    = cdId "sim/Kpi/Throughput/1/0"
let SimKpiCapacitySemanticId      = cdId "sim/Kpi/Capacity/1/0"
let SimKpiConstraintsSemanticId   = cdId "sim/Kpi/Constraints/1/0"
let SimKpiResourceUtilSemanticId  = cdId "sim/Kpi/ResourceUtilization/1/0"
let SimKpiOeeSemanticId           = cdId "sim/Kpi/OEE/1/0"
let SimKpiPerTokenSemanticId      = cdId "sim/Kpi/PerToken/1/0"

// ── ds2 도메인 엔티티 CD ───────────────────────────────────────────────────────
let EntityProjectSemanticId       = cdId "entity/Project/1/0"
let EntitySystemSemanticId        = cdId "entity/System/1/0"
let EntityDeviceSemanticId        = cdId "entity/Device/1/0"
let EntityFlowSemanticId          = cdId "entity/Flow/1/0"
let EntityWorkSemanticId          = cdId "entity/Work/1/0"
let EntityCallSemanticId          = cdId "entity/Call/1/0"
let EntityApiDefSemanticId        = cdId "entity/ApiDef/1/0"
let EntityApiCallSemanticId       = cdId "entity/ApiCall/1/0"
let EntityTokenSpecSemanticId     = cdId "entity/TokenSpec/1/0"
let EntityArrowWorkSemanticId     = cdId "entity/ArrowWork/1/0"
let EntityArrowCallSemanticId     = cdId "entity/ArrowCall/1/0"

// ── 시퀀스 모델 + 도메인 서브모델 CD ──────────────────────────────────────────
let SequenceModelSubmodelSemanticId        = cdId "sm/SequenceModel/1/0"
let SequenceSimulationSubmodelSemanticId   = cdId "sm/SequenceSimulation/1/0"
let SequenceControlSubmodelSemanticId      = cdId "sm/SequenceControl/1/0"
let SequenceMonitoringSubmodelSemanticId   = cdId "sm/SequenceMonitoring/1/0"
let SequenceLoggingSubmodelSemanticId      = cdId "sm/SequenceLogging/1/0"
let SequenceMaintenanceSubmodelSemanticId  = cdId "sm/SequenceMaintenance/1/0"
let SequenceHmiSubmodelSemanticId          = cdId "sm/SequenceHmi/1/0"
let SequenceQualitySubmodelSemanticId      = cdId "sm/SequenceQuality/1/0"
let SequenceCostAnalysisSubmodelSemanticId = cdId "sm/SequenceCostAnalysis/1/0"

/// 서브모델 idShort → SemanticId (런타임 매핑 — 도메인 서브모델 export 시 사용)
let submodelSemanticIdByIdShort (idShort: string) : string =
    cdId ("sm/" + idShort + "/1/0")

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


// =============================================================================
// Phase 0 · AAS × OPC UA 통합 스택 · IDTA 표준 서브모델 상수
// =============================================================================
//
// 6개 IDTA 표준 SM + 1개 사내 SM.
// CollectionPolicy 는 별도 SM 아님 — SequenceLogging 흡수 (Phase 0 리팩터).

/// Asset Interfaces Description (IDTA 02017 v1.1) — spec §04-B 핵심
let [<Literal>] AidSubmodelIdShort  = "AssetInterfacesDescription"
let [<Literal>] AidSubmodelSemanticId =
    "https://admin-shell.io/idta/AssetInterfacesDescription/1/1/Submodel"

/// Asset Interfaces Mapping Configuration (IDTA 02027 v2.0) — spec §04-D 선택
let [<Literal>] AimcSubmodelIdShort = "AssetInterfacesMappingConfiguration"
let [<Literal>] AimcSubmodelSemanticId =
    "https://admin-shell.io/idta/AssetInterfacesMappingConfiguration/2/0/Submodel"

/// Operational Data (사내 발행 · AIMC northbound 투영 sink)
let [<Literal>] OperationalDataSubmodelIdShort = "OperationalData"
let OperationalDataSubmodelSemanticId = cdId "sm/OperationalData/1/0"

/// Time Series Data (IDTA 02008 v1.1) LinkedSegment access points.
let [<Literal>] TimeSeriesSubmodelIdShort = "TimeSeries"
let [<Literal>] TimeSeriesSubmodelSemanticId =
    "https://admin-shell.io/idta/TimeSeries/1/1"
let [<Literal>] TimeSeriesMetadataSemanticId =
    "https://admin-shell.io/idta/TimeSeries/Metadata/1/1"
let [<Literal>] TimeSeriesSegmentsSemanticId =
    "https://admin-shell.io/idta/TimeSeries/Segments/1/1"
let [<Literal>] TimeSeriesLinkedSegmentSemanticId =
    "https://admin-shell.io/idta/TimeSeries/Segments/LinkedSegment/1/1"
let [<Literal>] TimeSeriesEndpointSemanticId =
    "https://admin-shell.io/idta/TimeSeries/Endpoint/1/1"
let [<Literal>] TimeSeriesQuerySemanticId =
    "https://admin-shell.io/idta/TimeSeries/Query/1/1"
let TimeSeriesIdExtensionSemanticId = cdId "cd/ext.time-series-id/1/0"

// -----------------------------------------------------------------------------
// AID / 신호 확장 요소 CD (ADR-002 · ADR-008 준수)
// -----------------------------------------------------------------------------

/// AID InteractionMetadata 안에 부착되는 DualSoft 확장: 신호 유일 식별자.
/// SignalId 는 자산 내 유일, {lineId}.{assetId}.{shortName} kebab-case.
let SignalIdExtensionSemanticId = cdId "cd/ext.signal-id/1/0"

/// DualSoft-managed AID binding for LS ELECTRIC XGT endpoints.
let [<Literal>] XgtInterfaceSemanticId = "urn:dualsoft:smc:aid-interface-xgt:1:0"

/// AID EndpointMetadata 의 인증 정보 Vault 참조 필드.
/// 실제 secret 은 저장하지 않고 `@vault:path` 참조만 (ADR-005).
let VaultReferenceExtensionSemanticId = cdId "cd/ext.vault-reference/1/0"

/// SequenceLogging 서브모델 안의 신호별 수집 정책 컬렉션 (SubmodelElementCollection).
/// CollectionPolicy 를 별도 SM 이 아니라 Logging 흡수 형태로 emit 하는 SMC.
let SignalPoliciesCollectionSemanticId = cdId "sm/SequenceLogging/SignalPoliciesCollection/1/0"

/// AID · CollectionPolicy 안에서 사용되는 acquisitionMode 값의 CD.
let SignalAcquisitionModeSemanticId = cdId "cd/ext.acquisition-mode/1/0"

/// AutoID (BCR-05 등) 이벤트 상호작용 CD — OPC 30010 OpticalScanEventType 매핑.
let AutoIdEventBindingSemanticId = cdId "cd/ext.autoid-event/1/0"

/// AAS 자산 소유자 (owner) identity 확장 — ADR-005 자원 인가에 사용.
let AssetOwnerExtensionSemanticId = cdId "cd/ext.owner/1/0"

// -----------------------------------------------------------------------------
// Provenance §C — AID/AIMC/OperationalData round-trip 시 auto/user 구분
//   * Qualifier(type="dualsoft:origin" value="Auto"|"User")  — AAS §5.7.7 HasQualifiers
//   * Submodel Extension(name="dualsoft:auto-suppressed")    — AAS §5.7.6 HasExtensions
//
// IDTA-02017/02027 템플릿 모두 확장/Qualifier 허용. 결정론적 재실행 안전.
// Origin 이 명시되지 않은 SME (구 aasx 또는 사용자 편집) 는 기본값 User.
// -----------------------------------------------------------------------------

let [<Literal>] ProvenanceOriginQualifierType = "dualsoft:origin"
let [<Literal>] ProvenanceOriginAuto          = "Auto"
let [<Literal>] ProvenanceOriginUser          = "User"

/// Submodel-level Extension 이름 (tombstones 직렬화).
/// Value 는 세미콜론(`;`) 구분 IdShort 목록 — export 시 알파벳 정렬로 결정론 보장.
let [<Literal>] ProvenanceSuppressedExtensionName = "dualsoft:auto-suppressed"
