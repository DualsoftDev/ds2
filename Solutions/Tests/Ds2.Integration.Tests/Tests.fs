module Tests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open Ds2.Core
open Ds2.Serialization
open Ds2.Database

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

        let store = Ds2.UI.Core.DsStore.empty()
        store.Projects.[project.Id] <- project
        store.Systems.[system1.Id] <- system1
        store.Systems.[system2.Id] <- system2

        let json = JsonConverter.serialize store
        let deserialized = JsonConverter.deserialize<Ds2.UI.Core.DsStore> json

        Assert.Equal(1, deserialized.ProjectsReadOnly.Count)
        Assert.Equal(2, deserialized.SystemsReadOnly.Count)

/// 데이터베이스 통합 테스트
module DatabaseTests =

    let createInMemoryConnection () =
        let conn = new SqliteConnection("Data Source=:memory:")
        conn.Open()
        Schema.createSchema conn
        conn

    [<Fact>]
    let ``Project should save and load from database`` () =
        use conn = createInMemoryConnection()

        // Arrange
        let originalProject = Project("TestProject")

        // Act
        ProjectDb.save conn originalProject
        let loadedProject = ProjectDb.load conn (originalProject.Id.ToString())

        // Assert
        Assert.True(loadedProject.IsSome, "Project should be loaded")
        let loaded = loadedProject.Value
        Assert.Equal(originalProject.Id, loaded.Id)
        Assert.Equal(originalProject.Name, loaded.Name)

    [<Fact>]
    let ``Project should exist after saving`` () =
        use conn = createInMemoryConnection()

        let project = Project("TestProject")
        ProjectDb.save conn project

        Assert.True(ProjectDb.exists conn (project.Id.ToString()))

    [<Fact>]
    let ``Non-existent project should not exist`` () =
        use conn = createInMemoryConnection()

        Assert.False(ProjectDb.exists conn "non-existent-id")

    [<Fact>]
    let ``loadAll should return all saved projects`` () =
        use conn = createInMemoryConnection()

        let project1 = Project("Project1")
        let project2 = Project("Project2")

        ProjectDb.save conn project1
        ProjectDb.save conn project2

        let allProjects = ProjectDb.loadAll conn

        Assert.Equal(2, allProjects.Length)

    [<Fact>]
    let ``delete should remove project`` () =
        use conn = createInMemoryConnection()

        let project = Project("TestProject")
        ProjectDb.save conn project

        Assert.True(ProjectDb.exists conn (project.Id.ToString()))

        ProjectDb.delete conn (project.Id.ToString())

        Assert.False(ProjectDb.exists conn (project.Id.ToString()))

    [<Fact>]
    let ``System should save and load by project ID`` () =
        use conn = createInMemoryConnection()

        let project = Project("TestProject")
        let system = DsSystem("TestSystem")

        // Project에 ActiveSystem 추가
        project.ActiveSystemIds.Add(system.Id)

        ProjectDb.save conn project
        SystemDb.save conn (project.Id.ToString()) system

        let systems = SystemDb.loadByProjectId conn (project.Id.ToString())

        Assert.Single(systems) |> ignore
        Assert.Equal(system.Id, systems.[0].Id)
        Assert.Equal(system.Name, systems.[0].Name)

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
            let store = Ds2.UI.Core.DsStore.empty()
            store.Projects.[project.Id] <- project

            // Act
            let json = JsonConverter.serialize store
            File.WriteAllText(filePath, json)
            let loadedJson = File.ReadAllText(filePath)
            let loadedStore = JsonConverter.deserialize<Ds2.UI.Core.DsStore> loadedJson

            // Assert
            Assert.Single(loadedStore.ProjectsReadOnly) |> ignore
            let loadedProject = loadedStore.ProjectsReadOnly.Values |> Seq.head
            Assert.Equal(project.Id.ToString(), loadedProject.Id.ToString())
            Assert.Equal(project.Name, loadedProject.Name)
        finally
            if File.Exists(filePath) then File.Delete(filePath)

/// 전체 워크플로우 통합 테스트
module WorkflowTests =

    [<Fact>]
    let ``Complete workflow: Create, Validate, Serialize, Save to DB`` () =
        use conn = new SqliteConnection("Data Source=:memory:")
        conn.Open()
        Schema.createSchema conn

        // 1. Create domain objects
        let project = Project("WorkflowProject")
        let system = DsSystem("WorkflowSystem")

        // Project에 ActiveSystem 추가
        project.ActiveSystemIds.Add(system.Id)
        let store = Ds2.UI.Core.DsStore.empty()
        store.Projects.[project.Id] <- project
        store.Systems.[system.Id] <- system
        // 2. Serialize to JSON
        let json = JsonConverter.serialize store
        Assert.NotEmpty(json)

        // 3. Save to database
        ProjectDb.save conn project
        SystemDb.save conn (project.Id.ToString()) system

        // 4. Load from database
        let loadedProject = ProjectDb.load conn (project.Id.ToString())
        Assert.True(loadedProject.IsSome)

        let loadedSystems = SystemDb.loadByProjectId conn (project.Id.ToString())
        Assert.Single(loadedSystems) |> ignore

        // 5. Verify data integrity
        Assert.Equal(project.Id, loadedProject.Value.Id)
        Assert.Equal(system.Id, loadedSystems.[0].Id)

/// AASX 라운드트립 통합 테스트
module AasxRoundTripTests =

    open AasCore.Aas3_0
    open Ds2.UI.Core
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

        store.AddCallsWithDevice(projectId, workId, [ "Dev.Api1"; "Dev.Api2" ], true)
        let callIds = DsQuery.callsOf workId store |> List.map (fun c -> c.Id)
        let arrowCount = store.ConnectSelectionInOrder(callIds, ArrowType.ResetReset)
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
    let ``AASX import skips Work when FlowGuid is missing`` () =
        let store = DsStore()
        let projectId = store.AddProject("P")
        let systemId = store.AddSystem("S", projectId, true)
        let flowId = store.AddFlow("F", systemId)
        let workId = store.AddWork("W", flowId)
        store.AddCallsWithDevice(projectId, workId, [ "Dev.Api" ], true)

        let path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}.aasx")
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path
            Assert.True(exported, "Export should succeed")

            let env = Ds2.Aasx.AasxFileIO.readEnvironment path
            Assert.True(env.IsSome, "Environment should be readable")
            removeFlowGuidProperties env.Value
            Ds2.Aasx.AasxFileIO.writeEnvironment env.Value path

            let store2 = DsStore()
            let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 path
            Assert.True(imported, "Import should succeed even if malformed work exists")
            Assert.Empty(store2.Works)
            Assert.Empty(store2.Calls)
        finally
            if System.IO.File.Exists(path) then System.IO.File.Delete(path)

