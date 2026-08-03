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
    Assert.Equal(spindleOriginal.Retention, spindleRestored.Retention)
