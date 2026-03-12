module Ds2.Aasx.AasxExporter

open System
open AasCore.Aas3_0
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxConceptDescriptions
open Ds2.Aasx.AasxFileIO
open Ds2.UI.Core

// ── 내부 헬퍼 ──────────────────────────────────────────────────────────────

let private mkProp (idShort: string) (value: string) : ISubmodelElement =
    let p = Property(valueType = DataTypeDefXsd.String)
    p.IdShort <- idShort
    p.Value <- if isNull value then "" else value
    p :> ISubmodelElement

let private mkJsonProp<'T> (idShort: string) (obj: 'T) : ISubmodelElement =
    mkProp idShort (Ds2.Serialization.JsonConverter.serialize obj)

let private mkSmc (idShort: string) (elems: ISubmodelElement list) : ISubmodelElement =
    let smc = SubmodelElementCollection()
    smc.IdShort <- idShort
    smc.Value <- ResizeArray<ISubmodelElement>(elems)
    smc :> ISubmodelElement

let private mkSml (idShort: string) (items: ISubmodelElement list) : ISubmodelElement =
    let sml = SubmodelElementList(typeValueListElement = AasSubmodelElements.SubmodelElementCollection)
    sml.IdShort <- idShort
    sml.Value <- ResizeArray<ISubmodelElement>(items)
    sml :> ISubmodelElement

let private mkSmlProp (idShort: string) (items: ISubmodelElement list) : ISubmodelElement =
    let sml = SubmodelElementList(typeValueListElement = AasSubmodelElements.Property)
    sml.IdShort <- idShort
    sml.Value <- ResizeArray<ISubmodelElement>(items)
    sml :> ISubmodelElement

/// MultiLanguageProperty — 단일 언어(en)만 지원
let private mkMlp (idShort: string) (value: string) : ISubmodelElement =
    let mlp = MultiLanguageProperty()
    mlp.IdShort <- idShort
    let v = if isNull value then "" else value
    mlp.Value <- ResizeArray<ILangStringTextType>([LangStringTextType("en", v) :> ILangStringTextType])
    mlp :> ISubmodelElement

let private mkSemanticRef (semanticId: string) : IReference =
    Reference(
        ReferenceTypes.ExternalReference,
        ResizeArray<IKey>([Key(KeyTypes.GlobalReference, semanticId) :> IKey])) :> IReference

let private mkSubmodel (id: string) (idShort: string) (semanticId: string) (elems: ISubmodelElement list) : Submodel =
    let sm = Submodel(id = id)
    sm.IdShort <- idShort
    sm.SemanticId <- mkSemanticRef semanticId
    sm.SubmodelElements <- ResizeArray<ISubmodelElement>(elems)
    sm

// ── 변환 계층 ──────────────────────────────────────────────────────────────

let private callToSmc (call: Call) : ISubmodelElement =
    mkSmc "Call" [
        mkProp     Name_         call.Name
        mkProp     Guid_         (call.Id.ToString())
        mkProp     DevicesAlias_ call.DevicesAlias
        mkProp     ApiName_      call.ApiName
        mkJsonProp<CallProperties>              Properties_      call.Properties
        mkJsonProp<Xywh option>                 Position_        call.Position
        mkProp     Status_       (string call.Status4)
        mkJsonProp<ResizeArray<ApiCall>>        ApiCalls_        call.ApiCalls
        mkJsonProp<ResizeArray<CallCondition>>  CallConditions_  call.CallConditions
    ]

let private arrowCallToSmc (arrow: ArrowBetweenCalls) : ISubmodelElement =
    mkSmc "Arrow" [
        mkProp Guid_    (arrow.Id.ToString())
        mkProp Source_  (arrow.SourceId.ToString())
        mkProp Target_  (arrow.TargetId.ToString())
        mkProp Type_    (string arrow.ArrowType)
    ]

let private workToSmc (store: DsStore) (work: Work) : ISubmodelElement =
    let rawCalls = DsQuery.callsOf work.Id store
    let calls    = rawCalls |> List.map callToSmc
    let callIds  = rawCalls |> List.map (fun c -> c.Id) |> Set.ofList
    let arrows   =
        DsQuery.arrowCallsOf work.Id store
        |> List.filter (fun a -> callIds.Contains a.SourceId && callIds.Contains a.TargetId)
        |> List.map arrowCallToSmc
    mkSmc "Work" [
        mkProp     Name_       work.Name
        mkProp     Guid_       (work.Id.ToString())
        mkProp     FlowGuid_   (work.ParentId.ToString())
        mkJsonProp<WorkProperties> Properties_ work.Properties
        mkJsonProp<Xywh option>    Position_   work.Position
        mkProp     Status_     (string work.Status4)
        mkSml Calls_   calls
        mkSml Arrows_  arrows
    ]

