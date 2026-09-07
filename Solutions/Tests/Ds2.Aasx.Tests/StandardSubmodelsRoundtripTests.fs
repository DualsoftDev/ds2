module Ds2.Aasx.Tests.StandardSubmodelsRoundtripTests

open System
open AasCore.Aas3_1
open Ds2.Core
open Ds2.Core.Kpi
open Ds2.Core.StandardSubmodels
open Ds2.Aasx
open Ds2.Backend.Plc
open Ds2.Aasx.Tests.PilotAssetFixtures
open Xunit

let rec private descendants (items: seq<ISubmodelElement>) = seq {
    for item in items do
        yield item
        match item with
        | :? SubmodelElementCollection as smc when not (isNull smc.Value) ->
            yield! descendants smc.Value
        | _ -> ()
}

// Phase 1 · 5 파일럿 자산 SM 왕복 (F# → AAS Submodel → F#) 동등성 회귀.
// Ds2.Aasx.AasxExportStandardSubmodels · AasxImportStandardSubmodels 는 internal 이므로
// InternalsVisibleTo 없이도 이 어셈블리에서 참조 가능하도록 module 을 internal 로 두었으나,
// 테스트 접근 편의를 위해 리플렉션 대신 wrapper 함수를 이 테스트가 재정의.
//
// 실제 API 는 Phase 2/3 로 wire-up 시점에 public 승격 예정.

// AID 를 만들고, 라운드트립 후 필드 개수 · signalId · href 등이 유지되는지만 스모크.
[<Fact>]
let ``CNC01 AID roundtrip preserves signalId and href`` () =
    let aid = cnc01Aid()
    let sm = AasxExportStandardSubmodels.aidToSubmodel aid "cnc01"
    let restored = AasxImportStandardSubmodels.submodelToAid sm
    Assert.Equal(aid.Interfaces.Count, restored.Interfaces.Count)
    // 첫 인터페이스는 OpcUa 이고, InteractionMetadata 3개 (SpindleSpeed/MotorTemp/CycleCount)
    match restored.Interfaces.[0] with
    | OpcUa (ep, interactions, events) ->
        Assert.Equal("opc.tcp://uaserver.plant1.local:4840", ep.Base)
        Assert.Equal(3, List.length interactions)
        Assert.Equal(0, List.length events)
        let spindle = interactions |> List.find (fun i -> i.IdShort = "SpindleSpeed")
        Assert.Equal("line1.cnc01.spindle-speed", spindle.SignalId.Value)
        Assert.Equal("ns=2;s=Line1.CNC01.SpindleSpeed", spindle.Href)
        Assert.Equal(XsDouble, spindle.ValueType)
        Assert.Equal(Some "rpm", spindle.Unit)
    | _ -> Assert.Fail "expected OpcUa binding"

[<Fact>]
let ``TimeSeries LinkedSegments use the same stable Collector series identity`` () =
    let projectId = Guid.Parse("2f5d9e90-38a7-4f99-b44b-c2bd510fa8b8")
    let assetId = AssetTelemetryIdentity.aidProject projectId
    let endpoint = "https://agent.example.test/data/v1/series"
    let sm =
        AasxExportStandardSubmodels.timeSeriesToSubmodel
            (cnc01Aid()) assetId projectId "CNC01" endpoint

    Assert.Equal(AasxSemantics.TimeSeriesSubmodelIdShort, sm.IdShort)
    Assert.Equal(AasxSemantics.TimeSeriesSubmodelSemanticId, sm.SemanticId.Keys.[0].Value)

    let linked = AasxImportStandardSubmodels.linkedSeriesFromTimeSeries sm
    Assert.Equal(3, linked.Length)
    let spindle = linked |> List.find (fun item -> item.SignalId = "line1.cnc01.spindle-speed")
    let expected =
        AssetTelemetryIdentity.seriesId assetId (SignalId "line1.cnc01.spindle-speed")
    Assert.Equal(expected, spindle.SeriesId)
    Assert.Equal(endpoint, spindle.Endpoint)
    Assert.Equal("seriesId=" + Uri.EscapeDataString(expected), spindle.Query)

