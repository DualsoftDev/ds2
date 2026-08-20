module Ds2.Aasx.Tests.NewProjectExportTests

open System
open System.IO
open System.Collections.Generic
open Xunit
open AasCore.Aas3_1
open Ds2.Aasx
open Ds2.Aasx.AasxSemantics
open Ds2.Backend.Plc
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

[<Fact>]
let ``multiple active Systems retain distinct AID XGT endpoint ownership`` () =
    let store = newPromakerProject ()
    let project = store.Projects.Values |> Seq.head
    let system1 = project.ActiveSystemIds.[0]
    let system2 = store.AddSystem("SecondSystem", project.Id, true)
    store.AddFlow("SecondFlow", system2) |> ignore

    let aid = AssetInterfacesDescription()
    project.AssetInterfaces <- Some aid
    AidXgtEndpointSettings.ensureBindingForSystem(
        aid, system1, "LsXgi", "192.168.0.10", 2004, false, true,
        0uy, 255uy, 3000, 100, [ "%QX0.1.13" ])
    |> ignore
    AidXgtEndpointSettings.ensureBindingForSystem(
        aid, system2, "LsXgb", "192.168.0.20", 2004, false, true,
        0uy, 255uy, 3000, 100, [ "%IX0.2.7" ])
    |> ignore

    let path = Path.Combine(Path.GetTempPath(), $"multi-system-xgt-{Guid.NewGuid():N}.aasx")
    try
        Assert.True(AasxExporter.exportFromStore store path "" false false)
        let restored = DsStore.empty()
        match AasxImporter.importIntoStoreWithError restored path with
        | Ok () -> ()
        | Error error -> Assert.Fail(error)

        let restoredProject = restored.Projects.Values |> Seq.head
        Assert.Equal(2, restoredProject.ActiveSystemIds.Count)
        let restoredAid = restoredProject.AssetInterfaces.Value
        let endpointSystemIds =
            restoredAid.Interfaces
            |> Seq.choose (function Xgt (endpoint, _) -> endpoint.SystemId | _ -> None)
            |> Set.ofSeq
        Assert.Equal<Set<Guid>>(Set.ofList [ system1; system2 ], endpointSystemIds)

        let plan = AidXgtGatewayConfig.buildForProject(restored, restoredProject, restoredAid)
        Assert.True(plan.Success, String.Join(" / ", plan.Errors))
        Assert.Equal(2, plan.Config.Connections.Length)
        Assert.All(plan.Config.Connections, fun connection -> Assert.True(connection.SystemId.IsSome))
        Assert.Equal<Set<Guid>>(
            Set.ofList [ system1; system2 ],
            plan.Config.Connections |> Seq.choose _.SystemId |> Set.ofSeq)
    finally
        if File.Exists(path) then File.Delete(path)

[<Fact>]
let ``PLC CSV facade creates one active System for each SYSTEM column value`` () =
    let csv =
        "Flow,Work,Device,System,Api,InName,InAddress,OutName,OutAddress\n" +
        "Main,Load,Loader,PLC-1,Advance,Ready,%IX0.0,Run,%QX0.0\n" +
        "Main,Load,Loader,PLC-2,Advance,Ready,%IX0.1,Run,%QX0.1\n"
    let bytes = PlcAasxFacade.exportDs2CsvToAasxBytes "KGM-Line" csv ""
    Assert.NotNull(bytes)
    Assert.NotEmpty(bytes)

    let path = Path.Combine(Path.GetTempPath(), $"csv-multi-system-{Guid.NewGuid():N}.aasx")
    try
        File.WriteAllBytes(path, bytes)
        let restored = DsStore.empty()
        match AasxImporter.importIntoStoreWithError restored path with
        | Ok () -> ()
        | Error error -> Assert.Fail(error)

        let project = restored.Projects.Values |> Seq.head
        let names =
            project.ActiveSystemIds
            |> Seq.map (fun id -> restored.Systems.[id].Name)
            |> Set.ofSeq
        Assert.Equal<Set<string>>(Set.ofList [ "PLC-1"; "PLC-2" ], names)
    finally
        if File.Exists(path) then File.Delete(path)

let private submodelReference (submodelId: string) =
    Reference(
        ReferenceTypes.ModelReference,
        ResizeArray<IKey>([Key(KeyTypes.Submodel, submodelId) :> IKey]))
    :> IReference

let private vendorSubmodel id idShort propertyIdShort propertyValue =
    let property = Property(valueType = DataTypeDefXsd.String)
    property.IdShort <- propertyIdShort
    property.Value <- propertyValue
    let submodel = Submodel(id = id)
    submodel.IdShort <- idShort
    submodel.SubmodelElements <- ResizeArray<ISubmodelElement>([property :> ISubmodelElement])
    submodel

