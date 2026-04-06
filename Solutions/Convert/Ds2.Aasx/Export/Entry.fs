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

    // ────────────────────────────────────────────────────────────────────────────
    // 내부 헬퍼 함수들
    // ────────────────────────────────────────────────────────────────────────────

    /// Submodel에 대한 ModelReference 생성
    let private mkSmRef (submodel: Submodel) : IReference =
        let key = Key(KeyTypes.Submodel, submodel.Id) :> IKey
        Reference(ReferenceTypes.ModelReference, ResizeArray<IKey>([key])) :> IReference

    /// Project 메타데이터 Submodel 추가 (Nameplate + Documentation)
    let private appendProjectMetadataSubmodels (project: Project) (submodels: ResizeArray<ISubmodel>) (smRefs: ResizeArray<IReference>) =
        match project.Nameplate with
        | Some np ->
            let npSm = nameplateToSubmodel np project.Id
            submodels.Add(npSm :> ISubmodel)
            smRefs.Add(mkSmRef npSm)
        | None -> ()

        match project.HandoverDocumentation with
        | Some doc ->
            let docSm = documentationToSubmodel doc project.Id
            submodels.Add(docSm :> ISubmodel)
            smRefs.Add(mkSmRef docSm)
        | None -> ()

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
        let activeSystems  = Queries.activeSystemsOf  project.Id store |> List.map (fun s -> systemToSmc store s true)
        let passiveSystems = Queries.passiveSystemsOf project.Id store |> List.map (fun s -> systemToSmc store s false)
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

        let sm = Submodel(id = mkSubmodelId project.Id SubmodelOffsets.Model)
        sm.IdShort <- SubmodelModelIdShort
        sm.SemanticId <- mkSemanticRef $"{SubmodelSemanticId}/model"
        sm.SubmodelElements <- ResizeArray<ISubmodelElement>([projectSmc :> ISubmodelElement])
        sm

    // ────────────────────────────────────────────────────────────────────────────
    // Reference 생성 함수들 (GUID 기반 정확한 경로)
    // ────────────────────────────────────────────────────────────────────────────

    /// SequenceModel의 System 참조 생성
    /// AASd-128: SubmodelElementList 이후 Key는 정수 인덱스를 사용
    let private mkSystemReference (store: DsStore) (project: Project) (system: DsSystem) : ISubmodelElement =
        let modelSubmodelId = mkSubmodelId project.Id SubmodelOffsets.Model
        // AASd-128: SubmodelElementList 자식은 정수 인덱스로 참조
        let allSystems = Queries.activeSystemsOf project.Id store |> List.sortBy (fun s -> s.Id)
        let systemIndex = allSystems |> List.findIndex (fun s -> s.Id = system.Id)

        let refElem = ReferenceElement()
        refElem.IdShort <- sanitizeIdShort "ModelRef"
        refElem.Value <- Reference(
            ReferenceTypes.ModelReference,
            ResizeArray<IKey>([
                Key(KeyTypes.Submodel, modelSubmodelId) :> IKey
                Key(KeyTypes.SubmodelElementCollection, "Project") :> IKey
                Key(KeyTypes.SubmodelElementList, "ActiveSystems") :> IKey
                Key(KeyTypes.SubmodelElementCollection, string systemIndex) :> IKey  // 정수를 문자열로 변환
            ])) :> IReference
        refElem :> ISubmodelElement

    /// SequenceModel의 Flow 참조 생성
    /// AASd-128: SubmodelElementList 이후 Key는 정수 인덱스를 사용
    let private mkFlowReference (store: DsStore) (project: Project) (flow: Flow) : ISubmodelElement =
        let modelSubmodelId = mkSubmodelId project.Id SubmodelOffsets.Model
        let allSystems = Queries.activeSystemsOf project.Id store |> List.sortBy (fun s -> s.Id)
        let systemIndex = allSystems |> List.findIndex (fun s -> s.Id = flow.ParentId)

        let allFlows = Queries.flowsOf flow.ParentId store |> List.sortBy (fun f -> f.Id)
        let flowIndex = allFlows |> List.findIndex (fun f -> f.Id = flow.Id)

        let refElem = ReferenceElement()
        refElem.IdShort <- sanitizeIdShort "ModelRef"
        refElem.Value <- Reference(
            ReferenceTypes.ModelReference,
            ResizeArray<IKey>([
                Key(KeyTypes.Submodel, modelSubmodelId) :> IKey
                Key(KeyTypes.SubmodelElementCollection, "Project") :> IKey
                Key(KeyTypes.SubmodelElementList, "ActiveSystems") :> IKey
                Key(KeyTypes.SubmodelElementCollection, string systemIndex) :> IKey
                Key(KeyTypes.SubmodelElementList, "Flows") :> IKey
                Key(KeyTypes.SubmodelElementCollection, string flowIndex) :> IKey
            ])) :> IReference
        refElem :> ISubmodelElement

    /// SequenceModel의 Work 참조 생성
    /// AASd-128: SubmodelElementList 이후 Key는 정수 인덱스를 사용
    let private mkWorkReference (store: DsStore) (project: Project) (work: Work) : ISubmodelElement =
        let modelSubmodelId = mkSubmodelId project.Id SubmodelOffsets.Model
        // Flow → System 경로 찾기 및 인덱스 계산
        let flowOpt = Queries.getFlow work.ParentId store
        let systemIndexOpt =
            flowOpt
            |> Option.map (fun flow ->
                let allSystems = Queries.activeSystemsOf project.Id store |> List.sortBy (fun s -> s.Id)
                allSystems |> List.findIndex (fun s -> s.Id = flow.ParentId))

        let workIndex =
            flowOpt
            |> Option.map (fun flow ->
                let allWorks = Queries.worksOf flow.Id store |> List.sortBy (fun w -> w.Id)
                allWorks |> List.findIndex (fun w -> w.Id = work.Id))
            |> Option.defaultValue 0

        let refElem = ReferenceElement()
        refElem.IdShort <- sanitizeIdShort "ModelRef"
        refElem.Value <- Reference(
            ReferenceTypes.ModelReference,
            ResizeArray<IKey>([
                Key(KeyTypes.Submodel, modelSubmodelId) :> IKey
                Key(KeyTypes.SubmodelElementCollection, "Project") :> IKey
                Key(KeyTypes.SubmodelElementList, "ActiveSystems") :> IKey
                if systemIndexOpt.IsSome then
                    Key(KeyTypes.SubmodelElementCollection, string systemIndexOpt.Value) :> IKey
                    Key(KeyTypes.SubmodelElementList, "Works") :> IKey
                Key(KeyTypes.SubmodelElementCollection, string workIndex) :> IKey
            ])) :> IReference
        refElem :> ISubmodelElement

    /// SequenceModel의 Call 참조 생성
    /// AASd-128: SubmodelElementList 이후 Key는 정수 인덱스를 사용
    let private mkCallReference (store: DsStore) (project: Project) (call: Call) : ISubmodelElement =
        let modelSubmodelId = mkSubmodelId project.Id SubmodelOffsets.Model
        // Work → Flow → System 경로 찾기 및 인덱스 계산
        let workOpt = Queries.getWork call.ParentId store
        let flowOpt = workOpt |> Option.bind (fun work -> Queries.getFlow work.ParentId store)

        let systemIndexOpt =
            flowOpt |> Option.map (fun flow ->
                let allSystems = Queries.activeSystemsOf project.Id store |> List.sortBy (fun s -> s.Id)
                allSystems |> List.findIndex (fun s -> s.Id = flow.ParentId))

        let workIndexOpt =
            workOpt |> Option.bind (fun work ->
                Queries.getFlow work.ParentId store
                |> Option.map (fun flow ->
                    let allWorks = Queries.worksOf flow.Id store |> List.sortBy (fun w -> w.Id)
                    allWorks |> List.findIndex (fun w -> w.Id = work.Id)))

        let callIndex =
            workOpt
            |> Option.map (fun work ->
                let allCalls = Queries.callsOf work.Id store |> List.sortBy (fun c -> c.Id)
                allCalls |> List.findIndex (fun c -> c.Id = call.Id))
            |> Option.defaultValue 0

        let refElem = ReferenceElement()
        refElem.IdShort <- sanitizeIdShort "ModelRef"
        refElem.Value <- Reference(
            ReferenceTypes.ModelReference,
            ResizeArray<IKey>([
                Key(KeyTypes.Submodel, modelSubmodelId) :> IKey
                Key(KeyTypes.SubmodelElementCollection, "Project") :> IKey
                Key(KeyTypes.SubmodelElementList, "ActiveSystems") :> IKey
                if systemIndexOpt.IsSome && workIndexOpt.IsSome then
                    Key(KeyTypes.SubmodelElementCollection, string systemIndexOpt.Value) :> IKey
                    Key(KeyTypes.SubmodelElementList, "Works") :> IKey
                    Key(KeyTypes.SubmodelElementCollection, string workIndexOpt.Value) :> IKey
                    Key(KeyTypes.SubmodelElementList, "Calls") :> IKey
                Key(KeyTypes.SubmodelElementCollection, string callIndex) :> IKey
            ])) :> IReference
        refElem :> ISubmodelElement

    /// 도메인별 서브모델 생성 헬퍼 (Reference 기반, 범용)
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

        // System Properties (with ModelRef to SequenceModel)
        // Use GUID-based idShorts since entity names may contain invalid characters
        let sysPropsWithRefs =
            activeSystems |> List.choose (fun sys ->
                match getSysProp sys with
                | Some props ->
                    let propElements = sysConverter props
                    let modelRef = mkSystemReference store project sys
                    // ModelRef를 Properties 앞에 추가
                    let elementsWithRef = modelRef :: propElements
                    // Use GUID-based idShort: "System_" + GUID (N format, no hyphens)
                    Some (mkSmc (sanitizeIdShort ("System_" + sys.Id.ToString("N"))) elementsWithRef)
                | None -> None)

        // Flow Properties (with ModelRef to SequenceModel)
        // Use GUID-based idShorts since entity names may contain invalid characters
        let flowPropsWithRefs =
            allFlows |> List.choose (fun f ->
                match getFlowProp f with
                | Some props ->
                    let propElements = flowConverter props
                    let modelRef = mkFlowReference store project f
                    // ModelRef를 Properties 앞에 추가
                    let elementsWithRef = modelRef :: propElements
                    // Use GUID-based idShort: "Flow_" + GUID (N format, no hyphens)
                    Some (mkSmc (sanitizeIdShort ("Flow_" + f.Id.ToString("N"))) elementsWithRef)
                | None -> None)

        // Work Properties (with ModelRef to SequenceModel)
        // Use GUID-based idShorts since entity names may contain invalid characters (e.g., FlowPrefix.LocalName)
        let workPropsWithRefs =
            allWorks |> List.choose (fun w ->
                match getWorkProp w with
                | Some props ->
                    let propElements = workConverter props
                    let modelRef = mkWorkReference store project w
                    // ModelRef를 Properties 앞에 추가
                    let elementsWithRef = modelRef :: propElements
                    // Use GUID-based idShort: "Work_" + GUID (N format, no hyphens)
                    Some (mkSmc (sanitizeIdShort ("Work_" + w.Id.ToString("N"))) elementsWithRef)
                | None -> None)

        // Call Properties (with ModelRef to SequenceModel)
        // Use GUID-based idShorts since entity names may contain invalid characters
        let callPropsWithRefs =
            allCalls |> List.choose (fun c ->
                match getCallProp c with
                | Some props ->
                    let propElements = callConverter props
                    let modelRef = mkCallReference store project c
                    // ModelRef를 Properties 앞에 추가
                    let elementsWithRef = modelRef :: propElements
                    // Use GUID-based idShort: "Call_" + GUID (N format, no hyphens)
                    Some (mkSmc (sanitizeIdShort ("Call_" + c.Id.ToString("N"))) elementsWithRef)
                | None -> None)

        let elements =
            [ yield! mkSml "SystemProperties" sysPropsWithRefs |> Option.toList
              yield! mkSml "FlowProperties" flowPropsWithRefs |> Option.toList
              yield! mkSml "WorkProperties" workPropsWithRefs |> Option.toList
              yield! mkSml "CallProperties" callPropsWithRefs |> Option.toList ]

        if elements.IsEmpty then None
        else
            let sm = Submodel(id = mkSubmodelId project.Id submodelOffset)
            sm.IdShort <- idShort
            sm.SemanticId <- mkSemanticRef $"{SubmodelSemanticId}/{semanticPath.ToLower()}"
            sm.SubmodelElements <- ResizeArray<ISubmodelElement>(elements)
            Some sm

    // ────────────────────────────────────────────────────────────────────────────
    // 도메인별 Submodel 생성 함수들
    // ────────────────────────────────────────────────────────────────────────────

    /// SequenceSimulation Submodel 생성
    let internal tryExportToSimulationSubmodel (store: DsStore) (project: Project) : Submodel option =
        tryCreateSubmodel store project SubmodelOffsets.Simulation SubmodelSimulationIdShort "Simulation"
            (fun sys -> sys.GetSimulationProperties()) (fun f -> f.GetSimulationProperties()) (fun w -> w.GetSimulationProperties()) (fun c -> c.GetSimulationProperties())
            simulationSystemPropsToElements simulationFlowPropsToElements simulationWorkPropsToElements simulationCallPropsToElements

    /// SequenceControl Submodel 생성
    let internal tryExportToControlSubmodel (store: DsStore) (project: Project) : Submodel option =
        tryCreateSubmodel store project SubmodelOffsets.Control SubmodelControlIdShort "Control"
            (fun sys -> sys.GetControlProperties()) (fun f -> f.GetControlProperties()) (fun w -> w.GetControlProperties()) (fun c -> c.GetControlProperties())
            controlSystemPropsToElements controlFlowPropsToElements controlWorkPropsToElements controlCallPropsToElements

    /// SequenceMonitoring Submodel 생성
    let internal tryExportToMonitoringSubmodel (store: DsStore) (project: Project) : Submodel option =
        tryCreateSubmodel store project SubmodelOffsets.Monitoring SubmodelMonitoringIdShort "Monitoring"
            (fun sys -> sys.GetMonitoringProperties()) (fun f -> f.GetMonitoringProperties()) (fun w -> w.GetMonitoringProperties()) (fun c -> c.GetMonitoringProperties())
            monitoringSystemPropsToElements monitoringFlowPropsToElements monitoringWorkPropsToElements monitoringCallPropsToElements

    /// SequenceLogging Submodel 생성
    let internal tryExportToLoggingSubmodel (store: DsStore) (project: Project) : Submodel option =
        tryCreateSubmodel store project SubmodelOffsets.Logging SubmodelLoggingIdShort "Logging"
            (fun sys -> sys.GetLoggingProperties()) (fun f -> f.GetLoggingProperties()) (fun w -> w.GetLoggingProperties()) (fun c -> c.GetLoggingProperties())
            loggingSystemPropsToElements loggingFlowPropsToElements loggingWorkPropsToElements loggingCallPropsToElements

    /// SequenceMaintenance Submodel 생성
    let internal tryExportToMaintenanceSubmodel (store: DsStore) (project: Project) : Submodel option =
        tryCreateSubmodel store project SubmodelOffsets.Maintenance SubmodelMaintenanceIdShort "Maintenance"
            (fun sys -> sys.GetMaintenanceProperties()) (fun f -> f.GetMaintenanceProperties()) (fun w -> w.GetMaintenanceProperties()) (fun c -> c.GetMaintenanceProperties())
            maintenanceSystemPropsToElements maintenanceFlowPropsToElements maintenanceWorkPropsToElements maintenanceCallPropsToElements
            
    /// SequenceCostAnalysis Submodel 생성
    let internal tryExportToCostAnalysisSubmodel (store: DsStore) (project: Project) : Submodel option =
        tryCreateSubmodel store project SubmodelOffsets.CostAnalysis SubmodelCostAnalysisIdShort "CostAnalysis"
            (fun sys -> sys.GetCostAnalysisProperties()) (fun f -> f.GetCostAnalysisProperties()) (fun w -> w.GetCostAnalysisProperties()) (fun c -> c.GetCostAnalysisProperties())
            costAnalysisSystemPropsToElements costAnalysisFlowPropsToElements costAnalysisWorkPropsToElements costAnalysisCallPropsToElements

    /// SequenceQuality Submodel 생성
    let internal tryExportToQualitySubmodel (store: DsStore) (project: Project) : Submodel option =
        tryCreateSubmodel store project SubmodelOffsets.Quality SubmodelQualityIdShort "Quality"
            (fun sys -> sys.GetQualityProperties()) (fun f -> f.GetQualityProperties()) (fun w -> w.GetQualityProperties()) (fun c -> c.GetQualityProperties())
            qualitySystemPropsToElements qualityFlowPropsToElements qualityWorkPropsToElements qualityCallPropsToElements

    /// SequenceHmi Submodel 생성
    let internal tryExportToHmiSubmodel (store: DsStore) (project: Project) : Submodel option =
        tryCreateSubmodel store project SubmodelOffsets.Hmi SubmodelHmiIdShort "Hmi"
            (fun sys -> sys.GetHMIProperties()) (fun f -> f.GetHMIProperties()) (fun w -> w.GetHMIProperties()) (fun c -> c.GetHMIProperties())
            hmiSystemPropsToElements hmiFlowPropsToElements hmiWorkPropsToElements hmiCallPropsToElements

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
        let activeSystems = Queries.activeSystemsOf project.Id store |> List.map (fun s -> systemToSmc store s true)
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

        let sm = Submodel(id = mkSubmodelId project.Id SubmodelOffsets.Model)
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
            if sys.GetCostAnalysisProperties().IsNone then sys.SetCostAnalysisProperties(CostAnalysisSystemProperties())
            if sys.GetQualityProperties().IsNone then sys.SetQualityProperties(QualitySystemProperties())
            if sys.GetHMIProperties().IsNone then sys.SetHMIProperties(HMISystemProperties())

            // Flow에도 기본 Properties 추가
            let flows = Queries.flowsOf sys.Id store
            for flow in flows do
                if flow.GetSimulationProperties().IsNone then flow.SetSimulationProperties(SimulationFlowProperties())
                if flow.GetControlProperties().IsNone then flow.SetControlProperties(ControlFlowProperties())
                if flow.GetMonitoringProperties().IsNone then flow.SetMonitoringProperties(MonitoringFlowProperties())
                if flow.GetLoggingProperties().IsNone then flow.SetLoggingProperties(LoggingFlowProperties())
                if flow.GetMaintenanceProperties().IsNone then flow.SetMaintenanceProperties(MaintenanceFlowProperties())
                if flow.GetCostAnalysisProperties().IsNone then flow.SetCostAnalysisProperties(CostAnalysisFlowProperties())
                if flow.GetQualityProperties().IsNone then flow.SetQualityProperties(QualityFlowProperties())
                if flow.GetHMIProperties().IsNone then flow.SetHMIProperties(HMIFlowProperties())

                // Work에도 기본 Properties 추가
                let works = Queries.worksOf flow.Id store
                for work in works do
                    if work.GetSimulationProperties().IsNone then work.SetSimulationProperties(SimulationWorkProperties())
                    if work.GetControlProperties().IsNone then work.SetControlProperties(ControlWorkProperties())
                    if work.GetMonitoringProperties().IsNone then work.SetMonitoringProperties(MonitoringWorkProperties())
                    if work.GetLoggingProperties().IsNone then work.SetLoggingProperties(LoggingWorkProperties())
                    if work.GetMaintenanceProperties().IsNone then work.SetMaintenanceProperties(MaintenanceWorkProperties())
                    if work.GetCostAnalysisProperties().IsNone then work.SetCostAnalysisProperties(CostAnalysisWorkProperties())
                    if work.GetQualityProperties().IsNone then work.SetQualityProperties(QualityWorkProperties())
                    if work.GetHMIProperties().IsNone then work.SetHMIProperties(HMIWorkProperties())

                    // Call에도 기본 Properties 추가
                    let calls = Queries.callsOf work.Id store
                    for call in calls do
                        if call.GetSimulationProperties().IsNone then call.SetSimulationProperties(SimulationCallProperties())
                        if call.GetControlProperties().IsNone then call.SetControlProperties(ControlCallProperties())
                        if call.GetMonitoringProperties().IsNone then call.SetMonitoringProperties(MonitoringCallProperties())
                        if call.GetLoggingProperties().IsNone then call.SetLoggingProperties(LoggingCallProperties())
                        if call.GetMaintenanceProperties().IsNone then call.SetMaintenanceProperties(MaintenanceCallProperties())
                        if call.GetCostAnalysisProperties().IsNone then call.SetCostAnalysisProperties(CostAnalysisCallProperties())
                        if call.GetQualityProperties().IsNone then call.SetQualityProperties(QualityCallProperties())
                        if call.GetHMIProperties().IsNone then call.SetHMIProperties(HMICallProperties())
        #endif

        // 서브모델 생성 (데이터가 있는 것만)
        let modelSm = exportToModelSubmodel store project prefix
        let optionalSubmodels =
            [ tryExportToSimulationSubmodel store project
              tryExportToControlSubmodel store project
              tryExportToMonitoringSubmodel store project
              tryExportToLoggingSubmodel store project
              tryExportToMaintenanceSubmodel store project
              tryExportToCostAnalysisSubmodel store project
              tryExportToQualitySubmodel store project
              tryExportToHmiSubmodel store project ]
            |> List.choose id

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
        let deviceSmc = systemToSmc store device false
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
            [ tryExportToSimulationSubmodel store project
              tryExportToControlSubmodel store project
              tryExportToMonitoringSubmodel store project
              tryExportToLoggingSubmodel store project
              tryExportToMaintenanceSubmodel store project
              tryExportToCostAnalysisSubmodel store project ]
            |> List.choose id

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
