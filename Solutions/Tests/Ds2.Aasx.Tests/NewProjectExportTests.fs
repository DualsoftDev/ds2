module Ds2.Aasx.Tests.NewProjectExportTests

open System
open System.IO
open Xunit
open Ds2.Aasx
open Ds2.Core.StandardSubmodels
open Ds2.Core.Store
open Ds2.Editor

let private newPromakerProject () =
    let store = DsStore()
    let projectId = store.AddProject("NewProject")
    let systemId = store.AddSystem("NewSystem", projectId, true)
    store.AddFlow("NewFlow", systemId) |> ignore
    store

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``new Promaker project can be saved and loaded as AASX`` splitDeviceAasx =
    let store = newPromakerProject ()
    let path = Path.Combine(Path.GetTempPath(), $"new-project-{Guid.NewGuid():N}.aasx")
    let devicesPath = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "_devices")

    try
        Assert.True(AasxExporter.exportFromStore store path "" splitDeviceAasx false)
        Assert.True(File.Exists(path))

        let restored = DsStore.empty()
        let result = AasxImporter.importIntoStoreWithError restored path
        match result with
        | Ok () -> ()
        | Error error -> Assert.Fail(error)
        Assert.Single(restored.Projects) |> ignore
    finally
        if File.Exists(path) then File.Delete(path)
        if Directory.Exists(devicesPath) then Directory.Delete(devicesPath, true)

[<Fact>]
let ``new project with auto-created XGT AID can be saved as AASX`` () =
    let store = newPromakerProject ()
    let project = store.Projects.Values |> Seq.head
    let aid = AssetInterfacesDescription()
    project.AssetInterfaces <- Some aid
    AidXgtEndpointSettings.ensureBinding(
        aid, "LsXgi", "192.168.0.10", 2004, false, true,
        0uy, 255uy, 3000, 100, [ "%QX0.1.13"; "%IX0.1.2" ])
    |> ignore

    let path = Path.Combine(Path.GetTempPath(), $"new-project-xgt-{Guid.NewGuid():N}.aasx")
    try
        Assert.True(AasxExporter.exportFromStore store path "" false false)
        let restored = DsStore.empty()
        match AasxImporter.importIntoStoreWithError restored path with
        | Ok () -> ()
        | Error error -> Assert.Fail(error)
        let restoredProject = restored.Projects.Values |> Seq.head
        Assert.True(restoredProject.AssetInterfaces.IsSome)
    finally
        if File.Exists(path) then File.Delete(path)
