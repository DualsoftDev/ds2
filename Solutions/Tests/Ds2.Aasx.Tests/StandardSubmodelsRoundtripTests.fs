module Ds2.Aasx.Tests.StandardSubmodelsRoundtripTests

open System
open AasCore.Aas3_1
open Ds2.Core
open Ds2.Core.Kpi
open Ds2.Core.StandardSubmodels
open Ds2.Aasx
open Ds2.TestKit.PilotAssetFixtures
open Xunit
open Ds2.TestKit.AidTestHelpers

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

/// base 해석은 Core(Promaker 읽기·쓰기)와 Backend(게이트웨이 조립)가 같은 함수를 쓴다 — 여기서 규칙을 고정한다.
[<Fact>]
let ``XGT endpoint base parser handles Ethernet defaults and USB selector paths`` () =
    // 이더넷: 포트 생략 = 2004. 자격증명·fragment·비 URI 는 거절.
    Assert.Equal(
        Ok (XgtEndpointBase.Ethernet ("192.168.1.10", 2004)),
        XgtEndpointBase.tryParse XgtTcp "xgt+tcp://192.168.1.10")
    assertError (XgtEndpointBase.tryParse XgtTcp "xgt+tcp://user:pw@192.168.1.10:2004")
    assertError (XgtEndpointBase.tryParse XgtTcp "xgt+tcp://192.168.1.10:2004#frag")
    assertError (XgtEndpointBase.tryParse XgtTcp "not a URI")

    // USB: 장치 선택 키는 path 에 실리고, 비우면 첫 장치. host 자리에 두면 bus:addr 가 host:port 로 갈린다.
    Assert.Equal(Ok (XgtEndpointBase.Usb "3:4"), XgtEndpointBase.tryParse XgtUsb "xgt+usb://localhost/3:4")
    Assert.Equal(Ok (XgtEndpointBase.Usb ""), XgtEndpointBase.tryParse XgtUsb "xgt+usb://localhost/")
    Assert.Equal(Ok (XgtEndpointBase.Usb ""), XgtEndpointBase.tryParse XgtUsb "xgt+usb://localhost")
    assertError (XgtEndpointBase.tryParse XgtUsb "xgt+tcp://192.168.1.10:2004")

    // 조립 → 해석 왕복. 공백이 있는 product 부분일치 키도 살아남고, URI 경계 문자는 조립에서 거절된다.
    match XgtEndpointBase.tryUsb "XGB Loader" with
    | Ok baseUri -> Assert.Equal(Ok (XgtEndpointBase.Usb "XGB Loader"), XgtEndpointBase.tryParse XgtUsb baseUri)
    | Error message -> Assert.Fail message
    Assert.Equal(Ok "xgt+usb://localhost/", XgtEndpointBase.tryUsb "")
    assertError (XgtEndpointBase.tryUsb "a/b")

    // 라벨 계약은 대소문자를 가리지 않고 되읽고, 모르는 라벨은 None.
    Assert.Equal(Some XgtUsb, XgtEndpointBase.tryTransportOfLabel "USB")
    Assert.Equal(Some XgtUdp, XgtEndpointBase.tryTransportOfLabel "udp")
    Assert.Equal(None, XgtEndpointBase.tryTransportOfLabel "serial")

/// 요청 하나가 소유 System 의 endpoint 만 갱신하고 기존 interaction 을 보존한다 — 다른 System 은 손대지 않는다.
[<Fact>]
let ``Promaker endpoint request updates only the owning System's XGT endpoint and keeps its interactions`` () =
    let owner = Guid.NewGuid()
    let other = Guid.NewGuid()
    let aid = AssetInterfacesDescription()
    aid.Interfaces.Add(Xgt(
        { XgtEndpointMetadata.empty with SystemId = Some owner },
        [ xgtInteraction "Ready" "%MX100" "line1.station01.ready" ]))
    aid.Interfaces.Add(Xgt(
        { XgtEndpointMetadata.empty with SystemId = Some other; Base = "xgt+tcp://10.0.0.9:2004" },
        [ xgtInteraction "OtherReady" "%MX200" "line1.station02.ready" ]))

    let updated =
        AidXgtEndpointSettings.ensureBindingForSystem(
            aid, owner, xgtEthernetRequest "LsXgb" XgtTcp "192.168.9.102" 2004 7000 250, [])

    // 주소 추가 없이 endpoint 만 갱신 — 기존 interaction 1개가 그대로 남아 1 을 돌려준다.
    Assert.Equal(1, updated)
    let connection = AidXgtEndpointSettings.tryReadForSystem(aid, owner)
    Assert.NotNull(connection)
    Assert.Equal("xgt+tcp://192.168.9.102:2004", connection.BaseUri)
    Assert.Equal("LsXgb", connection.Vendor)
    Assert.Equal(XgtEndpointBase.transportLabel XgtTcp, connection.Transport)
    Assert.False(connection.IsUsb)
    Assert.Equal("192.168.9.102", connection.IpAddress)
    Assert.Equal(2004, connection.Port)
    Assert.Equal(7000, connection.TimeoutMs)
    Assert.Equal(250, connection.ScanIntervalMs)
    Assert.Equal("192.168.9.102:2004", connection.EndpointLabel)

    match aid.Interfaces.[0], aid.Interfaces.[1] with
    | Xgt (endpoint, interactions), Xgt (untouched, _) ->
        Assert.Equal(Xgb, endpoint.CpuModel)
        Assert.Equal("%MX100", interactions.Head.Href)
        Assert.Equal("xgt+tcp://10.0.0.9:2004", untouched.Base)
    | _ -> Assert.Fail "expected two XGT bindings"

