namespace Ds2.Aasx

module internal AasxConceptDescriptionCatalog =

    type ConceptDescriptionInfo = {
        Id: string
        PreferredNameDe: string
        PreferredNameEn: string
        ShortName: string
        DefinitionDe: string
        DefinitionEn: string
    }

    /// DS2 커스텀 Sequence Submodel 전용 ConceptDescription
    /// (Nameplate/HandoverDocumentation은 임베디드 IDTA AASX 템플릿에서 로드)
    let sequenceConceptDescriptionInfos: ConceptDescriptionInfo list = [
        { Id = "https://ds2.example.com/ids/cd/SequenceWorkflow"
          PreferredNameDe = "Sequenzablauf"
          PreferredNameEn = "Sequence workflow"
          ShortName = "SeqWf"
          DefinitionDe = "Beschreibung eines Produktionsablaufs als Sequenz von Arbeitsschritten"
          DefinitionEn = "Description of a production workflow as a sequence of work steps" }

        { Id = "https://ds2.example.com/ids/cd/WorkStep"
          PreferredNameDe = "Arbeitsschritt"
          PreferredNameEn = "Work step"
          ShortName = "WkStp"
          DefinitionDe = "Einzelner Arbeitsschritt innerhalb eines Sequenzablaufs"
          DefinitionEn = "Individual work step within a sequence workflow" }

        { Id = "https://ds2.example.com/ids/cd/DeviceCall"
          PreferredNameDe = "Geräteaufruf"
          PreferredNameEn = "Device call"
          ShortName = "DevCall"
          DefinitionDe = "Aufruf einer Gerätefunktion innerhalb eines Arbeitsschritts"
          DefinitionEn = "Invocation of a device function within a work step" }

        { Id = "https://ds2.example.com/ids/cd/TokenFlow"
          PreferredNameDe = "Token-Fluss"
          PreferredNameEn = "Token flow"
          ShortName = "TokFlw"
          DefinitionDe = "Fluss von Tokens zwischen Arbeitsschritten zur Steuerung der Ausführungsreihenfolge"
          DefinitionEn = "Flow of tokens between work steps to control execution order" }
    ]

    /// 시뮬레이션 결과 박제용 ConceptDescription
    /// (TechnicalData 서브모델 내 ProMaker Simulation Results 그룹의 자기서술 메타)
    let simulationConceptDescriptionInfos: ConceptDescriptionInfo list = [
        { Id = "https://dualsoft.com/semantics/promaker/sim/Results/1/0"
          PreferredNameDe = "Simulationsergebnisse"
          PreferredNameEn = "Simulation results"
          ShortName = "SimRes"
          DefinitionDe = "Sammlung von Simulationsergebnissen (KPI) je Szenario, eingebettet als digitales Asset"
          DefinitionEn = "Collection of simulation result KPIs per scenario, embedded as a digital asset" }

        { Id = "https://dualsoft.com/semantics/promaker/sim/Scenario/1/0"
          PreferredNameDe = "Simulationsszenario"
          PreferredNameEn = "Simulation scenario"
          ShortName = "SimScn"
          DefinitionDe = "Ein einzelner Simulationslauf mit Metadaten und KPI-Gruppen"
          DefinitionEn = "A single simulation run with metadata and KPI groups" }

        { Id = "https://dualsoft.com/semantics/promaker/sim/Meta/1/0"
          PreferredNameDe = "Simulationsmetadaten"
          PreferredNameEn = "Simulation metadata"
          ShortName = "SimMeta"
          DefinitionDe = "Provenienz: Simulator, Version, Modell-Hash, Szenario-ID, Laufzeit, Seed"
          DefinitionEn = "Provenance: simulator, version, model hash, scenario id, run time, seed" }

        { Id = "https://dualsoft.com/semantics/promaker/sim/Kpi/CycleTime/1/0"
          PreferredNameDe = "Zykluszeit-KPI"
          PreferredNameEn = "Cycle time KPI"
          ShortName = "CTkpi"
          DefinitionDe = "Statistische Auswertung der Zykluszeit pro Arbeitsschritt"
          DefinitionEn = "Statistical analysis of cycle time per work step" }

        { Id = "https://dualsoft.com/semantics/promaker/sim/Kpi/Throughput/1/0"
          PreferredNameDe = "Durchsatz-KPI"
          PreferredNameEn = "Throughput KPI"
          ShortName = "TPkpi"
          DefinitionDe = "Durchsatzkennzahlen je Stunde/Tag/Woche/Monat sowie Takt- und Zykluszeit"
          DefinitionEn = "Throughput per hour/day/week/month with takt and cycle time" }

        { Id = "https://dualsoft.com/semantics/promaker/sim/Kpi/Capacity/1/0"
          PreferredNameDe = "Kapazitäts-KPI"
          PreferredNameEn = "Capacity KPI"
          ShortName = "CAPkpi"
          DefinitionDe = "Design-, Effektiv-, Ist- und Plan-Kapazität mit Auslastung und Engpässen"
          DefinitionEn = "Design, effective, actual, and planned capacity with utilization and bottlenecks" }

        { Id = "https://dualsoft.com/semantics/promaker/sim/Kpi/Constraints/1/0"
          PreferredNameDe = "Engpass-KPI"
          PreferredNameEn = "Constraints KPI"
          ShortName = "TOCkpi"
          DefinitionDe = "TOC-basierte Engpassanalyse je Ressource"
          DefinitionEn = "TOC-based constraint analysis per resource" }

        { Id = "https://dualsoft.com/semantics/promaker/sim/Kpi/ResourceUtilization/1/0"
          PreferredNameDe = "Ressourcenauslastung-KPI"
          PreferredNameEn = "Resource utilization KPI"
          ShortName = "RUkpi"
          DefinitionDe = "Zeitliche Aufschlüsselung und Auslastungsraten je Ressource"
          DefinitionEn = "Time breakdown and utilization rates per resource" }

        { Id = "https://dualsoft.com/semantics/promaker/sim/Kpi/OEE/1/0"
          PreferredNameDe = "OEE-KPI"
          PreferredNameEn = "OEE KPI"
          ShortName = "OEEkpi"
          DefinitionDe = "Overall Equipment Effectiveness mit Verfügbarkeit, Leistung und Qualität"
          DefinitionEn = "Overall Equipment Effectiveness with availability, performance, and quality" }
    ]
