module Ds2.Aasx.Tests.PilotAssetFixtures

open System
open Ds2.Core
open Ds2.Core.StandardSubmodels

// Phase 1 · 스펙 §04 의 5 파일럿 자산 F# 픽스처.
//
// 이 픽스처는 다음 용도로 사용:
//   1) roundtrip 테스트 원본
//   2) AasHost seed 데이터 (Phase 2)
//   3) OPC UA 서버 시작 nodeset (Phase 3)
//   4) AasxEditor 개발 데모 자산 (Phase 5)

let private semId (s: string) = SemanticId s
let private sig' (s: string) = SignalId s

// -----------------------------------------------------------------------------
// CNC01 — 스펙 §04-B-1 (InterfaceOPCUA)
// -----------------------------------------------------------------------------

let cnc01AssetId = GlobalAssetId "urn:dualsoft:asset:cnc01"

let cnc01Aid () : AssetInterfacesDescription =
    let aid = AssetInterfacesDescription()
    aid.IdShort <- "AssetInterfacesDescription"
    let ep = {
        EndpointMetadata.empty with
            Base = "opc.tcp://uaserver.plant1.local:4840"
            Security = Some "Basic256Sha256 / Sign&Encrypt"
    }
    let interactions = [
        { IdShort = "SpindleSpeed"
          SemanticId = semId "urn:dualsoft:cd:motion.spindle-speed/1/0"
          ValueType = XsDouble; Unit = Some "rpm"
          Href = "ns=2;s=Line1.CNC01.SpindleSpeed"
          SignalId = sig' "line1.cnc01.spindle-speed" }
        { IdShort = "MotorTemp"
          SemanticId = semId "urn:dualsoft:cd:motion.motor-temp/1/0"
          ValueType = XsDouble; Unit = Some "°C"
          Href = "ns=2;s=Line1.CNC01.MotorTemp"
          SignalId = sig' "line1.cnc01.motor-temp" }
        { IdShort = "CycleCount"
          SemanticId = semId "urn:dualsoft:cd:motion.cycle-count/1/0"
          ValueType = XsLong; Unit = None
          Href = "ns=2;s=Line1.CNC01.CycleCount"
          SignalId = sig' "line1.cnc01.cycle-count" }
    ]
    aid.Interfaces.Add(OpcUa (ep, interactions, []))
    aid

let cnc01SignalPolicies () : SignalPolicy list =
    [
        { SignalId = sig' "line1.cnc01.spindle-speed"
          AcquisitionMode = AcquisitionMode.ChangeOfValue
          SamplingIntervalMs = Some 500
          PublishingIntervalMs = Some 1000
          DeadbandAbsolute = None
          DeadbandPercent = Some 1.0
          EngineeringRangeLow = Some 0.0
          EngineeringRangeHigh = Some 10000.0
          QueueSize = Some 10
          Retention = "P90D" }
        { SignalId = sig' "line1.cnc01.motor-temp"
          AcquisitionMode = AcquisitionMode.ChangeOfValue
          SamplingIntervalMs = Some 1000
          PublishingIntervalMs = Some 1000
          DeadbandAbsolute = Some 0.5
          DeadbandPercent = None
          EngineeringRangeLow = None
          EngineeringRangeHigh = None
          QueueSize = Some 10
          Retention = "P90D" }
        { SignalId = sig' "line1.cnc01.cycle-count"
          AcquisitionMode = AcquisitionMode.ChangeOfValue
          SamplingIntervalMs = Some 500
          PublishingIntervalMs = Some 1000
          DeadbandAbsolute = None
          DeadbandPercent = None
          EngineeringRangeLow = None
          EngineeringRangeHigh = None
          QueueSize = Some 10
          Retention = "P365D" }
    ]

// -----------------------------------------------------------------------------
// PM-03 — 스펙 §04-B-2 (InterfaceMODBUS)
// -----------------------------------------------------------------------------

let pm03AssetId = GlobalAssetId "urn:dualsoft:asset:pm03"

let pm03Aid () : AssetInterfacesDescription =
    let aid = AssetInterfacesDescription()
    aid.IdShort <- "AssetInterfacesDescription"
    let ep = { EndpointMetadata.empty with Base = "modbus+tcp://192.168.10.31:502"; UnitId = Some 1uy }
    let interactions = [
        { IdShort = "ActivePower"
          SemanticId = semId "urn:dualsoft:cd:power.active-power/1/0"
          ValueType = XsDouble; Unit = Some "kW"
          Href = "40001?quantity=2"
          Function = ReadHoldingRegisters
          MostSignificantWord = true
          Scale = 0.1; Offset = 0.0
          SignalId = sig' "line1.pm03.active-power" }
    ]
    aid.Interfaces.Add(Modbus (ep, interactions))
    aid

// -----------------------------------------------------------------------------
// VIB-11 — 스펙 §04-B-3 (InterfaceMQTT)
// -----------------------------------------------------------------------------

let vib11AssetId = GlobalAssetId "urn:dualsoft:asset:vib11"

let vib11Aid () : AssetInterfacesDescription =
    let aid = AssetInterfacesDescription()
    aid.IdShort <- "AssetInterfacesDescription"
    let ep = {
        EndpointMetadata.empty with
            Base = "mqtt://broker.plant1.local:1883"
            Security = Some "TLS + 계정 (Vault 참조)"
            AuthReferenceVault = Some "@vault:secret/ds2/adapter/mqtt/vib11#creds"
    }
    let interactions = [
        { IdShort = "VibrationRMS"
          SemanticId = semId "urn:dualsoft:cd:sensor.vibration-rms/1/0"
          ValueType = XsDouble; Unit = Some "mm/s"
          Href = "line1/vib11/data"
          ControlPacket = Subscribe
          Qos = 1
          ContentType = "application/json"
          PayloadPath = "$.rms"
          SignalId = sig' "line1.vib11.rms" }
    ]
    aid.Interfaces.Add(Mqtt (ep, interactions))
    aid

// -----------------------------------------------------------------------------
// VIS-02 — 스펙 §04-B-4 (InterfaceHTTP)
// -----------------------------------------------------------------------------

let vis02AssetId = GlobalAssetId "urn:dualsoft:asset:vis02"

let vis02Aid () : AssetInterfacesDescription =
    let aid = AssetInterfacesDescription()
    aid.IdShort <- "AssetInterfacesDescription"
    let ep = {
        EndpointMetadata.empty with
            Base = "https://qc.plant1.local/api"
            Security = Some "Bearer Token (Vault 참조)"
            AuthReferenceVault = Some "@vault:secret/ds2/adapter/http/vis02#bearer"
    }
    let interactions = [
        { IdShort = "LastJudgement"
          SemanticId = semId "urn:dualsoft:cd:inspection.judgement/1/0"
          ValueType = XsString; Unit = None
          Href = "/v1/results/latest?asset=CNC01"
          Method = Get
          ContentType = "application/json"
          PayloadPath = "$.judgement"
          PollIntervalMs = Some 5000
          SignalId = sig' "line1.vis02.judgement" }
    ]
    aid.Interfaces.Add(Http (ep, interactions))
    aid

// -----------------------------------------------------------------------------
// BCR-05 — 스펙 §04-B-5 (AutoID / OPC 30010)
// -----------------------------------------------------------------------------

let bcr05AssetId = GlobalAssetId "urn:dualsoft:asset:bcr05"

let bcr05Aid () : AssetInterfacesDescription =
    let aid = AssetInterfacesDescription()
    aid.IdShort <- "AssetInterfacesDescription"
    let ep = { EndpointMetadata.empty with Base = "opc.tcp://edge01.plant1.local:4841" }
    let events = [
        { IdShort = "ScanCompleted"
          SemanticId = semId "urn:dualsoft:cd:autoid.scanned-code/1/0"
          EventType = semId "urn:opcfoundation:autoid:OpticalScanEventType"
          SourceNodeHref = "ns=4;s=BCR05.AutoIdDevice"
          PayloadPath = "ScanResult.Code"
          SignalId = sig' "line1.bcr05.code" }
    ]
    aid.Interfaces.Add(OpcUa (ep, [], events))
    aid