[<Fact>]
let ``TimeSeries parser falls back to the standard Query seriesId`` () =
    let projectId = Guid.NewGuid()
    let assetId = AssetTelemetryIdentity.aidProject projectId
    let sm =
        AasxExportStandardSubmodels.timeSeriesToSubmodel
            (cnc01Aid()) assetId projectId "CNC01" "https://agent.example.test/v1/series"
    let seriesIdProperties =
        descendants sm.SubmodelElements
        |> Seq.choose (function :? Property as p when p.IdShort = "SeriesId" -> Some p | _ -> None)
        |> Seq.toArray
    for property in seriesIdProperties do
        property.Value <- ""

    let linked = AasxImportStandardSubmodels.linkedSeriesFromTimeSeries sm
    Assert.Equal(3, linked.Length)
    Assert.All(linked, fun item -> Assert.False(String.IsNullOrWhiteSpace item.SeriesId))

[<Fact>]
let ``PM03 AID roundtrip preserves Modbus fields`` () =
    let aid = pm03Aid()
    let sm = AasxExportStandardSubmodels.aidToSubmodel aid "pm03"
    let restored = AasxImportStandardSubmodels.submodelToAid sm
    match restored.Interfaces.[0] with
    | Modbus (ep, interactions) ->
        Assert.Equal("modbus+tcp://192.168.10.31:502", ep.Base)
        Assert.Equal(Some 1uy, ep.UnitId)
        let ap = interactions |> List.head
        Assert.Equal(ReadHoldingRegisters, ap.Function)
        Assert.True(ap.MostSignificantWord)
        Assert.Equal(0.1, ap.Scale)
        Assert.Equal("line1.pm03.active-power", ap.SignalId.Value)
    | _ -> Assert.Fail "expected Modbus binding"

[<Fact>]
let ``VIB11 AID roundtrip preserves MQTT fields`` () =
    let aid = vib11Aid()
    let sm = AasxExportStandardSubmodels.aidToSubmodel aid "vib11"
    let restored = AasxImportStandardSubmodels.submodelToAid sm
    match restored.Interfaces.[0] with
    | Mqtt (ep, interactions) ->
        Assert.Equal("mqtt://broker.plant1.local:1883", ep.Base)
        Assert.Equal(Some "@vault:secret/ds2/adapter/mqtt/vib11#creds", ep.AuthReferenceVault)
        let v = interactions |> List.head
        Assert.Equal(Subscribe, v.ControlPacket)
        Assert.Equal(1, v.Qos)
        Assert.Equal("$.rms", v.PayloadPath)
    | _ -> Assert.Fail "expected Mqtt binding"

[<Fact>]
let ``VIS02 AID roundtrip preserves HTTP fields`` () =
    let aid = vis02Aid()
    let sm = AasxExportStandardSubmodels.aidToSubmodel aid "vis02"
    let restored = AasxImportStandardSubmodels.submodelToAid sm
    match restored.Interfaces.[0] with
    | Http (ep, interactions) ->
        Assert.Equal("https://qc.plant1.local/api", ep.Base)
        let v = interactions |> List.head
        Assert.Equal(Get, v.Method)
        Assert.Equal(Some 5000, v.PollIntervalMs)
        Assert.Equal("$.judgement", v.PayloadPath)
    | _ -> Assert.Fail "expected Http binding"

[<Fact>]
let ``BCR05 AID roundtrip preserves AutoID event`` () =
    let aid = bcr05Aid()
    let sm = AasxExportStandardSubmodels.aidToSubmodel aid "bcr05"
    let restored = AasxImportStandardSubmodels.submodelToAid sm
    match restored.Interfaces.[0] with
    | OpcUa (_ep, interactions, events) ->
        Assert.Empty(interactions)
        let ev = List.head events
        Assert.Equal("urn:opcfoundation:autoid:OpticalScanEventType", ev.EventType.Value)
        Assert.Equal("ScanResult.Code", ev.PayloadPath)
        Assert.Equal("line1.bcr05.code", ev.SignalId.Value)
    | _ -> Assert.Fail "expected OpcUa binding"

