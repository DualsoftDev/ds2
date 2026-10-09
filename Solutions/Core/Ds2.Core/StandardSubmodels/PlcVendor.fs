namespace Ds2.Core.StandardSubmodels

open System
open System.Collections.Generic
open System.Text.Json.Serialization
open Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes

/// PLC 벤더 선택 — 편집기(Promaker)의 접속 폼, Hub 의 PlcConnection.json, DSPilot 이 같은 값을 쓴다.
/// AID 로 표현되는 건 LS 세 종류(InterfaceXGT)와 MICREX-SX(InterfaceMicrexSx)뿐이고 Mitsubishi 는 아직 없다.
type PlcVendorChoice =
    | LsXgi = 0
    | LsXgk = 1
    | LsXgb = 2
    | Mitsubishi = 3
    /// Fuji MICREX-SX (SPH2000 계열) — 로더 프로토콜.
    | MicrexSx = 4

/// PLC 접속 공통 기본값.
[<RequireQualifiedAccess>]
module PlcVendorDefaults =
    /// 스캔 주기 기본 100ms — 편집 폼·PlcConnection.json·벤더 프로파일이 같은 값을 쓴다.
    [<Literal>]
    let ScanIntervalMs = 100

/// 접속 매체 라벨 — 문자열 계약의 정본은 XgtEndpointBase.transportLabel 이고 여기서는 이름을 붙일 뿐이다
/// (리터럴을 다시 적지 않는다). AASX Property `transport`, PlcConnection.json, 수집기 payload 가 모두 이 세 값을 쓴다.
[<RequireQualifiedAccess>]
module PlcTransports =
    let Tcp = XgtEndpointBase.transportLabel XgtTransport.XgtTcp
    let Udp = XgtEndpointBase.transportLabel XgtTransport.XgtUdp
    let Usb = XgtEndpointBase.transportLabel XgtTransport.XgtUsb

    /// 모르는 값(옛 파일·오타)은 TCP 로 읽는다 — 이더넷만 있던 시절의 기본값.
    [<CompiledName("Normalize")>]
    let normalize (label: string) =
        if String.Equals(label, Udp, StringComparison.OrdinalIgnoreCase) then Udp
        elif String.Equals(label, Usb, StringComparison.OrdinalIgnoreCase) then Usb
        else Tcp

