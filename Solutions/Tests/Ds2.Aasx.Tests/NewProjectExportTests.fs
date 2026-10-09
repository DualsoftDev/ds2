module Ds2.Aasx.Tests.NewProjectExportTests

open System
open System.IO
open System.Collections.Generic
open Xunit
open AasCore.Aas3_1
open Ds2.Aasx
open Ds2.Aasx.AasxSemantics
open Microsoft.FSharp.Reflection
open Ds2.Core.StandardSubmodels
open Ds2.Core.Store
open Ds2.TestKit.PilotAssetFixtures
open Ds2.TestKit.ModelBuilder

let private newPromakerProject () =
    let store = DsStore()
    let projectId = (addProject store "NewProject").Id
    let systemId = (addSystem store "NewSystem" projectId true).Id
    (addFlow store "NewFlow" systemId).Id |> ignore
    store

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
    store.SetAssetInterfaces(project.Id, aid)
    AidXgtEndpointSettings.ensureBindingForSystem(
        aid, project.ActiveSystemIds.[0], xgtTcpRequest "LsXgi" "192.168.0.10" 2004,
        [ "%QX0.1.13"; "%IX0.1.2" ])
    |> ignore

    let path = Path.Combine(Path.GetTempPath(), $"new-project-xgt-{Guid.NewGuid():N}.aasx")
    try
        Assert.True(AasxExporter.exportFromStore store path "" false false)
        let restored = DsStore.empty()
        match AasxImporter.importIntoStoreWithError restored path with
        | Ok () -> ()
        | Error error -> Assert.Fail(error)
        let restoredProject = restored.Projects.Values |> Seq.head
        Assert.True((restored.TryGetAssetInterfaces restoredProject.Id).IsSome)
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
        (addWork store "ModeledInPromaker" flow.Id).Id |> ignore

        Assert.True(AasxExporter.exportFromStore store savedPath "" false false)
        assertGenericContentPreserved savedPath

        // 같은 in-memory 프로젝트의 두 번째 저장도 원본 중복/유실 없이 동일해야 한다.
        Assert.True(AasxExporter.exportFromStore store savedPath "" false false)
        assertGenericContentPreserved savedPath
    finally
        if File.Exists(sourcePath) then File.Delete(sourcePath)
        if File.Exists(savedPath) then File.Delete(savedPath)

/// AID InterfaceXGT endpoint 의 CpuModel 은 `Xgi | Xgk | Xgb` 닫힌 DU 다. 그래서 LS 가 아닌
/// 벤더는 `tryCpuModel` 이 None 으로 떨어뜨려 EnsureBindingForSystem 이 0(변경 없음)을 돌려준다 — 저장되지 않는다.
///
/// Promaker 는 이 사실을 `PlcVendorProfile.IsAidXgtVendor` 로 복제해 저장 경로를 고르므로,
/// 여기서 권위 쪽 거동을 고정해 둔다. 이 DU 에 벤더가 늘면 이 시험이 먼저 깨져야 한다.
[<Fact>]
let ``AID XGT endpoint accepts only LS vendors`` () =
    let systemId = Guid.NewGuid()
    let aid = AssetInterfacesDescription()
    let created =
        AidXgtEndpointSettings.ensureBindingForSystem(
            aid, systemId, xgtTcpRequest "LsXgi" "192.168.9.102" 2004, [ "%QX0.1.13" ])
    Assert.True(created > 0, "LS endpoint 는 만들어져야 한다")

    let update vendor port =
        AidXgtEndpointSettings.ensureBindingForSystem(
            aid, systemId, xgtTcpRequest vendor "192.168.9.103" port, [])

    // LS 세 종류는 받는다.
    Assert.True(update "LsXgi" 2004 > 0)
    Assert.True(update "LsXgk" 2004 > 0)
    Assert.True(update "LsXgb" 2004 > 0)

    // 비-LS 는 조용히 거부된다(0 = 변경 없음). Promaker 가 이걸 "저장 실패" 로만 보여 주던
    // 것이 SX 를 UI 로 설정할 수 없던 원인이었다.
    Assert.Equal(0, update "MicrexSx" 509)
    Assert.Equal(0, update "Mitsubishi" 5007)

    // 거부가 기존 endpoint 를 훼손하지도 않아야 한다.
    let connection = AidXgtEndpointSettings.tryReadForSystem(aid, systemId)
    Assert.NotNull connection
    Assert.Equal("LsXgb", connection.Vendor)
    Assert.Equal("192.168.9.103", connection.IpAddress)
