module Ds2.Aasx.AasxExporter

open System
open AasCore.Aas3_0
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxFileIO
open Ds2.UI.Core

// ── 내부 헬퍼 ──────────────────────────────────────────────────────────────

let private mkProp (idShort: string) (value: string) : ISubmodelElement =
    let p = Property(valueType = DataTypeDefXsd.String)
    p.IdShort <- idShort
    p.Value <- value
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
    sm.SemanticId <- Reference(
        ReferenceTypes.ExternalReference,
        ResizeArray<IKey>([Key(KeyTypes.GlobalReference, SubmodelSemanticId) :> IKey]))
    sm.SubmodelElements <- ResizeArray<ISubmodelElement>([projectSmc :> ISubmodelElement])
    sm

let internal exportToAasxFile (store: DsStore) (project: Project) (outputPath: string) : unit =
    let sm = exportToSubmodel store project
    let key = Key(KeyTypes.Submodel, sm.Id) :> IKey
    let smRef = Reference(ReferenceTypes.ModelReference, ResizeArray<IKey>([key])) :> IReference
    let assetInfo = AssetInformation(assetKind = AssetKind.Instance, globalAssetId = $"urn:ds2:asset:{project.Id}")
    let shell = AssetAdministrationShell(id = $"urn:ds2:shell:{project.Id}", assetInformation = assetInfo)
    shell.IdShort <- "ProjectShell"
    shell.Submodels <- ResizeArray<IReference>([smRef])
    let env =
        Environment(
            submodels = ResizeArray<ISubmodel>([sm :> ISubmodel]),
            assetAdministrationShells = ResizeArray<IAssetAdministrationShell>([shell :> IAssetAdministrationShell]),
            conceptDescriptions = null)
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
