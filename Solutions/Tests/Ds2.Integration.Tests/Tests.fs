module Tests

open System
open System.IO
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Serialization

module private AasxPackageAssertions =

    let assertPngThumbnailInAasxPackage (path: string) =
        use fileStream = new FileStream(path, FileMode.Open, FileAccess.Read)
        use archive = new System.IO.Compression.ZipArchive(fileStream, System.IO.Compression.ZipArchiveMode.Read)
        let relsEntry : System.IO.Compression.ZipArchiveEntry =
            match archive.GetEntry("_rels/.rels") with
            | null -> failwith "_rels/.rels entry missing"
            | entry -> entry
        use relsReader = new StreamReader(relsEntry.Open())
        let relsXml = relsReader.ReadToEnd()
        Assert.Contains("http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail", relsXml)
        Assert.Contains("/ds_aasx_thumbnail_icon.png", relsXml)

        let thumbnailEntry : System.IO.Compression.ZipArchiveEntry =
            match archive.GetEntry("ds_aasx_thumbnail_icon.png") with
            | null -> failwith "thumbnail entry missing"
            | entry -> entry
        Assert.True(thumbnailEntry.Length > 0L)

        let contentTypesEntry : System.IO.Compression.ZipArchiveEntry =
            match archive.GetEntry("[Content_Types].xml") with
            | null -> failwith "[Content_Types].xml entry missing"
            | entry -> entry
        use contentTypesReader = new StreamReader(contentTypesEntry.Open())
        let contentTypesXml = contentTypesReader.ReadToEnd()
        Assert.Contains("Extension=\"png\"", contentTypesXml)
        Assert.Contains("image/png", contentTypesXml)

/// JSON 직렬화 통합 테스트
module JsonSerializationTests =

    [<Fact>]
    let ``Project should serialize and deserialize correctly`` () =
        // Arrange
        let originalProject = Project("TestProject")
        let json = JsonConverter.serialize originalProject

        // Act
        let deserializedProject = JsonConverter.deserialize<Project> json

        // Assert
        Assert.Equal(originalProject.Id.ToString(), deserializedProject.Id.ToString())
        Assert.Equal(originalProject.Name, deserializedProject.Name)

    [<Fact>]
    let ``Complex Store with Projects and Systems should serialize correctly`` () =
        let project = Project("ComplexProject")
        let system1 = DsSystem("System1")
        let system2 = DsSystem("System2")

        // Project에 ActiveSystems 추가
        project.ActiveSystemIds.Add(system1.Id)
        project.ActiveSystemIds.Add(system2.Id)

        let store = DsStore.empty()
        store.Projects.[project.Id] <- project
        store.Systems.[system1.Id] <- system1
        store.Systems.[system2.Id] <- system2

        let json = JsonConverter.serialize store
        let deserialized = JsonConverter.deserialize<DsStore> json

        Assert.Equal(1, deserialized.ProjectsReadOnly.Count)
        Assert.Equal(2, deserialized.SystemsReadOnly.Count)


/// 파일 기반 통합 테스트
module FileSerializationTests =

    let getTempFilePath () =
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json")

    [<Fact>]
    let ``Store should save and load from file`` () =
        let filePath = getTempFilePath()
        try
            // Arrange
            let project = Project("FileTestProject")
            let store = DsStore.empty()
            store.Projects.[project.Id] <- project

            // Act
            let json = JsonConverter.serialize store
            File.WriteAllText(filePath, json)
            let loadedJson = File.ReadAllText(filePath)
            let loadedStore = JsonConverter.deserialize<DsStore> loadedJson

            // Assert
            Assert.Single(loadedStore.ProjectsReadOnly) |> ignore
            let loadedProject = loadedStore.ProjectsReadOnly.Values |> Seq.head
            Assert.Equal(project.Id.ToString(), loadedProject.Id.ToString())
            Assert.Equal(project.Name, loadedProject.Name)
        finally
            if File.Exists(filePath) then File.Delete(filePath)


