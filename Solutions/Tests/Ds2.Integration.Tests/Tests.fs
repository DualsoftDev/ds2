module Tests

open System
open System.IO
open Xunit
open Ds2.Core
open Ds2.Serialization

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
    let ``AASX import fails when Work FlowGuid is missing and keeps store unchanged`` () =
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
            let baseProjectId = store2.AddProject("BaseP")
            let baseSystemId = store2.AddSystem("BaseS", baseProjectId, true)
            let baseFlowId = store2.AddFlow("BaseF", baseSystemId)
            store2.AddWork("BaseW", baseFlowId) |> ignore

            let beforeCounts = (store2.Projects.Count, store2.Systems.Count, store2.Flows.Count, store2.Works.Count, store2.Calls.Count)

            let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 path

            Assert.False(imported, "Import should fail when Work.FlowGuid is missing")

            let afterCounts = (store2.Projects.Count, store2.Systems.Count, store2.Flows.Count, store2.Works.Count, store2.Calls.Count)
            Assert.Equal(beforeCounts, afterCounts)

            let hasBaseProject = store2.Projects.Values |> Seq.exists (fun p -> p.Name = "BaseP")
            let hasBaseWork = store2.Works.Values |> Seq.exists (fun w -> w.Name = "BaseW")
            Assert.True(hasBaseProject, "Existing project data should remain untouched")
            Assert.True(hasBaseWork, "Existing work data should remain untouched")
        finally
            if System.IO.File.Exists(path) then System.IO.File.Delete(path)

    [<Fact>]
    let ``AASX round-trip preserves Work TokenRole`` () =
        let store = DsStore()
        let projectId = store.AddProject("P")
        let systemId = store.AddSystem("S", projectId, true)
        let flowId = store.AddFlow("F", systemId)
        let w1Id = store.AddWork("Source", flowId)
        store.AddWork("Pass", flowId) |> ignore
        let w3Id = store.AddWork("Ignore", flowId)

        store.UpdateWorkTokenRole(w1Id, TokenRole.Source)
        store.UpdateWorkTokenRole(w3Id, TokenRole.Ignore)

        let path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}.aasx")
        try
            let exported = Ds2.Aasx.AasxExporter.exportFromStore store path
            Assert.True(exported, "Export should succeed")

            let store2 = DsStore()
            let imported = Ds2.Aasx.AasxImporter.importIntoStore store2 path
            Assert.True(imported, "Import should succeed")

            let findWork name = store2.Works.Values |> Seq.find (fun w -> w.Name = name)
            Assert.Equal(TokenRole.Source, (findWork "Source").TokenRole)
            Assert.Equal(TokenRole.None,   (findWork "Pass").TokenRole)
            Assert.Equal(TokenRole.Ignore, (findWork "Ignore").TokenRole)
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
        project.TokenSpecs.Add({ Id = 1; Label = "Avante"; Fields = Map.ofList [ "LotId", "LOT-001" ] })
        project.TokenSpecs.Add({ Id = 2; Label = "Sonata"; Fields = Map.empty })

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


