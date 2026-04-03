module Tests

open System
open System.IO
open Xunit
open Ds2.Core
open Ds2.Store.DsQuery
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

        let store = Ds2.Store.DsStore.empty()
        store.Projects.[project.Id] <- project
        store.Systems.[system1.Id] <- system1
        store.Systems.[system2.Id] <- system2

        let json = JsonConverter.serialize store
        let deserialized = JsonConverter.deserialize<Ds2.Store.DsStore> json

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
            let store = Ds2.Store.DsStore.empty()
            store.Projects.[project.Id] <- project

            // Act
            let json = JsonConverter.serialize store
            File.WriteAllText(filePath, json)
            let loadedJson = File.ReadAllText(filePath)
            let loadedStore = JsonConverter.deserialize<Ds2.Store.DsStore> loadedJson

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
    open Ds2.Store
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
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path
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
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path
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
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path
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
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path
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
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path
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
    open Ds2.Store
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
        let project = store.Projects.[projectId]
        project.Properties.SplitDeviceAasx <- true

        let path = getTempAasxPath()
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path
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
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path
            Assert.True(exported)

            let store2 = DsStore()
            let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 path
            Assert.True(imported)

            let project2 = store2.Projects.Values |> Seq.head
            Assert.False(project2.Properties.SplitDeviceAasx, "기본값은 false여야 함")

            // _devices 폴더가 생성되지 않아야 함
            let baseName = Path.GetFileNameWithoutExtension(path)
            let devicesDir = Path.Combine(Path.GetDirectoryName(path), $"{baseName}_devices")
            Assert.False(Directory.Exists(devicesDir), "_devices 폴더가 없어야 함")
        finally
            cleanupSplitFiles path

    [<Fact>]
    let ``SplitDeviceAasx graceful degradation — missing device file skips`` () =
        let store, projectId = createStoreWithDevices()
        let project = store.Projects.[projectId]
        project.Properties.SplitDeviceAasx <- true

        let path = getTempAasxPath()
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path
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
        project.Properties.SplitDeviceAasx <- true
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
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path
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

            Assert.Contains(SubmodelIdShort, submodelIdShorts)
            Assert.Contains(NameplateSubmodelIdShort, submodelIdShorts)
            Assert.Contains(DocumentationSubmodelIdShort, submodelIdShorts)
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

            Assert.Equal("", manufacturerName)

            let documentation =
                env.Value.Submodels
                |> Seq.find (fun sm -> sm.IdShort = DocumentationSubmodelIdShort)

            let hasDocuments =
                documentation.SubmodelElements
                |> Seq.exists (function
                    | :? SubmodelElementList as sml when sml.IdShort = "Documents" && not (isNull sml.Value) && sml.Value.Count > 0 -> true
                    | _ -> false)

            Assert.False(hasDocuments)

            AasxPackageAssertions.assertPngThumbnailInAasxPackage devicePath
        finally
            cleanupSplitFiles path

    [<Fact>]
    let ``Main AASX export includes png thumbnail`` () =
        let store, _projectId = createStoreWithDevices()
        let path = getTempAasxPath()
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path
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
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path
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