/// AASX 라운드트립 통합 테스트
module AasxRoundTripTests =

    open AasCore.Aas3_0
    open Ds2.Core.Store
    open Ds2.Editor
    open Ds2.Aasx.AasxSemantics

    let private removeFlowGuidProperties (env: Environment) =
        let rec visitCollection (smc: SubmodelElementCollection) =
            if smc.Value <> null then
                let toRemove =
                    smc.Value
                    |> Seq.choose (function
                        | :? Property as p when p.IdShort = FlowGuid_ -> Some (p :> ISubmodelElement)
                        | _ -> None)
                    |> Seq.toList
                for item in toRemove do
                    smc.Value.Remove(item) |> ignore

                for child in smc.Value do
                    match child with
                    | :? SubmodelElementCollection as c -> visitCollection c
                    | :? SubmodelElementList as l when l.Value <> null ->
                        for li in l.Value do
                            match li with
                            | :? SubmodelElementCollection as lc -> visitCollection lc
                            | _ -> ()
                    | _ -> ()

        if env.Submodels <> null then
            for sm in env.Submodels do
                if sm.SubmodelElements <> null then
                    for elem in sm.SubmodelElements do
                        match elem with
                        | :? SubmodelElementCollection as c -> visitCollection c
                        | :? SubmodelElementList as l when l.Value <> null ->
                            for li in l.Value do
                                match li with
                                | :? SubmodelElementCollection as lc -> visitCollection lc
                                | _ -> ()
                        | _ -> ()

    [<Fact>]
    let ``AASX round-trip preserves ArrowBetweenCalls with parentId = workId`` () =
        let store = DsStore()
        let projectId = store.AddProject("P")
        let systemId = store.AddSystem("S", projectId, true)
        let flowId = store.AddFlow("F", systemId)
        let workId = store.AddWork("W", flowId)

        store.AddCallsWithDevice(projectId, workId, [ "Dev.Api1"; "Dev.Api2" ], true, None)
        let callIds = Queries.callsOf workId store |> List.map (fun c -> c.Id)
        // Call 간 화살표는 Start 또는 Group만 허용 (EntityKindRules)
        let arrowCount = store.ConnectSelectionInOrder(callIds, ArrowType.Start)
        Assert.Equal(1, arrowCount)
        let originalArrow = store.ArrowCalls.Values |> Seq.head
        Assert.Equal(workId, originalArrow.ParentId)

        let path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}.aasx")
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path "https://dualsoft.com/" false
            Assert.True(exported, "Export should succeed")

            let store2 = DsStore()
            let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 path
            Assert.True(imported, "Import should succeed")

            Assert.Equal(1, store2.ArrowCalls.Count)
            let restoredArrow = store2.ArrowCalls.Values |> Seq.head
            Assert.Equal(originalArrow.SourceId, restoredArrow.SourceId)
            Assert.Equal(originalArrow.TargetId, restoredArrow.TargetId)
            Assert.Equal(originalArrow.ArrowType, restoredArrow.ArrowType)
            // parentId = workId (flowId가 아님)
            let restoredWorkId = store2.Works.Values |> Seq.head |> fun w -> w.Id
            Assert.Equal(restoredWorkId, restoredArrow.ParentId)
        finally
            if System.IO.File.Exists(path) then System.IO.File.Delete(path)

    [<Fact>]
    let ``AASX round-trip preserves ArrowBetweenWorks with parentId = systemId`` () =
        let store = DsStore()
        let projectId = store.AddProject("P")
        let systemId = store.AddSystem("S", projectId, true)
        let flowId = store.AddFlow("F", systemId)
        let work1Id = store.AddWork("W1", flowId)
        let work2Id = store.AddWork("W2", flowId)

        let arrowCount = store.ConnectSelectionInOrder([ work1Id; work2Id ], ArrowType.StartReset)
        Assert.Equal(1, arrowCount)
        let originalArrow = store.ArrowWorks.Values |> Seq.head
        Assert.Equal(systemId, originalArrow.ParentId)

        let path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}.aasx")
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path "https://dualsoft.com/" false
            Assert.True(exported, "Export should succeed")

            let store2 = DsStore()
            let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 path
            Assert.True(imported, "Import should succeed")

            Assert.Equal(1, store2.ArrowWorks.Count)
            let restoredArrow = store2.ArrowWorks.Values |> Seq.head
            Assert.Equal(originalArrow.SourceId, restoredArrow.SourceId)
            Assert.Equal(originalArrow.TargetId, restoredArrow.TargetId)
            Assert.Equal(originalArrow.ArrowType, restoredArrow.ArrowType)
            // parentId = systemId
            let restoredSystemId = store2.Systems.Values |> Seq.head |> fun s -> s.Id
            Assert.Equal(restoredSystemId, restoredArrow.ParentId)
        finally
            if System.IO.File.Exists(path) then System.IO.File.Delete(path)

    [<Fact>]
    let ``AASX round-trip preserves Project TokenSpecs`` () =
        let store = DsStore()
        let projectId = store.AddProject("P")
        let systemId = store.AddSystem("S", projectId, true)
        let flowId = store.AddFlow("F", systemId)
        store.AddWork("W", flowId) |> ignore

        let project = store.Projects.Values |> Seq.head
        project.TokenSpecs.Add({ Id = 1; Label = "Avante"; Fields = Map.ofList [ "LotId", "LOT-001" ]; WorkId = None })
        project.TokenSpecs.Add({ Id = 2; Label = "Sonata"; Fields = Map.empty; WorkId = None })

        let path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}.aasx")
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path "https://dualsoft.com/" false
            Assert.True(exported, "Export should succeed")

            let store2 = DsStore()
            let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 path
            Assert.True(imported, "Import should succeed")

            let project2 = store2.Projects.Values |> Seq.head
            Assert.Equal(2, project2.TokenSpecs.Count)
            Assert.Equal("Avante", project2.TokenSpecs[0].Label)
            Assert.Equal("LOT-001", project2.TokenSpecs[0].Fields["LotId"])
            Assert.Equal("Sonata", project2.TokenSpecs[1].Label)
        finally
            if System.IO.File.Exists(path) then System.IO.File.Delete(path)


    [<Fact>]
    let ``AASX round-trip preserves Flow WorkIds order`` () =
        let store = DsStore()
        let projectId = store.AddProject("P")
        let systemId = store.AddSystem("S", projectId, true)
        let flowId = store.AddFlow("F", systemId)
        let w1 = store.AddWork("W1", flowId)
        let w2 = store.AddWork("W2", flowId)
        store.MoveWorkInFlow(flowId, w2, -1)
        let flow = store.Flows.[flowId]
        Assert.Equal(w2, flow.WorkIds[0])
        Assert.Equal(w1, flow.WorkIds[1])

        let path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}.aasx")
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path "https://test/" false
            Assert.True(exported, "Export should succeed")
            let store2 = DsStore()
            let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 path
            Assert.True(imported, "Import should succeed")
            let flow2 = store2.Flows.Values |> Seq.head
            Assert.Equal(2, flow2.WorkIds.Count)
            Assert.Equal(w2, flow2.WorkIds[0])
            Assert.Equal(w1, flow2.WorkIds[1])
        finally
            if System.IO.File.Exists(path) then System.IO.File.Delete(path)

    [<Fact>]
    let ``AASX round-trip preserves Work FlowPrefix and LocalName`` () =
        let store = DsStore()
        let projectId = store.AddProject("P")
        let systemId = store.AddSystem("S", projectId, true)
        let flowId = store.AddFlow("MyFlow", systemId)
        let workId = store.AddWork("MyWork", flowId)

        let work = store.Works.[workId]
        Assert.Equal("MyFlow", work.FlowPrefix)
        Assert.Equal("MyWork", work.LocalName)
        Assert.Equal("MyFlow.MyWork", work.Name)

        let path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}.aasx")
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path "https://dualsoft.com/" false
            Assert.True(exported, "Export should succeed")

            let store2 = DsStore()
            let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 path
            Assert.True(imported, "Import should succeed")

            let restoredWork = store2.Works.Values |> Seq.head
            Assert.Equal("MyFlow", restoredWork.FlowPrefix)
            Assert.Equal("MyWork", restoredWork.LocalName)
            Assert.Equal("MyFlow.MyWork", restoredWork.Name)
        finally
            if System.IO.File.Exists(path) then System.IO.File.Delete(path)

    [<Fact>]
    let ``AASX round-trip preserves Work TokenRole`` () =
        let store = DsStore()
        let projectId = store.AddProject("P")
        let systemId = store.AddSystem("S", projectId, true)
        let flowId = store.AddFlow("F", systemId)
        let workId = store.AddWork("W", flowId)

        store.Works.[workId].TokenRole <- TokenRole.Source
        Assert.Equal(TokenRole.Source, store.Works.[workId].TokenRole)

        let path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}.aasx")
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path "https://dualsoft.com/" false
            Assert.True(exported, "Export should succeed")

            let store2 = DsStore()
            let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 path
            Assert.True(imported, "Import should succeed")

            let restoredWork = store2.Works.Values |> Seq.head
            Assert.Equal(TokenRole.Source, restoredWork.TokenRole)
        finally
            if System.IO.File.Exists(path) then System.IO.File.Delete(path)


