namespace Ds2.Aasx

open Ds2.Aasx.AasxSemantics

/// CD (ConceptDescription) 카탈로그 — 모든 자체 IRI 는 <see cref="AasxSemantics.CdBaseUrl"/> 를 prefix 로 한다.
/// 호스팅 위치 변경 시 AasxSemantics.fs 의 CdBaseUrl 한 줄만 수정하면 일괄 전환됨.
module internal AasxConceptDescriptionCatalog =

    type ConceptDescriptionInfo = {
        Id: string
        PreferredNameDe: string
        PreferredNameEn: string
        PreferredNameKr: string
        ShortName: string
        DefinitionDe: string
        DefinitionEn: string
        DefinitionKr: string
    }

    // ── ds2 핵심 엔티티 CD (Entities.fs 대응) ────────────────────────────────────
    let entityConceptDescriptionInfos: ConceptDescriptionInfo list = [
        { Id = EntityProjectSemanticId
          PreferredNameDe = "Projekt"
          PreferredNameEn = "Project"
          PreferredNameKr = "프로젝트"
          ShortName = "Project"
          DefinitionDe = "Wurzelentität eines DualSoft ds2 Projekts (Sammlung aktiver/passiver Systeme + Metadaten)"
          DefinitionEn = "Root entity of a DualSoft ds2 project (collection of active/passive systems + metadata)"
          DefinitionKr = "DualSoft ds2 프로젝트의 최상위 엔티티 (활성/패시브 시스템 모음 + 메타데이터)" }

        { Id = EntitySystemSemanticId
          PreferredNameDe = "Aktives System"
          PreferredNameEn = "Active system"
          PreferredNameKr = "활성 시스템"
          ShortName = "System"
          DefinitionDe = "Aktives Steuersystem (Anlage/Linie/Zelle) bestehend aus Flows und Geräten"
          DefinitionEn = "Active control system (plant/line/cell) composed of flows and devices"
          DefinitionKr = "Flow 와 Device 로 구성된 능동 제어 시스템 (설비/라인/셀)" }

        { Id = EntityDeviceSemanticId
          PreferredNameDe = "Gerät"
          PreferredNameEn = "Device"
          PreferredNameKr = "디바이스"
          ShortName = "Device"
          DefinitionDe = "Passives Gerät / Aktor / Sensor, das von einem aktiven System aufgerufen wird"
          DefinitionEn = "Passive device / actuator / sensor invoked by an active system"
          DefinitionKr = "능동 시스템에서 호출되는 패시브 디바이스 / 액추에이터 / 센서" }

        { Id = EntityFlowSemanticId
          PreferredNameDe = "Flow"
          PreferredNameEn = "Flow"
          PreferredNameKr = "플로우"
          ShortName = "Flow"
          DefinitionDe = "Sequenzieller Ablauf innerhalb eines aktiven Systems, bestehend aus Arbeitsschritten"
          DefinitionEn = "Sequential flow within an active system, composed of work steps"
          DefinitionKr = "능동 시스템 내부의 순차 흐름 — 여러 Work 로 구성됨" }

        { Id = EntityWorkSemanticId
          PreferredNameDe = "Arbeitsschritt"
          PreferredNameEn = "Work step"
          PreferredNameKr = "작업 단계"
          ShortName = "Work"
          DefinitionDe = "Einzelner Arbeitsschritt — eine Einheit der Sequenzausführung mit R/G/F/H Zustandsmaschine"
          DefinitionEn = "Individual work step — a unit of sequence execution with R/G/F/H state machine"
          DefinitionKr = "단일 작업 단계 — R/G/F/H 상태머신을 가진 시퀀스 실행 단위" }

        { Id = EntityCallSemanticId
          PreferredNameDe = "Aufruf"
          PreferredNameEn = "Call"
          PreferredNameKr = "콜"
          ShortName = "Call"
          DefinitionDe = "Aufruf einer Geräte-API innerhalb eines Arbeitsschritts (Verbraucher der ApiDef)"
          DefinitionEn = "Invocation of a device API within a work step (consumer of ApiDef)"
          DefinitionKr = "Work 내부에서 디바이스 API 를 호출하는 요소 (ApiDef 의 소비자)" }

        { Id = EntityApiDefSemanticId
          PreferredNameDe = "API-Definition"
          PreferredNameEn = "API definition"
          PreferredNameKr = "API 정의"
          ShortName = "ApiDef"
          DefinitionDe = "Vom Gerät bereitgestellte API (Signatur — Eingangs-/Ausgangstags + Verhalten)"
          DefinitionEn = "API exposed by a device (signature — input/output tags + behavior)"
          DefinitionKr = "디바이스가 노출하는 API 정의 (입력/출력 태그 + 동작 시그니처)" }

        { Id = EntityApiCallSemanticId
          PreferredNameDe = "API-Aufruf"
          PreferredNameEn = "API call"
          PreferredNameKr = "API 호출"
          ShortName = "ApiCall"
          DefinitionDe = "Konkrete Bindung eines Calls an eine ApiDef mit Werten / Tags zur Laufzeit"
          DefinitionEn = "Concrete binding of a Call to an ApiDef with values / tags at runtime"
          DefinitionKr = "Call 과 ApiDef 의 런타임 바인딩 (실제 값/태그 연결)" }

        { Id = EntityTokenSpecSemanticId
          PreferredNameDe = "Token-Spezifikation"
          PreferredNameEn = "Token specification"
          PreferredNameKr = "토큰 사양"
          ShortName = "TokenSpec"
          DefinitionDe = "Spezifikation eines Token-Typs (Rezept/Produkt) — bildet Token-Nummer auf Daten ab"
          DefinitionEn = "Specification of a token type (recipe/product) — maps a token number to data"
          DefinitionKr = "토큰 유형 사양 (레시피/제품) — 토큰 번호를 실데이터에 매핑" }

        { Id = EntityArrowWorkSemanticId
          PreferredNameDe = "Arbeitsschritt-Übergang"
          PreferredNameEn = "Work transition"
          PreferredNameKr = "Work 전이"
          ShortName = "ArrowWork"
          DefinitionDe = "Gerichtete Kante zwischen Arbeitsschritten — definiert Reset-/Sequenzierungsregeln"
          DefinitionEn = "Directed edge between work steps — defines reset / sequencing rules"
          DefinitionKr = "Work 간 방향성 엣지 — 리셋/시퀀스 규칙 정의" }

        { Id = EntityArrowCallSemanticId
          PreferredNameDe = "Aufruf-Übergang"
          PreferredNameEn = "Call transition"
          PreferredNameKr = "Call 전이"
          ShortName = "ArrowCall"
          DefinitionDe = "Gerichtete Kante zwischen Calls innerhalb eines Arbeitsschritts"
          DefinitionEn = "Directed edge between Calls within a work step"
          DefinitionKr = "Work 내부 Call 간 방향성 엣지" }
    ]

    // ── 시퀀스 모델 + 도메인 서브모델 CD ────────────────────────────────────────
    let submodelConceptDescriptionInfos: ConceptDescriptionInfo list = [
        { Id = SequenceModelSubmodelSemanticId
          PreferredNameDe = "Sequenzmodell-Submodel"
          PreferredNameEn = "Sequence model submodel"
          PreferredNameKr = "시퀀스 모델 서브모델"
          ShortName = "SeqModelSm"
          DefinitionDe = "Submodel zur Beschreibung der Sequenzsteuerungsstruktur eines ds2 Projekts (Systeme/Flows/Works/Calls)"
          DefinitionEn = "Submodel describing the sequence control structure of a ds2 project (Systems/Flows/Works/Calls)"
          DefinitionKr = "ds2 프로젝트의 시퀀스 제어 구조(System/Flow/Work/Call)를 기술하는 서브모델" }

        { Id = SequenceSimulationSubmodelSemanticId
          PreferredNameDe = "Simulations-Submodel"
          PreferredNameEn = "Simulation submodel"
          PreferredNameKr = "시뮬레이션 서브모델"
          ShortName = "SeqSimSm"
          DefinitionDe = "Submodel mit Simulationseigenschaften je System/Flow/Work/Call"
          DefinitionEn = "Submodel carrying simulation properties for each System/Flow/Work/Call"
          DefinitionKr = "System/Flow/Work/Call 별 시뮬레이션 속성을 담는 서브모델" }

        { Id = SequenceControlSubmodelSemanticId
          PreferredNameDe = "Steuerungs-Submodel"
          PreferredNameEn = "Control submodel"
          PreferredNameKr = "제어 서브모델"
          ShortName = "SeqCtrlSm"
          DefinitionDe = "Submodel mit Steuerungseigenschaften (FBTagMap-Presets, IO-Konfiguration)"
          DefinitionEn = "Submodel carrying control properties (FBTagMap presets, IO configuration)"
          DefinitionKr = "제어 속성을 담는 서브모델 (FBTagMap 프리셋, IO 설정)" }

        { Id = SequenceMonitoringSubmodelSemanticId
          PreferredNameDe = "Monitoring-Submodel"
          PreferredNameEn = "Monitoring submodel"
          PreferredNameKr = "모니터링 서브모델"
          ShortName = "SeqMonSm"
          DefinitionDe = "Submodel mit Monitoring-Eigenschaften (Alarm/Trend/Status)"
          DefinitionEn = "Submodel carrying monitoring properties (alarm/trend/status)"
          DefinitionKr = "모니터링 속성을 담는 서브모델 (알람/트렌드/상태)" }

        { Id = SequenceLoggingSubmodelSemanticId
          PreferredNameDe = "Logging-Submodel"
          PreferredNameEn = "Logging submodel"
          PreferredNameKr = "로깅 서브모델"
          ShortName = "SeqLogSm"
          DefinitionDe = "Submodel mit Logging-Eigenschaften (Datenpunkte, Aufbewahrung)"
          DefinitionEn = "Submodel carrying logging properties (data points, retention)"
          DefinitionKr = "로깅 속성을 담는 서브모델 (데이터 포인트, 보존 정책)" }

        { Id = SequenceMaintenanceSubmodelSemanticId
          PreferredNameDe = "Wartungs-Submodel"
          PreferredNameEn = "Maintenance submodel"
          PreferredNameKr = "유지보수 서브모델"
          ShortName = "SeqMaintSm"
          DefinitionDe = "Submodel mit Wartungseigenschaften (Intervalle, Verschleißteile)"
          DefinitionEn = "Submodel carrying maintenance properties (intervals, wear parts)"
          DefinitionKr = "유지보수 속성을 담는 서브모델 (주기, 마모 부품)" }

        { Id = SequenceHmiSubmodelSemanticId
          PreferredNameDe = "HMI-Submodel"
          PreferredNameEn = "HMI submodel"
          PreferredNameKr = "HMI 서브모델"
          ShortName = "SeqHmiSm"
          DefinitionDe = "Submodel mit HMI-Eigenschaften (Bildschirme, Aliase, Bedienelemente)"
          DefinitionEn = "Submodel carrying HMI properties (screens, aliases, controls)"
          DefinitionKr = "HMI 속성을 담는 서브모델 (화면/별칭/조작 요소)" }

        { Id = SequenceQualitySubmodelSemanticId
          PreferredNameDe = "Qualitäts-Submodel"
          PreferredNameEn = "Quality submodel"
          PreferredNameKr = "품질 서브모델"
          ShortName = "SeqQualSm"
          DefinitionDe = "Submodel mit Qualitätseigenschaften (Inspektion, SPC, Defekte)"
          DefinitionEn = "Submodel carrying quality properties (inspection, SPC, defects)"
          DefinitionKr = "품질 속성을 담는 서브모델 (검사/SPC/결함)" }

        { Id = SequenceCostAnalysisSubmodelSemanticId
          PreferredNameDe = "Kostenanalyse-Submodel"
          PreferredNameEn = "Cost analysis submodel"
          PreferredNameKr = "비용 분석 서브모델"
          ShortName = "SeqCostSm"
          DefinitionDe = "Submodel mit Kostenanalyse-Eigenschaften (Materialkosten, Arbeitskosten, Gemeinkosten)"
          DefinitionEn = "Submodel carrying cost analysis properties (material/labor/overhead costs)"
          DefinitionKr = "비용 분석 속성을 담는 서브모델 (재료비/인건비/간접비)" }
    ]

    // ── 시뮬레이션 결과 CD (TechnicalData 안 SimulationResult 그룹) ──────────────
    let simulationConceptDescriptionInfos: ConceptDescriptionInfo list = [
        { Id = SimulationResultSemanticId
          PreferredNameDe = "Simulationsergebnis"
          PreferredNameEn = "Simulation result"
          PreferredNameKr = "시뮬레이션 결과"
          ShortName = "SimResult"
          DefinitionDe = "Eingebettetes Simulationsergebnis (Meta + KPI-Gruppen) für einen einzelnen Lauf"
          DefinitionEn = "Embedded simulation result (meta + KPI groups) for a single run"
          DefinitionKr = "단일 시뮬레이션 실행 결과 (메타 + KPI 그룹) 의 임베디드 표현" }

        { Id = SimulationMetaSemanticId
          PreferredNameDe = "Simulationsmetadaten"
          PreferredNameEn = "Simulation metadata"
          PreferredNameKr = "시뮬레이션 메타데이터"
          ShortName = "SimMeta"
          DefinitionDe = "Provenienz: Simulator, Version, Modell-Hash, Szenario-ID, Laufzeit, Seed"
          DefinitionEn = "Provenance: simulator, version, model hash, scenario id, run time, seed"
          DefinitionKr = "출처 정보: 시뮬레이터, 버전, 모델 해시, 시나리오 ID, 실행 시각, 시드" }

        { Id = SimKpiCycleTimeSemanticId
          PreferredNameDe = "Zykluszeit-KPI"
          PreferredNameEn = "Cycle time KPI"
          PreferredNameKr = "사이클 타임 KPI"
          ShortName = "CTkpi"
          DefinitionDe = "Statistische Auswertung der Zykluszeit pro Arbeitsschritt"
          DefinitionEn = "Statistical analysis of cycle time per work step"
          DefinitionKr = "Work 별 사이클 타임 통계 분석 (평균/최소/최대/표준편차/CI95)" }

        { Id = SimKpiThroughputSemanticId
          PreferredNameDe = "Durchsatz-KPI"
          PreferredNameEn = "Throughput KPI"
          PreferredNameKr = "처리량 KPI"
          ShortName = "TPkpi"
          DefinitionDe = "Durchsatzkennzahlen je Stunde/Tag/Woche/Monat sowie Takt- und Zykluszeit"
          DefinitionEn = "Throughput per hour/day/week/month with takt and cycle time"
          DefinitionKr = "시간/일/주/월별 처리량 + Takt/사이클 타임 지표" }

        { Id = SimKpiCapacitySemanticId
          PreferredNameDe = "Kapazitäts-KPI"
          PreferredNameEn = "Capacity KPI"
          PreferredNameKr = "능력 KPI"
          ShortName = "CAPkpi"
          DefinitionDe = "Design-, Effektiv-, Ist- und Plan-Kapazität mit Auslastung und Engpässen"
          DefinitionEn = "Design, effective, actual, and planned capacity with utilization and bottlenecks"
          DefinitionKr = "설계/실효/실제/계획 능력 + 활용률 + 병목 분석" }

        { Id = SimKpiConstraintsSemanticId
          PreferredNameDe = "Engpass-KPI"
          PreferredNameEn = "Constraints KPI"
          PreferredNameKr = "제약 KPI"
          ShortName = "TOCkpi"
          DefinitionDe = "TOC-basierte Engpassanalyse je Ressource"
          DefinitionEn = "TOC-based constraint analysis per resource"
          DefinitionKr = "리소스 별 TOC 기반 제약 분석" }

        { Id = SimKpiResourceUtilSemanticId
          PreferredNameDe = "Ressourcenauslastung-KPI"
          PreferredNameEn = "Resource utilization KPI"
          PreferredNameKr = "리소스 활용 KPI"
          ShortName = "RUkpi"
          DefinitionDe = "Zeitliche Aufschlüsselung und Auslastungsraten je Ressource"
          DefinitionEn = "Time breakdown and utilization rates per resource"
          DefinitionKr = "리소스 별 시간 분해 (생산/준비/유휴/고장) 및 활용률" }

        { Id = SimKpiOeeSemanticId
          PreferredNameDe = "OEE-KPI"
          PreferredNameEn = "OEE KPI"
          PreferredNameKr = "OEE 지표"
          ShortName = "OEEkpi"
          DefinitionDe = "Overall Equipment Effectiveness mit Verfügbarkeit, Leistung und Qualität"
          DefinitionEn = "Overall Equipment Effectiveness with availability, performance, and quality"
          DefinitionKr = "종합설비효율 (가동률 × 성능률 × 양품률)" }

        { Id = SimKpiPerTokenSemanticId
          PreferredNameDe = "Token-spezifische KPIs"
          PreferredNameEn = "Per-token KPIs"
          PreferredNameKr = "토큰별 KPI"
          ShortName = "PTkpi"
          DefinitionDe = "KPI-Aufschlüsselung je Token-Typ (Origin/Spec) für Mischfluss-Szenarien"
          DefinitionEn = "KPI breakdown per token type (origin/spec) for mixed-flow scenarios"
          DefinitionKr = "혼류 환경에서 토큰 유형(origin/spec) 별 KPI 분해" }
    ]

    /// 모든 ds2 자체 발급 CD (Entity + Submodel + Simulation) — 외부 표준 CD 는 임베디드 AASX 템플릿에서 로드.
    let allConceptDescriptionInfos: ConceptDescriptionInfo list =
        entityConceptDescriptionInfos
        @ submodelConceptDescriptionInfos
        @ simulationConceptDescriptionInfos
