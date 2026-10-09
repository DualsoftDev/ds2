module Ds2.Backend.Tests.AidRoundtripGatewayTests

open System
open AasCore.Aas3_1
open Ds2.Core
open Ds2.Core.Kpi
open Ds2.Core.StandardSubmodels
open Ds2.Aasx
open Ds2.Backend.Plc
open Ds2.TestKit.PilotAssetFixtures
open Xunit
open Ds2.TestKit.AidTestHelpers

/// 이더넷과 USB endpoint 가 한 AID 에 공존하며 AASX 왕복 뒤 Agent 게이트웨이 설정으로 이어진다.
/// USB 는 base 의 path 에 장치 선택 키를 싣고 transport Property 가 "usb" 로 실려야 Agent/Edge 가 USB 로 붙는다.
[<Fact>]
let ``InterfaceXGT roundtrip builds Agent gateway config for Ethernet and USB endpoints`` () =
    let ethernetSystem = Guid.NewGuid()
    let usbSystem = Guid.NewGuid()
    let aid = AssetInterfacesDescription()
    let ethernet = {
        XgtEndpointMetadata.empty with
            Base = "xgt+tcp://192.168.10.20:2004"
            SystemId = Some ethernetSystem
            CpuModel = Xgk
            NetworkNumber = 2uy
            StationNumber = 3uy
            ScanIntervalMs = 250
    }
    let usb = {
        XgtEndpointMetadata.empty with
            Base = "xgt+usb://localhost/SN12345"
            SystemId = Some usbSystem
            CpuModel = Xgb
            Transport = XgtUsb
    }
    aid.Interfaces.Add(Xgt(ethernet, [ xgtInteraction "CylinderReady" "%MX100" "line1.station01.cylinder-ready" ]))
    aid.Interfaces.Add(Xgt(usb, [ xgtInteraction "Run" "%MX0" "line1.station02.run" ]))

    let sm = AasxExportStandardSubmodels.aidToSubmodel aid "station01"
    let elements = descendants sm.SubmodelElements |> Seq.toArray
    let signalIdProperty =
        elements
        |> Seq.pick (function
            | :? Property as p when p.IdShort = "signalId" -> Some p
            | _ -> None)
    Assert.NotNull(signalIdProperty.SemanticId)
    Assert.Equal(AasxSemantics.SignalIdExtensionSemanticId, signalIdProperty.SemanticId.Keys.[0].Value)
    let xgtCollection =
        elements
        |> Seq.pick (function
            | :? SubmodelElementCollection as smc when smc.IdShort = "InterfaceXGT" -> Some smc
            | _ -> None)
    Assert.Equal(AasxSemantics.XgtInterfaceSemanticId, xgtCollection.SemanticId.Keys.[0].Value)
    // transport Property 는 라벨 계약("tcp"|"udp"|"usb")로 실린다.
    let transports =
        elements
        |> Seq.choose (function
            | :? Property as p when p.IdShort = "transport" -> Some p.Value
            | _ -> None)
        |> List.ofSeq
    Assert.Equal<string list>([ "tcp"; "usb" ], transports)

    let conceptIds =
        AasxConceptDescriptions.createAllConceptDescriptions ()
        |> Seq.map (fun cd -> cd.Id)
        |> Set.ofSeq
    Assert.Contains(AasxSemantics.SignalIdExtensionSemanticId, conceptIds)
    Assert.Contains(AasxSemantics.XgtInterfaceSemanticId, conceptIds)

    let restored = AasxImportStandardSubmodels.submodelToAid sm
    Assert.Equal(2, restored.Interfaces.Count)
    match restored.Interfaces.[0], restored.Interfaces.[1] with
    | Xgt (ep1, interactions1), Xgt (ep2, _) ->
        Assert.Equal(ethernet.Base, ep1.Base)
        Assert.Equal(XgtTcp, ep1.Transport)
        Assert.Equal(Xgk, ep1.CpuModel)
        Assert.Equal(2uy, ep1.NetworkNumber)
        Assert.Equal(3uy, ep1.StationNumber)
        Assert.Equal(250, ep1.ScanIntervalMs)
        Assert.Equal("%MX100", interactions1.Head.Href)
        Assert.Equal(XgtUsb, ep2.Transport)
        Assert.Equal("xgt+usb://localhost/SN12345", ep2.Base)
        Assert.Equal(Some usbSystem, ep2.SystemId)
    | _ -> Assert.Fail "expected two XGT bindings"

    let plan = AidXgtGatewayConfig.build restored
    Assert.True(plan.HasBinding)
    Assert.True(plan.Success, String.Join(" / ", plan.Errors))
    Assert.Equal(2, plan.Config.Connections.Length)
    let ethernetConnection = plan.Config.Connections |> List.find (fun c -> c.SystemId = Some ethernetSystem)
    Assert.Equal(PlcVendor.LsXgk, ethernetConnection.Vendor)
    Assert.Equal(PlcTransport.Tcp, ethernetConnection.Transport)
    Assert.Equal("192.168.10.20", ethernetConnection.IpAddress)
    Assert.Equal(2004, ethernetConnection.Port)
    Assert.Equal("%MX100", ethernetConnection.Tags.Head.HubAddress)
    Assert.Equal("192.168.10.20:2004", PlcConnectionConfig.endpointLabel ethernetConnection)
    let usbConnection = plan.Config.Connections |> List.find (fun c -> c.SystemId = Some usbSystem)
    Assert.Equal(PlcVendor.LsXgb, usbConnection.Vendor)
    Assert.Equal(PlcTransport.Usb, usbConnection.Transport)
    Assert.Equal("SN12345", usbConnection.UsbDeviceSelector)
    Assert.Equal("", usbConnection.IpAddress)
    Assert.Equal(0, usbConnection.Port)
    Assert.Equal("USB(SN12345)", PlcConnectionConfig.endpointLabel usbConnection)
    Assert.Equal(2, plan.Signals.Length)
    Assert.Equal("line1.station01.cylinder-ready", plan.Signals.[0].SignalId)
    Assert.Equal("boolean", plan.Signals.[0].ValueType)

