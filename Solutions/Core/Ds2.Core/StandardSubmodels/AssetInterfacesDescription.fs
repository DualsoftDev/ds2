namespace Ds2.Core.StandardSubmodels

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
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

    /// IDTA 02017 v1.1의 표준 4종 바인딩에는 LS ELECTRIC XGT 전용 프로토콜이 없다.
    /// 아래 형식은 InterfaceXGT SMC로 직렬화되는 DualSoft 관리 확장이다.
    type XgtCpuModel =
        | Xgi
        | Xgk
        /// XGB/XBC/XEC compact PLC family using the XGT FEnet protocol.
        | Xgb

    type XgtTransport =
        | XgtTcp
        | XgtUdp

    type XgtEndpointMetadata = {
        Base: string
        CpuModel: XgtCpuModel
        LocalEthernet: bool
        NetworkNumber: byte
        StationNumber: byte
        Transport: XgtTransport
        TimeoutMs: int
        ScanIntervalMs: int
        AuthReferenceVault: string option
    }
        with static member empty = {
                    Base = "xgt+tcp://127.0.0.1:2004"
                    CpuModel = Xgi
                    LocalEthernet = true
                    NetworkNumber = 0uy
                    StationNumber = 0xFFuy
                    Transport = XgtTcp
                    TimeoutMs = 3000
                    ScanIntervalMs = 100
                    AuthReferenceVault = None
                }

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
        /// XGT interaction의 공통 metadata 모양은 OPC UA interaction과 동일하며 Href만 XGT 주소를 담는다.
        | Xgt     of endpoint: XgtEndpointMetadata * interactions: OpcUaInteraction list

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

    /// C# Promaker 경계에서 AID InterfaceXGT endpoint를 읽기 위한 평탄화 DTO.
    /// PLC 접속정보의 유일한 정본은 AID다.
    [<AllowNullLiteral>]
    type AidXgtConnectionInfo
        (baseUri: string, vendor: string, ipAddress: string, port: int,
         isUdp: bool, localEthernet: bool, networkNumber: byte, stationNumber: byte,
         timeoutMs: int, scanIntervalMs: int) =
        member _.BaseUri = baseUri
        member _.Vendor = vendor
        member _.IpAddress = ipAddress
        member _.Port = port
        member _.IsUdp = isUdp
        member _.LocalEthernet = localEthernet
        member _.NetworkNumber = networkNumber
        member _.StationNumber = stationNumber
        member _.TimeoutMs = timeoutMs
        member _.ScanIntervalMs = scanIntervalMs

    /// Promaker PLC 설정 ↔ AID InterfaceXGT EndpointMetadata 동기화 경계.
    /// 새 AID 모델은 이 endpoint만 수집 SSOT로 사용한다.
    [<RequireQualifiedAccess>]
    module AidXgtEndpointSettings =
        let private addressHash (address: string) =
            SHA256.HashData(Encoding.UTF8.GetBytes(address))
            |> Array.take 6
            |> Convert.ToHexString

        /// PLC 주소는 `%QX0.1`처럼 AAS idShort/URN에 허용되지 않는 문자를 포함한다.
        /// 주소 원문은 href/signalId에 보존하고 식별자는 충돌 없는 고정 해시로 분리한다.
        let private interactionForAddress (address: string) =
            let hash = addressHash address
            { IdShort = $"Xgt_{hash}"
              SemanticId = SemanticId $"urn:dualsoft:cd:xgt:io:{hash.ToLowerInvariant()}:1:0"
              ValueType = XsBoolean
              Unit = None
              Href = address
              SignalId = SignalId address }

        let private normalizeLegacyGeneratedInteraction (interaction: OpcUaInteraction) =
            // 구버전 자동 생성본은 IdShort/SignalId/Href가 모두 PLC 주소였다. SDF로 먼저 저장된 모델도
            // 이후 AASX로 변환할 수 있도록 그 모양만 안전한 신규 식별자로 마이그레이션한다.
            if String.Equals(interaction.IdShort, interaction.Href, StringComparison.Ordinal)
               && String.Equals(interaction.SignalId.Value, interaction.Href, StringComparison.Ordinal) then
                interactionForAddress interaction.Href
            else interaction

        let private vendorOfCpuModel = function
            | Xgi -> "LsXgi"
            | Xgk -> "LsXgk"
            | Xgb -> "LsXgb"

        let private tryCpuModel (vendor: string) =
            match if isNull vendor then "" else vendor.Trim().ToUpperInvariant() with
            | "LSXGI" -> Some Xgi
            | "LSXGK" -> Some Xgk
            | "LSXGB" -> Some Xgb
            | _ -> None

        [<CompiledName("TryReadFirst")>]
        let tryReadFirst (aid: AssetInterfacesDescription) : AidXgtConnectionInfo =
            if isNull (box aid) then null
            else
                aid.Interfaces
                |> Seq.tryPick (function
                    | Xgt (endpoint, _) ->
                        match Uri.TryCreate(endpoint.Base, UriKind.Absolute) with
                        | true, uri when not (String.IsNullOrWhiteSpace uri.Host) && uri.Port > 0 ->
                            Some (AidXgtConnectionInfo(
                                endpoint.Base,
                                vendorOfCpuModel endpoint.CpuModel,
                                uri.Host,
                                uri.Port,
                                endpoint.Transport = XgtUdp,
                                endpoint.LocalEthernet,
                                endpoint.NetworkNumber,
                                endpoint.StationNumber,
                                endpoint.TimeoutMs,
                                endpoint.ScanIntervalMs))
                        | _ -> None
                    | _ -> None)
                |> Option.defaultValue null

        [<CompiledName("UpdateAll")>]
        let updateAll
            (aid: AssetInterfacesDescription,
             vendor: string,
             ipAddress: string,
             port: int,
             isUdp: bool,
             localEthernet: bool,
             networkNumber: byte,
             stationNumber: byte,
             timeoutMs: int,
             scanIntervalMs: int) : int =
            match tryCpuModel vendor with
            | None -> 0
            | Some _ when isNull (box aid) || String.IsNullOrWhiteSpace ipAddress || port <= 0 -> 0
            | Some cpuModel ->
                let transport = if isUdp then XgtUdp else XgtTcp
                let scheme = if isUdp then "xgt+udp" else "xgt+tcp"
                let baseUri = $"{scheme}://{ipAddress.Trim()}:{port}"
                let mutable updated = 0
                for index = 0 to aid.Interfaces.Count - 1 do
                    match aid.Interfaces.[index] with
                    | Xgt (endpoint, interactions) ->
                        let next =
                            { endpoint with
                                Base = baseUri
                                CpuModel = cpuModel
                                LocalEthernet = localEthernet
                                NetworkNumber = networkNumber
                                StationNumber = stationNumber
                                Transport = transport
                                TimeoutMs = if timeoutMs > 0 then timeoutMs else endpoint.TimeoutMs
                                ScanIntervalMs = if scanIntervalMs > 0 then scanIntervalMs else endpoint.ScanIntervalMs }
                        aid.Interfaces.[index] <- Xgt (next, interactions)
                        updated <- updated + 1
                    | _ -> ()
                updated

        /// XGT 수집 바인딩을 보장한다 — 기존 InterfaceXGT 가 있으면 endpoint 를 갱신하고 새 주소를 병합하며,
        /// 없으면 addresses(모델 IO맵의 OUT/IN + UserTag 주소)로 InteractionMetadata 를 만들어 새로 생성한다.
        /// bcf9121b(PLC 접속=AID 정본) 리팩터가 "갱신"만 두고 "생성"을 빠뜨려, XGT 바인딩이 없던 모델은
        /// PLC IP 를 넣어도 저장 시 바인딩이 안 생기던 구멍을 메운다. 반환 = 생성/갱신된 interaction 수.
        [<CompiledName("EnsureBinding")>]
        let ensureBinding
            (aid: AssetInterfacesDescription,
             vendor: string,
             ipAddress: string,
             port: int,
             isUdp: bool,
             localEthernet: bool,
             networkNumber: byte,
             stationNumber: byte,
             timeoutMs: int,
             scanIntervalMs: int,
             addresses: seq<string>) : int =
            match tryCpuModel vendor with
            | None -> 0
            | Some _ when isNull (box aid) || String.IsNullOrWhiteSpace ipAddress || port <= 0 -> 0
            | Some cpuModel ->
                let normalizedAddresses =
                    let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    addresses
                    |> Seq.choose (fun a ->
                        if String.IsNullOrWhiteSpace a then None
                        else
                            let addr = a.Trim()
                            if seen.Add addr then Some addr else None)
                    |> List.ofSeq

                let transport = if isUdp then XgtUdp else XgtTcp
                let scheme = if isUdp then "xgt+udp" else "xgt+tcp"
                let endpoint =
                    { XgtEndpointMetadata.empty with
                        Base = sprintf "%s://%s:%d" scheme (ipAddress.Trim()) port
                        CpuModel = cpuModel
                        LocalEthernet = localEthernet
                        NetworkNumber = networkNumber
                        StationNumber = stationNumber
                        Transport = transport
                        TimeoutMs = (if timeoutMs > 0 then timeoutMs else 3000)
                        ScanIntervalMs = (if scanIntervalMs > 0 then scanIntervalMs else 100) }

                let mutable touched = 0
                let mutable foundXgt = false
                for index = 0 to aid.Interfaces.Count - 1 do
                    match aid.Interfaces.[index] with
                    | Xgt (existingEndpoint, existing) ->
                        foundXgt <- true
                        let normalizedExisting = existing |> List.map normalizeLegacyGeneratedInteraction
                        let seen = HashSet<string>(
                            normalizedExisting
                            |> Seq.map (fun i -> i.Href),
                            StringComparer.OrdinalIgnoreCase)
                        let added =
                            normalizedAddresses
                            |> List.choose (fun addr ->
                                if seen.Add addr then Some (interactionForAddress addr) else None)
                        let merged = normalizedExisting @ added
                        let nextEndpoint =
                            { existingEndpoint with
                                Base = endpoint.Base
                                CpuModel = endpoint.CpuModel
                                LocalEthernet = endpoint.LocalEthernet
                                NetworkNumber = endpoint.NetworkNumber
                                StationNumber = endpoint.StationNumber
                                Transport = endpoint.Transport
                                TimeoutMs = if timeoutMs > 0 then timeoutMs else existingEndpoint.TimeoutMs
                                ScanIntervalMs = if scanIntervalMs > 0 then scanIntervalMs else existingEndpoint.ScanIntervalMs }
                        aid.Interfaces.[index] <- Xgt (nextEndpoint, merged)
                        // 반환값은 최종 동기화된 interaction 수. 주소가 추가되지 않아도 endpoint 갱신 성공을 드러낸다.
                        touched <- touched + List.length merged
                    | _ -> ()

                if foundXgt then touched
                elif List.isEmpty normalizedAddresses then 0
                else
                    let interactions = normalizedAddresses |> List.map interactionForAddress
                    aid.Interfaces.Add(Xgt (endpoint, interactions))
                    List.length interactions