let private arrowWorkToSmc (arrow: ArrowBetweenWorks) : ISubmodelElement =
    mkSmc "Arrow" [
        mkProp Guid_    (arrow.Id.ToString())
        mkProp Source_  (arrow.SourceId.ToString())
        mkProp Target_  (arrow.TargetId.ToString())
        mkProp Type_    (string arrow.ArrowType)
    ]

let private flowToSmc (flow: Flow) : ISubmodelElement =
    mkSmc "Flow" [
        mkProp     Name_       flow.Name
        mkProp     Guid_       (flow.Id.ToString())
        mkJsonProp<FlowProperties> Properties_ flow.Properties
    ]

let private apiDefToSmc (apiDef: ApiDef) : ISubmodelElement =
    mkSmc "ApiDef" [
        mkProp     Name_         apiDef.Name
        mkProp     Guid_         (apiDef.Id.ToString())
        mkJsonProp<ApiDefProperties> Properties_ apiDef.Properties
    ]

let private apiCallToSmc (apiCall: ApiCall) : ISubmodelElement =
    let apiDefProp = apiCall.ApiDefId |> Option.map (fun id -> $"{{\"ApiDef\":\"{id}\"}}") |> Option.defaultValue "{}"
    mkSmc "ApiCall" [
        mkProp Name_       apiCall.Name
        mkProp Guid_       (apiCall.Id.ToString())
        mkProp Properties_ apiDefProp
    ]

let private systemToSmc (store: DsStore) (system: DsSystem) (isActive: bool) : ISubmodelElement =
    let allFlows  = DsQuery.flowsOf system.Id store
    let flows     = allFlows |> List.map flowToSmc
    let works     = allFlows |> List.collect (fun f -> DsQuery.worksOf f.Id store)
                             |> List.map (workToSmc store)
    let arrows    = DsQuery.arrowWorksOf system.Id store
                    |> List.map arrowWorkToSmc
    let apiDefs   = DsQuery.apiDefsOf system.Id store |> List.map apiDefToSmc
    // ApiCalls/ReferencedApiDefs는 ActiveSystem 전용 — DeviceSystem은 빈 목록
    let apiCalls, referencedApiDefs =
        if isActive then
            let acs = DsQuery.allApiCalls store |> List.map apiCallToSmc
            let refs =
                DsQuery.allApiCalls store
                |> List.choose (fun ac -> ac.ApiDefId |> Option.bind (fun id -> DsQuery.getApiDef id store))
                |> List.filter (fun ad -> ad.ParentId <> system.Id)
                |> List.distinctBy (fun ad -> ad.Id)
                |> List.map apiDefToSmc
            acs, refs
        else [], []
    let iri       = system.IRI |> Option.defaultValue ""
    mkSmc "System" [
        mkProp     Name_             system.Name
        mkProp     Guid_             (system.Id.ToString())
        mkProp     IRI_              iri
        mkJsonProp<SystemProperties> Properties_ system.Properties
        mkSml ApiDefs_           apiDefs
        mkSml ApiCalls_          apiCalls
        mkSml ReferencedApiDefs_ referencedApiDefs
        mkSml Flows_             flows
        mkSml Arrows_            arrows
        mkSml Works_             works
    ]

// ── Nameplate → AAS Submodel (IDTA 02006-3-0) ──────────────────────────────

let private phoneToSmc (phone: PhoneInfo) : ISubmodelElement =
    mkSmc "Phone" [
        mkMlp  "TelephoneNumber" phone.TelephoneNumber
        mkProp "TypeOfTelephone" phone.TypeOfTelephone
    ]

let private faxToSmc (fax: FaxInfo) : ISubmodelElement =
    mkSmc "Fax" [
        mkMlp  "FaxNumber"       fax.FaxNumber
        mkProp "TypeOfFaxNumber" fax.TypeOfFaxNumber
    ]

let private emailToSmc (email: EmailInfo) : ISubmodelElement =
    mkSmc "Email" [
        mkProp "EmailAddress"       email.EmailAddress
        mkMlp  "PublicKey"          email.PublicKey
        mkProp "TypeOfEmailAddress" email.TypeOfEmailAddress
    ]

let private addressToSmc (addr: AddressInfo) : ISubmodelElement =
    mkSmc "AddressInformation" [
        mkMlp  "Street"       addr.Street
        mkMlp  "Zipcode"      addr.Zipcode
        mkMlp  "CityTown"     addr.CityTown
        mkProp "NationalCode" addr.NationalCode
        phoneToSmc addr.Phone
        faxToSmc   addr.Fax
        emailToSmc addr.Email
    ]

