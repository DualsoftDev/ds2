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
    mkSmc (call.Id.ToString()) [
        mkProp     Name_         call.Name
        mkProp     Guid_         (call.Id.ToString())
        mkProp     DevicesAlias_ call.DevicesAlias
        mkProp     ApiName_      call.ApiName
        mkJsonProp<CallProperties>              Properties_      call.Properties
        mkJsonProp<Xywh option>                 Position_        call.Position
        mkProp     Status_       (int call.Status4 |> string)
        mkJsonProp<ResizeArray<ApiCall>>        ApiCalls_        call.ApiCalls
        mkJsonProp<ResizeArray<CallCondition>>  CallConditions_  call.CallConditions
    ]

let private arrowCallToSmc (arrow: ArrowBetweenCalls) : ISubmodelElement =
    mkSmc (arrow.Id.ToString()) [
        mkProp Guid_      (arrow.Id.ToString())
        mkProp SourceId_  (arrow.SourceId.ToString())
        mkProp TargetId_  (arrow.TargetId.ToString())
        mkProp ArrowType_ (int arrow.ArrowType |> string)
    ]

let private workToSmc (store: DsStore) (work: Work) : ISubmodelElement =
    let calls  = DsQuery.callsOf work.Id store      |> List.map callToSmc
    let arrows = DsQuery.arrowCallsOf work.Id store |> List.map arrowCallToSmc
    mkSmc (work.Id.ToString()) [
        mkProp     Name_       work.Name
        mkProp     Guid_       (work.Id.ToString())
        mkJsonProp<WorkProperties> Properties_ work.Properties
        mkJsonProp<Xywh option>    Position_   work.Position
        mkProp     Status_     (int work.Status4 |> string)
        mkSml Calls_         calls
        mkSml ArrowsBtCalls_ arrows
    ]

let private arrowWorkToSmc (arrow: ArrowBetweenWorks) : ISubmodelElement =
    mkSmc (arrow.Id.ToString()) [
        mkProp Guid_      (arrow.Id.ToString())
        mkProp SourceId_  (arrow.SourceId.ToString())
        mkProp TargetId_  (arrow.TargetId.ToString())
        mkProp ArrowType_ (int arrow.ArrowType |> string)
    ]

let private flowToSmc (store: DsStore) (flow: Flow) : ISubmodelElement =
    let works  = DsQuery.worksOf flow.Id store      |> List.map (workToSmc store)
    let arrows = DsQuery.arrowWorksOf flow.Id store |> List.map arrowWorkToSmc
    mkSmc (flow.Id.ToString()) [
        mkProp     Name_         flow.Name
        mkProp     Guid_         (flow.Id.ToString())
        mkJsonProp<FlowProperties> Properties_ flow.Properties
        mkSml Works_         works
        mkSml ArrowsBtWorks_ arrows
    ]

let private apiDefToSmc (apiDef: ApiDef) : ISubmodelElement =
    mkSmc (apiDef.Id.ToString()) [
        mkProp     Name_         apiDef.Name
        mkProp     Guid_         (apiDef.Id.ToString())
        mkJsonProp<ApiDefProperties> Properties_ apiDef.Properties
    ]

let private systemToSmc (store: DsStore) (system: DsSystem) (isActive: bool) : ISubmodelElement =
    let flows   = DsQuery.flowsOf system.Id store   |> List.map (flowToSmc store)
    let apiDefs = DsQuery.apiDefsOf system.Id store |> List.map apiDefToSmc
    mkSmc (system.Id.ToString()) [
        mkProp     Name_     system.Name
        mkProp     Guid_     (system.Id.ToString())
        mkProp     IsActive_ (if isActive then "true" else "false")
        mkJsonProp<SystemProperties> Properties_ system.Properties
        mkSml Flows_   flows
        mkSml ApiDefs_ apiDefs
    ]

// ── 진입점 ─────────────────────────────────────────────────────────────────

let exportToSubmodel (store: DsStore) (project: Project) : Submodel =
    let activeSystems  = DsQuery.activeSystemsOf  project.Id store |> List.map (fun s -> systemToSmc store s true)
    let passiveSystems = DsQuery.passiveSystemsOf project.Id store |> List.map (fun s -> systemToSmc store s false)
    let projectElems : ISubmodelElement list = [
        mkProp     Name_         project.Name
        mkProp     Guid_         (project.Id.ToString())
        mkJsonProp<ProjectProperties> Properties_ project.Properties
        mkSml ActiveSystems_  activeSystems
        mkSml PassiveSystems_ passiveSystems
    ]
    let projectSmc = SubmodelElementCollection()
    projectSmc.IdShort <- project.Name
    projectSmc.Value <- ResizeArray<ISubmodelElement>(projectElems)

    let sm = Submodel(id = $"urn:ds2:submodel:{project.Id}")
    sm.IdShort <- SubmodelIdShort
    sm.SubmodelElements <- ResizeArray<ISubmodelElement>([projectSmc :> ISubmodelElement])
    sm

let exportToAasxFile (store: DsStore) (project: Project) (outputPath: string) : unit =
    let sm = exportToSubmodel store project
    let key = Key(KeyTypes.Submodel, sm.Id) :> IKey
    let smRef = Reference(ReferenceTypes.ModelReference, ResizeArray<IKey>([key])) :> IReference
    let assetInfo = AssetInformation(assetKind = AssetKind.Instance, globalAssetId = $"urn:ds2:asset:{project.Id}")
    let shell = AssetAdministrationShell(id = $"urn:ds2:shell:{project.Id}", assetInformation = assetInfo)
    shell.IdShort <- "Ds2Shell"
    shell.Submodels <- ResizeArray<IReference>([smRef])
    let env =
        Environment(
            submodels = ResizeArray<ISubmodel>([sm :> ISubmodel]),
            assetAdministrationShells = ResizeArray<IAssetAdministrationShell>([shell :> IAssetAdministrationShell]),
            conceptDescriptions = null)
    writeEnvironment env outputPath

/// Export helper for UI callers that should not access Project entity directly.
/// Returns false when there is no project in the store.
let tryExportFirstProjectToAasxFile (store: DsStore) (outputPath: string) : bool =
    match DsQuery.allProjects store |> List.tryHead with
    | None -> false
    | Some project ->
        exportToAasxFile store project outputPath
        true