[<Fact>]
let ``InterfaceXGT roundtrip builds Agent gateway config and signal map`` () =
    let aid = AssetInterfacesDescription()
    let endpoint = {
        XgtEndpointMetadata.empty with
            Base = "xgt+tcp://192.168.10.20:2004"
            CpuModel = Xgk
            NetworkNumber = 2uy
            StationNumber = 3uy
            ScanIntervalMs = 250
    }
    let interaction : OpcUaInteraction = {
        IdShort = "CylinderReady"
        SemanticId = SemanticId "urn:dualsoft:cd:cylinder-ready:1"
        ValueType = XsBoolean
        Unit = None
        Href = "%MX100"
        SignalId = SignalId "line1.station01.cylinder-ready"
    }
    aid.Interfaces.Add(Xgt(endpoint, [interaction]))

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

    let conceptIds =
        AasxConceptDescriptions.createAllConceptDescriptions ()
        |> Seq.map (fun cd -> cd.Id)
        |> Set.ofSeq
    Assert.Contains(AasxSemantics.SignalIdExtensionSemanticId, conceptIds)
    Assert.Contains(AasxSemantics.XgtInterfaceSemanticId, conceptIds)

    let restored = AasxImportStandardSubmodels.submodelToAid sm
    match restored.Interfaces.[0] with
    | Xgt (ep, interactions) ->
        Assert.Equal(endpoint.Base, ep.Base)
        Assert.Equal(Xgk, ep.CpuModel)
        Assert.Equal(2uy, ep.NetworkNumber)
        Assert.Equal(3uy, ep.StationNumber)
        Assert.Equal(250, ep.ScanIntervalMs)
        Assert.Equal("%MX100", interactions.Head.Href)
    | _ -> Assert.Fail "expected XGT binding"

    let plan = AidXgtGatewayConfig.build restored
    Assert.True(plan.HasBinding)
    Assert.True(plan.Success, String.Join(" / ", plan.Errors))
    Assert.Single(plan.Config.Connections) |> ignore
    Assert.Equal(PlcVendor.LsXgk, plan.Config.Connections.Head.Vendor)
    Assert.Equal("192.168.10.20", plan.Config.Connections.Head.IpAddress)
    Assert.Equal(2004, plan.Config.Connections.Head.Port)
    Assert.Equal("%MX100", plan.Config.Connections.Head.Tags.Head.HubAddress)
    Assert.Single(plan.Signals) |> ignore
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
let ``Promaker PLC settings update only the AID XGT endpoint`` () =
    let aid = AssetInterfacesDescription()
    let interaction : OpcUaInteraction = {
        IdShort = "Ready"
        SemanticId = SemanticId "urn:dualsoft:test:ready"
        ValueType = XsBoolean
        Unit = None
        Href = "%MX100"
        SignalId = SignalId "line1.station01.ready"
    }
    aid.Interfaces.Add(Xgt(XgtEndpointMetadata.empty, [interaction]))

    let updated =
        AidXgtEndpointSettings.updateAll(
            aid, "LsXgb", "192.168.9.102", 2004, false, true,
            2uy, 3uy, 7000, 250)

    Assert.Equal(1, updated)
    let connection = AidXgtEndpointSettings.tryReadFirst aid
    Assert.NotNull(connection)
    Assert.Equal("xgt+tcp://192.168.9.102:2004", connection.BaseUri)
    Assert.Equal("LsXgb", connection.Vendor)
    Assert.Equal("192.168.9.102", connection.IpAddress)
    Assert.Equal(2004, connection.Port)
    Assert.Equal(7000, connection.TimeoutMs)
    Assert.Equal(250, connection.ScanIntervalMs)

    match aid.Interfaces.[0] with
    | Xgt (endpoint, interactions) ->
        Assert.Equal(Xgb, endpoint.CpuModel)
        Assert.Equal("%MX100", interactions.Head.Href)
    | _ -> Assert.Fail "expected XGT binding"

