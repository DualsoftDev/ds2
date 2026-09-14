module Ds2.Aasx.Tests.NewProjectExportTests

open System
open System.IO
open System.Collections.Generic
open Xunit
open AasCore.Aas3_1
open Ds2.Aasx
open Ds2.Aasx.AasxSemantics
open Ds2.Backend.Plc
open Microsoft.FSharp.Reflection
open Ds2.Core.StandardSubmodels
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Aasx.Tests.PilotAssetFixtures

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
        Assert.True(restoredProject.AssetInterfaces.IsSome)
    finally
        if File.Exists(path) then File.Delete(path)

/// System 마다 다른 endpoint(이더넷 한 대, USB 한 대)가 AASX 저장·로드 뒤에도 각자 소유 System 에 귀속돼
/// Agent 게이트웨이 설정으로 이어진다. USB 는 매체와 장치 선택 키까지 살아남아야 Edge/Agent 가 USB 로 붙는다.
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
        aid, system1, xgtTcpRequest "LsXgi" "192.168.0.10" 2004, [ "%QX0.1.13" ])
    |> ignore
    AidXgtEndpointSettings.ensureBindingForSystem(
        aid, system2, xgtUsbRequest "LsXgb" "SN-2", [ "%IX0.2.7" ])
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

        let ethernet = plan.Config.Connections |> List.find (fun c -> c.SystemId = Some system1)
        Assert.Equal(PlcTransport.Tcp, ethernet.Transport)
        Assert.Equal("192.168.0.10", ethernet.IpAddress)
        let usb = plan.Config.Connections |> List.find (fun c -> c.SystemId = Some system2)
        Assert.Equal(PlcVendor.LsXgb, usb.Vendor)
        Assert.Equal(PlcTransport.Usb, usb.Transport)
        Assert.Equal("SN-2", usb.UsbDeviceSelector)
    finally
        if File.Exists(path) then File.Delete(path)

/// signalId 는 AID 전체에서 유일해야 하는데 기본값이 주소 원문이라, 예전에는 서로 다른 System 이
/// 같은 주소를 쓰면 활성화가 "signalId 중복" 에러로 거부됐다. 이제는 사용자가 아무것도 하지 않아도
/// 뒤에 오는 쪽만 자동으로 분화(`주소@System해시`)되어 그대로 뜬다.
/// ★기존에 부여된 id 는 절대 움직이지 않는다 — signalId 는 OPC UA NodeId·Collector 시계열의
/// 영속 키라, 먼저 자리를 잡은 System 은 주소 원문 그대로를 유지해야 한다.
[<Fact>]
let ``same address in two Systems is auto-qualified instead of failing`` () =
    let store = newPromakerProject ()
    let project = store.Projects.Values |> Seq.head
    let system1 = project.ActiveSystemIds.[0]
    let system2 = store.AddSystem("SecondSystem", project.Id, true)
    store.AddFlow("SecondFlow", system2) |> ignore

    let shared = "%IX0.1.2"
    let aid = AssetInterfacesDescription()
    project.AssetInterfaces <- Some aid
    AidXgtEndpointSettings.ensureBindingForSystem(
        aid, system1, xgtTcpRequest "LsXgi" "192.168.0.10" 2004, [ shared; "%QX0.1.13" ])
    |> ignore
    AidXgtEndpointSettings.ensureBindingForSystem(
        aid, system2, xgtTcpRequest "LsXgb" "192.168.0.20" 2004, [ shared; "%QX0.2.7" ])
    |> ignore

    let plan = AidXgtGatewayConfig.buildForProject(store, project, aid)
    Assert.True(plan.Success, String.Join(" / ", plan.Errors))
    Assert.Equal(2, plan.Config.Connections.Length)

    // 겹친 주소가 양쪽 연결에 모두 살아 있고, 각자 자기 System 에 귀속된다.
    let holders =
        plan.Config.Connections
        |> List.filter (fun c -> c.Tags |> List.exists (fun t -> t.HubAddress = shared))
    Assert.Equal(2, holders.Length)
    Assert.Equal<Set<Guid>>(
        Set.ofList [ system1; system2 ], holders |> Seq.choose _.SystemId |> Set.ofSeq)

    // signalId 는 유일해야 하고, 먼저 자리잡은 쪽은 주소 원문을 그대로 보존해야 한다.
    let sharedSignalIds =
        plan.Signals
        |> Seq.filter (fun s -> s.Address = shared)
        |> Seq.map _.SignalId
        |> List.ofSeq
    Assert.Equal(2, sharedSignalIds.Length)
    Assert.Equal(2, sharedSignalIds |> List.distinct |> List.length)
    Assert.Contains(shared, sharedSignalIds)
    Assert.Contains(sharedSignalIds, fun (s: string) -> s.StartsWith(shared + "@"))

    // 겹치지 않는 주소는 종전 그대로 주소 원문이 signalId 다(불필요한 분화 없음).
    let untouched = plan.Signals |> Seq.filter (fun s -> s.Address = "%QX0.2.7") |> Seq.exactlyOne
    Assert.Equal("%QX0.2.7", untouched.SignalId)

    // System 간 중복은 이제 경고가 아니라 정보다(복합키로 구분되는 정상 구성).
    Assert.Empty(plan.Warnings)
    Assert.NotEmpty(plan.Notices)