[<Fact>]
let ``AID without InterfaceXGT is explicitly distinguishable from invalid XGT`` () =
    let noXgt = AssetInterfacesDescription()
    noXgt.Interfaces.Add(OpcUa(EndpointMetadata.empty, [], []))
    let missing = AidXgtGatewayConfig.build noXgt
    Assert.False(missing.HasBinding)
    Assert.False(missing.Success)

    let invalid = AssetInterfacesDescription()
    invalid.Interfaces.Add(Xgt({ XgtEndpointMetadata.empty with Base = "not a URI" }, []))
    let broken = AidXgtGatewayConfig.build invalid
    Assert.True(broken.HasBinding)
    Assert.False(broken.Success)
    Assert.NotEmpty(broken.Errors)

[<Fact>]
let ``InterfaceXGT rejects transport mismatch, vault credentials and duplicate signal identities`` () =
    let aid = AssetInterfacesDescription()
    let endpoint = {
        XgtEndpointMetadata.empty with
            Transport = XgtUdp
            Base = "xgt+tcp://192.168.10.20:2004"
            AuthReferenceVault = Some "@vault:secret/xgt"
    }
    let run = xgtInteraction "Run" "%MX0" "line1.xgt.run"
    aid.Interfaces.Add(Xgt(endpoint, [ run; { run with IdShort = "RunAgain"; Href = "%MX1" } ]))

    let plan = AidXgtGatewayConfig.build aid
    Assert.True(plan.HasBinding)
    Assert.False(plan.Success)
    Assert.Contains(plan.Errors, fun message -> message.Contains("xgt+udp"))
    Assert.Contains(plan.Errors, fun message -> message.Contains("authReferenceVault"))

    let duplicateAid = AssetInterfacesDescription()
    duplicateAid.Interfaces.Add(Xgt(XgtEndpointMetadata.empty, [ run; { run with IdShort = "RunAgain"; Href = "%MX1" } ]))
    let duplicatePlan = AidXgtGatewayConfig.build duplicateAid
    Assert.False(duplicatePlan.Success)
    Assert.Contains(duplicatePlan.Errors, fun message -> message.Contains("중복"))

