namespace Ds2.Core.StandardSubmodels

open System
open System.Collections.Generic
open Ds2.Core

/// Asset Interfaces Description (IDTA 02017 v1.1).
///
/// AID is the **source of collection configuration** — every adapter reads this
/// submodel to build its polling / subscription / event topology.
///
/// v1.1 officially supports four bindings: OPC UA, Modbus, MQTT, HTTP.
/// BACnet is a v1.2 candidate and lives outside the current scope.
///
/// The DualSoft extension `signalId` (semanticId `urn:dualsoft:cd:ext.signal-id/1/0`)
/// is attached to every InteractionMetadata and is the persistent key used
/// downstream (Kafka partition, InfluxDB tag, Event Store column).
[<AutoOpen>]
module AssetInterfacesDescriptionTypes =

    /// XSD-flavoured value type. Kept as a DU so the F# domain refuses
    /// unknown types at construction time.
    type XsdType =
        | XsDouble
        | XsFloat
        | XsInt
        | XsLong
        | XsUnsignedInt
        | XsUnsignedLong
        | XsBoolean
        | XsString
        | XsDateTime
        | XsByteString

    /// Modbus function code slice used by ADR-002 §2 signal reads/writes.
    type ModbusFunction =
        | ReadHoldingRegisters
        | ReadInputRegisters
        | ReadCoils
        | ReadDiscreteInputs
        | WriteSingleRegister
        | WriteMultipleRegisters

    type MqttControlPacket =
        | Subscribe
        | Publish

    type HttpMethod =
        | Get
        | Post
        | Put
        | Delete

    /// Endpoint metadata — the "connection root" for every binding.
    /// Credentials are Vault references, never inline secrets (ADR-005).
    type EndpointMetadata = {
        /// Connection base URL (e.g. `opc.tcp://host:4840`, `modbus+tcp://host:502`).
        Base: string
        /// Free-form security profile string as it appears in the AID template.
        Security: string option
        /// Modbus slave / unit id (only meaningful for InterfaceMODBUS).
        UnitId: byte option
        /// `@vault:path` reference to the credential material.
        AuthReferenceVault: string option
    }
        with static member empty = {
                    Base = ""
                    Security = None
                    UnitId = None
                    AuthReferenceVault = None
                }

    /// OPC UA interaction (variable-typed data point).
    type OpcUaInteraction = {
        IdShort: string
        SemanticId: SemanticId
        ValueType: XsdType
        Unit: string option
        /// Source-hint href (`ns=…;s=…`). The central UA server's canonical
        /// NodeId is derived deterministically per ADR-002; this field records
        /// the vendor-side address for traceability.
        Href: string
        SignalId: SignalId
    }

    /// Modbus polling data point.
    type ModbusInteraction = {
        IdShort: string
        SemanticId: SemanticId
        ValueType: XsdType
        Unit: string option
        /// Address expression (`40001?quantity=2`).
        Href: string
        Function: ModbusFunction
        /// Two-word register order. `true` = MSW first (big-endian).
        MostSignificantWord: bool
        Scale: float
        Offset: float
        SignalId: SignalId
    }

    /// MQTT-carried data point.
    type MqttInteraction = {
        IdShort: string
        SemanticId: SemanticId
        ValueType: XsdType
        Unit: string option
        /// MQTT topic (or wildcard).
        Href: string
        ControlPacket: MqttControlPacket
        Qos: int
        ContentType: string
        /// JSONPath (per §Phase 4 - Json.Path library) extracting the value.
        PayloadPath: string
        SignalId: SignalId
    }

    /// HTTP-polled or webhook-received data point.
    type HttpInteraction = {
        IdShort: string
        SemanticId: SemanticId
        ValueType: XsdType
        Unit: string option
        /// Path (with query) relative to `EndpointMetadata.Base`.
        Href: string
        Method: HttpMethod
        ContentType: string
        PayloadPath: string
        /// None for webhook-driven; Some ms for polled.
        PollIntervalMs: int option
        SignalId: SignalId
    }

    /// AutoID / event-typed OPC UA interaction (BCR-05, RFID etc.).
    /// ADR-003 §1a: the payload MUST NOT contain any timestamp field —
    /// `sourceTimestamp` is a Method parameter only.
    type AutoIdEventBinding = {
        IdShort: string
        SemanticId: SemanticId
        /// `OpticalScanEventType` or similar (OPC 30010 Companion Spec).
        EventType: SemanticId
        /// EventNotifier node hint on the source side.
        SourceNodeHref: string
        /// JSONPath into the event field carrying the payload's primary value.
        PayloadPath: string
        SignalId: SignalId
    }

    /// One binding block within an AID submodel.
    type AidBinding =
        | OpcUa   of endpoint: EndpointMetadata * interactions: OpcUaInteraction list * events: AutoIdEventBinding list
        | Modbus  of endpoint: EndpointMetadata * interactions: ModbusInteraction list
        | Mqtt    of endpoint: EndpointMetadata * interactions: MqttInteraction list
        | Http    of endpoint: EndpointMetadata * interactions: HttpInteraction list

    /// AAS Submodel "AssetInterfacesDescription" — IDTA 02017 v1.1.
    type AssetInterfacesDescription() =
        member val IdShort = "AssetInterfacesDescription" with get, set
        member val SemanticId : SemanticId =
            SemanticId "https://admin-shell.io/idta/AssetInterfacesDescription/1/1/Submodel"
            with get, set
        member val Interfaces = ResizeArray<AidBinding>() with get, set

        /// Provenance §C — Interaction IdShort 중 KpiWalker 가 auto-generate 한 것들.
        /// Export 시 Qualifier(dualsoft:origin=Auto) 부여; import 시 Qualifier 값으로 복원.
        /// 여기 없는 IdShort 는 사용자가 편집/추가한 것으로 간주 (기본값 User).
        member val AutoOriginIdShorts = HashSet<string>() with get, set

        /// Provenance §C — 사용자가 명시적으로 삭제한 auto-generated IdShort 목록 (tombstones).
        /// Export 시 Submodel Extension(dualsoft:auto-suppressed) 로 직렬화.
        /// KpiWalker 는 이 집합에 있는 IdShort 를 재생성하지 않음.
        member val SuppressedAutoIdShorts = HashSet<string>() with get, set

        static member Empty () = AssetInterfacesDescription()
