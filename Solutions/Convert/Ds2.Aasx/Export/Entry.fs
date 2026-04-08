namespace Ds2.Aasx

open System
open System.IO
open AasCore.Aas3_0
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxConceptDescriptions
open Ds2.Aasx.AasxFileIO
open Ds2.Core.Store

open log4net

module AasxExporter =

    let private log = LogManager.GetLogger("Ds2.Aasx.AasxExporter")

    open AasxExportCore
    open AasxExportGraph
    open AasxExportMetadata

    // ────────────────────────────────────────────────────────────────────────────
    // 내부 헬퍼 함수들
    // ────────────────────────────────────────────────────────────────────────────

    /// Submodel에 대한 ModelReference 생성
    let private mkSmRef (submodel: Submodel) : IReference =
        let key = Key(KeyTypes.Submodel, submodel.Id) :> IKey
        Reference(ReferenceTypes.ModelReference, ResizeArray<IKey>([key])) :> IReference

    /// Project 메타데이터 Submodel 추가 (Nameplate + Documentation)
    let private appendProjectMetadataSubmodels (project: Project) (submodels: ResizeArray<ISubmodel>) (smRefs: ResizeArray<IReference>) =
        let nameplate = project.Nameplate |> Option.defaultValue (Nameplate())
        let npSm = nameplateToSubmodel nameplate project.Id
        submodels.Add(npSm :> ISubmodel)
        smRefs.Add(mkSmRef npSm)

        // HandoverDocumentation은 항상 추가 (기본 샘플 Document 포함)
        let documentation = project.HandoverDocumentation |> Option.defaultValue (HandoverDocumentation())
        let docSm = documentationToSubmodel documentation project.Id
        submodels.Add(docSm :> ISubmodel)
        smRefs.Add(mkSmRef docSm)

    /// 메타데이터 Submodel 추가 (범용 - Device용)
    let private appendMetadataSubmodels (ownerId: Guid) (nameplate: Nameplate) (documentation: HandoverDocumentation) (submodels: ResizeArray<ISubmodel>) (smRefs: ResizeArray<IReference>) =
        let npSm = nameplateToSubmodel nameplate ownerId
        submodels.Add(npSm :> ISubmodel)
        smRefs.Add(mkSmRef npSm)

        // Device의 경우 Documentation은 Documents가 있을 때만 추가
        if documentation.Documents.Count > 0 then
            let docSm = documentationToSubmodel documentation ownerId
            submodels.Add(docSm :> ISubmodel)
            smRefs.Add(mkSmRef docSm)

    /// 기본 썸네일 이미지 로드 (embedded resource)
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

    /// Device용 기본 메타데이터 Submodel 추가
    let private appendDefaultDeviceMetadataSubmodels (device: DsSystem) (submodels: ResizeArray<ISubmodel>) (smRefs: ResizeArray<IReference>) =
        appendMetadataSubmodels device.Id (Nameplate()) (HandoverDocumentation()) submodels smRefs

    // ────────────────────────────────────────────────────────────────────────────
    // Submodel 생성 함수들
    // ────────────────────────────────────────────────────────────────────────────

    /// Device 이름을 파일명으로 안전하게 변환 (특수문자 → _)
    let sanitizeDeviceName (name: string) : string =
        let invalid = Path.GetInvalidFileNameChars()
        name.ToCharArray()
        |> Array.map (fun c -> if Array.contains c invalid then '_' else c)
        |> String.Concat

    /// SequenceModel 서브모델 생성 (기본 모델 정보)
    let internal exportToModelSubmodel (store: DsStore) (project: Project) (_iriPrefix: string) : Submodel =
        let activeSystems  = Queries.activeSystemsOf  project.Id store |> List.map (fun s -> systemToSmc store s true project.Id)
        let passiveSystems = Queries.passiveSystemsOf project.Id store |> List.map (fun s -> systemToSmc store s false project.Id)
        let projectElems : ISubmodelElement list = [
            mkProp     Name_         project.Name
            mkProp     Guid_         (project.Id.ToString())
            mkJsonProp<ResizeArray<TokenSpec>> TokenSpecs_ project.TokenSpecs
            // Project 메타데이터
            mkProp     Author_                   project.Author
            mkProp     Version_                  project.Version
            mkProp     DateTime_                 (project.DateTime.ToString("o"))
            // 시스템 계층 구조
            yield! mkSml ActiveSystems_    activeSystems |> Option.toList
            yield! mkSml DeviceReferences_ passiveSystems |> Option.toList
        ]
        let projectSmc = SubmodelElementCollection()
        projectSmc.IdShort <- "Project"
        projectSmc.Value <- ResizeArray<ISubmodelElement>(projectElems)

        let sm = Submodel(id = mkSubmodelId project.Id SequenceModel.Offset)
        sm.IdShort <- SubmodelModelIdShort
        sm.SemanticId <- mkSemanticRef $"{SubmodelSemanticId}/model"
        sm.SubmodelElements <- ResizeArray<ISubmodelElement>([projectSmc :> ISubmodelElement])
        sm

    /// 도메인별 서브모델 생성 헬퍼 (역방향 Reference 기반, 범용)
    /// System/Work/Call Properties가 있는 경우에만 해당 섹션 포함
    let private tryCreateSubmodel
        (store: DsStore)
        (project: Project)
        (submodelOffset: byte)
        (idShort: string)
        (semanticPath: string)
        (getSysProp: DsSystem -> 'TSys option)
        (getFlowProp: Flow -> 'TFlow option)
        (getWorkProp: Work -> 'TWork option)
        (getCallProp: Call -> 'TCall option)
        (sysConverter: 'TSys -> ISubmodelElement list)
        (flowConverter: 'TFlow -> ISubmodelElement list)
        (workConverter: 'TWork -> ISubmodelElement list)
        (callConverter: 'TCall -> ISubmodelElement list) : Submodel option =

        let activeSystems = Queries.activeSystemsOf project.Id store
        let allFlows = activeSystems |> List.collect (fun sys -> Queries.flowsOf sys.Id store)
        let allWorks = allFlows |> List.collect (fun flow -> Queries.worksOf flow.Id store)
        let allCalls = allWorks |> List.collect (fun work -> Queries.callsOf work.Id store)

        // System Properties
        // Use GUID-based idShorts since entity names may contain invalid characters
        // Create SMC even if properties are None to ensure References from SequenceModel are valid
        let sysPropsWithRefs =
            activeSystems |> List.map (fun sys ->
                match getSysProp sys with
                | Some props ->
                    let propElements = sysConverter props
                    // Use GUID-based idShort: "System_" + GUID (N format, no hyphens)
                    mkSmc (sanitizeIdShort ("System_" + sys.Id.ToString("N"))) propElements
                | None ->
                    // Create SMC with minimal metadata to ensure Reference target exists
                    // (Empty SMC causes AAS validation error: "Value must have at least one item")
                    mkSmc (sanitizeIdShort ("System_" + sys.Id.ToString("N"))) [
                        mkProp "Guid" (sys.Id.ToString())
                        mkProp "Name" sys.Name
                    ])

        // Flow Properties
        // Use GUID-based idShorts since entity names may contain invalid characters
        // Create SMC even if properties are None to ensure References from SequenceModel are valid
        let flowPropsWithRefs =
            allFlows |> List.map (fun f ->
                match getFlowProp f with
                | Some props ->
                    let propElements = flowConverter props
                    // Use GUID-based idShort: "Flow_" + GUID (N format, no hyphens)
                    mkSmc (sanitizeIdShort ("Flow_" + f.Id.ToString("N"))) propElements
                | None ->
                    // Create SMC with minimal metadata to ensure Reference target exists
                    mkSmc (sanitizeIdShort ("Flow_" + f.Id.ToString("N"))) [
                        mkProp "Guid" (f.Id.ToString())
                        mkProp "Name" f.Name
                    ])

        // Work Properties
        // Use GUID-based idShorts since entity names may contain invalid characters (e.g., FlowPrefix.LocalName)
        // Create SMC even if properties are None to ensure References from SequenceModel are valid
        let workPropsWithRefs =
            allWorks |> List.map (fun w ->
                match getWorkProp w with
                | Some props ->
                    let propElements = workConverter props
                    // Use GUID-based idShort: "Work_" + GUID (N format, no hyphens)
                    mkSmc (sanitizeIdShort ("Work_" + w.Id.ToString("N"))) propElements
                | None ->
                    // Create SMC with minimal metadata to ensure Reference target exists
                    mkSmc (sanitizeIdShort ("Work_" + w.Id.ToString("N"))) [
                        mkProp "Guid" (w.Id.ToString())
                        mkProp "Name" w.Name
                    ])

        // Call Properties
        // Use GUID-based idShorts since entity names may contain invalid characters
        // Create SMC even if properties are None to ensure References from SequenceModel are valid
        let callPropsWithRefs =
            allCalls |> List.map (fun c ->
                match getCallProp c with
                | Some props ->
                    let propElements = callConverter props
                    // Use GUID-based idShort: "Call_" + GUID (N format, no hyphens)
                    mkSmc (sanitizeIdShort ("Call_" + c.Id.ToString("N"))) propElements
                | None ->
                    // Create SMC with minimal metadata to ensure Reference target exists
                    mkSmc (sanitizeIdShort ("Call_" + c.Id.ToString("N"))) [
                        mkProp "Guid" (c.Id.ToString())
                        mkProp "Name" c.Name
                    ])

        // Use SubmodelElementCollection instead of SubmodelElementList
        // to allow idShort-based references from SequenceModel (avoiding AASd-120/128 constraints)
        let elements =
            [ if not sysPropsWithRefs.IsEmpty then yield mkSmc "SystemProperties" sysPropsWithRefs
              if not flowPropsWithRefs.IsEmpty then yield mkSmc "FlowProperties" flowPropsWithRefs
              if not workPropsWithRefs.IsEmpty then yield mkSmc "WorkProperties" workPropsWithRefs
              if not callPropsWithRefs.IsEmpty then yield mkSmc "CallProperties" callPropsWithRefs ]

        if elements.IsEmpty then None
        else
            let sm = Submodel(id = mkSubmodelId project.Id submodelOffset)
            sm.IdShort <- idShort
            sm.SemanticId <- mkSemanticRef $"{SubmodelSemanticId}/{semanticPath.ToLower()}"
            sm.SubmodelElements <- ResizeArray<ISubmodelElement>(elements)
            Some sm

    // ────────────────────────────────────────────────────────────────────────────
    // 도메인별 Submodel 생성 함수들 (DU 패턴 기반 통합)
    // ────────────────────────────────────────────────────────────────────────────

    /// 도메인 서브모델 생성 (통합 버전 - PropertyConversion 사용)
    let private tryExportToDomainSubmodel (submodelType: SubmodelType) (store: DsStore) (project: Project) : Submodel option =
        let activeSystems = Queries.activeSystemsOf project.Id store
        let allFlows = activeSystems |> List.collect (fun sys -> Queries.flowsOf sys.Id store)
        let allWorks = allFlows |> List.collect (fun flow -> Queries.worksOf flow.Id store)
        let allCalls = allWorks |> List.collect (fun work -> Queries.callsOf work.Id store)

        let sysPropsWithRefs =
            activeSystems |> List.choose (fun sys ->
                let propElements = PropertyConversion.getEntityElements submodelType sys
                if propElements.IsEmpty then None
                else Some (mkSmc (sanitizeIdShort ("System_" + sys.Id.ToString("N"))) propElements))

        let flowPropsWithRefs =
            allFlows |> List.choose (fun f ->
                let propElements = PropertyConversion.getEntityElements submodelType f
                if propElements.IsEmpty then None
                else Some (mkSmc (sanitizeIdShort ("Flow_" + f.Id.ToString("N"))) propElements))

        let workPropsWithRefs =
            allWorks |> List.choose (fun w ->
                // Properties가 있는 Work만 export (간결화)
                let propElements = PropertyConversion.getEntityElements submodelType w
                if propElements.IsEmpty then None
                else
                    // idShort에 GUID 인코딩 (밑줄 제거하면 파싱 가능)
                    Some (mkSmc (sanitizeIdShort ("Work_" + w.Id.ToString("N"))) propElements))

        let callPropsWithRefs =
            allCalls |> List.choose (fun c ->
                let propElements = PropertyConversion.getEntityElements submodelType c
                if propElements.IsEmpty then None
                else Some (mkSmc (sanitizeIdShort ("Call_" + c.Id.ToString("N"))) propElements))

        let elements =
            [ if not sysPropsWithRefs.IsEmpty then yield mkSmc "SystemProperties" sysPropsWithRefs
              if not flowPropsWithRefs.IsEmpty then yield mkSmc "FlowProperties" flowPropsWithRefs
              if not workPropsWithRefs.IsEmpty then yield mkSmc "WorkProperties" workPropsWithRefs
              if not callPropsWithRefs.IsEmpty then yield mkSmc "CallProperties" callPropsWithRefs ]

        if elements.IsEmpty then None
        else
            let sm = Submodel(id = mkSubmodelId project.Id submodelType.Offset)
            sm.IdShort <- submodelType.IdShort
            let semanticPath = submodelType.IdShort.ToLower().Replace("sequence", "")
            sm.SemanticId <- mkSemanticRef $"{SubmodelSemanticId}/{semanticPath}"
            sm.SubmodelElements <- ResizeArray<ISubmodelElement>(elements)
            Some sm


    // ────────────────────────────────────────────────────────────────────────────
    // 레거시 호환 함수들
    // ────────────────────────────────────────────────────────────────────────────

    /// 레거시 호환: 단일 Submodel 생성 (SequenceModel만)
    let internal exportToSubmodel (store: DsStore) (project: Project) (iriPrefix: string) : Submodel =
        exportToModelSubmodel store project iriPrefix

    // ────────────────────────────────────────────────────────────────────────────
    // 분리 모드 (Split Mode) 관련 함수들
    // ────────────────────────────────────────────────────────────────────────────

    /// DeviceReference SMC 생성 (분리 모드용 경량 참조)
    let internal deviceReferenceToSmc (device: DsSystem) (relativePath: string) : ISubmodelElement =
        mkSmc "DeviceReference" [
            mkProp DeviceGuid_         (device.Id.ToString())
            mkProp DeviceName_         device.Name
            mkProp DeviceIRI_          (device.IRI |> Option.defaultValue "")
            mkProp DeviceRelativePath_ relativePath
        ]

    /// 분리 모드용 SequenceModel Submodel 생성 (PassiveSystem 대신 DeviceReference 참조만 포함)
    let internal exportToModelSubmodelSplit (store: DsStore) (project: Project) (_iriPrefix: string) (deviceRefs: ISubmodelElement list) : Submodel =
        let activeSystems = Queries.activeSystemsOf project.Id store |> List.map (fun s -> systemToSmc store s true project.Id)
        let projectElems : ISubmodelElement list = [
            mkProp     Name_         project.Name
            mkProp     Guid_         (project.Id.ToString())
            mkJsonProp<ResizeArray<TokenSpec>> TokenSpecs_ project.TokenSpecs
            // Project 메타데이터
            mkProp     Author_                   project.Author
            mkProp     Version_                  project.Version
            mkProp     DateTime_                 (project.DateTime.ToString("o"))
            // 시스템 계층 구조
            yield! mkSml ActiveSystems_    activeSystems |> Option.toList
            yield! mkSml DeviceReferences_ deviceRefs |> Option.toList
        ]
        let projectSmc = SubmodelElementCollection()
        projectSmc.IdShort <- "Project"
        projectSmc.Value <- ResizeArray<ISubmodelElement>(projectElems)

        let sm = Submodel(id = mkSubmodelId project.Id SequenceModel.Offset)
        sm.IdShort <- SubmodelModelIdShort
        sm.SemanticId <- mkSemanticRef $"{SubmodelSemanticId}/model"
        sm.SubmodelElements <- ResizeArray<ISubmodelElement>([projectSmc :> ISubmodelElement])
        sm

    /// 레거시 호환: 분리 모드용 Submodel 생성
    let internal exportToSubmodelSplit (store: DsStore) (project: Project) (iriPrefix: string) (deviceRefs: ISubmodelElement list) : Submodel =
        exportToModelSubmodelSplit store project iriPrefix deviceRefs

    // ────────────────────────────────────────────────────────────────────────────
    // 공용 유틸리티 함수들
    // ────────────────────────────────────────────────────────────────────────────

    /// GlobalAssetId 생성 (IriPrefix와 ProjectName 기반)
    let private resolveGlobalAssetId (iriPrefix: string) (projectName: string) : string =
        let prefix = if String.IsNullOrWhiteSpace(iriPrefix) then DefaultIriPrefix else iriPrefix
        $"{prefix}assetId/{projectName}"

    // ────────────────────────────────────────────────────────────────────────────
    // AASX 파일 Export 진입점
    // ────────────────────────────────────────────────────────────────────────────

    /// 프로젝트를 단일 AASX 파일로 Export
    let internal exportToAasxFile (store: DsStore) (project: Project) (iriPrefix: string) (outputPath: string) : unit =
        let prefix = if String.IsNullOrWhiteSpace(iriPrefix) then DefaultIriPrefix else iriPrefix
        let thumbnail = tryGetDefaultThumbnail ()

        // Debug 모드 체크 및 기본 서브모델 생성
        #if DEBUG
        log.Info("Debug 모드: 모든 엔티티에 기본 서브모델 Properties를 추가합니다.")

        // Debug 모드일 때 모든 ActiveSystem에 기본 Properties 추가
        let activeSystems = Queries.activeSystemsOf project.Id store
        for sys in activeSystems do
            if sys.GetSimulationProperties().IsNone then sys.SetSimulationProperties(SimulationSystemProperties())
            if sys.GetControlProperties().IsNone then sys.SetControlProperties(ControlSystemProperties())
            if sys.GetMonitoringProperties().IsNone then sys.SetMonitoringProperties(MonitoringSystemProperties())
            if sys.GetLoggingProperties().IsNone then sys.SetLoggingProperties(LoggingSystemProperties())
            if sys.GetMaintenanceProperties().IsNone then sys.SetMaintenanceProperties(MaintenanceSystemProperties())
            if sys.GetHMIProperties().IsNone then sys.SetHMIProperties(HMISystemProperties())
            if sys.GetQualityProperties().IsNone then sys.SetQualityProperties(QualitySystemProperties())
            if sys.GetCostAnalysisProperties().IsNone then sys.SetCostAnalysisProperties(CostAnalysisSystemProperties())

            // Flow에도 기본 Properties 추가
            let flows = Queries.flowsOf sys.Id store
            for flow in flows do
                if flow.GetSimulationProperties().IsNone then flow.SetSimulationProperties(SimulationFlowProperties())
                if flow.GetControlProperties().IsNone then flow.SetControlProperties(ControlFlowProperties())
                if flow.GetMonitoringProperties().IsNone then flow.SetMonitoringProperties(MonitoringFlowProperties())
                if flow.GetLoggingProperties().IsNone then flow.SetLoggingProperties(LoggingFlowProperties())
                if flow.GetMaintenanceProperties().IsNone then flow.SetMaintenanceProperties(MaintenanceFlowProperties())
                if flow.GetHMIProperties().IsNone then flow.SetHMIProperties(HMIFlowProperties())
                if flow.GetQualityProperties().IsNone then flow.SetQualityProperties(QualityFlowProperties())
                if flow.GetCostAnalysisProperties().IsNone then flow.SetCostAnalysisProperties(CostAnalysisFlowProperties())

                // Work에도 기본 Properties 추가
                let works = Queries.worksOf flow.Id store
                for work in works do
                    if work.GetSimulationProperties().IsNone then work.SetSimulationProperties(SimulationWorkProperties())
                    if work.GetControlProperties().IsNone then work.SetControlProperties(ControlWorkProperties())
                    if work.GetMonitoringProperties().IsNone then work.SetMonitoringProperties(MonitoringWorkProperties())
                    if work.GetLoggingProperties().IsNone then work.SetLoggingProperties(LoggingWorkProperties())
                    if work.GetMaintenanceProperties().IsNone then work.SetMaintenanceProperties(MaintenanceWorkProperties())
                    if work.GetHMIProperties().IsNone then work.SetHMIProperties(HMIWorkProperties())
                    if work.GetQualityProperties().IsNone then work.SetQualityProperties(QualityWorkProperties())
                    if work.GetCostAnalysisProperties().IsNone then work.SetCostAnalysisProperties(CostAnalysisWorkProperties())

                    // Call에도 기본 Properties 추가
                    let calls = Queries.callsOf work.Id store
                    for call in calls do
                        if call.GetSimulationProperties().IsNone then call.SetSimulationProperties(SimulationCallProperties())
                        if call.GetControlProperties().IsNone then call.SetControlProperties(ControlCallProperties())
                        if call.GetMonitoringProperties().IsNone then call.SetMonitoringProperties(MonitoringCallProperties())
                        if call.GetLoggingProperties().IsNone then call.SetLoggingProperties(LoggingCallProperties())
                        if call.GetMaintenanceProperties().IsNone then call.SetMaintenanceProperties(MaintenanceCallProperties())
                        if call.GetHMIProperties().IsNone then call.SetHMIProperties(HMICallProperties())
                        if call.GetQualityProperties().IsNone then call.SetQualityProperties(QualityCallProperties())
                        if call.GetCostAnalysisProperties().IsNone then call.SetCostAnalysisProperties(CostAnalysisCallProperties())
        #endif

        // 서브모델 생성 (데이터가 있는 것만)
        let modelSm = exportToModelSubmodel store project prefix
        let optionalSubmodels =
            SubmodelType.AllDomains
            |> List.choose (fun submodelType -> tryExportToDomainSubmodel submodelType store project)

        let allSubmodels = modelSm :: optionalSubmodels

        let submodels = ResizeArray<ISubmodel>(allSubmodels |> List.map (fun sm -> sm :> ISubmodel))
        let smRefs = ResizeArray<IReference>(allSubmodels |> List.map mkSmRef)

        // Nameplate / Documentation (항상 포함 — Ev2 동일)
        appendProjectMetadataSubmodels project submodels smRefs

        // Shell — IriPrefix 기반 ID
        let globalAssetId = resolveGlobalAssetId prefix project.Name
        let assetInfo = AssetInformation(assetKind = AssetKind.Instance, globalAssetId = globalAssetId)
        let shell = AssetAdministrationShell(id = $"{prefix}shell/{project.Name}", assetInformation = assetInfo)
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
    let internal exportDeviceAasx (store: DsStore) (project: Project) (device: DsSystem) (iriPrefix: string) (outputPath: string) : unit =
        let prefix = if String.IsNullOrWhiteSpace(iriPrefix) then DefaultIriPrefix else iriPrefix
        let deviceSmc = systemToSmc store device false project.Id
        let projectElems : ISubmodelElement list = [
            mkProp     Name_         project.Name
            mkProp     Guid_         (project.Id.ToString())
            // Project 메타데이터
            mkProp     Author_                   project.Author
            mkProp     Version_                  project.Version
            mkProp     DateTime_                 (project.DateTime.ToString("o"))
            // 시스템 계층 구조
            yield! mkSml ActiveSystems_    [deviceSmc] |> Option.toList
            yield! mkSml DeviceReferences_ [] |> Option.toList
        ]
        let projectSmc = SubmodelElementCollection()
        projectSmc.IdShort <- "Project"
        projectSmc.Value <- ResizeArray<ISubmodelElement>(projectElems)

        let sm = Submodel(id = project.Id.ToString())
        sm.IdShort <- SubmodelModelIdShort
        sm.SemanticId <- mkSemanticRef SubmodelSemanticId
        sm.SubmodelElements <- ResizeArray<ISubmodelElement>([projectSmc :> ISubmodelElement])

        let globalAssetId = resolveGlobalAssetId prefix device.Name
        let assetInfo = AssetInformation(assetKind = AssetKind.Instance, globalAssetId = globalAssetId)
        let shell = AssetAdministrationShell(id = $"{prefix}shell/{device.Name}", assetInformation = assetInfo)
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
    let internal exportSplitAasx (store: DsStore) (project: Project) (iriPrefix: string) (outputPath: string) : unit =
        let prefix = if String.IsNullOrWhiteSpace(iriPrefix) then DefaultIriPrefix else iriPrefix
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
                exportDeviceAasx store project device prefix deviceAasxPath
                deviceReferenceToSmc device relativePath)

        // 메인 AASX에 서브모델 생성 (데이터가 있는 것만)
        let modelSm = exportToModelSubmodelSplit store project prefix deviceRefs
        let optionalSubmodels =
            SubmodelType.AllDomains
            |> List.choose (fun submodelType -> tryExportToDomainSubmodel submodelType store project)

        let allSubmodels = modelSm :: optionalSubmodels

        let submodels = ResizeArray<ISubmodel>(allSubmodels |> List.map (fun sm -> sm :> ISubmodel))
        let smRefs = ResizeArray<IReference>(allSubmodels |> List.map mkSmRef)

        appendProjectMetadataSubmodels project submodels smRefs

        let globalAssetId = resolveGlobalAssetId prefix project.Name
        let assetInfo = AssetInformation(assetKind = AssetKind.Instance, globalAssetId = globalAssetId)
        let shell = AssetAdministrationShell(id = $"{prefix}shell/{project.Name}", assetInformation = assetInfo)
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
    let internal tryExportFirstProjectToAasxFile (store: DsStore) (iriPrefix: string) (outputPath: string) : bool =
        match Queries.allProjects store |> List.tryHead with
        | None -> false
        | Some project ->
            exportToAasxFile store project iriPrefix outputPath
            true

    let exportFromStore (store: DsStore) (path: string) (iriPrefix: string) (splitDeviceAasx: bool) : bool =
        match Queries.allProjects store |> List.tryHead with
        | None -> false
        | Some project ->
            if splitDeviceAasx then
                exportSplitAasx store project iriPrefix path
            else
                exportToAasxFile store project iriPrefix path
            true
