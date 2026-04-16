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
