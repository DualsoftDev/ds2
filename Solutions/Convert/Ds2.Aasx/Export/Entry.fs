namespace Ds2.Aasx

open System
open AasCore.Aas3_0
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxConceptDescriptions
open Ds2.Aasx.AasxFileIO
open Ds2.Store

module AasxExporter =

    open AasxExportCore
    open AasxExportGraph
    open AasxExportMetadata

    let internal exportToSubmodel (store: DsStore) (project: Project) : Submodel =
        let activeSystems  = DsQuery.activeSystemsOf  project.Id store |> List.map (fun s -> systemToSmc store s true)
        let passiveSystems = DsQuery.passiveSystemsOf project.Id store |> List.map (fun s -> systemToSmc store s false)
        let projectElems : ISubmodelElement list = [
            mkProp     Name_         project.Name
            mkProp     Guid_         (project.Id.ToString())
            mkJsonProp<ProjectProperties> Properties_ project.Properties
            mkJsonProp<ResizeArray<TokenSpec>> TokenSpecs_ project.TokenSpecs
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