/// USB 요청은 IP/포트 없이 성립하고, 장치 선택 키가 base 의 path 에 실린다. 읽기 DTO 는 IsUsb·선택 키·표기를 돌려주고
/// 게이트웨이 조립은 Transport=Usb 로 이어진다. URI 경계 문자가 든 키는 거절되어 endpoint 가 그대로다.
[<Fact>]
let ``USB endpoint request stores the selector in the base path and rejects URI delimiters`` () =
    let systemId = Guid.NewGuid()
    let aid = AssetInterfacesDescription()
    let created =
        AidXgtEndpointSettings.ensureBindingForSystem(
            aid, systemId, xgtUsbRequest "LsXgb" "SN12345", [ "%MX0"; "%MX1" ])
    Assert.Equal(2, created)
    match aid.Interfaces.[0] with
    | Xgt (endpoint, _) ->
        Assert.Equal(XgtUsb, endpoint.Transport)
        Assert.Equal("xgt+usb://localhost/SN12345", endpoint.Base)
        Assert.Equal(Xgb, endpoint.CpuModel)
    | _ -> Assert.Fail "expected XGT binding"

    let connection = AidXgtEndpointSettings.tryReadForSystem(aid, systemId)
    Assert.NotNull(connection)
    Assert.True(connection.IsUsb)
    Assert.Equal("SN12345", connection.UsbDeviceSelector)
    Assert.Equal("", connection.IpAddress)
    Assert.Equal(0, connection.Port)
    Assert.Equal("USB(SN12345)", connection.EndpointLabel)

    // 빈 선택 키 = 첫 장치. 이더넷처럼 IP 를 요구하지 않는다.
    Assert.True(AidXgtEndpointSettings.ensureBindingForSystem(aid, systemId, xgtUsbRequest "LsXgb" "", []) > 0)
    Assert.Equal("USB", (AidXgtEndpointSettings.tryReadForSystem(aid, systemId)).EndpointLabel)

    // URI 경계 문자는 거절 — 0 을 돌려주고 endpoint 는 그대로.
    Assert.Equal(0, AidXgtEndpointSettings.ensureBindingForSystem(aid, systemId, xgtUsbRequest "LsXgb" "a/b", []))
    Assert.Equal("xgt+usb://localhost/", (AidXgtEndpointSettings.tryReadForSystem(aid, systemId)).BaseUri)

    let plan = AidXgtGatewayConfig.build aid
    Assert.True(plan.Success, String.Join(" / ", plan.Errors))
    let usbConnection = Assert.Single plan.Config.Connections
    Assert.Equal(PlcTransport.Usb, usbConnection.Transport)
    Assert.Equal("", usbConnection.UsbDeviceSelector)

[<Fact>]
let ``standard AID bindings build executable southbound plans`` () =
    let cases = [
        cnc01Aid(), AidSouthboundProtocol.OpcUa, 3, 0
        pm03Aid(), AidSouthboundProtocol.Modbus, 1, 0
        vib11Aid(), AidSouthboundProtocol.Mqtt, 1, 0
        vis02Aid(), AidSouthboundProtocol.Http, 1, 0
        bcr05Aid(), AidSouthboundProtocol.OpcUa, 0, 1
    ]
    for aid, protocol, signalCount, eventCount in cases do
        let plan = AidSouthboundConfig.build aid
        Assert.True(plan.HasBinding)
        Assert.True(plan.Success, String.Join(" / ", plan.Errors))
        let endpoint = Assert.Single(plan.Endpoints)
        Assert.Equal(protocol, endpoint.Protocol)
        Assert.Equal(signalCount, endpoint.Signals.Length)
        Assert.Equal(eventCount, endpoint.Events.Length)