[<Fact>]
let ``Promaker PLC addresses create exportable AID interactions and preserve raw hrefs`` () =
    let aid = AssetInterfacesDescription()
    let created =
        AidXgtEndpointSettings.ensureBinding(
            aid, "LsXgi", "192.168.9.102", 2004, false, true,
            0uy, 255uy, 3000, 100,
            [ "%QX0.1.13"; "%IX0.1.2"; "%qx0.1.13" ])

    Assert.Equal(2, created)
    match aid.Interfaces.[0] with
    | Xgt (_, interactions) ->
        Assert.Equal<string list>([ "%QX0.1.13"; "%IX0.1.2" ], interactions |> List.map _.Href)
        Assert.All(interactions, fun i -> Assert.Matches("^[A-Za-z][A-Za-z0-9_]*$", i.IdShort))
        Assert.Equal(2, interactions |> List.map _.IdShort |> Set.ofList |> Set.count)
    | _ -> Assert.Fail "expected XGT binding"

    // AASX 변환기의 엄격한 idShort 검증까지 통과해야 새 프로젝트 저장이 성공한다.
    let submodel = AasxExportStandardSubmodels.aidToSubmodel aid "new-project"
    let restored = AasxImportStandardSubmodels.submodelToAid submodel
    match restored.Interfaces.[0] with
    | Xgt (_, interactions) ->
        Assert.Equal<string list>([ "%QX0.1.13"; "%IX0.1.2" ], interactions |> List.map _.Href)
    | _ -> Assert.Fail "expected XGT binding"

[<Fact>]
let ``Promaker AID synchronization merges addresses added after first save`` () =
    let aid = AssetInterfacesDescription()
    AidXgtEndpointSettings.ensureBinding(
        aid, "LsXgi", "192.168.9.102", 2004, false, true,
        0uy, 255uy, 3000, 100, [ "%QX0.1" ])
    |> ignore

    let synchronized =
        AidXgtEndpointSettings.ensureBinding(
            aid, "LsXgi", "192.168.9.103", 2004, false, true,
            0uy, 255uy, 3000, 100, [ "%QX0.1"; "%IX0.2" ])

    Assert.Equal(2, synchronized)
    match aid.Interfaces.[0] with
    | Xgt (endpoint, interactions) ->
        Assert.Equal("xgt+tcp://192.168.9.103:2004", endpoint.Base)
        Assert.Equal<string list>([ "%QX0.1"; "%IX0.2" ], interactions |> List.map _.Href)
    | _ -> Assert.Fail "expected XGT binding"

[<Fact>]
let ``Promaker AID synchronization repairs legacy raw-address idShort`` () =
    let aid = AssetInterfacesDescription()
    let legacy : OpcUaInteraction =
        { IdShort = "%QX0.1"
          SemanticId = SemanticId "urn:dualsoft:cd:xgt:io:%qx0.1:1:0"
          ValueType = XsBoolean
          Unit = None
          Href = "%QX0.1"
          SignalId = SignalId "%QX0.1" }
    aid.Interfaces.Add(Xgt(XgtEndpointMetadata.empty, [ legacy ]))

    AidXgtEndpointSettings.ensureBinding(
        aid, "LsXgi", "192.168.9.102", 2004, false, true,
        0uy, 255uy, 3000, 100, [ "%QX0.1" ])
    |> ignore

    match aid.Interfaces.[0] with
    | Xgt (_, [ repaired ]) ->
        Assert.Matches("^[A-Za-z][A-Za-z0-9_]*$", repaired.IdShort)
        Assert.Equal("%QX0.1", repaired.Href)
        AasxExportStandardSubmodels.aidToSubmodel aid "legacy-project" |> ignore
    | _ -> Assert.Fail "expected one repaired XGT interaction"

[<Fact>]
let ``AID endpoint update does not create a compatibility binding`` () =
    let aid = AssetInterfacesDescription()
    aid.Interfaces.Add(OpcUa(EndpointMetadata.empty, [], []))

    let updated =
        AidXgtEndpointSettings.updateAll(
            aid, "LsXgi", "192.168.9.102", 2004, false, true,
            0uy, 255uy, 3000, 100)

    Assert.Equal(0, updated)
    Assert.Null(AidXgtEndpointSettings.tryReadFirst aid)