let private markingToSmc (m: MarkingInfo) : ISubmodelElement =
    mkSmc "Marking" [
        mkProp "MarkingName"                         m.MarkingName
        mkProp "DesignationOfCertificateOrApproval"  m.DesignationOfCertificateOrApproval
        mkProp "IssueDate"                           m.IssueDate
        mkProp "ExpiryDate"                          m.ExpiryDate
        mkProp "MarkingFile"                         m.MarkingFile
        mkMlp  "MarkingAdditionalText"               m.MarkingAdditionalText
    ]

let private nameplateToSubmodel (np: Nameplate) (projectId: Guid) : Submodel =
    let elems : ISubmodelElement list = [
        // 필수 요소
        mkProp "URIOfTheProduct"                np.URIOfTheProduct
        mkMlp  "ManufacturerName"               np.ManufacturerName
        mkMlp  "ManufacturerProductDesignation" np.ManufacturerProductDesignation
        addressToSmc np.AddressInformation
        mkProp "OrderCodeOfManufacturer"        np.OrderCodeOfManufacturer
        // 선택 요소
        mkMlp  "ManufacturerProductRoot"        np.ManufacturerProductRoot
        mkMlp  "ManufacturerProductFamily"      np.ManufacturerProductFamily
        mkProp "ManufacturerProductType"        np.ManufacturerProductType
        mkProp "ProductArticleNumberOfManufacturer" np.ProductArticleNumberOfManufacturer
        mkProp "SerialNumber"                   np.SerialNumber
        mkProp "YearOfConstruction"             np.YearOfConstruction
        mkProp "DateOfManufacture"              np.DateOfManufacture
        mkProp "HardwareVersion"                np.HardwareVersion
        mkProp "FirmwareVersion"                np.FirmwareVersion
        mkProp "SoftwareVersion"                np.SoftwareVersion
        mkProp "CountryOfOrigin"                np.CountryOfOrigin
        mkProp "UniqueFacilityIdentifier"       np.UniqueFacilityIdentifier
        mkProp "CompanyLogo"                    np.CompanyLogo
    ]
    let markingsElems =
        if np.Markings.Count > 0 then
            [ mkSml "Markings" (np.Markings |> Seq.map markingToSmc |> Seq.toList) ]
        else []
    mkSubmodel
        $"urn:dualsoft:nameplate:{projectId}"
        NameplateSubmodelIdShort
        NameplateSemanticId
        (elems @ markingsElems)

// ── HandoverDocumentation → AAS Submodel (IDTA 02004-1-2) ──────────────────

let private documentIdToSmc (did: DocumentId) : ISubmodelElement =
    mkSmc "DocumentId" [
        mkProp "DocumentDomainId" did.DocumentDomainId
        mkProp "ValueId"          did.ValueId
        mkProp "IsPrimary"        (did.IsPrimary.ToString().ToLowerInvariant())
    ]

let private documentClassToSmc (dc: DocumentClassification) : ISubmodelElement =
    mkSmc "DocumentClassification" [
        mkProp "ClassId"               dc.ClassId
        mkProp "ClassName"             dc.ClassName
        mkProp "ClassificationSystem"  dc.ClassificationSystem
    ]

let private documentVersionToSmc (dv: DocumentVersion) : ISubmodelElement =
    let baseElems : ISubmodelElement list = [
        if dv.Languages.Count > 0 then
            mkSmlProp "Languages" (dv.Languages |> Seq.map (fun lang -> mkProp "Language" lang) |> Seq.toList)
        mkProp "DocumentVersionId"       dv.DocumentVersionId
        mkProp "Title"                   dv.Title
        mkProp "SubTitle"                dv.SubTitle
        mkProp "Summary"                 dv.Summary
        mkProp "KeyWords"                dv.KeyWords
        mkProp "SetDate"                 dv.SetDate
        mkProp "StatusSetDate"           dv.StatusSetDate
        mkProp "StatusValue"             dv.StatusValue
        mkProp "OrganizationName"        dv.OrganizationName
        mkProp "OrganizationOfficialName" dv.OrganizationOfficialName
        mkProp "Role"                    dv.Role
        if dv.DigitalFiles.Count > 0 then
            mkSmlProp "DigitalFiles" (dv.DigitalFiles |> Seq.map (fun f -> mkProp "DigitalFile" f) |> Seq.toList)
        mkProp "PreviewFile"             dv.PreviewFile
    ]
    mkSmc "DocumentVersion" baseElems

let private documentToSmc (doc: Document) : ISubmodelElement =
    let elems : ISubmodelElement list = [
        if doc.DocumentIds.Count > 0 then
            mkSml "DocumentIds" (doc.DocumentIds |> Seq.map documentIdToSmc |> Seq.toList)
        if doc.DocumentClassifications.Count > 0 then
            mkSml "DocumentClassifications" (doc.DocumentClassifications |> Seq.map documentClassToSmc |> Seq.toList)
        if doc.DocumentVersions.Count > 0 then
            mkSml "DocumentVersions" (doc.DocumentVersions |> Seq.map documentVersionToSmc |> Seq.toList)
    ]
    mkSmc "Document" elems

