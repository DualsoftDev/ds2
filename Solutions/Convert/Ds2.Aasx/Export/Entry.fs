namespace Ds2.Aasx

open System
open System.IO
open AasCore.Aas3_0
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxConceptDescriptions
open Ds2.Aasx.AasxFileIO
open Ds2.Store
open Ds2.Store.DsQuery

open log4net

module AasxExporter =

    let private log = LogManager.GetLogger("Ds2.Aasx.AasxExporter")

    open AasxExportCore
    open AasxExportGraph
    open AasxExportMetadata

    let private mkSmRef (submodel: Submodel) : IReference =
        let key = Key(KeyTypes.Submodel, submodel.Id) :> IKey
        Reference(ReferenceTypes.ModelReference, ResizeArray<IKey>([key])) :> IReference

    let private appendProjectMetadataSubmodels (project: Project) (submodels: ResizeArray<ISubmodel>) (smRefs: ResizeArray<IReference>) =
        let npSm = nameplateToSubmodel project.Nameplate project.Id
        submodels.Add(npSm :> ISubmodel)
        smRefs.Add(mkSmRef npSm)

        let docSm = documentationToSubmodel project.HandoverDocumentation project.Id
        submodels.Add(docSm :> ISubmodel)
        smRefs.Add(mkSmRef docSm)

    let private appendMetadataSubmodels (ownerId: Guid) (nameplate: Nameplate) (documentation: HandoverDocumentation) (submodels: ResizeArray<ISubmodel>) (smRefs: ResizeArray<IReference>) =
        let npSm = nameplateToSubmodel nameplate ownerId
        submodels.Add(npSm :> ISubmodel)
        smRefs.Add(mkSmRef npSm)

        let docSm = documentationToSubmodel documentation ownerId
        submodels.Add(docSm :> ISubmodel)
        smRefs.Add(mkSmRef docSm)

    let private tryGetDefaultThumbnail () =
        let resourceName = "Ds2.Aasx.Thumbnail.ds_aasx_thumbnail_icon.png"
        let asm = System.Reflection.Assembly.GetExecutingAssembly()
        use stream = asm.GetManifestResourceStream(resourceName)
        if isNull stream then
            log.Warn($"기본 AASX 썸네일 리소스를 찾을 수 없습니다: {resourceName}")
            None
        else
            use mem = new MemoryStream()
            stream.CopyTo(mem)
            Some
                { EntryName = "ds_aasx_thumbnail_icon.png"
                  ContentType = "image/png"
                  Bytes = mem.ToArray() }

    let private appendDefaultDeviceMetadataSubmodels (device: DsSystem) (submodels: ResizeArray<ISubmodel>) (smRefs: ResizeArray<IReference>) =
        appendMetadataSubmodels device.Id (Nameplate()) (HandoverDocumentation()) submodels smRefs

    /// Device 이름을 파일명으로 안전하게 변환 (특수문자 → _)
    let sanitizeDeviceName (name: string) : string =
        let invalid = Path.GetInvalidFileNameChars()
        name.ToCharArray()
        |> Array.map (fun c -> if Array.contains c invalid then '_' else c)
        |> String.Concat

    /// 메인 AASX의 Submodel 생성 (인라인 모드 — 모든 PassiveSystem 포함)
    let internal exportToSubmodel (store: DsStore) (project: Project) : Submodel =
        let activeSystems  = Queries.activeSystemsOf  project.Id store |> List.map (fun s -> systemToSmc store s true)
        let passiveSystems = Queries.passiveSystemsOf project.Id store |> List.map (fun s -> systemToSmc store s false)
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

    /// DeviceReference SMC 생성 (분리 모드용 — 경량 참조만)
    let internal deviceReferenceToSmc (device: DsSystem) (relativePath: string) : ISubmodelElement =
        mkSmc "DeviceReference" [
            mkProp DeviceGuid_         (device.Id.ToString())
            mkProp DeviceName_         device.Name
            mkProp DeviceIRI_          (device.IRI |> Option.defaultValue "")
            mkProp DeviceRelativePath_ relativePath
        ]

    /// 분리 모드용 Submodel 생성 (PassiveSystem 대신 DeviceReference 참조만 포함)
    let internal exportToSubmodelSplit (store: DsStore) (project: Project) (deviceRefs: ISubmodelElement list) : Submodel =
        let activeSystems = Queries.activeSystemsOf project.Id store |> List.map (fun s -> systemToSmc store s true)
        let projectElems : ISubmodelElement list = [
            mkProp     Name_         project.Name
            mkProp     Guid_         (project.Id.ToString())
            mkJsonProp<ProjectProperties> Properties_ project.Properties
            mkJsonProp<ResizeArray<TokenSpec>> TokenSpecs_ project.TokenSpecs
            mkSml ActiveSystems_    activeSystems
            mkSml DeviceReferences_ deviceRefs
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
        let thumbnail = tryGetDefaultThumbnail ()

        // 메인 프로젝트 Submodel
        let sm = exportToSubmodel store project

        let submodels = ResizeArray<ISubmodel>([sm :> ISubmodel])
        let smRefs = ResizeArray<IReference>([mkSmRef sm])

        // Nameplate / Documentation (항상 포함 — Ev2 동일)
        appendProjectMetadataSubmodels project submodels smRefs

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
        writeEnvironment env outputPath thumbnail

    /// 단일 Device를 독립 AASX로 저장 (ev2처럼 ActiveSystems=[device] 래핑)
    let internal exportDeviceAasx (store: DsStore) (project: Project) (device: DsSystem) (outputPath: string) : unit =
        let deviceSmc = systemToSmc store device false
        let projectElems : ISubmodelElement list = [
            mkProp     Name_         project.Name
            mkProp     Guid_         (project.Id.ToString())
            mkJsonProp<ProjectProperties> Properties_ project.Properties
            mkSml ActiveSystems_    [deviceSmc]
            mkSml DeviceReferences_ []
        ]
        let projectSmc = SubmodelElementCollection()
        projectSmc.IdShort <- "Project"
        projectSmc.Value <- ResizeArray<ISubmodelElement>(projectElems)

        let sm = Submodel(id = project.Id.ToString())
        sm.IdShort <- SubmodelIdShort
        sm.SemanticId <- mkSemanticRef SubmodelSemanticId
        sm.SubmodelElements <- ResizeArray<ISubmodelElement>([projectSmc :> ISubmodelElement])

        let iriPrefix = resolveIriPrefix project
        let globalAssetId = resolveGlobalAssetId project
        let assetInfo = AssetInformation(assetKind = AssetKind.Instance, globalAssetId = globalAssetId)
        let shell = AssetAdministrationShell(id = $"{iriPrefix}shell/{device.Name}", assetInformation = assetInfo)
        shell.IdShort <- "DeviceShell"
        let submodels = ResizeArray<ISubmodel>([sm :> ISubmodel])
        let smRefs = ResizeArray<IReference>([mkSmRef sm])
        appendDefaultDeviceMetadataSubmodels device submodels smRefs
        shell.Submodels <- smRefs

        let conceptDescs = createAllConceptDescriptions true true

        let env =
            Environment(
                submodels = submodels,
                assetAdministrationShells = ResizeArray<IAssetAdministrationShell>([shell :> IAssetAdministrationShell]),
                conceptDescriptions = conceptDescs)
        let thumbnail = tryGetDefaultThumbnail ()
        writeEnvironment env outputPath thumbnail

    /// Device 이름 → 유니크 파일명 (같은 이름이 있으면 Guid 해시 추가)
    let internal resolveDeviceFileName (usedNames: System.Collections.Generic.HashSet<string>) (device: DsSystem) : string =
        let baseName = sanitizeDeviceName device.Name
        if usedNames.Add(baseName) then
            baseName
        else
            let shortHash = device.Id.ToString("N").[..7]
            let uniqueName = $"{baseName}_{shortHash}"
            usedNames.Add(uniqueName) |> ignore
            uniqueName

    /// 분리 저장 오케스트레이션
    let internal exportSplitAasx (store: DsStore) (project: Project) (outputPath: string) : unit =
        let mainDir = Path.GetDirectoryName(outputPath)
        let baseName = Path.GetFileNameWithoutExtension(outputPath)
        let devicesDir = Path.Combine(mainDir, $"{baseName}_devices")
        Directory.CreateDirectory(devicesDir) |> ignore

        let passiveSystems = Queries.passiveSystemsOf project.Id store
        let usedNames = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

        let deviceRefs =
            passiveSystems
            |> List.map (fun device ->
                let fileName = resolveDeviceFileName usedNames device
                let deviceAasxPath = Path.Combine(devicesDir, $"{fileName}.aasx")
                let relativePath = $"{baseName}_devices/{fileName}.aasx"
                exportDeviceAasx store project device deviceAasxPath
                deviceReferenceToSmc device relativePath)

        // 메인 AASX에는 DeviceReference만 포함
        let sm = exportToSubmodelSplit store project deviceRefs

        let submodels = ResizeArray<ISubmodel>([sm :> ISubmodel])
        let smRefs = ResizeArray<IReference>([mkSmRef sm])

        appendProjectMetadataSubmodels project submodels smRefs

        let iriPrefix = resolveIriPrefix project
        let globalAssetId = resolveGlobalAssetId project
        let assetInfo = AssetInformation(assetKind = AssetKind.Instance, globalAssetId = globalAssetId)
        let shell = AssetAdministrationShell(id = $"{iriPrefix}shell/{project.Name}", assetInformation = assetInfo)
        shell.IdShort <- "ProjectShell"
        shell.Submodels <- smRefs

        let conceptDescs = createAllConceptDescriptions true true
        let env =
            Environment(
                submodels = submodels,
                assetAdministrationShells = ResizeArray<IAssetAdministrationShell>([shell :> IAssetAdministrationShell]),
                conceptDescriptions = conceptDescs)
        let thumbnail = tryGetDefaultThumbnail ()
        writeEnvironment env outputPath thumbnail
        log.Info($"분리 저장 완료: {passiveSystems.Length}개 Device → {devicesDir}")

    /// Export helper for UI callers that should not access Project entity directly.
    /// Returns false when there is no project in the store.
    let internal tryExportFirstProjectToAasxFile (store: DsStore) (outputPath: string) : bool =
        match Queries.allProjects store |> List.tryHead with
        | None -> false
        | Some project ->
            exportToAasxFile store project outputPath
            true

    let exportFromStore (store: DsStore) (path: string) : bool =
        match Queries.allProjects store |> List.tryHead with
        | None -> false
        | Some project ->
            if project.Properties.SplitDeviceAasx then
                exportSplitAasx store project path
            else
                exportToAasxFile store project path
            true