[<Fact>]
let ``InterfaceXGT rejects mismatched transport credentials and duplicate signal identities`` () =
    let aid = AssetInterfacesDescription()
    let endpoint = {
        XgtEndpointMetadata.empty with
            Transport = XgtUdp
            Base = "xgt+tcp://192.168.10.20:2004"
            AuthReferenceVault = Some "@vault:secret/xgt"
    }
    let interaction : OpcUaInteraction = {
        IdShort = "Run"
        SemanticId = SemanticId "urn:dualsoft:test:run"
        ValueType = XsBoolean
        Unit = None
        Href = "%MX0"
        SignalId = SignalId "line1.xgt.run"
    }
    aid.Interfaces.Add(Xgt(endpoint, [interaction; { interaction with IdShort = "RunAgain"; Href = "%MX1" }]))

    let plan = AidXgtGatewayConfig.build aid
    Assert.True(plan.HasBinding)
    Assert.False(plan.Success)
    Assert.Contains(plan.Errors, fun message -> message.Contains("xgt+udp"))
    Assert.Contains(plan.Errors, fun message -> message.Contains("authReferenceVault"))

    let duplicateAid = AssetInterfacesDescription()
    duplicateAid.Interfaces.Add(Xgt(XgtEndpointMetadata.empty,
        [interaction; { interaction with IdShort = "RunAgain"; Href = "%MX1" }]))
    let duplicatePlan = AidXgtGatewayConfig.build duplicateAid
    Assert.False(duplicatePlan.Success)
    Assert.Contains(duplicatePlan.Errors, fun message -> message.Contains("중복"))

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
            aid, systemId, "LsXgi", "192.168.250.101", 2004, false, true,
            0uy, 255uy, 3000, 100, [ "%IX3.30.0.00" ])
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

    AidXgtEndpointSettings.ensureBindingForSystem(
        aid, systemA, "LsXgi", "10.0.0.1", 2004, false, true, 0uy, 255uy, 3000, 100, [ shared ])
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

[<Fact>]
let ``Provenance · AID Auto origin survives roundtrip via Qualifier`` () =
    let aid = cnc01Aid()
    // SpindleSpeed 만 auto-origin 으로 표기 (KpiAppender 가 하듯이).
    aid.AutoOriginIdShorts.Add("SpindleSpeed") |> ignore
    let sm = AasxExportStandardSubmodels.aidToSubmodel aid "cnc01"
    let restored = AasxImportStandardSubmodels.submodelToAid sm
    Assert.Contains("SpindleSpeed", restored.AutoOriginIdShorts)
    Assert.DoesNotContain("MotorTemp", restored.AutoOriginIdShorts)
    Assert.DoesNotContain("CycleCount", restored.AutoOriginIdShorts)

[<Fact>]
let ``Provenance · AID Suppressed set survives roundtrip via Extension`` () =
    let aid = cnc01Aid()
    aid.SuppressedAutoIdShorts.Add("Kpi_Sys_deadbeef_OEE") |> ignore
    aid.SuppressedAutoIdShorts.Add("Kpi_Wk_cafef00d_CT") |> ignore
    let sm = AasxExportStandardSubmodels.aidToSubmodel aid "cnc01"
    let restored = AasxImportStandardSubmodels.submodelToAid sm
    Assert.Equal(2, restored.SuppressedAutoIdShorts.Count)
    Assert.Contains("Kpi_Sys_deadbeef_OEE", restored.SuppressedAutoIdShorts)
    Assert.Contains("Kpi_Wk_cafef00d_CT", restored.SuppressedAutoIdShorts)

[<Fact>]
let ``Provenance · OperationalData Auto + Suppressed roundtrip`` () =
    let od = OperationalData()
    let a = OperationalDataItem()
    a.IdShort <- "AutoItem"
    a.SemanticId <- SemanticId "urn:dualsoft:signal:auto1"
    a.ValueType <- XsDouble
    od.Items.Add(a)
    let u = OperationalDataItem()
    u.IdShort <- "UserItem"
    u.SemanticId <- SemanticId "urn:custom:user:1/0"
    u.ValueType <- XsDouble
    od.Items.Add(u)
    od.AutoOriginIdShorts.Add("AutoItem") |> ignore
    od.SuppressedAutoIdShorts.Add("Kpi_Deleted_1") |> ignore
    let sm = AasxExportStandardSubmodels.operationalDataToSubmodel od "asset01"
    let restored = AasxImportStandardSubmodels.submodelToOperationalData sm
    Assert.Contains("AutoItem", restored.AutoOriginIdShorts)
    Assert.DoesNotContain("UserItem", restored.AutoOriginIdShorts)
    Assert.Contains("Kpi_Deleted_1", restored.SuppressedAutoIdShorts)