/// 이미 저장돼 있던 "signalId 가 중복인" 모델(= 예전 코드로는 활성화가 안 되던 파일)도
/// 불러오는 시점에 자동 복구되어야 한다. 사용자가 파일을 손보지 않아도 뜨는 것이 목표.
[<Fact>]
let ``already saved model with duplicate signalIds is repaired on load`` () =
    let store = newPromakerProject ()
    let project = store.Projects.Values |> Seq.head
    let system1 = project.ActiveSystemIds.[0]
    let system2 = store.AddSystem("SecondSystem", project.Id, true)
    store.AddFlow("SecondFlow", system2) |> ignore

    // 예전 코드가 만들어 낸 상태를 재현 — 두 endpoint 가 같은 signalId 를 갖는다.
    let shared = "%IX0.1.2"
    let aid = AssetInterfacesDescription()
    project.AssetInterfaces <- Some aid
    AidXgtEndpointSettings.ensureBindingForSystem(
        aid, system1, xgtTcpRequest "LsXgi" "192.168.0.10" 2004, [ shared ])
    |> ignore
    AidXgtEndpointSettings.ensureBindingForSystem(
        aid, system2, xgtTcpRequest "LsXgb" "192.168.0.20" 2004, [ "%QX0.2.7" ])
    |> ignore
    // system2 의 interaction 을 강제로 중복 signalId 로 되돌린다(구버전 산출물 모사).
    for index = 0 to aid.Interfaces.Count - 1 do
        match aid.Interfaces.[index] with
        | Xgt (endpoint, interactions) when endpoint.SystemId = Some system2 ->
            let broken =
                interactions
                |> List.map (fun i -> { i with Href = shared; SignalId = Ds2.Core.SignalId shared })
            aid.Interfaces.[index] <- Xgt (endpoint, broken)
        | _ -> ()

    let plan = AidXgtGatewayConfig.buildForProject(store, project, aid)
    Assert.True(plan.Success, String.Join(" / ", plan.Errors))
    let signalIds = plan.Signals |> Seq.map _.SignalId |> List.ofSeq
    Assert.Equal(signalIds.Length, signalIds |> List.distinct |> List.length)