[<Fact>]
let ``standard AID plan rejects duplicate signal identities`` () =
    let aid = cnc01Aid()
    let duplicate =
        match aid.Interfaces.[0] with
        | OpcUa (endpoint, first :: _, events) ->
            OpcUa(endpoint, [first; { first with IdShort = "DuplicateSpindle" }], events)
        | _ -> failwith "fixture must contain OPC UA"
    aid.Interfaces.Clear()
    aid.Interfaces.Add duplicate
    let plan = AidSouthboundConfig.build aid
    Assert.True(plan.HasBinding)
    Assert.False(plan.Success)
    Assert.Contains(plan.Errors, fun message -> message.Contains("duplicate signalId"))

[<Fact>]
let ``standard AID plan rejects an endpoint without a host`` () =
    let aid = cnc01Aid()
    let invalid =
        match aid.Interfaces.[0] with
        | OpcUa (endpoint, interactions, events) ->
            OpcUa({ endpoint with Base = "opc.tcp:///missing-host" }, interactions, events)
        | _ -> failwith "fixture must contain OPC UA"
    aid.Interfaces.Clear()
    aid.Interfaces.Add invalid

    let plan = AidSouthboundConfig.build aid
    Assert.False(plan.Success)
    Assert.Contains(plan.Errors, fun message -> message.Contains("has no host"))

[<Fact>]
let ``InterfaceXGT XGB roundtrip selects the XGT compact PLC driver`` () =
    let aid = AssetInterfacesDescription()
    let endpoint = { XgtEndpointMetadata.empty with CpuModel = Xgb }
    let interaction : OpcUaInteraction = {
        IdShort = "Run"
        SemanticId = SemanticId "urn:dualsoft:test:run"
        ValueType = XsBoolean
        Unit = None
        Href = "%MX0"
        SignalId = SignalId "line1.xgb01.run"
    }
    aid.Interfaces.Add(Xgt(endpoint, [interaction]))
    let sm = AasxExportStandardSubmodels.aidToSubmodel aid "xgb01"
    let restored = AasxImportStandardSubmodels.submodelToAid sm
    match restored.Interfaces.[0] with
    | Xgt (restoredEndpoint, _) -> Assert.Equal(Xgb, restoredEndpoint.CpuModel)
    | _ -> Assert.Fail "expected XGT binding"
    let plan = AidXgtGatewayConfig.build restored
    Assert.True(plan.Success, String.Join(" / ", plan.Errors))
    Assert.Equal(PlcVendor.LsXgb, plan.Config.Connections.Head.Vendor)

/// InterfaceMicrexSx 는 Promaker.Agent 가 SX PLC 에 닿는 유일한 경로다 — Agent 는 게이트웨이를
/// AID 에서만 조립하므로(MonitoringSupervisor → AidXgtGatewayConfig.buildForProject) 이 왕복이
/// 깨지면 모니터링이 PLC 에 붙지 못한다. 전역 PlcConnection.json 에만 저장했을 때 실제로 그랬다.
[<Fact>]
let ``InterfaceMicrexSx roundtrip builds a MICREX-SX connection on the loader port`` () =
    let systemId = Guid.NewGuid()
    let aid = AssetInterfacesDescription()
    let endpoint =
        { MicrexSxEndpointMetadata.empty with
            Base = "sx+tcp://192.168.250.101:509"
            SystemId = Some systemId
            IoMapPath = Some @"C:\plc\io_map.json"
            WritableAreas = [ "M1" ]
            TimeoutMs = 4000
            ScanIntervalMs = 120 }
    let interaction : OpcUaInteraction = {
        IdShort = "Sx_ABCDEF"
        SemanticId = SemanticId "urn:dualsoft:cd:micrexsx:io:abcdef:1:0"
        ValueType = XsBoolean
        Unit = None
        // 실제 모델이 쓰는 4단계 IEC 원격 주소.
        Href = "%IX3.30.0.00"
        SignalId = SignalId "%IX3.30.0.00"
    }
    aid.Interfaces.Add(MicrexSx(endpoint, [ interaction ]))

    let sm = AasxExportStandardSubmodels.aidToSubmodel aid "sx01"
    let restored = AasxImportStandardSubmodels.submodelToAid sm
    match restored.Interfaces.[0] with
    | MicrexSx (ep, [ restoredInteraction ]) ->
        Assert.Equal("sx+tcp://192.168.250.101:509", ep.Base)
        Assert.Equal(Some systemId, ep.SystemId)
        Assert.Equal(Some @"C:\plc\io_map.json", ep.IoMapPath)
        Assert.Equal<string list>([ "M1" ], ep.WritableAreas)
        Assert.Equal(4000, ep.TimeoutMs)
        Assert.Equal(120, ep.ScanIntervalMs)
        Assert.Equal("%IX3.30.0.00", restoredInteraction.Href)
    | _ -> Assert.Fail "expected a MICREX-SX binding"

    let plan = AidXgtGatewayConfig.build restored
    Assert.True(plan.Success, String.Join(" / ", plan.Errors))
    let connection = Assert.Single plan.Config.Connections
    Assert.Equal(PlcVendor.MicrexSx, connection.Vendor)
    Assert.Equal("192.168.250.101", connection.IpAddress)
    Assert.Equal(509, connection.Port)
    Assert.Equal(@"C:\plc\io_map.json", connection.SxIoMapPath)
    Assert.Equal<string list>([ "M1" ], connection.SxWritableAreas)
    Assert.Equal("%IX3.30.0.00", connection.Tags.Head.PlcAddress)

