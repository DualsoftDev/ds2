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

    /// XGT 접속 매체. TCP/UDP 는 이더넷(FEnet·내장 이더넷)이고 USB 는 CPU 전면 USB 로더 포트다.
    /// USB 는 host/port 가 없어 `Base` 가 장치 선택 키를 담는다 — XgtEndpointBase 참조.
    type XgtTransport =
        | XgtTcp
        | XgtUdp
        | XgtUsb

    /// InterfaceXGT `base` URI 의 조립·해석 SSOT.
    ///
    ///   이더넷: `xgt+tcp://host:port` · `xgt+udp://host:port`
    ///   USB   : `xgt+usb://localhost/<selector>` — selector 는 dsev2 LS USB 로더의 장치 선택 키
    ///           (목록번호 · serial · bus:addr · product 부분일치). 비우면 첫 매칭 장치.
    ///
    /// selector 를 host 자리에 두지 않는 이유: `3:4` 같은 bus:addr 가 host:port 로 갈린다.
    /// host 를 `localhost` 로 고정하는 이유: 스캔하는 프로세스(Agent·Edge 수집기)가 붙어 있는
    /// 그 PC 의 USB 라는 뜻이고, 표준 URI 파서가 authority 없는 형태보다 안정적으로 읽는다.
    [<RequireQualifiedAccess>]
    module XgtEndpointBase =
        [<Literal>]
        let UsbHost = "localhost"
        [<Literal>]
        let DefaultPort = 2004
        [<Literal>]
        let private Label = "InterfaceXGT"

        let schemeOf (transport: XgtTransport) =
            match transport with
            | XgtTcp -> "xgt+tcp"
            | XgtUdp -> "xgt+udp"
            | XgtUsb -> "xgt+usb"

        /// AASX Property `transport` 와 C# 경계(Promaker·DSPilot·수집기 payload)가 공유하는 라벨.
        let transportLabel (transport: XgtTransport) =
            match transport with
            | XgtTcp -> "tcp"
            | XgtUdp -> "udp"
            | XgtUsb -> PlcEndpointLabel.UsbTransport

        let tryTransportOfLabel (label: string) =
            match (if isNull label then "" else label.Trim().ToLowerInvariant()) with
            | "tcp" -> Some XgtTcp
            | "udp" -> Some XgtUdp
            | "usb" -> Some XgtUsb
            | _ -> None

        /// base 를 해석한 접속 대상.
        type XgtEndpointTarget =
            | Ethernet of host: string * port: int
            | Usb of selector: string

        let ethernet (transport: XgtTransport) (host: string) (port: int) =
            AidEndpointBase.hostPort (schemeOf transport) host port

        /// USB base 조립. selector 에 URI 경계 문자(`/` `?` `#`)가 있으면 path 로 실을 수 없어 거절한다 —
        /// dsev2 의 선택 키(번호·serial·bus:addr·product 부분일치)에는 원래 들어가지 않는 문자다.
        let tryUsb (selector: string) : Result<string, string> =
            let key = if isNull selector then "" else selector.Trim()
            if key |> Seq.exists (fun c -> c = '/' || c = '?' || c = '#') then
                Error(sprintf "%s USB 장치 선택 키에는 '/', '?', '#' 을 쓸 수 없습니다 — '%s'." Label key)
            else
                Ok(sprintf "%s://%s/%s" (schemeOf XgtUsb) UsbHost key)

        let tryParse (transport: XgtTransport) (value: string) : Result<XgtEndpointTarget, string> =
            match transport with
            | XgtUsb ->
                AidEndpointBase.tryAbsoluteUri Label (schemeOf XgtUsb) value
                |> Result.map (fun uri -> Usb(Uri.UnescapeDataString(uri.AbsolutePath.TrimStart '/')))
            | XgtTcp
            | XgtUdp ->
                AidEndpointBase.tryParseHostPort Label (schemeOf transport) DefaultPort value
                |> Result.map (fun (host, port) -> Ethernet(host, port))

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

    /// MICREX-SX(Fuji SPH) 로더 프로토콜 endpoint. InterfaceXGT 와 형제인 DualSoft 관리 확장이다 —
    /// IDTA 02017 v1.1 의 표준 4종 바인딩(OPC UA·Modbus·MQTT·HTTP)에는 로더 프로토콜이 없다.
    ///
    /// XGT endpoint 를 재사용하지 않는 이유가 둘 있다.
    /// * `XgtCpuModel` 은 `Xgi|Xgk|Xgb` 닫힌 DU 이고 base 스킴이 `xgt+…://` 계열이다 — SX 를 거기
    ///   넣으면 AASX 의 규격 서술이 사실과 어긋난다.
    /// * SX 에는 XGT 에 없는 두 값이 필요하다. 매핑표와 쓰기 허용 영역이다.
    type MicrexSxEndpointMetadata = {
        /// `sx+tcp://host:509`. 509 = 로더 인터페이스 서버(권장), 507 = 로더 명령 서버.
        Base: string
        /// 이 접속을 소유한 active System.
        SystemId: Guid option
        /// D300win 프로젝트에서 뽑은 I/O 매핑표 경로. None 이면 네이티브 주소만 쓴다 —
        /// IEC 원격 주소(`%QX5.43.0.04`)는 어느 국번이 이미지 몇 번째 워드에 실리는지를
        /// 프로토콜로 물어볼 수 없어 이 표가 있어야 해석된다.
        IoMapPath: string option
        /// 쓰기를 허용할 영역("M1"·"IO"·"M10"). **빈 목록이면 읽기 전용이다** —
        /// SX 커넥터가 쓰기 권한 발급 자체를 거부한다. 기본을 잠금으로 두는 이유:
        /// 현장 PLC 의 메모리 배치는 설비마다 다르고 잘못 쓰면 설비를 오동작시킨다.
        WritableAreas: string list
        TimeoutMs: int
        ScanIntervalMs: int
    }
        with static member empty = {
                    Base = "sx+tcp://127.0.0.1:509"
                    SystemId = None
                    IoMapPath = None
                    WritableAreas = []
                    TimeoutMs = 3000
                    ScanIntervalMs = 100
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
        /// MICREX-SX interaction 의 metadata 모양도 XGT 와 같고 Href 가 SX 주소를 담는다
        /// (네이티브 `M1.2000.0` 또는 IEC `%QX3.31.0.00`).
        | MicrexSx of endpoint: MicrexSxEndpointMetadata * interactions: OpcUaInteraction list

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

    /// C# 경계(Promaker·DSPilot)가 AID InterfaceXGT endpoint 를 **읽고 쓸 때** 함께 쓰는 평탄화 DTO.
    ///
    /// 읽기 결과와 쓰기 요청이 같은 축(벤더·전송·주소·타이밍)이라 한 타입을 양방향으로 쓴다 —
    /// `BaseUri`/`SystemId` 는 읽기에서만 채워지고 쓰기 요청에서는 무시된다(base 는 재조립, 소유
    /// System 은 인자). `Transport` 는 XgtEndpointBase.transportLabel 의 "tcp"|"udp"|"usb".
    /// USB 면 IpAddress=""·Port=0 이고 `UsbDeviceSelector` 가 장치 선택 키다.
    /// PLC 접속정보의 유일한 정본은 AID다.
    [<AllowNullLiteral>]
    type AidXgtConnectionInfo
        (baseUri: string, vendor: string, transport: string, ipAddress: string, port: int,
         usbDeviceSelector: string, localEthernet: bool, networkNumber: byte, stationNumber: byte,
         timeoutMs: int, scanIntervalMs: int, systemId: Guid option) =
        member _.BaseUri = baseUri
        member _.Vendor = vendor
        member _.Transport = transport
        member _.IsUsb = PlcEndpointLabel.isUsb transport
        member _.IpAddress = ipAddress
        member _.Port = port
        member _.UsbDeviceSelector = if isNull usbDeviceSelector then "" else usbDeviceSelector
        member _.LocalEthernet = localEthernet
        member _.NetworkNumber = networkNumber
        member _.StationNumber = stationNumber
        member _.TimeoutMs = timeoutMs
        member _.ScanIntervalMs = scanIntervalMs
        member _.SystemId = systemId |> Option.toNullable
        /// 사람이 읽는 접속 표기 — 로그·상태바·배너가 같은 문자열을 쓴다.
        member this.EndpointLabel = PlcEndpointLabel.format transport ipAddress port this.UsbDeviceSelector

    /// C# 경계에서 AID InterfaceMicrexSx endpoint 를 읽기 위한 평탄화 DTO.
    [<AllowNullLiteral>]
    type AidMicrexSxConnectionInfo
        (baseUri: string, ipAddress: string, port: int, ioMapPath: string,
         writableAreas: string array, timeoutMs: int, scanIntervalMs: int, systemId: Guid option) =
        member _.BaseUri = baseUri
        member _.IpAddress = ipAddress
        member _.Port = port
        /// 빈 문자열 = 매핑표 없음(네이티브 주소만).
        member _.IoMapPath = ioMapPath
        /// 빈 배열 = 읽기 전용.
        member _.WritableAreas = writableAreas
        member _.TimeoutMs = timeoutMs
        member _.ScanIntervalMs = scanIntervalMs
        member _.SystemId = systemId |> Option.toNullable

    /// XGT·MICREX-SX endpoint 가 공유하는 interaction 식별자 규칙.
    ///
    /// signalId 는 AID **전체**에서 유일해야 한다(OPC UA NodeId·Collector 시계열의 영속 키).
    /// 두 바인딩 종류가 한 AID 에 공존하므로 점유 집합도 둘을 함께 세야 한다 — 한쪽만 세면
    /// 서로의 signalId 를 덮어써 활성화가 깨진다.
    module internal AidInteractionIds =
        let addressHash (address: string) =
            SHA256.HashData(Encoding.UTF8.GetBytes(address))
            |> Array.take 6
            |> Convert.ToHexString

        /// System 별 signalId 한정자. **이름이 아니라 GUID 로 만든다** — System 이름은 사용자가 바꿀 수
        /// 있는데 signalId 는 다운스트림 영속 키라 흔들리면 안 된다.
        let systemHash (systemId: Guid) =
            SHA256.HashData(systemId.ToByteArray())
            |> Array.take 6
            |> Convert.ToHexString
            |> fun hex -> hex.ToLowerInvariant()

        /// signalId 자동 부여. 기본값은 주소 원문이고, **그 값을 다른 endpoint 가 이미 쓰고 있을
        /// 때만** System 한정자를 붙여 분화한다.
        let mintSignalId (claimed: HashSet<string>) (systemId: Guid option) (address: string) =
            if not (claimed.Contains address) then address
            else
                match systemId with
                | None -> address
                | Some sid -> $"{address}@{systemHash sid}"

        /// 이 AID 안에서 excludeIndex 를 제외한 **모든** endpoint 가 점유한 signalId 집합.
        let claimedSignalIdsExcept (aid: AssetInterfacesDescription) (excludeIndex: int) =
            let claimed = HashSet<string>(StringComparer.Ordinal)
            aid.Interfaces
            |> Seq.iteri (fun index binding ->
                if index <> excludeIndex then
                    match binding with
                    | Xgt (_, interactions)
                    | MicrexSx (_, interactions) ->
                        for interaction in interactions do
                            claimed.Add interaction.SignalId.Value |> ignore
                    | _ -> ())
            claimed

    /// Promaker PLC 설정 ↔ AID InterfaceXGT EndpointMetadata 동기화 경계.
    /// 새 AID 모델은 이 endpoint만 수집 SSOT로 사용한다.
    [<RequireQualifiedAccess>]
    module AidXgtEndpointSettings =
        // 식별자 규칙은 AidInteractionIds 가 SSOT 다 — MICREX-SX 바인딩과 같은 규칙을 쓰고,
        // signalId 점유 집합도 두 바인딩을 함께 센다(한쪽만 세면 서로를 덮어쓴다).
        let private addressHash = AidInteractionIds.addressHash
        let private mintSignalId = AidInteractionIds.mintSignalId

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

        let private claimedSignalIdsExcept = AidInteractionIds.claimedSignalIdsExcept

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

        /// endpoint → C# 경계 DTO. base 해석은 게이트웨이 조립(Ds2.Backend.Plc)과 같은 파서를 쓴다 —
        /// 여기서 읽히는 값은 반드시 활성화도 되어야 한다.
        let private toConnectionInfo (endpoint: XgtEndpointMetadata) =
            match XgtEndpointBase.tryParse endpoint.Transport endpoint.Base with
            | Error _ -> None
            | Ok target ->
                let ipAddress, port, selector =
                    match target with
                    | XgtEndpointBase.Ethernet (host, port) -> host, port, ""
                    | XgtEndpointBase.Usb selector -> "", 0, selector
                Some (AidXgtConnectionInfo(
                    endpoint.Base,
                    vendorOfCpuModel endpoint.CpuModel,
                    XgtEndpointBase.transportLabel endpoint.Transport,
                    ipAddress,
                    port,
                    selector,
                    endpoint.LocalEthernet,
                    endpoint.NetworkNumber,
                    endpoint.StationNumber,
                    endpoint.TimeoutMs,
                    endpoint.ScanIntervalMs,
                    endpoint.SystemId))

        /// C# 경계 요청 → 이 System 이 소유할 endpoint 값.
        /// 벤더가 LS 가 아니거나, 전송 라벨을 모르거나, 이더넷인데 host/port 가 비었거나, USB selector 에
        /// URI 경계 문자가 있으면 None — 호출자는 0(변경 없음)을 돌려준다.
        /// `existing` 이 있으면 그 endpoint 의 AuthReferenceVault 와 (요청이 0 인) 타이밍을 물려받는다.
        let private tryEndpointOf
            (systemId: Guid) (existing: XgtEndpointMetadata option) (request: AidXgtConnectionInfo) =
            if isNull (box request) then None
            else
                match tryCpuModel request.Vendor, XgtEndpointBase.tryTransportOfLabel request.Transport with
                | Some cpuModel, Some transport ->
                    let baseUri =
                        match transport with
                        | XgtUsb -> XgtEndpointBase.tryUsb request.UsbDeviceSelector |> Result.toOption
                        | XgtTcp
                        | XgtUdp ->
                            if String.IsNullOrWhiteSpace request.IpAddress || request.Port <= 0 then None
                            else Some (XgtEndpointBase.ethernet transport request.IpAddress request.Port)
                    baseUri
                    |> Option.map (fun b ->
                        let prior = defaultArg existing XgtEndpointMetadata.empty
                        { prior with
                            SystemId = Some systemId
                            Base = b
                            CpuModel = cpuModel
                            Transport = transport
                            LocalEthernet = request.LocalEthernet
                            NetworkNumber = request.NetworkNumber
                            StationNumber = request.StationNumber
                            TimeoutMs = if request.TimeoutMs > 0 then request.TimeoutMs else prior.TimeoutMs
                            ScanIntervalMs = if request.ScanIntervalMs > 0 then request.ScanIntervalMs else prior.ScanIntervalMs })
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

        /// 한 active System 의 XGT 수집 바인딩을 보장한다 — 이 System 에 배정된 endpoint 가 있으면 요청 값으로
        /// 갱신하고 새 주소를 병합하며, 없으면 addresses(모델 IO맵 OUT/IN + UserTag 주소)로 InteractionMetadata 를
        /// 만들어 새로 만든다. systemRef 없는 구버전 endpoint 가 **하나만** 있으면 그것을 이 System 으로 귀속(claim)
        /// 한다 — 단일 System 프로젝트는 그대로 살리고, 다중 System 저장이 한 PLC 프로파일을 모든 endpoint 에
        /// 찍는 것은 막는다. 다른 System 의 바인딩은 건드리지 않는다.
        ///
        /// 요청은 읽기 DTO 와 같은 AidXgtConnectionInfo 다 — 접속 축(이더넷 host:port / USB selector)의 검증과
        /// base 조립은 tryEndpointOf 한 곳에서만 한다. 반환 = 최종 동기화된 interaction 수(0 = 변경 없음).
        [<CompiledName("EnsureBindingForSystem")>]
        let ensureBindingForSystem
            (aid: AssetInterfacesDescription,
             systemId: Guid,
             request: AidXgtConnectionInfo,
             addresses: seq<string>) : int =
            if isNull (box aid) || systemId = Guid.Empty then 0
            else
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
                match tryEndpointOf systemId (target |> Option.map (fun (_, endpoint, _) -> endpoint)) request with
                | None -> 0
                | Some nextEndpoint ->
                    let normalizedAddresses =
                        let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        addresses
                        |> Seq.choose (fun address ->
                            if String.IsNullOrWhiteSpace address then None
                            else
                                let trimmed = address.Trim()
                                if seen.Add trimmed then Some trimmed else None)
                        |> List.ofSeq
                    match target with
                    | Some (index, _, existing) ->
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
                        let merged = normalizedExisting @ added
                        aid.Interfaces.[index] <- Xgt (nextEndpoint, merged)
                        // 반환값은 최종 동기화된 interaction 수. 주소가 추가되지 않아도 endpoint 갱신 성공을 드러낸다.
                        List.length merged
                    | None when normalizedAddresses.IsEmpty -> 0
                    | None ->
                        // 새 endpoint — 기존 endpoint 전체가 점유한 signalId 를 피해서 부여한다.
                        let claimed = claimedSignalIdsExcept aid -1
                        let interactions =
                            normalizedAddresses |> List.map (interactionForAddressIn claimed (Some systemId))
                        aid.Interfaces.Add(Xgt (nextEndpoint, interactions))
                        List.length interactions

        /// 주소별 표시 단위(engineering unit)를 이 System 의 XGT interaction 에 심는다.
        /// 단위는 값 자체가 아니라 "그 값을 어떻게 읽어야 하는가" 라서 신호 정의(interaction)의 자리이며,
        /// AASX 로 export/import 될 때 `unit` Property 로 왕복한다(SignalUaMetadata.Unit 계약).
        ///
        /// 태그 모니터링 화면이 값 옆에 bar·A·℃ 를 붙이는 근거가 이것이다. 빈 문자열/공백은 "단위 없음"
        /// 으로 보고 지운다 — 사용자가 칸을 비우면 지워지는 것이 자연스럽다.
        /// 사전에 없는 주소의 interaction 은 건드리지 않는다. 반환 = 값이 실제로 바뀐 interaction 수.
        [<CompiledName("SetUnitsForSystem")>]
        let setUnitsForSystem
            (aid: AssetInterfacesDescription,
             systemId: Guid,
             unitsByAddress: IDictionary<string, string>) : int =
            if isNull (box aid) || systemId = Guid.Empty || isNull (box unitsByAddress) then 0
            else
                let lookup = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                for kv in unitsByAddress do
                    if not (String.IsNullOrWhiteSpace kv.Key) then
                        lookup.[kv.Key.Trim()] <- (if isNull kv.Value then "" else kv.Value.Trim())
                if lookup.Count = 0 then 0
                else
                    let mutable changed = 0
                    for index in 0 .. aid.Interfaces.Count - 1 do
                        match aid.Interfaces.[index] with
                        | Xgt (endpoint, interactions) when endpoint.SystemId = Some systemId ->
                            let mutable touched = 0
                            let next =
                                interactions
                                |> List.map (fun interaction ->
                                    match lookup.TryGetValue interaction.Href with
                                    | true, unit ->
                                        let nextUnit = if String.IsNullOrWhiteSpace unit then None else Some unit
                                        if nextUnit = interaction.Unit then interaction
                                        else
                                            touched <- touched + 1
                                            { interaction with Unit = nextUnit }
                                    | _ -> interaction)
                            if touched > 0 then
                                aid.Interfaces.[index] <- Xgt (endpoint, next)
                                changed <- changed + touched
                        | _ -> ()
                    changed

        /// 이 System 의 주소 → 단위 표. <see cref="setUnitsForSystem"/> 의 역연산이며 편집기가 저장된 값을 되읽는다.
        /// 단위가 없는 주소는 담지 않는다.
        [<CompiledName("UnitsByAddressForSystem")>]
        let unitsByAddressForSystem
            (aid: AssetInterfacesDescription, systemId: Guid) : IDictionary<string, string> =
            let result = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            if isNull (box aid) || systemId = Guid.Empty then result :> IDictionary<string, string>
            else
                for binding in aid.Interfaces do
                    match binding with
                    | Xgt (endpoint, interactions) when endpoint.SystemId = Some systemId ->
                        for interaction in interactions do
                            match interaction.Unit with
                            | Some unit when not (String.IsNullOrWhiteSpace interaction.Href) ->
                                result.[interaction.Href] <- unit
                            | _ -> ()
                    | _ -> ()
                result :> IDictionary<string, string>

        /// 이 System 의 주소 → signalId 표. SignalPolicy(수집 정책)가 signalId 로 키를 잡으므로,
        /// 주소로 정책을 걸려면 이 다리를 건너야 한다. 주소를 모르는 호출자는 빈 표를 받는다.
        [<CompiledName("SignalIdsByAddressForSystem")>]
        let signalIdsByAddressForSystem
            (aid: AssetInterfacesDescription, systemId: Guid) : IDictionary<string, string> =
            let result = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            if isNull (box aid) || systemId = Guid.Empty then result :> IDictionary<string, string>
            else
                for binding in aid.Interfaces do
                    match binding with
                    | Xgt (endpoint, interactions) when endpoint.SystemId = Some systemId ->
                        for interaction in interactions do
                            if not (String.IsNullOrWhiteSpace interaction.Href)
                               && not (String.IsNullOrWhiteSpace interaction.SignalId.Value) then
                                result.[interaction.Href] <- interaction.SignalId.Value
                    | _ -> ()
                result :> IDictionary<string, string>

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
        /// 이 System 의 XGT 바인딩을 지운다 — 벤더를 LS 에서 다른 종류로 바꿨을 때 옛 endpoint 가
        /// 남으면 Agent 가 AID 에서 연결을 하나 더 만든다(현장에서 "1대만 설정했는데 2대" 로 나타났다).
        /// 반환값은 지운 개수.
        [<CompiledName("RemoveForSystem")>]
        let removeForSystem (aid: AssetInterfacesDescription, systemId: Guid) : int =
            if isNull (box aid) || systemId = Guid.Empty then 0
            else
                let doomed =
                    aid.Interfaces
                    |> Seq.indexed
                    |> Seq.choose (function
                        | index, Xgt (endpoint, _) when endpoint.SystemId = Some systemId -> Some index
                        | _ -> None)
                    |> List.ofSeq
                for index in List.rev doomed do aid.Interfaces.RemoveAt index
                List.length doomed

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

    /// Promaker PLC 설정 ↔ AID InterfaceMicrexSx endpoint 동기화 경계.
    ///
    /// XGT 쪽(`AidXgtEndpointSettings`)이 품고 있는 레거시 경로 — systemRef 없는 무주인
    /// endpoint claim, 구버전 자동생성 interaction 정규화 — 는 여기 없다. SX 바인딩은 신규라
    /// 그런 파일이 존재하지 않는다. 없는 마이그레이션을 흉내내면 검증할 수 없는 코드가 된다.
    [<RequireQualifiedAccess>]
    module AidMicrexSxEndpointSettings =

        [<Literal>]
        let private Label = "InterfaceMicrexSx"
        /// 로더 프로토콜 스킴. XGT 와 다르게 두어 AASX 를 읽는 쪽이 프로토콜을 착각하지 않게 한다.
        [<Literal>]
        let Scheme = "sx+tcp"
        /// 509 = 로더 인터페이스 서버(권장). 507 = 로더 명령 서버.
        [<Literal>]
        let DefaultPort = 509

        let private baseUriOf (ipAddress: string) (port: int) =
            AidEndpointBase.hostPort Scheme ipAddress port

        let private interactionForAddressIn (claimed: HashSet<string>) (systemId: Guid option) (address: string) =
            let hash = AidInteractionIds.addressHash address
            let signalId = AidInteractionIds.mintSignalId claimed systemId address
            claimed.Add signalId |> ignore
            { IdShort = $"Sx_{hash}"
              SemanticId = SemanticId $"urn:dualsoft:cd:micrexsx:io:{hash.ToLowerInvariant()}:1:0"
              ValueType = XsBoolean
              Unit = None
              Href = address
              SignalId = SignalId signalId }

        /// endpoint → C# 경계 DTO. 게이트웨이 조립과 같은 파서·기본 포트를 쓴다.
        let private toConnectionInfo (endpoint: MicrexSxEndpointMetadata) =
            match AidEndpointBase.tryParseHostPort Label Scheme DefaultPort endpoint.Base with
            | Error _ -> None
            | Ok (host, port) ->
                Some (AidMicrexSxConnectionInfo(
                    endpoint.Base,
                    host,
                    port,
                    defaultArg endpoint.IoMapPath "",
                    endpoint.WritableAreas |> List.toArray,
                    endpoint.TimeoutMs,
                    endpoint.ScanIntervalMs,
                    endpoint.SystemId))

        /// 지정 System 이 소유한 SX endpoint 를 읽는다. 없으면 null.
        [<CompiledName("TryReadForSystem")>]
        let tryReadForSystem (aid: AssetInterfacesDescription, systemId: Guid) : AidMicrexSxConnectionInfo =
            if isNull (box aid) || systemId = Guid.Empty then null
            else
                aid.Interfaces
                |> Seq.tryPick (function
                    | MicrexSx (endpoint, _) when endpoint.SystemId = Some systemId -> toConnectionInfo endpoint
                    | _ -> None)
                |> Option.defaultValue null

        /// 지정 System 의 SX 바인딩을 보장한다 — 없으면 주소 목록으로 만들고, 있으면 endpoint 를
        /// 갱신하고 새 주소만 병합한다(기존 interaction 의 signalId 는 보존 — 다운스트림 영속 키다).
        /// 반환값은 만들거나 고친 요소 수. 입력이 성립하지 않으면 0 이고 AID 를 건드리지 않는다.
        [<CompiledName("EnsureBindingForSystem")>]
        let ensureBindingForSystem
            (aid: AssetInterfacesDescription,
             systemId: Guid,
             ipAddress: string,
             port: int,
             ioMapPath: string,
             writableAreas: seq<string>,
             timeoutMs: int,
             scanIntervalMs: int,
             addresses: seq<string>) : int =
            if isNull (box aid) || systemId = Guid.Empty
               || String.IsNullOrWhiteSpace ipAddress || port <= 0 then 0
            else
                let normalizedAddresses =
                    let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    addresses
                    |> Seq.filter (fun a -> not (String.IsNullOrWhiteSpace a))
                    |> Seq.map (fun a -> a.Trim())
                    |> Seq.filter seen.Add
                    |> List.ofSeq

                let endpointOf systemIdOpt = {
                    Base = baseUriOf ipAddress port
                    SystemId = systemIdOpt
                    IoMapPath = if String.IsNullOrWhiteSpace ioMapPath then None else Some (ioMapPath.Trim())
                    WritableAreas =
                        writableAreas
                        |> Seq.filter (fun a -> not (String.IsNullOrWhiteSpace a))
                        |> Seq.map (fun a -> a.Trim())
                        |> Seq.distinct
                        |> List.ofSeq
                    TimeoutMs = timeoutMs
                    ScanIntervalMs = scanIntervalMs
                }

                let existing =
                    aid.Interfaces
                    |> Seq.indexed
                    |> Seq.tryPick (function
                        | index, MicrexSx (endpoint, interactions) when endpoint.SystemId = Some systemId ->
                            Some (index, endpoint, interactions)
                        | _ -> None)

                match existing with
                | Some (index, _, interactions) ->
                    let claimed = AidInteractionIds.claimedSignalIdsExcept aid index
                    for interaction in interactions do
                        claimed.Add interaction.SignalId.Value |> ignore
                    let known =
                        HashSet<string>(
                            interactions |> List.map (fun i -> i.Href),
                            StringComparer.OrdinalIgnoreCase)
                    let added =
                        normalizedAddresses
                        |> List.filter (fun a -> not (known.Contains a))
                        |> List.map (interactionForAddressIn claimed (Some systemId))
                    aid.Interfaces.[index] <- MicrexSx (endpointOf (Some systemId), interactions @ added)
                    1 + List.length added
                | None ->
                    let claimed = AidInteractionIds.claimedSignalIdsExcept aid -1
                    let interactions =
                        normalizedAddresses |> List.map (interactionForAddressIn claimed (Some systemId))
                    aid.Interfaces.Add(MicrexSx (endpointOf (Some systemId), interactions))
                    1 + List.length interactions

        /// 이 System 의 SX 바인딩을 지운다 — 벤더를 SX 에서 LS 로 되돌렸을 때 옛 endpoint 가
        /// 남아 Agent 가 연결을 하나 더 만드는 것을 막는다. 반환값은 지운 개수.
        [<CompiledName("RemoveForSystem")>]
        let removeForSystem (aid: AssetInterfacesDescription, systemId: Guid) : int =
            if isNull (box aid) || systemId = Guid.Empty then 0
            else
                let doomed =
                    aid.Interfaces
                    |> Seq.indexed
                    |> Seq.choose (function
                        | index, MicrexSx (endpoint, _) when endpoint.SystemId = Some systemId -> Some index
                        | _ -> None)
                    |> List.ofSeq
                for index in List.rev doomed do aid.Interfaces.RemoveAt index
                List.length doomed