/// signalId 가 **전량 빈 값**으로 저장된 모델의 로드 시 자동 복구.
/// SignalId struct 에 JsonConstructor 가 없던 빌드(6421d525 이전)로 SDF 를 열면 역직렬화가
/// raw=null 로 남겨 모든 signalId 가 "" 가 되고, 재저장하면 파일에 그대로 박제됐다(KGM 0902 사례).
/// 그런 파일도 사용자가 손보지 않고 뜨도록, 빈 id 는 같은 규칙(주소, 충돌 시 주소@System해시)으로
/// 재발급되어야 한다.
[<Fact>]
let ``already saved model with all-empty signalIds is repaired on load`` () =
    let store = newPromakerProject ()
    let project = store.Projects.Values |> Seq.head
    let system1 = project.ActiveSystemIds.[0]
    let system2 = store.AddSystem("SecondSystem", project.Id, true)
    store.AddFlow("SecondFlow", system2) |> ignore

    // 두 System 이 같은 주소를 하나 공유하는 구성 — 재발급이 분화 규칙까지 타는지 본다.
    let shared = "%IX0.1.2"
    let aid = AssetInterfacesDescription()
    project.AssetInterfaces <- Some aid
    AidXgtEndpointSettings.ensureBindingForSystem(
        aid, system1, xgtTcpRequest "LsXgi" "192.168.0.10" 2004, [ shared; "%QX0.1.13" ])
    |> ignore
    AidXgtEndpointSettings.ensureBindingForSystem(
        aid, system2, xgtTcpRequest "LsXgb" "192.168.0.20" 2004, [ shared; "%QX0.2.7" ])
    |> ignore
    // 구버전 역직렬화 산출물 모사 — 모든 interaction 의 signalId 를 "" 로 박제.
    for index = 0 to aid.Interfaces.Count - 1 do
        match aid.Interfaces.[index] with
        | Xgt (endpoint, interactions) ->
            let blanked =
                interactions |> List.map (fun i -> { i with SignalId = Ds2.Core.SignalId "" })
            aid.Interfaces.[index] <- Xgt (endpoint, blanked)
        | _ -> ()

    let plan = AidXgtGatewayConfig.buildForProject(store, project, aid)
    Assert.True(plan.Success, String.Join(" / ", plan.Errors))

    // 4개 신호 전부 살아나고, id 는 비어있지 않으며 AID 전체에서 유일하다.
    Assert.Equal(4, plan.Signals.Length)
    let signalIds = plan.Signals |> Seq.map _.SignalId |> List.ofSeq
    Assert.All(signalIds, fun s -> Assert.False(String.IsNullOrWhiteSpace s))
    Assert.Equal(signalIds.Length, signalIds |> List.distinct |> List.length)

    // 겹치지 않는 주소는 주소 원문, 겹친 주소는 한쪽만 @System해시로 분화.
    Assert.Contains("%QX0.1.13", signalIds)
    Assert.Contains("%QX0.2.7", signalIds)
    Assert.Contains(shared, signalIds)
    Assert.Contains(signalIds, fun (s: string) -> s.StartsWith(shared + "@"))

/// 게이트웨이/수집기 계층의 주소 중복 처리. 모델 계층이 위 제약으로 막더라도 이 계층은
/// 외부 도구가 만든 AASX·수동 설정에서 중복을 만날 수 있으므로 (SystemId, 주소) 복합키로 동작해야 한다.
/// 예전엔 여기서 ① 라우팅이 주소 단독 키라 한쪽 PLC 태그가 사라지고("last wins")
/// ② 쓰기가 마지막 등록 PLC 로만 나갔으며 ③ 수집기 payload 에 System 귀속이 실리지 않았다.
[<Fact>]
let ``gateway config distinguishes the same address on two PLCs by System`` () =
    let system1 = Guid.NewGuid()
    let system2 = Guid.NewGuid()
    let shared = "%IX0.1.2"
    let tag address = { HubAddress = address; PlcAddress = address; DataType = PlcDataTypes.Bool }
    let connection name systemId ip tags =
        { PlcConnectionConfig.defaultLs name ip with SystemId = Some systemId; Tags = tags }
    let config =
        { Connections =
            [ connection "AID-XGT#1" system1 "192.168.0.10" [ tag shared; tag "%QX0.1.13" ]
              connection "AID-XGT#2" system2 "192.168.0.20" [ tag shared; tag "%QX0.2.7" ] ] }

    // ① 겹친 주소를 두 연결이 각각 보유한 상태가 유지된다(복합키라 서로 덮지 않는다).
    let duplicates = PlcGatewayConfig.duplicateAddresses config
    let dup = Assert.Single(duplicates)
    Assert.Equal(TagKey.normalize shared, dup.Address)
    Assert.Equal(AcrossSystems, dup.Conflict)   // SystemId 가 서로 달라 복합키로 구분 가능
    Assert.Equal(2, dup.Owners.Length)

    // ② 폴백 판정: 겹친 주소는 소유자를 특정할 수 없어야(Error) 한다 —
    //    게이트웨이 쓰기가 조용히 한쪽 PLC 로 나가지 않고 명시적으로 실패하는 근거.
    let owners = PlcGatewayConfig.addressOwners config
    match PlcGatewayConfig.resolveSoleOwner owners shared with
    | Error candidates -> Assert.Equal(2, candidates.Length)
    | Ok _ -> Assert.Fail("겹친 주소는 소유자가 모호해야 한다(조용히 한쪽으로 보내면 안 됨)")

    // ③ 고유 주소는 소유 System 으로 확정된다(구버전 송신자도 정상 라우팅).
    match PlcGatewayConfig.resolveSoleOwner owners "%QX0.2.7" with
    | Ok (Some sid) -> Assert.Equal(Some system2, sid)
    | other -> Assert.Fail($"고유 주소는 소유 System 이 확정돼야 한다: {other}")

    // ④ 주소 비교는 대소문자를 가리지 않는다(종전 OrdinalIgnoreCase 규약 유지).
    match PlcGatewayConfig.resolveSoleOwner owners "%qx0.2.7" with
    | Ok (Some sid) -> Assert.Equal(Some system2, sid)
    | other -> Assert.Fail($"주소 비교는 대소문자 무시여야 한다: {other}")

    // ⑤ 복합키가 실제로 두 태그를 갈라놓는지 — 같은 주소라도 System 이 다르면 다른 키다.
    Assert.NotEqual(TagKey.create (Some system1) shared, TagKey.create (Some system2) shared)
    Assert.Equal(TagKey.create (Some system1) shared, TagKey.create (Some system1) (shared.ToLowerInvariant()))

    // ⑥ 수집기(Pi5) payload 에 System 귀속이 실려야 분리 아키텍처에서도 구분된다.
    let payload = CollectorConfig.fromGateway config
    Assert.Equal(2, payload.Connections.Length)
    Assert.All(payload.Connections, fun c -> Assert.False(String.IsNullOrWhiteSpace c.SystemId))