/// 벤더별 연결 파라미터 한 벌 — 편집 폼의 값이자, Hub 의 PlcConnection.json `profiles` 가 벤더 이름을 키로
/// 보관하는 값. 벤더별 기본값(포트·매체 선택지·AID 표현 가능 여부)도 여기 있다 — Promaker 와 DSPilot 이 같은
/// 규칙으로 endpoint 를 만들어야 하므로 앱이 아니라 Core 가 갖는다.
[<AllowNullLiteral>]
type PlcVendorProfile() =
    member val Name = "PLC#1" with get, set
    /// 이더넷 host. USB 접속에서는 쓰이지 않는다(빈 값 허용).
    member val IpAddress = "192.168.0.10" with get, set
    member val Port = 2004 with get, set
    member val TimeoutMs = 3000 with get, set
    member val ScanIntervalMs = PlcVendorDefaults.ScanIntervalMs with get, set
    member val LocalEthernet = true with get, set
    member val NetworkNumber = 0uy with get, set
    member val StationNumber = 0xFFuy with get, set
    /// 접속 매체 — PlcTransports 의 "tcp" | "udp" | "usb".
    /// UDP 는 미쓰비시 MC 프로토콜에서만 의미가 있고(PLC Ethernet 모듈 파라미터가 UDP 면 클라이언트도 UDP 로),
    /// USB 는 LS(XGI/XGK/XGB) CPU 전면 USB 로더 포트다.
    member val Transport = PlcTransports.Tcp with get, set
    /// USB 전용 — 장치 선택 키(목록번호 · serial · bus:addr · product 부분일치). "" = 첫 매칭 장치.
    member val UsbDeviceSelector = "" with get, set

    [<JsonIgnore>]
    member this.IsUsb = PlcEndpointLabel.isUsb this.Transport

    /// 사람이 읽는 접속 표기(host:port | USB | USB(selector)) — 상태바·로그가 그대로 쓴다.
    [<JsonIgnore>]
    member this.EndpointLabel =
        PlcEndpointLabel.format
            this.Transport
            (if isNull this.IpAddress then "" else this.IpAddress)
            this.Port
            (if isNull this.UsbDeviceSelector then "" else this.UsbDeviceSelector)

    member this.Clone() =
        let c = PlcVendorProfile()
        c.Name <- this.Name
        c.IpAddress <- this.IpAddress
        c.Port <- this.Port
        c.TimeoutMs <- this.TimeoutMs
        c.ScanIntervalMs <- this.ScanIntervalMs
        c.LocalEthernet <- this.LocalEthernet
        c.NetworkNumber <- this.NetworkNumber
        c.StationNumber <- this.StationNumber
        c.Transport <- this.Transport
        c.UsbDeviceSelector <- this.UsbDeviceSelector
        c

    /// 벤더별 기본 프로파일 — 포트만 다르다. LS 2004, Mitsubishi 5007,
    /// SX 509(로더 인터페이스 서버, 권장. 507 은 로더 명령 서버).
    static member Defaults(vendor: PlcVendorChoice) =
        let p = PlcVendorProfile()
        p.Port <-
            match vendor with
            | PlcVendorChoice.Mitsubishi -> 5007
            | PlcVendorChoice.MicrexSx -> 509
            | _ -> 2004
        p

    /// 이 벤더를 AID InterfaceXGT endpoint 로 표현할 수 있는가.
    ///
    /// 진실의 출처는 `XgtCpuModel = Xgi | Xgk | Xgb` 닫힌 DU 와 `AidXgtEndpointSettings.tryCpuModel` 이다 —
    /// 비-LS 벤더는 거기서 None 이 되어 EnsureBindingForSystem 이 0(변경 없음)을 돌려준다.
    /// 이 판정이 갈라놓는 것: 저장 경로(XGT ↔ SX endpoint)와 속성 패널이 화면에 무엇을 진실로 삼을지.
    static member IsAidXgtVendor(vendor: PlcVendorChoice) =
        vendor = PlcVendorChoice.LsXgi || vendor = PlcVendorChoice.LsXgk || vendor = PlcVendorChoice.LsXgb

    /// 이 벤더가 고를 수 있는 접속 매체. USB 는 LS 로더 포트만 dsev2 가 지원하고(미쓰비시 USB 는
    /// Linux 전용 진단 경로, SX 는 이더넷 로더만), UDP 는 미쓰비시 MC 프로토콜만 쓴다.
    static member TransportsFor(vendor: PlcVendorChoice) : IReadOnlyList<string> =
        match vendor with
        | PlcVendorChoice.Mitsubishi -> [| PlcTransports.Tcp; PlcTransports.Udp |]
        | PlcVendorChoice.MicrexSx -> [| PlcTransports.Tcp |]
        | _ -> [| PlcTransports.Tcp; PlcTransports.Usb |]
        :> IReadOnlyList<string>

    /// 이 포트 값이 어떤 벤더의 기본 포트인가 — 사용자가 직접 넣은 포트인지 판정하는 데 쓴다.
    /// 벤더를 바꿀 때 기본 포트 상태면 새 벤더 기본값으로 옮기고, 손으로 넣은 값은 건드리지 않는다.
    /// 열거는 enum 에서 파생시킨다 — 포트 목록을 손으로 적어 두면 벤더가 늘 때 조용히 낡는다.
    static member IsAnyVendorDefaultPort(port: int) =
        port > 0
        && Enum.GetValues<PlcVendorChoice>()
           |> Seq.exists (fun v -> PlcVendorProfile.Defaults(v).Port = port)