let private documentationToSubmodel (hd: HandoverDocumentation) (projectId: Guid) : Submodel =
    let elems : ISubmodelElement list = [
        if hd.Documents.Count > 0 then
            mkSml "Documents" (hd.Documents |> Seq.map documentToSmc |> Seq.toList)
    ]
    mkSubmodel
        $"urn:dualsoft:documentation:{projectId}"
        DocumentationSubmodelIdShort
        DocumentationSemanticId
        elems

// ── 진입점 ─────────────────────────────────────────────────────────────────

let internal exportToSubmodel (store: DsStore) (project: Project) : Submodel =
    let activeSystems  = DsQuery.activeSystemsOf  project.Id store |> List.map (fun s -> systemToSmc store s true)
    let passiveSystems = DsQuery.passiveSystemsOf project.Id store |> List.map (fun s -> systemToSmc store s false)
    let projectElems : ISubmodelElement list = [
        mkProp     Name_         project.Name
        mkProp     Guid_         (project.Id.ToString())
        mkJsonProp<ProjectProperties> Properties_ project.Properties
        mkSml ActiveSystems_    activeSystems
        mkSml DeviceReferences_ passiveSystems
    ]
    let projectSmc = SubmodelElementCollection()
    projectSmc.IdShort <- "Project"
    projectSmc.Value <- ResizeArray<ISubmodelElement>(projectElems)

    let sm = Submodel(id = project.Id.ToString())
    sm.IdShort <- SubmodelIdShort
    sm.SemanticId <- mkSemanticRef SubmodelSemanticId
    sm.SubmodelElements <- ResizeArray<ISubmodelElement>([projectSmc :> ISubmodelElement])
    sm

/// IriPrefix 기반 Shell ID 생성 (Ev2 동일)
let private resolveIriPrefix (project: Project) : string =
    project.Properties.IriPrefix
    |> Option.defaultValue DefaultIriPrefix

/// GlobalAssetId 해석 — 설정값이 없으면 IriPrefix + "assetId/" + ProjectName
let private resolveGlobalAssetId (project: Project) : string =
    match project.Properties.GlobalAssetId with
    | Some id when not (String.IsNullOrWhiteSpace id) -> id
    | _ -> $"{resolveIriPrefix project}assetId/{project.Name}"

let internal exportToAasxFile (store: DsStore) (project: Project) (outputPath: string) : unit =
    let iriPrefix = resolveIriPrefix project

    // 메인 프로젝트 Submodel
    let sm = exportToSubmodel store project
    let mkSmRef (submodel: Submodel) : IReference =
        let key = Key(KeyTypes.Submodel, submodel.Id) :> IKey
        Reference(ReferenceTypes.ModelReference, ResizeArray<IKey>([key])) :> IReference

    let submodels = ResizeArray<ISubmodel>([sm :> ISubmodel])
    let smRefs = ResizeArray<IReference>([mkSmRef sm])

    // Nameplate Submodel (항상 포함 — Ev2 동일)
    let npSm = nameplateToSubmodel project.Nameplate project.Id
    submodels.Add(npSm :> ISubmodel)
    smRefs.Add(mkSmRef npSm)

    // Documentation Submodel (항상 포함 — Ev2 동일)
    let docSm = documentationToSubmodel project.HandoverDocumentation project.Id
    submodels.Add(docSm :> ISubmodel)
    smRefs.Add(mkSmRef docSm)

    // Shell — IriPrefix 기반 ID
    let globalAssetId = resolveGlobalAssetId project
    let assetInfo = AssetInformation(assetKind = AssetKind.Instance, globalAssetId = globalAssetId)
    let shell = AssetAdministrationShell(id = $"{iriPrefix}shell/{project.Name}", assetInformation = assetInfo)
    shell.IdShort <- "ProjectShell"
    shell.Submodels <- smRefs

    // ConceptDescriptions (Nameplate + Documentation)
    let conceptDescs = createAllConceptDescriptions true true

    let env =
        Environment(
            submodels = submodels,
            assetAdministrationShells = ResizeArray<IAssetAdministrationShell>([shell :> IAssetAdministrationShell]),
            conceptDescriptions = conceptDescs)
    writeEnvironment env outputPath

/// Export helper for UI callers that should not access Project entity directly.
/// Returns false when there is no project in the store.
let internal tryExportFirstProjectToAasxFile (store: DsStore) (outputPath: string) : bool =
    match DsQuery.allProjects store |> List.tryHead with
    | None -> false
    | Some project ->
        exportToAasxFile store project outputPath
        true

let exportFromStore (store: DsStore) (path: string) : bool =
    tryExportFirstProjectToAasxFile store path
