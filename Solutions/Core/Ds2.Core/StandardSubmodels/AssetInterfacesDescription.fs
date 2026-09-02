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
        /// Active System that owns this connection.  AID itself stays Project-scoped,
        /// while every southbound endpoint is explicitly scoped to one System.
        /// None means an imported legacy endpoint whose owner has not been assigned yet.
        SystemId: Guid option
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
                    SystemId = None
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
        /// Active System that owns this endpoint.  Kept optional so pre-systemRef
        /// AASX files retain their original semantics on import.
        SystemId: Guid option
        /// Free-form security profile string as it appears in the AID template.
        Security: string option
        /// Modbus slave / unit id (only meaningful for InterfaceMODBUS).
        UnitId: byte option
        /// `@vault:path` reference to the credential material.
        AuthReferenceVault: string option
    }
        with static member empty = {
                    Base = ""
                    SystemId = None
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
         timeoutMs: int, scanIntervalMs: int, systemId: Guid option) =
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
        member _.SystemId = systemId |> Option.toNullable
        /// Source-compatible constructor for integrations that predate systemRef.
        new
            (baseUri: string, vendor: string, ipAddress: string, port: int,
             isUdp: bool, localEthernet: bool, networkNumber: byte, stationNumber: byte,
             timeoutMs: int, scanIntervalMs: int) =
            AidXgtConnectionInfo(
                baseUri, vendor, ipAddress, port, isUdp, localEthernet, networkNumber,
                stationNumber, timeoutMs, scanIntervalMs, None)

    /// Promaker PLC 설정 ↔ AID InterfaceXGT EndpointMetadata 동기화 경계.
    /// 새 AID 모델은 이 endpoint만 수집 SSOT로 사용한다.
    [<RequireQualifiedAccess>]
    module AidXgtEndpointSettings =
        let private addressHash (address: string) =
            SHA256.HashData(Encoding.UTF8.GetBytes(address))
            |> Array.take 6
            |> Convert.ToHexString

        /// System 별 signalId 한정자. **이름이 아니라 GUID 로 만든다** — System 이름은 사용자가 바꿀 수
        /// 있는데 signalId 는 다운스트림 영속 키(OPC UA NodeId·Collector 시계열)라 흔들리면 안 된다.
        let private systemHash (systemId: Guid) =
            SHA256.HashData(systemId.ToByteArray())
            |> Array.take 6
            |> Convert.ToHexString
            |> fun hex -> hex.ToLowerInvariant()

        /// signalId 자동 부여. 기본값은 주소 원문(종전과 완전히 동일)이고,
        /// **그 값을 다른 endpoint 가 이미 쓰고 있을 때만** System 한정자를 붙여 분화한다.
        /// 멀티 PLC 에서 서로 다른 System 이 같은 주소를 써도 사용자가 아무것도 하지 않게 하는 장치다.
        /// (signalId 는 AID 전체에서 유일해야 하며, 예전엔 이 충돌이 곧 활성화 실패였다.)
        /// 한 번 부여된 id 는 이후 저장에서 그대로 보존되므로(ensureBinding 이 기존 interaction 유지)
        /// 이미 배포된 signalId 는 이 규칙으로도 절대 움직이지 않는다.
        let private mintSignalId (claimed: HashSet<string>) (systemId: Guid option) (address: string) =
            if not (claimed.Contains address) then address
            else
                match systemId with
                // 귀속 미상 endpoint 는 분화 근거가 없다 — 종전대로 두고 상위 검증이 드러내게 한다.
                | None -> address
                | Some sid ->
                    let qualified = $"{address}@{systemHash sid}"
                    // 한정자까지 겹치는 건 같은 System 안 중복(모델 오류)이라 여기서 더 손대지 않는다.
                    qualified

        /// PLC 주소는 `%QX0.1`처럼 AAS idShort/URN에 허용되지 않는 문자를 포함한다.
        /// 주소 원문은 href/signalId에 보존하고 식별자는 충돌 없는 고정 해시로 분리한다.
        /// claimed = 이 AID 의 다른 endpoint 들이 이미 점유한 signalId 집합(자동 분화 판정 근거).
        /// 새로 부여한 id 도 집합에 넣어, 같은 배치 안 뒤 주소들이 다시 충돌하지 않게 한다.
        let private interactionForAddressIn (claimed: HashSet<string>) (systemId: Guid option) (address: string) =
            let hash = addressHash address
            let signalId = mintSignalId claimed systemId address
            claimed.Add signalId |> ignore
            { IdShort = $"Xgt_{hash}"
              SemanticId = SemanticId $"urn:dualsoft:cd:xgt:io:{hash.ToLowerInvariant()}:1:0"
              ValueType = XsBoolean
              Unit = None
              Href = address
              SignalId = SignalId signalId }

        /// System 귀속을 모르는 경로(레거시 ensureBinding·구버전 정규화)용 — 종전 동작 그대로.
        let private interactionForAddress (address: string) =
            interactionForAddressIn (HashSet<string>(StringComparer.Ordinal)) None address

        /// 이 AID 안에서 excludeIndex 를 제외한 endpoint 들이 점유한 signalId 집합.
        let private claimedSignalIdsExcept (aid: AssetInterfacesDescription) (excludeIndex: int) =
            let claimed = HashSet<string>(StringComparer.Ordinal)
            aid.Interfaces
            |> Seq.iteri (fun index binding ->
                if index <> excludeIndex then
                    match binding with
                    | Xgt (_, interactions) ->
                        for interaction in interactions do
                            claimed.Add interaction.SignalId.Value |> ignore
                    | _ -> ())
            claimed

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

        let private toConnectionInfo (endpoint: XgtEndpointMetadata) =
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
                    endpoint.ScanIntervalMs,
                    endpoint.SystemId))
            | _ -> None

        [<CompiledName("TryReadFirst")>]
        let tryReadFirst (aid: AssetInterfacesDescription) : AidXgtConnectionInfo =
            if isNull (box aid) then null
            else
                aid.Interfaces
                |> Seq.tryPick (function
                    | Xgt (endpoint, _) -> toConnectionInfo endpoint
                    | _ -> None)
                |> Option.defaultValue null

        /// Reads the XGT endpoint explicitly assigned to one active System.
        /// Legacy endpoints without a systemRef are deliberately excluded: callers
        /// must claim them through EnsureBindingForSystem before a multi-System save.
        [<CompiledName("TryReadForSystem")>]
        let tryReadForSystem (aid: AssetInterfacesDescription, systemId: Guid) : AidXgtConnectionInfo =
            if isNull (box aid) || systemId = Guid.Empty then null
            else
                aid.Interfaces
                |> Seq.tryPick (function
                    | Xgt (endpoint, _) when endpoint.SystemId = Some systemId -> toConnectionInfo endpoint
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

        /// Updates only the InterfaceXGT endpoint assigned to `systemId`.
        /// A single unassigned legacy endpoint is claimed on first update, which
        /// preserves one-System projects while preventing a multi-System save from
        /// stamping one PLC profile across every endpoint.
        [<CompiledName("UpdateForSystem")>]
        let updateForSystem
            (aid: AssetInterfacesDescription,
             systemId: Guid,
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
            | Some _ when isNull (box aid) || systemId = Guid.Empty || String.IsNullOrWhiteSpace ipAddress || port <= 0 -> 0
            | Some cpuModel ->
                let transport = if isUdp then XgtUdp else XgtTcp
                let scheme = if isUdp then "xgt+udp" else "xgt+tcp"
                let baseUri = $"{scheme}://{ipAddress.Trim()}:{port}"
                let xgtBindings =
                    aid.Interfaces
                    |> Seq.mapi (fun index binding -> index, binding)
                    |> Seq.choose (function index, Xgt (endpoint, interactions) -> Some (index, endpoint, interactions) | _ -> None)
                    |> List.ofSeq
                let assigned = xgtBindings |> List.filter (fun (_, endpoint, _) -> endpoint.SystemId = Some systemId)
                let targets =
                    if not assigned.IsEmpty then assigned
                    elif xgtBindings.Length = 1 && (let _, endpoint, _ = xgtBindings.Head in endpoint.SystemId.IsNone) then xgtBindings
                    else []
                for index, endpoint, interactions in targets do
                    let next =
                        { endpoint with
                            SystemId = Some systemId
                            Base = baseUri
                            CpuModel = cpuModel
                            LocalEthernet = localEthernet
                            NetworkNumber = networkNumber
                            StationNumber = stationNumber
                            Transport = transport
                            TimeoutMs = if timeoutMs > 0 then timeoutMs else endpoint.TimeoutMs
                            ScanIntervalMs = if scanIntervalMs > 0 then scanIntervalMs else endpoint.ScanIntervalMs }
                    aid.Interfaces.[index] <- Xgt (next, interactions)
                targets.Length

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

        /// Ensures a distinct XGT binding for one active System.
        /// Existing bindings for other systems are intentionally untouched.  A
        /// one-endpoint legacy AID is upgraded in place; otherwise a new endpoint
        /// is appended for the selected System.
        [<CompiledName("EnsureBindingForSystem")>]
        let ensureBindingForSystem
            (aid: AssetInterfacesDescription,
             systemId: Guid,
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
            | Some _ when isNull (box aid) || systemId = Guid.Empty || String.IsNullOrWhiteSpace ipAddress || port <= 0 -> 0
            | Some cpuModel ->
                let normalizedAddresses =
                    let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    addresses
                    |> Seq.choose (fun address ->
                        if String.IsNullOrWhiteSpace address then None
                        else
                            let trimmed = address.Trim()
                            if seen.Add trimmed then Some trimmed else None)
                    |> List.ofSeq
                let transport = if isUdp then XgtUdp else XgtTcp
                let scheme = if isUdp then "xgt+udp" else "xgt+tcp"
                let requestedEndpoint =
                    { XgtEndpointMetadata.empty with
                        SystemId = Some systemId
                        Base = sprintf "%s://%s:%d" scheme (ipAddress.Trim()) port
                        CpuModel = cpuModel
                        LocalEthernet = localEthernet
                        NetworkNumber = networkNumber
                        StationNumber = stationNumber
                        Transport = transport
                        TimeoutMs = (if timeoutMs > 0 then timeoutMs else 3000)
                        ScanIntervalMs = (if scanIntervalMs > 0 then scanIntervalMs else 100) }
                let xgtBindings =
                    aid.Interfaces
                    |> Seq.mapi (fun index binding -> index, binding)
                    |> Seq.choose (function index, Xgt (endpoint, interactions) -> Some (index, endpoint, interactions) | _ -> None)
                    |> List.ofSeq
                let target =
                    xgtBindings
                    |> List.tryFind (fun (_, endpoint, _) -> endpoint.SystemId = Some systemId)
                    |> Option.orElseWith (fun () ->
                        if xgtBindings.Length = 1 && (let _, endpoint, _ = xgtBindings.Head in endpoint.SystemId.IsNone)
                        then Some xgtBindings.Head
                        else None)
                match target with
                | Some (index, existingEndpoint, existing) ->
                    let normalizedExisting = existing |> List.map normalizeLegacyGeneratedInteraction
                    let seen = HashSet<string>(normalizedExisting |> Seq.map _.Href, StringComparer.OrdinalIgnoreCase)
                    // 다른 System 의 endpoint 가 이미 쓰는 signalId 는 피해서 부여한다(자동 분화).
                    // 이 endpoint 자신이 이미 가진 id 는 보존 대상이라 제외한다.
                    let claimed = claimedSignalIdsExcept aid index
                    let added =
                        normalizedAddresses
                        |> List.choose (fun address ->
                            if seen.Add address then Some (interactionForAddressIn claimed (Some systemId) address)
                            else None)
                    let nextEndpoint =
                        { existingEndpoint with
                            SystemId = Some systemId
                            Base = requestedEndpoint.Base
                            CpuModel = requestedEndpoint.CpuModel
                            LocalEthernet = requestedEndpoint.LocalEthernet
                            NetworkNumber = requestedEndpoint.NetworkNumber
                            StationNumber = requestedEndpoint.StationNumber
                            Transport = requestedEndpoint.Transport
                            TimeoutMs = if timeoutMs > 0 then timeoutMs else existingEndpoint.TimeoutMs
                            ScanIntervalMs = if scanIntervalMs > 0 then scanIntervalMs else existingEndpoint.ScanIntervalMs }
                    let merged = normalizedExisting @ added
                    aid.Interfaces.[index] <- Xgt (nextEndpoint, merged)
                    List.length merged
                | None when normalizedAddresses.IsEmpty -> 0
                | None ->
                    // 새 endpoint — 기존 endpoint 전체가 점유한 signalId 를 피해서 부여한다.
                    let claimed = claimedSignalIdsExcept aid -1
                    let interactions =
                        normalizedAddresses |> List.map (interactionForAddressIn claimed (Some systemId))
                    aid.Interfaces.Add(Xgt (requestedEndpoint, interactions))
                    List.length interactions

        /// 이미 저장된 모델의 signalId 중복·공백 **자동 복구**.
        /// 중복 주소 모델은 AASX 저장 자체는 성공하고 불러와 활성화할 때만 실패하므로,
        /// 현장에 "저장은 됐는데 안 뜨는" 파일이 이미 존재할 수 있다. 그런 파일도 사용자가
        /// 아무것도 하지 않고 뜨도록, 불러오는 시점에 같은 규칙(주소@System해시)으로 분화시킨다.
        /// 빈 signalId 도 같은 이유로 여기서 재발급한다 — SignalId struct 의 STJ 역직렬화 버그
        /// (JsonConstructor 부재, 6421d525 에서 수정) 이전 빌드가 SDF 재저장 시 전량 "" 로
        /// 박제한 파일이 현장에 존재한다. 빈 id 는 다운스트림 영속 키로 쓰인 적이 없으니 안전하다.
        /// 그 모델은 지금 아예 활성화가 안 되는 상태라 깨질 다운스트림이 존재하지 않는다 — 안전하다.
        /// 앞선 endpoint 가 선점한 id 는 그대로 두고 뒤에 오는 중복만 바꾼다(기존 id 보존 우선).
        /// 반환 = 바뀐 interaction 수(0 이면 손댈 것이 없었음).
        [<CompiledName("DeduplicateSignalIds")>]
        let deduplicateSignalIds (aid: AssetInterfacesDescription) : int =
            if isNull (box aid) then 0
            else
                let claimed = HashSet<string>(StringComparer.Ordinal)
                let mutable repaired = 0
                for index = 0 to aid.Interfaces.Count - 1 do
                    match aid.Interfaces.[index] with
                    | Xgt (endpoint, interactions) ->
                        let mutable changedHere = false
                        let next =
                            interactions
                            |> List.map (fun interaction ->
                                let current = interaction.SignalId.Value
                                // 빈 id 는 점유(claimed)로 치지 않고 무조건 재발급 대상이다.
                                if current <> "" && claimed.Add current then interaction
                                else
                                    let minted = mintSignalId claimed endpoint.SystemId interaction.Href
                                    if minted = current || not (claimed.Add minted) then interaction
                                    else
                                        changedHere <- true
                                        repaired <- repaired + 1
                                        { interaction with SignalId = SignalId minted })
                        if changedHere then aid.Interfaces.[index] <- Xgt (endpoint, next)
                    | _ -> ()
                repaired