/// 같은 System(또는 귀속 미상) 안에서 두 연결이 같은 주소를 보유하면 복합키로도 못 가른다 —
/// 진단이 이를 다른 종류(WithinSameSystem)로 분류해야 사용자가 "모델을 고쳐야 하는 상황"임을 안다.
[<Fact>]
let ``same address within one System is classified as unresolvable`` () =
    let systemId = Guid.NewGuid()
    let shared = "%IX0.1.2"
    let tag address = { HubAddress = address; PlcAddress = address; DataType = PlcDataTypes.Bool }
    let connection name ip =
        { PlcConnectionConfig.defaultLs name ip with SystemId = Some systemId; Tags = [ tag shared ] }
    let config = { Connections = [ connection "A" "192.168.0.10"; connection "B" "192.168.0.20" ] }

    let dup = Assert.Single(PlcGatewayConfig.duplicateAddresses config)
    Assert.Equal(WithinSameSystem, dup.Conflict)

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

/// 수집기(Pi5) payload 의 vendor/transport/dtype 라벨은 Agent 가 내보내고 Edge 수집기(Ds2.Edge.Scanner)가
/// 되읽는 **문자열 계약**이다. 내보내기와 되읽기가 Ds2.Backend.Plc 한 곳에 짝으로 있으므로 DU 케이스를
/// 리플렉션으로 전수 돌려 왕복이 항등인지 확인한다 — 한쪽에만 케이스를 추가하면 여기서 깨진다.
/// 별칭·모르는 라벨은 None 이어야 한다(예전 폴백은 SX 설정을 LsXgk 로 떨어뜨려 509 포트에 LS 프로토콜로 붙었다).
[<Fact>]
let ``collector payload labels round-trip through the parsers the Edge scanner uses`` () =
    let vendors =
        FSharpType.GetUnionCases typeof<PlcVendor>
        |> Array.map (fun case -> FSharpValue.MakeUnion(case, [||]) :?> PlcVendor)
    Assert.NotEmpty vendors
    for vendor in vendors do
        Assert.Equal(Some vendor, CollectorConfig.tryVendorOf (CollectorConfig.vendorStr vendor))
    Assert.Equal(None, CollectorConfig.tryVendorOf "Mx")
    Assert.Equal(None, CollectorConfig.tryVendorOf "")

    let transports =
        FSharpType.GetUnionCases typeof<PlcTransport>
        |> Array.map (fun case -> FSharpValue.MakeUnion(case, [||]) :?> PlcTransport)
    Assert.Equal(3, transports.Length)
    for transport in transports do
        Assert.Equal(Some transport, PlcTransport.tryOfLabel (PlcTransport.label transport))
    Assert.Equal(Some PlcTransport.Usb, PlcTransport.tryOfLabel "USB")
    Assert.Equal(None, PlcTransport.tryOfLabel "serial")

    for dataType in [ PlcDataTypes.Bool; PlcDataTypes.Int16; PlcDataTypes.UInt16; PlcDataTypes.Int32
                      PlcDataTypes.UInt32; PlcDataTypes.Float32; PlcDataTypes.Float64 ] do
        Assert.Equal(dataType, CollectorConfig.dataTypeOf (CollectorConfig.dtypeStr dataType))

    // USB 접속은 payload 에 매체와 장치 선택 키가 실려야 수집기가 USB 로 붙는다.
    let usb =
        { PlcConnectionConfig.defaultLs "USB-1" "" with
            Port = 0
            Transport = PlcTransport.Usb
            UsbDeviceSelector = "3:4" }
    let payload = CollectorConfig.fromGateway { Connections = [ usb ] }
    let connection = Assert.Single payload.Connections
    Assert.Equal("usb", connection.Transport)
    Assert.Equal("3:4", connection.UsbDeviceSelector)
    Assert.Equal("", connection.Ip)
    Assert.Equal(0, connection.Port)
    Assert.Equal("LsXgi", connection.Vendor)