/// SplitDeviceAasx 분리 저장 통합 테스트
module SplitDeviceAasxTests =

    open AasCore.Aas3_0
    open Ds2.Core.Store
    open Ds2.Editor
    open Ds2.Aasx.AasxFileIO
    open Ds2.Aasx.AasxSemantics

    let private createStoreWithDevices () =
        let store = DsStore()
        let projectId = store.AddProject("TestProject")
        let activeId  = store.AddSystem("ActiveSys", projectId, true)
        let aFlowId   = store.AddFlow("AF", activeId)
        store.AddWork("AW", aFlowId) |> ignore

        let dev1Id = store.AddSystem("PLC_Siemens", projectId, false)
        let f1     = store.AddFlow("DF1", dev1Id)
        store.AddWork("DW1", f1) |> ignore

        let dev2Id = store.AddSystem("PLC_Mitsubishi", projectId, false)
        let f2     = store.AddFlow("DF2", dev2Id)
        store.AddWork("DW2", f2) |> ignore

        store, projectId

    let private getTempAasxPath () =
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.aasx")

    let private cleanupSplitFiles (path: string) =
        if File.Exists(path) then File.Delete(path)
        let dir = Path.GetDirectoryName(path)
        let baseName = Path.GetFileNameWithoutExtension(path)
        let devicesDir = Path.Combine(dir, $"{baseName}_devices")
        if Directory.Exists(devicesDir) then
            Directory.Delete(devicesDir, true)

    [<Fact>]
    let ``SplitDeviceAasx round-trip preserves all entities`` () =
        let store, projectId = createStoreWithDevices()
        let _ = store.Projects.[projectId]
        // SplitDeviceAasx는 export 함수의 파라미터로 전달

        let path = getTempAasxPath()
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path "https://dualsoft.com/" true
            Assert.True(exported, "Export should succeed")

            // _devices 폴더와 Device AASX 파일 존재 확인
            let baseName = Path.GetFileNameWithoutExtension(path)
            let devicesDir = Path.Combine(Path.GetDirectoryName(path), $"{baseName}_devices")
            Assert.True(Directory.Exists(devicesDir), "_devices 폴더가 생성되어야 함")
            let deviceFiles = Directory.GetFiles(devicesDir, "*.aasx")
            Assert.Equal(2, deviceFiles.Length)

            // Import로 복원
            let store2 = DsStore()
            let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 path
            Assert.True(imported, "Import should succeed")

            // Active + Passive 시스템 확인
            let project2 = store2.Projects.Values |> Seq.head
            Assert.Equal(1, project2.ActiveSystemIds.Count)
            Assert.Equal(2, project2.PassiveSystemIds.Count)
            Assert.Equal(3, store2.Systems.Count)

            // Device 이름 확인
            let deviceNames =
                project2.PassiveSystemIds
                |> Seq.map (fun id -> store2.Systems.[id].Name)
                |> Seq.sort |> Seq.toList
            Assert.Equal<string list>(["PLC_Mitsubishi"; "PLC_Siemens"], deviceNames)

            // Flow/Work도 복원되었는지 확인
            Assert.True(store2.Flows.Count >= 3, "모든 Flow가 복원되어야 함")
            Assert.True(store2.Works.Count >= 3, "모든 Work가 복원되어야 함")
        finally
            cleanupSplitFiles path

    [<Fact>]
    let ``SplitDeviceAasx backward compat — old AASX without field defaults to false`` () =
        let store = DsStore()
        let projectId = store.AddProject("P")
        store.AddSystem("S", projectId, true) |> ignore
        // SplitDeviceAasx를 명시적으로 설정하지 않음 → false

        let path = getTempAasxPath()
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path "https://dualsoft.com/" false
            Assert.True(exported)

            let store2 = DsStore()
            let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 path
            Assert.True(imported)

            let _ = store2.Projects.Values |> Seq.head
            // SplitDeviceAasx는 더 이상 Project 속성이 아니므로 검사 제거

            // _devices 폴더가 생성되지 않아야 함
            let baseName = Path.GetFileNameWithoutExtension(path)
            let devicesDir = Path.Combine(Path.GetDirectoryName(path), $"{baseName}_devices")
            Assert.False(Directory.Exists(devicesDir), "_devices 폴더가 없어야 함")
        finally
            cleanupSplitFiles path

    [<Fact>]
    let ``SplitDeviceAasx graceful degradation — missing device file skips`` () =
        let store, projectId = createStoreWithDevices()
        let _ = store.Projects.[projectId]
        // SplitDeviceAasx는 export 함수의 파라미터로 전달

        let path = getTempAasxPath()
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path "https://dualsoft.com/" true
            Assert.True(exported)

            // Device AASX 파일 하나 삭제
            let baseName = Path.GetFileNameWithoutExtension(path)
            let devicesDir = Path.Combine(Path.GetDirectoryName(path), $"{baseName}_devices")
            let deviceFiles = Directory.GetFiles(devicesDir, "*.aasx")
            File.Delete(deviceFiles.[0])

            // Import — 삭제된 Device는 스킵, 나머지는 로드
            let store2 = DsStore()
            let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 path
            Assert.True(imported, "Import should succeed even with missing device")

            let project2 = store2.Projects.Values |> Seq.head
            Assert.Equal(1, project2.ActiveSystemIds.Count)
            // 1개는 스킵되어 1개만 로드됨
            Assert.Equal(1, project2.PassiveSystemIds.Count)
        finally
            cleanupSplitFiles path

    [<Fact>]
    let ``SplitDeviceAasx device files include default metadata submodels and png thumbnail`` () =
        let store, projectId = createStoreWithDevices()
        let project = store.Projects.[projectId]
        // SplitDeviceAasx는 export 함수의 파라미터로 전달
        let np = Nameplate()
        np.ManufacturerName <- "Project Manufacturer"
        project.Nameplate <- Some np
        let doc = HandoverDocumentation()
        let projectDoc = Document()
        projectDoc.DocumentIds.Add(DocumentId(DocumentDomainId = "Project", ValueId = "DOC-001", IsPrimary = true))
        doc.Documents.Add(projectDoc)
        project.HandoverDocumentation <- Some doc

        let path = getTempAasxPath()
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path "https://dualsoft.com/" true
            Assert.True(exported)

            let baseName = Path.GetFileNameWithoutExtension(path)
            let devicesDir = Path.Combine(Path.GetDirectoryName(path), $"{baseName}_devices")
            let deviceFiles = Directory.GetFiles(devicesDir, "*.aasx")
            Assert.NotEmpty(deviceFiles)

            let devicePath = deviceFiles[0]
            let env = readEnvironment devicePath
            Assert.True(env.IsSome, "Device AASX should be readable")

            let submodelIdShorts =
                env.Value.Submodels
                |> Seq.map (fun sm -> sm.IdShort)
                |> Seq.toList

            Assert.Contains(SubmodelModelIdShort, submodelIdShorts)
            Assert.Contains(NameplateSubmodelIdShort, submodelIdShorts)
            // Documentation은 Documents가 있을 때만 생성됨 (AAS 빈 submodel 규칙)
            // Device AASX는 빈 HandoverDocumentation을 사용하므로 포함되지 않음
            Assert.DoesNotContain(DocumentationSubmodelIdShort, submodelIdShorts)
            Assert.NotNull(env.Value.ConceptDescriptions)
            Assert.NotEmpty(env.Value.ConceptDescriptions)

            let nameplate =
                env.Value.Submodels
                |> Seq.find (fun sm -> sm.IdShort = NameplateSubmodelIdShort)

            let manufacturerName =
                nameplate.SubmodelElements
                |> Seq.tryPick (function
                    | :? MultiLanguageProperty as mlp when mlp.IdShort = "ManufacturerName" ->
                        if isNull mlp.Value || mlp.Value.Count = 0 then Some ""
                        else Some mlp.Value.[0].Text
                    | _ -> None)
                |> Option.defaultValue "<missing>"

            // 빈 값은 AAS 검증 규칙에 따라 "N/A"로 대체됨
            Assert.Equal("N/A", manufacturerName)

            // Device AASX는 빈 HandoverDocumentation이므로 submodel이 생성되지 않음 (이미 위에서 검증)
            // 따라서 Documentation submodel 접근 불필요

            AasxPackageAssertions.assertPngThumbnailInAasxPackage devicePath
        finally
            cleanupSplitFiles path

    [<Fact>]
    let ``Main AASX export includes png thumbnail`` () =
        let store, _projectId = createStoreWithDevices()
        let path = getTempAasxPath()
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path "https://dualsoft.com/" false
            Assert.True(exported)
            AasxPackageAssertions.assertPngThumbnailInAasxPackage path
        finally
            cleanupSplitFiles path

    [<Fact>]
    let ``SplitDeviceAasx non-split uses existing path`` () =
        let store, _projectId = createStoreWithDevices()
        // SplitDeviceAasx = false (기본값)

        let path = getTempAasxPath()
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path "https://dualsoft.com/" false
            Assert.True(exported)

            // _devices 폴더가 생성되지 않아야 함
            let baseName = Path.GetFileNameWithoutExtension(path)
            let devicesDir = Path.Combine(Path.GetDirectoryName(path), $"{baseName}_devices")
            Assert.False(Directory.Exists(devicesDir), "_devices 폴더가 없어야 함")

            // Import 후 모든 엔티티 존재
            let store2 = DsStore()
            let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 path
            Assert.True(imported)

            let project2 = store2.Projects.Values |> Seq.head
            Assert.Equal(1, project2.ActiveSystemIds.Count)
            Assert.Equal(2, project2.PassiveSystemIds.Count)
        finally
            cleanupSplitFiles path

    [<Fact>]
    let ``Device name sanitization replaces special chars`` () =
        Assert.Equal("PLC_Test", Ds2.Aasx.AasxExporter.sanitizeDeviceName "PLC_Test")
        Assert.Equal("My_Device", Ds2.Aasx.AasxExporter.sanitizeDeviceName "My/Device")
        Assert.Equal("A_B_C", Ds2.Aasx.AasxExporter.sanitizeDeviceName "A\\B:C")

