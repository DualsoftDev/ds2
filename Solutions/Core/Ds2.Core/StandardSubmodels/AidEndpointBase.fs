namespace Ds2.Core.StandardSubmodels

open System

/// AID endpoint `base` URI 의 공용 검증·조립.
///
/// IDTA 02017 은 `base` 를 URI 로 두고 스킴이 프로토콜·매체를 말한다(`opc.tcp://`, `modbus+tcp://`).
/// DualSoft 확장(InterfaceXGT·InterfaceMicrexSx)도 같은 관례를 따르므로, 절대 URI·스킴 일치·inline
/// credential 금지·fragment 금지 같은 규칙은 바인딩 종류와 무관하게 여기 한 곳에서만 검사한다.
/// 예전에는 Core(읽기)와 Backend(게이트웨이 조립)가 각자 URI 를 파싱해 "저장은 되는데 활성화만
/// 실패하는" 값이 생길 수 있었다.
[<RequireQualifiedAccess>]
module AidEndpointBase =

    /// base 문자열 길이 상한 — Promaker 입력과 외부 도구가 만든 AASX 모두 이 위에서 거절한다.
    [<Literal>]
    let MaxLength = 2048

    /// 절대 URI 이고 스킴이 기대값과 같은지, 자격증명·fragment 가 없는지까지만 본다.
    /// host/port/path 해석은 호출자가 한다. `label` 은 오류 문구에 쓰는 인터페이스 이름.
    let tryAbsoluteUri (label: string) (expectedScheme: string) (value: string) : Result<Uri, string> =
        if isNull value || value.Length > MaxLength then
            Error(sprintf "%s EndpointMetadata.base가 잘못되었습니다: 비어 있거나 %d자를 넘습니다." label MaxLength)
        else
            match Uri.TryCreate(value, UriKind.Absolute) with
            | false, _ ->
                Error(sprintf "%s EndpointMetadata.base가 잘못되었습니다: 절대 URI 가 아닙니다 — '%s'." label value)
            | true, uri when not (uri.Scheme.Equals(expectedScheme, StringComparison.OrdinalIgnoreCase)) ->
                Error(sprintf "%s EndpointMetadata.base scheme은 transport에 맞는 '%s'여야 합니다." label expectedScheme)
            | true, uri when not (String.IsNullOrWhiteSpace uri.UserInfo) ->
                Error(sprintf "%s EndpointMetadata.base에 inline credential을 넣을 수 없습니다." label)
            | true, uri when not (String.IsNullOrEmpty uri.Fragment) ->
                Error(sprintf "%s EndpointMetadata.base에 URI fragment를 넣을 수 없습니다." label)
            | true, uri -> Ok uri

    /// `scheme://host:port` 조립. 이더넷 계열 바인딩(XGT TCP/UDP·SX)이 공유한다.
    let hostPort (scheme: string) (host: string) (port: int) =
        sprintf "%s://%s:%d" scheme (host.Trim()) port

    /// `scheme://host[:port]` 해석. 포트가 없으면 defaultPort.
    let tryParseHostPort (label: string) (scheme: string) (defaultPort: int) (value: string)
        : Result<string * int, string> =
        tryAbsoluteUri label scheme value
        |> Result.bind (fun uri ->
            if String.IsNullOrWhiteSpace uri.Host then
                Error(sprintf "%s EndpointMetadata.base에 host가 없습니다." label)
            else
                Ok(uri.Host, (if uri.Port > 0 then uri.Port else defaultPort)))

/// PLC 접속을 사람이 읽는 한 줄로 만드는 유일한 자리.
///
/// 게이트웨이 로그·연결 상태 계약(`PlcConnectionStatus.Endpoint`)·Promaker 상태바·DSPilot 배너가
/// 전부 이 함수를 쓴다. 이더넷은 `host:port`, USB 는 `USB` 또는 `USB(<selector>)`.
/// 이걸 각 화면이 `{ip}:{port}` 로 직접 조립하면 USB 접속이 `:0` 으로 보인다.
[<RequireQualifiedAccess>]
module PlcEndpointLabel =

    /// 전송 라벨 중 USB 를 뜻하는 값. XgtEndpointBase.transportLabel 과 Ds2.Backend.Plc 의 전송
    /// 라벨이 같은 문자열을 쓰므로 여기서 한 번만 비교한다.
    [<Literal>]
    let UsbTransport = "usb"

    let isUsb (transportLabel: string) =
        String.Equals(transportLabel, UsbTransport, StringComparison.OrdinalIgnoreCase)

    let format (transportLabel: string) (host: string) (port: int) (usbSelector: string) =
        if isUsb transportLabel then
            if String.IsNullOrWhiteSpace usbSelector then "USB"
            else sprintf "USB(%s)" (usbSelector.Trim())
        else
            sprintf "%s:%d" (if isNull host then "" else host) port