[<Fact>]
let ``Provenance · AIMC Auto + Suppressed roundtrip via mapping SME idShort`` () =
    let aimc = AssetInterfacesMappingConfiguration()
    let source = Ds2.Core.Kpi.KpiIdentifiers.aidSourcePath "Kpi_Sys_dead_OEE"
    let sink   = Ds2.Core.Kpi.KpiIdentifiers.opDataSinkPath "Kpi_Sys_dead_OEE"
    aimc.Mappings.Add({ SourceAidPath = source; SinkAasElementPath = sink; Transform = Identity })
    let autoMappingIdShort = Ds2.Core.Kpi.KpiIdentifiers.aimcMappingIdShort source sink
    aimc.AutoOriginIdShorts.Add(autoMappingIdShort) |> ignore
    aimc.SuppressedAutoIdShorts.Add("Mapping_deadbeef") |> ignore
    let sm = AasxExportStandardSubmodels.aimcToSubmodel aimc "asset01"
    let restored = AasxImportStandardSubmodels.submodelToAimc sm
    Assert.Equal(1, restored.Mappings.Count)
    Assert.Contains(autoMappingIdShort, restored.AutoOriginIdShorts)
    Assert.Contains("Mapping_deadbeef", restored.SuppressedAutoIdShorts)

[<Fact>]
let ``Provenance · KpiAppender skips tombstoned IdShort`` () =
    let aid = cnc01Aid()
    let targetIdShort = "Kpi_Test_abcdef01_OEE"
    aid.SuppressedAutoIdShorts.Add(targetIdShort) |> ignore
    let originalCount =
        match aid.Interfaces.[0] with OpcUa (_, xs, _) -> List.length xs | _ -> 0
    let target : KpiTarget = {
        Kind         = SystemKind
        EntityFqdn   = "Test"
        Metric       = { IdShortSuffix = "OEE"
                         SemanticId    = "urn:ds:kpi/Sys/OEE/1/0"
                         DataType      = XsDouble
                         Unit          = ""
                         UpdateHint    = OnChange
                         DescriptionKr = ""
                         DescriptionEn = "" }
        IdShort      = targetIdShort
        SignalId     = SignalId "line1.test.oee"
    }
    let state = KpiAidAppender.ensure aid target
    Assert.Equal(EnsureState.Suppressed, state)
    let afterCount =
        match aid.Interfaces.[0] with OpcUa (_, xs, _) -> List.length xs | _ -> 0
    Assert.Equal(originalCount, afterCount)

[<Fact>]
let ``SignalPolicy attaches to SequenceLogging and roundtrips`` () =
    let policies = cnc01SignalPolicies()
    // 빈 Logging Submodel 시뮬레이션
    let loggingSm = AasCore.Aas3_1.Submodel("urn:test:logging:cnc01")
    loggingSm.IdShort <- "SequenceLogging"
    loggingSm.SubmodelElements <-
        System.Collections.Generic.List<AasCore.Aas3_1.ISubmodelElement>()
    AasxExportStandardSubmodels.attachSignalPoliciesToLogging loggingSm policies
    let restored = AasxImportStandardSubmodels.signalPoliciesFromLogging loggingSm
    Assert.Equal(policies.Length, restored.Length)
    let spindleOriginal = policies |> List.find (fun p -> p.SignalId.Value = "line1.cnc01.spindle-speed")
    let spindleRestored = restored |> List.find (fun p -> p.SignalId.Value = "line1.cnc01.spindle-speed")
    Assert.Equal(spindleOriginal.AcquisitionMode, spindleRestored.AcquisitionMode)
    Assert.Equal(spindleOriginal.SamplingIntervalMs, spindleRestored.SamplingIntervalMs)
    Assert.Equal(spindleOriginal.DeadbandAbsolute, spindleRestored.DeadbandAbsolute)
    Assert.Equal(spindleOriginal.DeadbandPercent, spindleRestored.DeadbandPercent)
    Assert.Equal(spindleOriginal.EngineeringRangeLow, spindleRestored.EngineeringRangeLow)
    Assert.Equal(spindleOriginal.EngineeringRangeHigh, spindleRestored.EngineeringRangeHigh)
    Assert.Equal(spindleOriginal.Retention, spindleRestored.Retention)