/// Nameplate & HandoverDocumentation 라운드트립 테스트
module NameplateRoundTripTests =

    [<Fact>]
    let ``Nameplate roundtrip SDF → AASX → SDF preserves all fields`` () =
        // 1. 초기 DsStore 생성 및 Nameplate 설정
        let store = DsStore()
        let project = Project("RoundTripProject")

        // Nameplate 설정
        let npData = Nameplate()
        npData.ManufacturerName <- "Test Manufacturer"
        npData.ManufacturerProductDesignation <- "Test Product"
        npData.SerialNumber <- "SN-12345"
        npData.YearOfConstruction <- "2024"
        npData.CountryOfOrigin <- "KR"
        npData.AddressInformation.Street <- "123 Test St"
        npData.AddressInformation.CityTown <- "Seoul"
        npData.AddressInformation.Phone.TelephoneNumber <- "+82-10-1234-5678"
        npData.AddressInformation.Email.EmailAddress <- "test@example.com"
        project.Nameplate <- Some npData

        store.Projects.Add(project.Id, project)

        // 2. SDF 저장
        let sdfPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.sdf")
        try
            ProjectSerializer.saveProject sdfPath project

            // 3. SDF → AASX Export
            let aasxPath = Path.ChangeExtension(sdfPath, ".aasx")
            try
                let exported = Ds2.Aasx.AasxExporter.exportFromStore store aasxPath "https://dualsoft.com/" false
                Assert.True(exported, "AASX export should succeed")

                // 4. AASX → DsStore Import
                let store2 = DsStore()
                let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 aasxPath
                Assert.True(imported, "AASX import should succeed")

                // 5. Nameplate 검증
                let project2 = store2.Projects.Values |> Seq.head
                let np2 = project2.Nameplate.Value
                Assert.Equal("Test Manufacturer", np2.ManufacturerName)
                Assert.Equal("Test Product", np2.ManufacturerProductDesignation)
                Assert.Equal("SN-12345", np2.SerialNumber)
                Assert.Equal("2024", np2.YearOfConstruction)
                Assert.Equal("KR", np2.CountryOfOrigin)
                Assert.Equal("123 Test St", np2.AddressInformation.Street)
                Assert.Equal("Seoul", np2.AddressInformation.CityTown)
                Assert.Equal("+82-10-1234-5678", np2.AddressInformation.Phone.TelephoneNumber)
                Assert.Equal("test@example.com", np2.AddressInformation.Email.EmailAddress)

                // 6. DsStore → SDF 저장 및 재로드
                let sdfPath2 = Path.ChangeExtension(aasxPath, ".sdf")
                try
                    ProjectSerializer.saveProject sdfPath2 project2
                    let project3 = ProjectSerializer.loadProject sdfPath2

                    // 7. 최종 검증
                    let np3 = project3.Nameplate.Value
                    Assert.Equal("Test Manufacturer", np3.ManufacturerName)
                    Assert.Equal("Test Product", np3.ManufacturerProductDesignation)
                    Assert.Equal("SN-12345", np3.SerialNumber)
                finally
                    if File.Exists(sdfPath2) then File.Delete(sdfPath2)
            finally
                if File.Exists(aasxPath) then File.Delete(aasxPath)
        finally
            if File.Exists(sdfPath) then File.Delete(sdfPath)

    [<Fact>]
    let ``HandoverDocumentation roundtrip SDF → AASX → SDF preserves documents`` () =
        // 1. 초기 DsStore 생성 및 HandoverDocumentation 설정
        let store = DsStore()
        let project = Project("DocRoundTripProject")

        // Document 추가
        let hdoc = HandoverDocumentation()
        let doc1 = Document()
        doc1.DocumentIds.Add(DocumentId(DocumentDomainId = "Manufacturer", ValueId = "DOC-001", IsPrimary = true))

        let classification1 = DocumentClassification()
        classification1.ClassId <- "01-01"
        classification1.ClassName <- "Technical Documentation"
        classification1.ClassificationSystem <- "VDI2770:2018"
        doc1.DocumentClassifications.Add(classification1)

        let version = DocumentVersion()
        version.Languages.Add("en")
        version.Languages.Add("ko")
        version.Title <- "User Manual"
        version.SubTitle <- "Installation Guide"
        doc1.DocumentVersions.Add(version)

        hdoc.Documents.Add(doc1)
        project.HandoverDocumentation <- Some hdoc

        store.Projects.Add(project.Id, project)

        // 2. SDF 저장
        let sdfPath = Path.Combine(Path.GetTempPath(), $"test_doc_{Guid.NewGuid()}.sdf")
        try
            ProjectSerializer.saveProject sdfPath project

            // 3. SDF → AASX Export
            let aasxPath = Path.ChangeExtension(sdfPath, ".aasx")
            try
                let exported = Ds2.Aasx.AasxExporter.exportFromStore store aasxPath "https://dualsoft.com/" false
                Assert.True(exported, "AASX export should succeed")

                // 4. AASX → DsStore Import
                let store2 = DsStore()
                let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 aasxPath
                Assert.True(imported, "AASX import should succeed")

                // 5. HandoverDocumentation 검증
                let project2 = store2.Projects.Values |> Seq.head
                Assert.Equal(1, project2.HandoverDocumentation.Value.Documents.Count)

                let doc2 = project2.HandoverDocumentation.Value.Documents.[0]
                Assert.Equal(1, doc2.DocumentIds.Count)
                Assert.Equal("Manufacturer", doc2.DocumentIds.[0].DocumentDomainId)
                Assert.Equal("DOC-001", doc2.DocumentIds.[0].ValueId)
                Assert.True(doc2.DocumentIds.[0].IsPrimary)
                Assert.Equal(1, doc2.DocumentClassifications.Count)
                Assert.Equal("01-01", doc2.DocumentClassifications.[0].ClassId)
                Assert.Equal("Technical Documentation", doc2.DocumentClassifications.[0].ClassName)

                Assert.Equal(1, doc2.DocumentVersions.Count)
                let ver2 = doc2.DocumentVersions.[0]
                Assert.Equal(2, ver2.Languages.Count)
                Assert.Contains("en", ver2.Languages)
                Assert.Contains("ko", ver2.Languages)
                Assert.Equal("User Manual", ver2.Title)
                Assert.Equal("Installation Guide", ver2.SubTitle)

                // 6. DsStore → SDF 저장 및 재로드
                let sdfPath2 = Path.ChangeExtension(aasxPath, ".sdf")
                try
                    ProjectSerializer.saveProject sdfPath2 project2
                    let project3 = ProjectSerializer.loadProject sdfPath2

                    // 7. 최종 검증
                    Assert.Equal(1, project3.HandoverDocumentation.Value.Documents.Count)
                    Assert.Equal("User Manual", project3.HandoverDocumentation.Value.Documents.[0].DocumentVersions.[0].Title)
                finally
                    if File.Exists(sdfPath2) then File.Delete(sdfPath2)
            finally
                if File.Exists(aasxPath) then File.Delete(aasxPath)
        finally
            if File.Exists(sdfPath) then File.Delete(sdfPath)

    [<Fact>]
    let ``Complete roundtrip AASX → SDF → AASX preserves Nameplate and Documentation`` () =
        // 1. 초기 AASX 생성
        let store = DsStore()
        let project = Project("CompleteRoundTrip")

        // Nameplate 설정
        let np = Nameplate()
        np.ManufacturerName <- "Original Manufacturer"
        np.SerialNumber <- "SN-99999"
        project.Nameplate <- Some np

        // HandoverDocumentation 설정
        let hdDoc = HandoverDocumentation()
        let doc = Document()
        doc.DocumentIds.Add(DocumentId(DocumentDomainId = "Original", ValueId = "ORIG-001", IsPrimary = true))
        hdDoc.Documents.Add(doc)
        project.HandoverDocumentation <- Some hdDoc

        store.Projects.Add(project.Id, project)

        // 2. AASX Export
        let aasxPath1 = Path.Combine(Path.GetTempPath(), $"test_complete_{Guid.NewGuid()}.aasx")
        try
            let exported1 = Ds2.Aasx.AasxExporter.exportFromStore store aasxPath1 "https://dualsoft.com/" false
            Assert.True(exported1)

            // 3. AASX → SDF
            let store2 = DsStore()
            let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 aasxPath1
            Assert.True(imported)

            let project2 = store2.Projects.Values |> Seq.head
            let sdfPath = Path.ChangeExtension(aasxPath1, ".sdf")
            try
                ProjectSerializer.saveProject sdfPath project2

                // 4. SDF → AASX
                let project3 = ProjectSerializer.loadProject sdfPath
                let store3 = DsStore()
                store3.Projects.Add(project3.Id, project3)

                let aasxPath2 = Path.Combine(Path.GetTempPath(), $"test_complete2_{Guid.NewGuid()}.aasx")
                try
                    let exported2 = Ds2.Aasx.AasxExporter.exportFromStore store3 aasxPath2 "https://dualsoft.com/" false
                    Assert.True(exported2)

                    // 5. 최종 AASX Import 및 검증
                    let store4 = DsStore()
                    let imported2 = Ds2.Aasx.AasxImporter.importIntoStore store4 aasxPath2
                    Assert.True(imported2)

                    let project4 = store4.Projects.Values |> Seq.head
                    Assert.Equal("Original Manufacturer", project4.Nameplate.Value.ManufacturerName)
                    Assert.Equal("SN-99999", project4.Nameplate.Value.SerialNumber)
                    Assert.Equal(1, project4.HandoverDocumentation.Value.Documents.Count)
                    Assert.Equal("ORIG-001", project4.HandoverDocumentation.Value.Documents.[0].DocumentIds.[0].ValueId)
                finally
                    if File.Exists(aasxPath2) then File.Delete(aasxPath2)
            finally
                if File.Exists(sdfPath) then File.Delete(sdfPath)
        finally
            if File.Exists(aasxPath1) then File.Delete(aasxPath1)