/// 쓰기 허용은 **없던 권한이 import 로 생기면 안 된다**. writableAreas 속성이 없는 파일과
/// 빈 문자열인 파일은 모두 읽기 전용으로 복원되어야 한다 — 그 상태에서만 SX 커넥터가
/// 쓰기 권한 발급을 거부한다.
[<Fact>]
let ``InterfaceMicrexSx defaults to read-only when writableAreas is absent`` () =
    let aid = AssetInterfacesDescription()
    let interaction : OpcUaInteraction = {
        IdShort = "Sx_1"; SemanticId = SemanticId "urn:t:1"; ValueType = XsBoolean
        Unit = None; Href = "M1.2000.0"; SignalId = SignalId "M1.2000.0" }
    aid.Interfaces.Add(MicrexSx({ MicrexSxEndpointMetadata.empty with WritableAreas = [] }, [ interaction ]))

    let restored =
        AasxExportStandardSubmodels.aidToSubmodel aid "sx02"
        |> AasxImportStandardSubmodels.submodelToAid
    match restored.Interfaces.[0] with
    | MicrexSx (ep, _) ->
        Assert.Empty ep.WritableAreas
        Assert.Equal(None, ep.IoMapPath)
    | _ -> Assert.Fail "expected a MICREX-SX binding"

    let plan = AidXgtGatewayConfig.build restored
    Assert.True(plan.Success, String.Join(" / ", plan.Errors))
    Assert.Empty plan.Config.Connections.Head.SxWritableAreas

/// 한 System 이 XGT·SX 바인딩을 동시에 가지면 Agent 가 AID 에서 연결을 **두 개** 만든다.
/// 현장에서 "1대만 설정했는데 2대 끊김" 으로 나타난 그 상황이다. 벤더를 바꿀 때 상대
/// 바인딩을 지우는 것이 해법이고, 이 시험이 그 제거를 지킨다.
[<Fact>]
let ``switching a System to MICREX-SX leaves exactly one AID connection`` () =
    let systemId = Guid.NewGuid()
    let aid = AssetInterfacesDescription()

    // ① 예전에 LS 로 저장해 둔 endpoint.
    let created =
        AidXgtEndpointSettings.ensureBindingForSystem(
            aid, systemId, xgtTcpRequest "LsXgi" "192.168.250.101" 2004, [ "%IX3.30.0.00" ])
    Assert.True(created > 0)

    // ② 같은 System 을 SX 로 다시 저장 — endpoint 를 만들고 옛 XGT 바인딩을 지운다.
    let written =
        AidMicrexSxEndpointSettings.ensureBindingForSystem(
            aid, systemId, "192.168.250.101", 509, "", [ "M1" ], 3000, 100, [ "%IX3.30.0.00" ])
    Assert.True(written > 0)
    AidXgtEndpointSettings.removeForSystem(aid, systemId) |> ignore

    let plan = AidXgtGatewayConfig.build aid
    Assert.True(plan.Success, String.Join(" / ", plan.Errors))
    let connection = Assert.Single plan.Config.Connections
    Assert.Equal(PlcVendor.MicrexSx, connection.Vendor)
    Assert.Equal(509, connection.Port)

    // 되돌리기도 대칭이어야 한다 — SX 바인딩을 지우면 다시 하나도 남지 않는다.
    Assert.Equal(1, AidMicrexSxEndpointSettings.removeForSystem(aid, systemId))
    Assert.Empty aid.Interfaces