/// SX 는 잠긴 채로 시작해야 한다. 쓰기 허용 영역이 비어 있으면 커넥터가 쓰기 권한 발급을
/// 거부하므로, 이 기본값이 곧 "현장 PLC 에 아무것도 쓰지 않는다" 는 보장이다.
[<Fact>]
let ``MicrexSx default connection is read-only on the loader port`` () =
    let sx = PlcConnectionConfig.defaultSx "SX" "192.168.9.89"
    Assert.Equal(PlcVendor.MicrexSx, sx.Vendor)
    Assert.Equal(509, sx.Port)
    Assert.Empty sx.SxWritableAreas
    Assert.Equal("", sx.SxIoMapPath)

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

/// 현장 KIT1(2026-09-14): 다른 AASX 를 가져와 System 3개 중 2개를 지우고 남은 하나만 쓰자
/// Agent 가 기동조차 못 했다. 지워진 System 의 AID endpoint 가 파일에 남아 systemRef 가
/// 유령이 되는데, 예전 검증은 그것을 **에러**로 올려 계획 전체를 무효화했다 — 멀쩡한 PLC 까지
/// 같이 죽고 Hub 이 안 떠서 화면에는 "연결 끊김" 으로만 보였다.
/// 유령 endpoint 는 그것만 빼고 경고로 남기고, 살아 있는 System 의 수집은 그대로 떠야 한다.
[<Fact>]
let ``orphaned endpoints of deleted systems are skipped, not fatal`` () =
    let store = newPromakerProject ()
    let project = store.Projects.Values |> Seq.head
    let survivor = project.ActiveSystemIds.[0]
    let deleted1 = store.AddSystem("DeletedSystem1", project.Id, true)
    let deleted2 = store.AddSystem("DeletedSystem2", project.Id, true)

    let aid = AssetInterfacesDescription()
    project.AssetInterfaces <- Some aid
    AidXgtEndpointSettings.ensureBindingForSystem(
        aid, survivor, xgtTcpRequest "LsXgk" "192.168.9.103" 2004, [ "%IX0.1.2" ]) |> ignore
    AidXgtEndpointSettings.ensureBindingForSystem(
        aid, deleted1, xgtTcpRequest "LsXgi" "192.168.0.20" 2004, [ "%QX0.2.7" ]) |> ignore
    AidXgtEndpointSettings.ensureBindingForSystem(
        aid, deleted2, xgtTcpRequest "LsXgb" "192.168.0.30" 2004, [ "%QX0.3.7" ]) |> ignore

    // Promaker 에서 System 두 개를 지운 상태 — AID endpoint 는 파일에 그대로 남는다.
    project.ActiveSystemIds.Remove deleted1 |> ignore
    project.ActiveSystemIds.Remove deleted2 |> ignore

    let plan = AidXgtGatewayConfig.buildForProject(store, project, aid)

    // 살아 있는 System 하나로 기동해야 한다.
    Assert.True(plan.Success, String.Join(" / ", plan.Errors))
    Assert.Equal(1, plan.Config.Connections.Length)
    Assert.Equal("192.168.9.103", plan.Config.Connections.Head.IpAddress)
    Assert.Equal(Some survivor, plan.Config.Connections.Head.SystemId)

    // 제외된 둘은 조용히 사라지지 않고 경고로 남는다(사람이 모델을 고쳐야 하는 사항).
    Assert.Equal(2, plan.Warnings |> Array.filter (fun w -> w.Contains "제외") |> Array.length)