[<Fact>]
let ``Promaker PLC addresses create exportable AID interactions and preserve raw hrefs`` () =
    let systemId = Guid.NewGuid()
    let aid = AssetInterfacesDescription()
    let created =
        AidXgtEndpointSettings.ensureBindingForSystem(
            aid, systemId, xgtTcpRequest "LsXgi" "192.168.9.102" 2004,
            [ "%QX0.1.13"; "%IX0.1.2"; "%qx0.1.13" ])

    Assert.Equal(2, created)
    match aid.Interfaces.[0] with
    | Xgt (endpoint, interactions) ->
        Assert.Equal(Some systemId, endpoint.SystemId)
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
    let systemId = Guid.NewGuid()
    let aid = AssetInterfacesDescription()
    AidXgtEndpointSettings.ensureBindingForSystem(
        aid, systemId, xgtTcpRequest "LsXgi" "192.168.9.102" 2004, [ "%QX0.1" ])
    |> ignore

    let synchronized =
        AidXgtEndpointSettings.ensureBindingForSystem(
            aid, systemId, xgtTcpRequest "LsXgi" "192.168.9.103" 2004, [ "%QX0.1"; "%IX0.2" ])

    Assert.Equal(2, synchronized)
    Assert.Equal(1, aid.Interfaces.Count)
    match aid.Interfaces.[0] with
    | Xgt (endpoint, interactions) ->
        Assert.Equal("xgt+tcp://192.168.9.103:2004", endpoint.Base)
        Assert.Equal<string list>([ "%QX0.1"; "%IX0.2" ], interactions |> List.map _.Href)
    | _ -> Assert.Fail "expected XGT binding"

/// systemRef 없는 구버전 endpoint 가 하나만 있으면 저장하는 System 이 그것을 귀속(claim)하고,
/// 주소 원문을 idShort 로 쓰던 구버전 interaction 은 안전한 식별자로 고쳐진다.
[<Fact>]
let ``Promaker AID synchronization repairs legacy raw-address idShort and claims the unassigned endpoint`` () =
    let systemId = Guid.NewGuid()
    let aid = AssetInterfacesDescription()
    let legacy : OpcUaInteraction =
        { IdShort = "%QX0.1"
          SemanticId = SemanticId "urn:dualsoft:cd:xgt:io:%qx0.1:1:0"
          ValueType = XsBoolean
          Unit = None
          Href = "%QX0.1"
          SignalId = SignalId "%QX0.1" }
    aid.Interfaces.Add(Xgt(XgtEndpointMetadata.empty, [ legacy ]))

    AidXgtEndpointSettings.ensureBindingForSystem(
        aid, systemId, xgtTcpRequest "LsXgi" "192.168.9.102" 2004, [ "%QX0.1" ])
    |> ignore

    Assert.Equal(1, aid.Interfaces.Count)
    match aid.Interfaces.[0] with
    | Xgt (endpoint, [ repaired ]) ->
        Assert.Equal(Some systemId, endpoint.SystemId)
        Assert.Matches("^[A-Za-z][A-Za-z0-9_]*$", repaired.IdShort)
        Assert.Equal("%QX0.1", repaired.Href)
        AasxExportStandardSubmodels.aidToSubmodel aid "legacy-project" |> ignore
    | _ -> Assert.Fail "expected one repaired XGT interaction"

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