/// AAS 검증 API 테스트
module AasValidationTests =

    open AasCore.Aas3_0
    open Ds2.Core.Store
    open Ds2.Editor

    [<Fact>]
    let ``Exported AASX should pass AasCore validation`` () =
        // Arrange
        let store = DsStore()
        let projectId = store.AddProject("TestProject")
        let systemId = store.AddSystem("TestSystem", projectId, true)
        let flowId = store.AddFlow("TestFlow", systemId)
        let workId = store.AddWork("TestWork", flowId)
        store.AddCallsWithDevice(projectId, workId, ["TestDevice.TestApi"], true, None)

        let path = Path.Combine(Path.GetTempPath(), $"validation_test_{Guid.NewGuid()}.aasx")

        try
            // Act - Export
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path "https://dualsoft.com/" false
            Assert.True(exported)

            // Act - Load and validate Environment
            let envOpt = Ds2.Aasx.AasxFileIO.readEnvironment path
            Assert.True(envOpt.IsSome, "Failed to read AASX environment")

            let env = envOpt.Value

            // Assert - Run AasCore validation
            let errorList = ResizeArray<Reporting.Error>()
            for item in Verification.Verify(env) do
                errorList.Add(item)

            // Report any validation errors with detailed path information
            if errorList.Count > 0 then
                let errorMessages =
                    errorList
                    |> Seq.mapi (fun i e ->
                        let pathStr =
                            e.PathSegments
                            |> Seq.map (fun seg ->
                                match seg with
                                | :? Reporting.NameSegment as ns -> sprintf "[%s]" ns.Name
                                | :? Reporting.IndexSegment as is -> sprintf "[%d]" is.Index
                                | _ -> sprintf "[%O]" seg)
                            |> String.concat ""
                        sprintf "%d. %s\n   %O" (i+1) pathStr e.Cause)
                    |> String.concat "\n\n"

                // 에러 타입별 집계
                let errorGroups =
                    errorList
                    |> Seq.groupBy (fun e -> e.Cause.ToString().Substring(0, min 80 (e.Cause.ToString().Length)))
                    |> Seq.map (fun (cause, errors) -> sprintf "  - %s: %d개" cause (Seq.length errors))
                    |> String.concat "\n"

                Assert.True(false, sprintf "AAS validation failed with %d errors:\n\n에러 타입별:\n%s\n\n상세:\n%s" errorList.Count errorGroups errorMessages)

            Assert.Equal(0, errorList.Count)

        finally
            if File.Exists(path) then File.Delete(path)