let private vendorShell id assetId idShort submodelId =
    let assetInfo = AssetInformation(assetKind = AssetKind.Instance, globalAssetId = assetId)
    let shell = AssetAdministrationShell(id = id, assetInformation = assetInfo)
    shell.IdShort <- idShort
    shell.Submodels <- ResizeArray<IReference>([submodelReference submodelId])
    shell

let private assertGenericContentPreserved path =
    let env = AasxFileIO.readEnvironmentOrRaise path

    let assertSubmodel id expectedIdShort expectedPropertyValue =
        let matches = env.Submodels |> Seq.filter (fun sm -> sm.Id = id) |> Seq.toList
        Assert.Single(matches) |> ignore
        let submodel = matches.Head
        Assert.Equal(expectedIdShort, submodel.IdShort)
        let property = Assert.IsType<Property>(submodel.SubmodelElements.[0])
        Assert.Equal(expectedPropertyValue, property.Value)

    let assertShell id expectedSubmodelId =
        let shell = env.AssetAdministrationShells |> Seq.find (fun candidate -> candidate.Id = id)
        Assert.Single(shell.Submodels) |> ignore
        Assert.Equal(expectedSubmodelId, shell.Submodels.[0].Keys.[0].Value)

    assertSubmodel "urn:vendor:submodel:documentation" "VendorDocumentation" "keep-documentation"
    // Promaker도 사용하는 idShort라 하더라도 다른 Shell 소유이면 제거하면 안 된다.
    assertSubmodel "urn:vendor:submodel:timeseries" TimeSeriesSubmodelIdShort "keep-vendor-timeseries"
    assertShell "urn:vendor:shell:one" "urn:vendor:submodel:documentation"
    assertShell "urn:vendor:shell:two" "urn:vendor:submodel:timeseries"

    Assert.Equal(3, env.AssetAdministrationShells.Count)
    Assert.Single(env.Submodels |> Seq.filter (fun sm -> sm.IdShort = SubmodelModelIdShort)) |> ignore
    Assert.Single(env.ConceptDescriptions |> Seq.filter (fun cd -> cd.Id = "urn:vendor:concept:keep")) |> ignore

[<Fact>]
let ``generic AASX standards shells and concepts survive repeated Promaker saves`` () =
    let documentation =
        vendorSubmodel
            "urn:vendor:submodel:documentation"
            "VendorDocumentation"
            "VendorDocumentValue"
            "keep-documentation"
    let timeSeries =
        vendorSubmodel
            "urn:vendor:submodel:timeseries"
            TimeSeriesSubmodelIdShort
            "VendorSeriesValue"
            "keep-vendor-timeseries"
    let shellOne =
        vendorShell
            "urn:vendor:shell:one"
            "urn:vendor:asset:one"
            "VendorShellOne"
            documentation.Id
    let shellTwo =
        vendorShell
            "urn:vendor:shell:two"
            "urn:vendor:asset:two"
            "VendorShellTwo"
            timeSeries.Id
    let concept = ConceptDescription(id = "urn:vendor:concept:keep")
    concept.IdShort <- "VendorConcept"

    let sourceEnv =
        Environment(
            submodels = ResizeArray<ISubmodel>([documentation :> ISubmodel; timeSeries :> ISubmodel]),
            assetAdministrationShells =
                ResizeArray<IAssetAdministrationShell>([shellOne :> IAssetAdministrationShell; shellTwo :> IAssetAdministrationShell]),
            conceptDescriptions = ResizeArray<IConceptDescription>([concept :> IConceptDescription]))

    let sourcePath = Path.Combine(Path.GetTempPath(), $"generic-source-{Guid.NewGuid():N}.aasx")
    let savedPath = Path.Combine(Path.GetTempPath(), $"generic-promaker-{Guid.NewGuid():N}.aasx")
    try
        AasxFileIO.writeEnvironment sourceEnv sourcePath None None
        let store = DsStore.empty()
        match AasxImporter.importIntoStoreWithError store sourcePath with
        | Ok () -> ()
        | Error error -> Assert.Fail(error)

        let flow = store.Flows.Values |> Seq.head
        store.AddWork("ModeledInPromaker", flow.Id) |> ignore

        Assert.True(AasxExporter.exportFromStore store savedPath "" false false)
        assertGenericContentPreserved savedPath

        // 같은 in-memory 프로젝트의 두 번째 저장도 원본 중복/유실 없이 동일해야 한다.
        Assert.True(AasxExporter.exportFromStore store savedPath "" false false)
        assertGenericContentPreserved savedPath
    finally
        if File.Exists(sourcePath) then File.Delete(sourcePath)
        if File.Exists(savedPath) then File.Delete(savedPath)
