namespace Ds2.Aasx

open System
open System.IO
open AasCore.Aas3_1
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxConceptDescriptions
open Ds2.Aasx.AasxFileIO
open Ds2.Core.Store

module AasxExporter =

    let private log = SimpleLog.create "Ds2.Aasx.AasxExporter"

    open AasxExportCore
    open AasxExportGraph
    open AasxExportMetadata
    open AasxExportTechnicalData
    open FieldValidation

    let private mkSmRef (submodel: ISubmodel) : IReference =
        let key = Key(KeyTypes.Submodel, submodel.Id) :> IKey
        Reference(ReferenceTypes.ModelReference, ResizeArray<IKey>([key])) :> IReference

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

        // TechnicalData (IDTA 02003) — 시뮬결과 박제 가능. 항상 추가하되 비어있으면 기본 골격만.
        let techData = project.TechnicalData |> Option.defaultValue (TechnicalData())
        let tdSm = technicalDataToSubmodel techData project.Id
        submodels.Add(tdSm :> ISubmodel)
        smRefs.Add(mkSmRef tdSm)

    let private appendMetadataSubmodels (ownerId: Guid) (nameplate: Nameplate) (documentation: HandoverDocumentation) (submodels: ResizeArray<ISubmodel>) (smRefs: ResizeArray<IReference>) =
        let npSm = nameplateToSubmodel nameplate ownerId
        submodels.Add(npSm :> ISubmodel)
        smRefs.Add(mkSmRef npSm)

        if documentation.Documents.Count > 0 then
            let docSm = documentationToSubmodel documentation ownerId
            submodels.Add(docSm :> ISubmodel)
            smRefs.Add(mkSmRef docSm)

    let private tryGetOriginalThumbnail (originalEntries: System.Collections.Generic.Dictionary<string, byte[]> option) : AasxThumbnail option =
        match originalEntries with
        | None -> None
        | Some entries ->
            // _rels/.rels 파일에서 썸네일 관계 찾기
            match entries.TryGetValue("_rels/.rels") with
            | true, relsBytes when relsBytes <> null && relsBytes.Length > 0 ->
                try
                    use memStream = new MemoryStream(relsBytes)
                    use reader = new StreamReader(memStream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks = true)
                    let xml = reader.ReadToEnd()

                    if String.IsNullOrWhiteSpace(xml) then
                        None
                    else
                        let doc = System.Xml.XmlDocument()
                        doc.LoadXml(xml.Trim())
                        let nsm = System.Xml.XmlNamespaceManager(doc.NameTable)
                        nsm.AddNamespace("r", "http://schemas.openxmlformats.org/package/2006/relationships")

                        let node = doc.SelectSingleNode("//r:Relationship[@Type='http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail']", nsm)
                        if node <> null && node.Attributes.["Target"] <> null then
                            let target = node.Attributes.["Target"].Value.TrimStart('/')
                            match entries.TryGetValue(target) with
                            | true, thumbBytes when thumbBytes <> null && thumbBytes.Length > 0 ->
                                let contentType =
                                    if target.EndsWith(".png", StringComparison.OrdinalIgnoreCase) then "image/png"
                                    elif target.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) then "image/jpeg"
                                    else "application/octet-stream"
                                Some { EntryName = target; ContentType = contentType; Bytes = thumbBytes }
                            | _ -> None
                        else
                            None
                with ex ->
                    log.Warn($"원본 썸네일 추출 실패 (무시됨): {ex.Message}")
                    None
            | _ -> None

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

    let private selectThumbnail (originalEntries: System.Collections.Generic.Dictionary<string, byte[]> option) : AasxThumbnail option =
        match tryGetOriginalThumbnail originalEntries with
        | Some thumb -> Some thumb
        | None -> tryGetDefaultThumbnail ()

    let private appendDefaultDeviceMetadataSubmodels (device: DsSystem) (submodels: ResizeArray<ISubmodel>) (smRefs: ResizeArray<IReference>) =
        appendMetadataSubmodels device.Id (Nameplate()) (HandoverDocumentation()) submodels smRefs

    let sanitizeDeviceName (name: string) : string =
        let invalid = Path.GetInvalidFileNameChars()
        name.ToCharArray()
        |> Array.map (fun c -> if Array.contains c invalid then '_' else c)
        |> String.Concat

    let internal exportToModelSubmodel (store: DsStore) (project: Project) (_iriPrefix: string) : Submodel =
        let activeSystems  = Queries.activeSystemsOf  project.Id store |> List.map (fun s -> systemToSmc store s true project.Id)
        let passiveSystems = Queries.passiveSystemsOf project.Id store |> List.map (fun s -> systemToSmc store s false project.Id)
        let projectElems : ISubmodelElement list = [
            yield! mkPropsFromAasxFields project
            yield! mkSmlSem ActiveSystems_    activeSystems |> Option.toList
            yield! mkSmlSem DeviceReferences_ passiveSystems |> Option.toList
        ]
        let projectSmc = SubmodelElementCollection()
        projectSmc.IdShort <- "Project"
        projectSmc.Value <- ResizeArray<ISubmodelElement>(projectElems)

        let sm = Submodel(id = mkSubmodelId project.Id SequenceModel.Offset)
        sm.IdShort <- SubmodelModelIdShort
        sm.SemanticId <- mkSemanticRef SequenceModelSubmodelSemanticId
        sm.SubmodelElements <- ResizeArray<ISubmodelElement>([projectSmc :> ISubmodelElement])
        sm

    let private tryExportToDomainSubmodel (submodelType: SubmodelType) (store: DsStore) (project: Project) : Submodel option =
        let activeSystems = Queries.activeSystemsOf project.Id store
        let allFlows = activeSystems |> List.collect (fun sys -> Queries.flowsOf sys.Id store)
        let allWorks = allFlows |> List.collect (fun flow -> Queries.worksOf flow.Id store)
        let allCalls = allWorks |> List.collect (fun work -> Queries.callsOf work.Id store)

        let sysPropsWithRefs =
            activeSystems |> List.choose (fun sys ->
                let propElements = PropertyConversion.getEntityElements submodelType sys
                let extraElements =
                    match submodelType with
                    | SequenceControl ->
                        sys.GetControlProperties() |> Option.map controlIoConfigElems |> Option.defaultValue []
                    | _ -> []
                let allElems = propElements @ extraElements
                if allElems.IsEmpty then None
                else Some (mkSmc (sanitizeIdShort ("System_" + sys.Id.ToString("N"))) allElems))

        let flowPropsWithRefs =
            allFlows |> List.choose (fun f ->
                let propElements = PropertyConversion.getEntityElements submodelType f
                if propElements.IsEmpty then None
                else Some (mkSmc (sanitizeIdShort ("Flow_" + f.Id.ToString("N"))) propElements))

        let workPropsWithRefs =
            allWorks |> List.choose (fun w ->
                let propElements = PropertyConversion.getEntityElements submodelType w
                if propElements.IsEmpty then None
                else Some (mkSmc (sanitizeIdShort ("Work_" + w.Id.ToString("N"))) propElements))

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
            sm.SemanticId <- mkSemanticRef (submodelSemanticIdByIdShort submodelType.IdShort)
            sm.SubmodelElements <- ResizeArray<ISubmodelElement>(elements)
            Some sm


    let internal exportToSubmodel (store: DsStore) (project: Project) (iriPrefix: string) : Submodel =
        exportToModelSubmodel store project iriPrefix

    let internal deviceReferenceToSmc (device: DsSystem) (relativePath: string) : ISubmodelElement =
        mkSmc "DeviceReference" [
            mkProp DeviceGuid_         (device.Id.ToString())
            mkProp DeviceName_         device.Name
            mkProp DeviceIRI_          (device.IRI |> Option.defaultValue "")
            mkProp DeviceRelativePath_ relativePath
        ]

    let internal exportToModelSubmodelSplit (store: DsStore) (project: Project) (_iriPrefix: string) (deviceRefs: ISubmodelElement list) : Submodel =
        let activeSystems = Queries.activeSystemsOf project.Id store |> List.map (fun s -> systemToSmc store s true project.Id)
        let projectElems : ISubmodelElement list = [
            yield! mkPropsFromAasxFields project
            yield! mkSmlSem ActiveSystems_    activeSystems |> Option.toList
            yield! mkSmlSem DeviceReferences_ deviceRefs |> Option.toList
        ]
        let projectSmc = SubmodelElementCollection()
        projectSmc.IdShort <- "Project"
        projectSmc.Value <- ResizeArray<ISubmodelElement>(projectElems)

        let sm = Submodel(id = mkSubmodelId project.Id SequenceModel.Offset)
        sm.IdShort <- SubmodelModelIdShort
        sm.SemanticId <- mkSemanticRef SequenceModelSubmodelSemanticId
        sm.SubmodelElements <- ResizeArray<ISubmodelElement>([projectSmc :> ISubmodelElement])
        sm

    let internal exportToSubmodelSplit (store: DsStore) (project: Project) (iriPrefix: string) (deviceRefs: ISubmodelElement list) : Submodel =
        exportToModelSubmodelSplit store project iriPrefix deviceRefs

    let private resolveGlobalAssetId (iriPrefix: string) (projectName: string) : string =
        let prefix = if String.IsNullOrWhiteSpace(iriPrefix) then DefaultIriPrefix else iriPrefix
        $"{prefix}assetId/{projectName}"

    let internal exportToAasxFile (store: DsStore) (project: Project) (iriPrefix: string) (outputPath: string) (autoCreateEmptySubmodels: bool) : unit =
        let prefix = if String.IsNullOrWhiteSpace(iriPrefix) then DefaultIriPrefix else iriPrefix
        let thumbnail = selectThumbnail (AasxProjectCache.tryGetEntries project)

        if autoCreateEmptySubmodels then
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

        let modelSm = exportToModelSubmodel store project prefix
        let optionalSubmodels =
            SubmodelType.AllDomains
            |> List.choose (fun submodelType -> tryExportToDomainSubmodel submodelType store project)

        let allNewSubmodels = modelSm :: optionalSubmodels

        let (finalSubmodels, finalShells, finalConceptDescs) =
            match AasxProjectCache.tryGetEnvironment project with
            | Some envObj ->
                try
                    let originalEnv = envObj :?> Environment
                    // ds2 가 진실의 원천 (오버라이트): SequenceModel + 모든 도메인 서브모델만 매번 새로 생성.
                    // Nameplate / HandoverDocumentation / TechnicalData 는 원본이 있으면 보존 (ds2 변경분 무시).
                    // 원본에 없는 경우에만 project.* 로부터 신규 생성.
                    let sequenceIdShorts =
                        Set.ofList [
                            SubmodelModelIdShort
                            yield! SubmodelType.AllDomains |> List.map (fun t -> t.IdShort)
                        ]

                    let preservedSubmodels =
                        if originalEnv.Submodels <> null then
                            originalEnv.Submodels
                            |> Seq.filter (fun sm -> not (sequenceIdShorts.Contains(sm.IdShort)))
                            |> Seq.toList
                        else []

                    let combinedSubmodels = ResizeArray<ISubmodel>()
                    allNewSubmodels |> List.iter (fun sm -> combinedSubmodels.Add(sm :> ISubmodel))
                    preservedSubmodels |> List.iter combinedSubmodels.Add

                    // 원본에 메타 서브모델이 이미 존재하면 보존 (ds2 변경분 무시), 없으면 project.* 로부터 신규 생성.
                    let hasNameplate = combinedSubmodels |> Seq.exists (fun sm -> sm.IdShort = NameplateSubmodelIdShort)
                    let hasDocumentation = combinedSubmodels |> Seq.exists (fun sm -> sm.IdShort = DocumentationSubmodelIdShort)
                    let hasTechnicalData = combinedSubmodels |> Seq.exists (fun sm -> sm.IdShort = TechnicalDataSubmodelIdShort)

                    let smRefs = ResizeArray<IReference>()
                    allNewSubmodels |> List.iter (fun sm -> smRefs.Add(mkSmRef sm))
                    preservedSubmodels |> List.iter (fun sm -> smRefs.Add(mkSmRef sm))

                    if not hasNameplate then
                        let nameplate = project.Nameplate |> Option.defaultValue (Nameplate())
                        let npSm = nameplateToSubmodel nameplate project.Id
                        combinedSubmodels.Add(npSm :> ISubmodel)
                        smRefs.Add(mkSmRef npSm)

                    if not hasDocumentation then
                        let documentation = project.HandoverDocumentation |> Option.defaultValue (HandoverDocumentation())
                        let docSm = documentationToSubmodel documentation project.Id
                        combinedSubmodels.Add(docSm :> ISubmodel)
                        smRefs.Add(mkSmRef docSm)

                    if not hasTechnicalData then
                        let techData = project.TechnicalData |> Option.defaultValue (TechnicalData())
                        let tdSm = technicalDataToSubmodel techData project.Id
                        combinedSubmodels.Add(tdSm :> ISubmodel)
                        smRefs.Add(mkSmRef tdSm)

                    let finalShells =
                        if originalEnv.AssetAdministrationShells <> null && originalEnv.AssetAdministrationShells.Count > 0 then
                            let originalShell = originalEnv.AssetAdministrationShells.[0]
                            originalShell.Submodels <- smRefs
                            ResizeArray<IAssetAdministrationShell>([originalShell])
                        else
                            let globalAssetId = resolveGlobalAssetId prefix project.Name
                            let assetInfo = AssetInformation(assetKind = AssetKind.Instance, globalAssetId = globalAssetId)
                            let shell = AssetAdministrationShell(id = $"{prefix}shell/{project.Name}", assetInformation = assetInfo)
                            shell.IdShort <- "ProjectShell"
                            shell.Submodels <- smRefs
                            ResizeArray<IAssetAdministrationShell>([shell :> IAssetAdministrationShell])

                    let finalConcepts = createAllConceptDescriptions ()

                    (combinedSubmodels, finalShells, finalConcepts)
                with ex ->
                    log.Warn($"원본 Environment 처리 실패: {ex.Message}. 새로운 Environment를 생성합니다.", ex)
                    let submodels = ResizeArray<ISubmodel>(allNewSubmodels |> List.map (fun sm -> sm :> ISubmodel))
                    let smRefs = ResizeArray<IReference>(allNewSubmodels |> List.map mkSmRef)
                    appendProjectMetadataSubmodels project submodels smRefs

                    let globalAssetId = resolveGlobalAssetId prefix project.Name
                    let assetInfo = AssetInformation(assetKind = AssetKind.Instance, globalAssetId = globalAssetId)
                    let shell = AssetAdministrationShell(id = $"{prefix}shell/{project.Name}", assetInformation = assetInfo)
                    shell.IdShort <- "ProjectShell"
                    shell.Submodels <- smRefs

                    (submodels, ResizeArray<IAssetAdministrationShell>([shell :> IAssetAdministrationShell]), createAllConceptDescriptions ())
            | None ->
                let submodels = ResizeArray<ISubmodel>(allNewSubmodels |> List.map (fun sm -> sm :> ISubmodel))
                let smRefs = ResizeArray<IReference>(allNewSubmodels |> List.map mkSmRef)
                appendProjectMetadataSubmodels project submodels smRefs

                let globalAssetId = resolveGlobalAssetId prefix project.Name
                let assetInfo = AssetInformation(assetKind = AssetKind.Instance, globalAssetId = globalAssetId)
                let shell = AssetAdministrationShell(id = $"{prefix}shell/{project.Name}", assetInformation = assetInfo)
                shell.IdShort <- "ProjectShell"
                shell.Submodels <- smRefs

                (submodels, ResizeArray<IAssetAdministrationShell>([shell :> IAssetAdministrationShell]), createAllConceptDescriptions ())

        let env =
            Environment(
                submodels = finalSubmodels,
                assetAdministrationShells = finalShells,
                conceptDescriptions = finalConceptDescs)
        writeEnvironment env outputPath thumbnail (AasxProjectCache.tryGetEntries project)

    let internal exportDeviceAasx (store: DsStore) (project: Project) (device: DsSystem) (iriPrefix: string) (outputPath: string) : unit =
        let prefix = if String.IsNullOrWhiteSpace(iriPrefix) then DefaultIriPrefix else iriPrefix
        let deviceSmc = systemToSmc store device false project.Id
        let projectElems : ISubmodelElement list = [
            yield! mkPropsFromAasxFields project
            yield! mkSmlSem ActiveSystems_    [deviceSmc] |> Option.toList
            yield! mkSmlSem DeviceReferences_ [] |> Option.toList
        ]
        let projectSmc = SubmodelElementCollection()
        projectSmc.IdShort <- "Project"
        projectSmc.Value <- ResizeArray<ISubmodelElement>(projectElems)

        let sm = Submodel(id = project.Id.ToString())
        sm.IdShort <- SubmodelModelIdShort
        sm.SemanticId <- mkSemanticRef SequenceModelSubmodelSemanticId
        sm.SubmodelElements <- ResizeArray<ISubmodelElement>([projectSmc :> ISubmodelElement])

        let globalAssetId = resolveGlobalAssetId prefix device.Name
        let assetInfo = AssetInformation(assetKind = AssetKind.Instance, globalAssetId = globalAssetId)
        let shell = AssetAdministrationShell(id = $"{prefix}shell/{device.Name}", assetInformation = assetInfo)
        shell.IdShort <- "DeviceShell"
        let submodels = ResizeArray<ISubmodel>([sm :> ISubmodel])
        let smRefs = ResizeArray<IReference>([mkSmRef sm])
        appendDefaultDeviceMetadataSubmodels device submodels smRefs
        shell.Submodels <- smRefs

        let conceptDescs = createAllConceptDescriptions ()

        let env =
            Environment(
                submodels = submodels,
                assetAdministrationShells = ResizeArray<IAssetAdministrationShell>([shell :> IAssetAdministrationShell]),
                conceptDescriptions = conceptDescs)
        let thumbnail = selectThumbnail None
        writeEnvironment env outputPath thumbnail None

    let internal resolveDeviceFileName (usedNames: System.Collections.Generic.HashSet<string>) (device: DsSystem) : string =
        let baseName = sanitizeDeviceName device.Name
        if usedNames.Add(baseName) then
            baseName
        else
            let shortHash = device.Id.ToString("N").[..7]
            let uniqueName = $"{baseName}_{shortHash}"
            usedNames.Add(uniqueName) |> ignore
            uniqueName

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

        let conceptDescs = createAllConceptDescriptions ()
        let env =
            Environment(
                submodels = submodels,
                assetAdministrationShells = ResizeArray<IAssetAdministrationShell>([shell :> IAssetAdministrationShell]),
                conceptDescriptions = conceptDescs)
        let entries = AasxProjectCache.tryGetEntries project
        let thumbnail = selectThumbnail entries
        writeEnvironment env outputPath thumbnail entries
        log.Info($"분리 저장 완료: {passiveSystems.Length}개 Device → {devicesDir}")

    let internal tryExportFirstProjectToAasxFile (store: DsStore) (iriPrefix: string) (outputPath: string) (autoCreateEmptySubmodels: bool) : bool =
        match Queries.allProjects store |> List.tryHead with
        | None -> false
        | Some project ->
            exportToAasxFile store project iriPrefix outputPath autoCreateEmptySubmodels
            true

    let exportFromStore (store: DsStore) (path: string) (iriPrefix: string) (splitDeviceAasx: bool) (autoCreateEmptySubmodels: bool) : bool =
        validateAll () |> ignore

        match Queries.allProjects store |> List.tryHead with
        | None -> false
        | Some project ->
            if splitDeviceAasx then
                exportSplitAasx store project iriPrefix path
            else
                exportToAasxFile store project iriPrefix path autoCreateEmptySubmodels
            true

    let exportFromStoreOrRaise (store: DsStore) (path: string) (iriPrefix: string) (splitDeviceAasx: bool) (autoCreateEmptySubmodels: bool) : unit =
        validateAll () |> ignore

        match Queries.allProjects store |> List.tryHead with
        | None -> raise (InvalidOperationException("AASX export failed: store에 export할 Project가 없습니다."))
        | Some project ->
            if splitDeviceAasx then
                exportSplitAasx store project iriPrefix path
            else
                exportToAasxFile store project iriPrefix path autoCreateEmptySubmodels