/// signalId 는 AID **전체**에서 유일해야 한다(OPC UA NodeId·수집 시계열의 영속 키).
/// 두 바인딩 종류가 공존하므로 점유 집합도 둘을 함께 세야 한다 — 한쪽만 세면 같은 주소를
/// 쓰는 다른 System 의 signalId 를 그대로 재발급해 게이트웨이 조립이 중복으로 실패한다.
[<Fact>]
let ``signalId minting sees both XGT and MICREX-SX bindings`` () =
    let systemA = Guid.NewGuid()
    let systemB = Guid.NewGuid()
    let shared = "%IX3.30.0.00"
    let aid = AssetInterfacesDescription()

    AidXgtEndpointSettings.ensureBindingForSystem(aid, systemA, xgtTcpRequest "LsXgi" "10.0.0.1" 2004, [ shared ])
    |> ignore
    AidMicrexSxEndpointSettings.ensureBindingForSystem(
        aid, systemB, "10.0.0.2", 509, "", [], 3000, 100, [ shared ])
    |> ignore

    let ids =
        [ for binding in aid.Interfaces do
            match binding with
            | Xgt (_, interactions) | MicrexSx (_, interactions) ->
                for i in interactions -> i.SignalId.Value
            | _ -> () ]
    Assert.Equal(2, List.length ids)
    Assert.Equal(2, ids |> List.distinct |> List.length)

    // 게이트웨이 조립이 중복 signalId 로 실패하지 않아야 한다.
    let plan = AidXgtGatewayConfig.build aid
    Assert.True(plan.Success, String.Join(" / ", plan.Errors))
    Assert.Equal(2, plan.Config.Connections.Length)

[<Fact>]
let ``HTTP webhook AID requires Vault auth and builds an ingress signal`` () =
    let aid = AssetInterfacesDescription()
    let endpoint = {
        EndpointMetadata.empty with
            Base = "https://agent.example.test/aid"
            AuthReferenceVault = Some "@vault:secret/ds2/webhook#bearer"
    }
    let interaction : HttpInteraction = {
        IdShort = "InspectionCompleted"
        SemanticId = SemanticId "urn:dualsoft:test:inspection"
        ValueType = XsString
        Unit = None
        Href = "/hooks/inspection"
        Method = Post
        ContentType = "application/json"
        PayloadPath = "$.result"
        PollIntervalMs = None
        SignalId = SignalId "line1.inspection.result"
    }
    aid.Interfaces.Add(Http(endpoint, [interaction]))
    let plan = AidSouthboundConfig.build aid
    Assert.True(plan.Success, String.Join(" / ", plan.Errors))
    let signal = Assert.Single(Assert.Single(plan.Endpoints).Signals)
    Assert.False(signal.PollIntervalMs.HasValue)

    let noAuth = AssetInterfacesDescription()
    noAuth.Interfaces.Add(Http({ endpoint with AuthReferenceVault = None }, [interaction]))
    let rejected = AidSouthboundConfig.build noAuth
    Assert.False(rejected.Success)
    Assert.Contains(rejected.Errors, fun message -> message.Contains("requires authReferenceVault"))

[<Fact>]
let ``HTTP AID rejects cross-origin href that could leak endpoint credentials`` () =
    let aid = vis02Aid()
    let escaped =
        match aid.Interfaces.[0] with
        | Http (endpoint, first :: rest) ->
            Http(endpoint, { first with Href = "https://attacker.example/steal" } :: rest)
        | _ -> failwith "fixture must contain HTTP"
    aid.Interfaces.Clear()
    aid.Interfaces.Add escaped
    let plan = AidSouthboundConfig.build aid
    Assert.False(plan.Success)
    Assert.Contains(plan.Errors, fun message -> message.Contains("base origin"))

