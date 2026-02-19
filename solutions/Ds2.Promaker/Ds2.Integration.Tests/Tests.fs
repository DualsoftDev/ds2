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
        Ds2.UI.Core.Mutation.addProject project store |> ignore
        Ds2.UI.Core.Mutation.addSystem system1 store |> ignore
        Ds2.UI.Core.Mutation.addSystem system2 store |> ignore

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

/// Validation 통합 테스트
module ValidationIntegrationTests =

    [<Fact>]
    let ``Valid project should pass validation`` () =
        let project = Project("ValidProject")
        let result = Ds2.UI.Core.ProjectValidation.validate project

        match result with
        | Ds2.UI.Core.Valid -> Assert.True(true)
        | Ds2.UI.Core.Invalid errors -> Assert.True(false, sprintf "Validation should pass but got errors: %A" errors)

    [<Fact>]
    let ``Project with empty name should fail validation`` () =
        let project = Project("Test")
        project.Name <- ""
        let result = Ds2.UI.Core.ProjectValidation.validate project

        match result with
        | Ds2.UI.Core.Valid -> Assert.True(false, "Validation should fail for empty name")
        | Ds2.UI.Core.Invalid errors ->
            Assert.NotEmpty(errors)
            Assert.Contains("Name", errors.[0])

    [<Fact>]
    let ``Project with empty GUID should fail validation`` () =
        let project = Project("Test")
        project.Id <- Guid.Empty
        let result = Ds2.UI.Core.ProjectValidation.validate project

        match result with
        | Ds2.UI.Core.Valid -> Assert.True(false, "Validation should fail for empty GUID")
        | Ds2.UI.Core.Invalid errors ->
            Assert.NotEmpty(errors)
            Assert.Contains("GUID", errors.[0])

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
            Ds2.UI.Core.Mutation.addProject project store |> ignore

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
        Ds2.UI.Core.Mutation.addProject project store |> ignore
        Ds2.UI.Core.Mutation.addSystem system store |> ignore

        // 2. Validate
        let validationResult = Ds2.UI.Core.ProjectValidation.validate project
        Assert.Equal(Ds2.UI.Core.Valid, validationResult)

        // 3. Serialize to JSON
        let json = JsonConverter.serialize store
        Assert.NotEmpty(json)

        // 4. Save to database
        ProjectDb.save conn project
        SystemDb.save conn (project.Id.ToString()) system

        // 5. Load from database
        let loadedProject = ProjectDb.load conn (project.Id.ToString())
        Assert.True(loadedProject.IsSome)

        let loadedSystems = SystemDb.loadByProjectId conn (project.Id.ToString())
        Assert.Single(loadedSystems) |> ignore

        // 6. Verify data integrity
        Assert.Equal(project.Id, loadedProject.Value.Id)
        Assert.Equal(system.Id, loadedSystems.[0].Id)