[<Fact>]
let ``HTTP AID rejects state-changing polling and non-ingress webhook methods`` () =
    let polled = vis02Aid()
    let unsafePolling =
        match polled.Interfaces.[0] with
        | Http (endpoint, first :: rest) -> Http(endpoint, { first with Method = Delete } :: rest)
        | _ -> failwith "fixture must contain HTTP"
    polled.Interfaces.Clear()
    polled.Interfaces.Add unsafePolling
    let polledPlan = AidSouthboundConfig.build polled
    Assert.False(polledPlan.Success)
    Assert.Contains(polledPlan.Errors, fun message -> message.Contains("state-changing"))

    let webhook = AssetInterfacesDescription()
    let endpoint = {
        EndpointMetadata.empty with
            Base = "https://agent.example.test/aid"
            AuthReferenceVault = Some "@vault:secret/ds2/webhook#bearer"
    }
    let interaction : HttpInteraction = {
        IdShort = "UnsafeGetWebhook"
        SemanticId = SemanticId "urn:dualsoft:test:webhook"
        ValueType = XsString
        Unit = None
        Href = "/hooks/value"
        Method = Get
        ContentType = "application/json"
        PayloadPath = "$.value"
        PollIntervalMs = None
        SignalId = SignalId "line1.webhook.value"
    }
    webhook.Interfaces.Add(Http(endpoint, [interaction]))
    let webhookPlan = AidSouthboundConfig.build webhook
    Assert.False(webhookPlan.Success)
    Assert.Contains(webhookPlan.Errors, fun message -> message.Contains("POST or PUT"))

[<Fact>]
let ``standard AID plaintext transports require explicit private-network opt-in`` () =
    let mqtt = vib11Aid()
    let plaintextMqtt =
        match mqtt.Interfaces.[0] with
        | Mqtt (endpoint, interactions) ->
            Mqtt({ endpoint with Security = None; AuthReferenceVault = None }, interactions)
        | _ -> failwith "fixture must contain MQTT"
    mqtt.Interfaces.Clear()
    mqtt.Interfaces.Add plaintextMqtt
    let mqttPlan = AidSouthboundConfig.build mqtt
    Assert.False(mqttPlan.Success)
    Assert.Contains(mqttPlan.Errors, fun message -> message.Contains("insecure-private"))

    let http = vis02Aid()
    let plaintextHttp =
        match http.Interfaces.[0] with
        | Http (endpoint, interactions) ->
            Http({ endpoint with Base = "http://camera.plant.local/api"; Security = None; AuthReferenceVault = None }, interactions)
        | _ -> failwith "fixture must contain HTTP"
    http.Interfaces.Clear()
    http.Interfaces.Add plaintextHttp
    let httpPlan = AidSouthboundConfig.build http
    Assert.False(httpPlan.Success)
    Assert.Contains(httpPlan.Errors, fun message -> message.Contains("insecure-private"))

[<Fact>]
let ``invalid CollectionPolicy fails AID preflight instead of disappearing`` () =
    let aid = vis02Aid()
    let invalidPolicy : SignalPolicy = {
        SignalId = SignalId "line1.vis02.judgement"
        AcquisitionMode = AcquisitionMode.Sampled
        SamplingIntervalMs = None
        PublishingIntervalMs = None
        DeadbandAbsolute = None
        DeadbandPercent = None
        EngineeringRangeLow = None
        EngineeringRangeHigh = None
        QueueSize = None
        Retention = "P90D"
    }
    let plan = AidSouthboundConfig.buildWithPolicies(aid, [invalidPolicy])
    Assert.False(plan.Success)
    Assert.Contains(plan.Errors, fun message -> message.Contains("Sampled acquisition"))

// -----------------------------------------------------------------------------
// Provenance §C — Qualifier(dualsoft:origin) + Extension(auto-suppressed) 라운드트립
// -----------------------------------------------------------------------------
