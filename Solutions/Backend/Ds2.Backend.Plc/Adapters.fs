namespace Ds2.Backend.Plc

open System
open System.Threading.Tasks
open Ev2.PLC.Common
open Ev2.PLC.Protocol.LS
open Ev2.PLC.Protocol.MX
open Ev2.Backend.PLC

// LsConnector/MxConnector 의 packetLogger·config 인자는 F# `?param` optional 로 정의돼 있어
// 호출 측에서는 unwrap 된 값을 전달하거나 named-arg 로 omit 한다.

/// PLC ReadTag/WriteTag 가 돌려준 에러 메시지 분류. "통신 자체가 죽었나 vs 패킷은 오갔는데
/// 요청 내용이 부적합한가" 를 구분하기 위한 한 곳.
///
/// MELSEC MC protocol response (0xC0xx / 0xC1xx 등) 는 **PLC 가 패킷을 받아서 답까지 돌려보낸**
/// 신호다. 즉 통신은 alive, 다만 요청 자체가 부적합(없는 주소 / 범위 외 / unit count 초과 등).
/// 같은 주소를 매 scan 마다 다시 시도해도 답은 같으므로 임계치 누적 후 skip 처리하는 게 합리적.
[<RequireQualifiedAccess>]
module PlcErrorClassifier =
    let isProtocolError (msg: string) : bool =
        if isNull msg then false
        else
            let m = msg.ToLowerInvariant()
            m.Contains("code:") || m.Contains("0xc0") || m.Contains("0xc1")

/// PlcValue 와 string 사이의 변환. Hub 는 value 를 string 으로 다루므로,
/// Bool 은 "true"/"false", 정수/실수는 invariant culture 로 직렬화한다.
[<RequireQualifiedAccess>]
module PlcValueIo =
    let toHubString (v: CoreDataTypesModule.PlcValue) : string =
        match v.GetValue() with
        | null -> ""
        | :? bool as b -> if b then "true" else "false"
        | other -> Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture)

    let parseFromHubString (dataType: CoreDataTypesModule.PlcDataType) (s: string)
        : CoreDataTypesModule.PlcValue option =
        // 1차: TryParse 시도. bool 은 "1"/"0" 도 허용해야 하므로 별도 처리.
        if dataType.IsBool then
            match s.Trim().ToLowerInvariant() with
            | "1" | "true"  -> Some (CoreDataTypesModule.PlcValue.BoolValue true)
            | "0" | "false" -> Some (CoreDataTypesModule.PlcValue.BoolValue false)
            | _ -> None
        else
            let parsed = CoreDataTypesModule.PlcValue.TryParse(s, dataType)
            if parsed.IsSome then Some parsed.Value else None

[<RequireQualifiedAccess>]
module LsPackRead =
    let private tagName index (tag: PlcTagDef) =
        if String.IsNullOrWhiteSpace tag.HubAddress then $"tag{index}"
        else tag.HubAddress

    let toTagSpecs (tags: PlcTagDef list) =
        tags
        |> List.mapi (fun index tag -> TagSpec(tagName index tag, tag.PlcAddress, tag.DataType))
        |> Array.ofList

    let scanAddressesForTags isLocalEthernet plcModel (tags: PlcTagDef list) =
        let tagSpecs = toTagSpecs tags
        let packs = PackModule.packTagSpecsForType("LS", tagSpecs, isLocalEthernet, plcModel)
        PackModule.getScanAddressesForPacks("LS", packs, isLocalEthernet, plcModel)

    let readTags (connector: LsConnector) (tags: PlcTagDef list) =
        try
            let tagArray = tags |> List.toArray
            let tagSpecs = toTagSpecs tags
            let plcConnector = connector :> IPLCConnector
            let packs = plcConnector.CompilePacks(tagSpecs)

            for pack in packs do
                plcConnector.ReadCompiledPack(pack)

            let values = ResizeArray<struct (PlcTagDef * CoreDataTypesModule.PlcValue)>()
            let mutable error = None

            for index = 0 to tagSpecs.Length - 1 do
                match error, tagSpecs.[index].Value with
                | Some _, _ -> ()
                | None, Ok value ->
                    values.Add(struct (tagArray.[index], value))
                | None, Error msg ->
                    let tag = tagArray.[index]
                    error <- Some $"tag={tag.HubAddress} plc={tag.PlcAddress}: {msg}"

            match error with
            | Some msg -> Error msg
            | None -> Ok (List.ofSeq values)
        with ex ->
            Error ex.Message

/// 한 PLC 인스턴스에 대한 어댑터. 게이트웨이는 이 인터페이스만 본다.
type IPlcConnectorAdapter =
    abstract member Name : string
    abstract member ConnectAsync : unit -> Task<bool>
    abstract member DisconnectAsync : unit -> Task
    abstract member IsConnected : bool
    abstract member ReadTag : tag: PlcTagDef -> Result<CoreDataTypesModule.PlcValue, string>
    abstract member ReadTags : tags: PlcTagDef list -> Result<struct (PlcTagDef * CoreDataTypesModule.PlcValue) list, string> option
    abstract member WriteTag : tag: PlcTagDef * value: CoreDataTypesModule.PlcValue -> Result<unit, string>

[<RequireQualifiedAccess>]
module LsAdapter =
    let private log = log4net.LogManager.GetLogger("LsAdapter")

    let create (cfg: PlcConnectionConfig) : IPlcConnectorAdapter =
        let connector =
            new LsConnector(
                cfg.IpAddress,
                cfg.Port,
                cfg.TimeoutMs,
                cfg.LocalEthernet)
        let mutable connected = false

        { new IPlcConnectorAdapter with
            member _.Name = cfg.Name
            member _.IsConnected = connected
            member _.ConnectAsync () =
                task {
                    try
                        // LsConnector.ConnectAsync 는 실패를 예외가 아니라 Result.Error 로 반환한다
                        // (내부 establishConnection 이 소켓 예외를 흡수). Error 를 버리면 단절 상태가
                        // "연결 성공"으로 둔갑해 가짜 연결됨/통신실패 플래핑이 생긴다 — 반드시 match.
                        match! connector.ConnectAsync() with
                        | Ok () ->
                            connected <- true
                            return true
                        | Error msg ->
                            log.Warn($"LS [{cfg.Name}] ConnectAsync failed: {msg}")
                            connected <- false
                            return false
                    with ex ->
                        log.Error($"LS [{cfg.Name}] ConnectAsync threw: {ex.Message}")
                        connected <- false
                        return false
                }
            member _.DisconnectAsync () =
                task {
                    // disconnect 는 실패해도 어댑터 상태는 단절로 본다 — 좀비 connected 방지.
                    connected <- false
                    try
                        match! connector.DisconnectAsync() with
                        | Ok () -> ()
                        | Error msg -> log.Warn($"LS [{cfg.Name}] DisconnectAsync: {msg}")
                    with ex ->
                        log.Warn($"LS [{cfg.Name}] DisconnectAsync threw: {ex.Message}")
                }
            member _.ReadTag (tag) =
                try
                    match connector.ReadTag(tag.PlcAddress, tag.DataType) with
                    | Ok v -> Ok v
                    | Error e -> Error (sprintf "%A" e)
                with ex -> Error ex.Message
            member _.ReadTags tags =
                Some (LsPackRead.readTags connector tags)
            member _.WriteTag (tag, value) =
                try
                    if connector.WriteTag(tag.PlcAddress, tag.DataType, value) then Ok ()
                    else Error "WriteTag returned false"
                with ex -> Error ex.Message
        }

[<RequireQualifiedAccess>]
module MxAdapter =
    let private log = log4net.LogManager.GetLogger("MxAdapter")

    let create (cfg: PlcConnectionConfig) : IPlcConnectorAdapter =
        // Defaults.config 로 base 를 만든 뒤, Transport 만 사용자 선택값으로 교체해 새 config 생성.
        // 다른 필드(FrameType, AccessRoute, MonitoringTimer 등) 는 라이브러리 default 유지.
        let baseCfg = Constants.Defaults.config cfg.Name cfg.IpAddress cfg.Port
        let protocol =
            match cfg.Transport with
            | PlcTransport.Udp -> TransportProtocol.UDP
            | PlcTransport.Tcp -> TransportProtocol.TCP
        let mxCfg = { baseCfg with Protocol = protocol }
        log.Info($"MX [{cfg.Name}] transport={mxCfg.Protocol}, frame={mxCfg.FrameType}")
        let connector = new MxConnector(mxCfg)
        // UDP 는 socket bind 만으로 IsConnected=true 가 될 수 있어 connect-time 에 실제 응답을 봐야 한다.
        // TCP 도 일부 라이브러리 구현에서 SYN-only 성공만으로 true 가 되는 경우가 있어 동일하게 검증.
        // PlcConnectionConfig.Tags 의 첫 항목 1개를 probe 로 1회 ReadTag → 실패면 connect 실패로 간주.
        // 태그가 비어있는 어댑터(write-only / 등록 직후)는 probe 생략하고 IsConnected 만 반환.
        //
        // 핵심: probe 의 목적은 wire 위에서 PLC 가 응답하는지 확인하는 것. 0xC0xx 같은 MELSEC
        // protocol-level error code 가 돌아왔다는 건 PLC 가 패킷을 받고 답까지 돌려보냈다는 뜻이라
        // alive 로 인정해야 한다. 진짜 dead 신호는 timeout / socket exception 뿐.
        // (예: probe 주소가 PLC 디바이스 범위를 살짝 벗어나 0xC056 이 돌아와도 통신은 정상이므로
        //  연결 자체는 OK 로 처리. 잘못된 주소는 이후 ReadTag debug 로그로 가시화됨.)
        let probeTag = cfg.Tags |> List.tryHead
        let probeAlive () : bool =
            match probeTag with
            | None -> connector.IsConnected
            | Some tag ->
                try
                    match connector.ReadTag(tag.PlcAddress, tag.DataType) with
                    | Ok _ -> true
                    | Error e ->
                        let msg = sprintf "%A" e
                        if PlcErrorClassifier.isProtocolError msg then
                            // PLC 가 응답함 → 통신 자체는 정상. 주소 부적합 같은 사용자 설정 이슈는
                            // 정기 scan 의 ReadTag debug 로그로 가시화 — connection up 자체는 인정.
                            log.Info($"MX [{cfg.Name}] probe ReadTag {tag.PlcAddress} returned protocol error '{msg}' — PLC alive, accepting connection")
                            true
                        else
                            log.Warn($"MX [{cfg.Name}] probe ReadTag {tag.PlcAddress} failed (no PLC response): {msg}")
                            false
                with ex ->
                    log.Warn($"MX [{cfg.Name}] probe ReadTag {tag.PlcAddress} threw: {ex.Message}")
                    false
        let mutable verified = false
        { new IPlcConnectorAdapter with
            member _.Name = cfg.Name
            // 라이브러리 IsConnected 만 신뢰하지 말고 probe 검증 결과를 함께 본다 — UDP false-up 차단.
            member _.IsConnected = connector.IsConnected && verified
            member _.ConnectAsync () =
                task {
                    try
                        connector.Connect()
                        if not connector.IsConnected then
                            verified <- false
                            return false
                        else
                            let alive = probeAlive ()
                            verified <- alive
                            return alive
                    with ex ->
                        log.Error($"MX [{cfg.Name}] Connect failed: {ex.Message}")
                        verified <- false
                        return false
                }
            member _.DisconnectAsync () =
                task {
                    verified <- false
                    try connector.Disconnect()
                    with ex -> log.Warn($"MX [{cfg.Name}] Disconnect: {ex.Message}")
                }
            member _.ReadTag (tag) =
                // BackendTypesModule 의 type augmentation 이 MxConnector 에 ReadTag 를 추가해 둔다.
                // open Ev2.Backend.PLC 가 위에 있어 인스턴스 메서드처럼 호출 가능.
                try
                    let r = connector.ReadTag(tag.PlcAddress, tag.DataType)
                    match r with
                    | Ok v -> Ok v
                    | Error e -> Error (sprintf "%A" e)
                with ex -> Error ex.Message
            member _.ReadTags _ =
                None
            member _.WriteTag (tag, value) =
                try
                    let r = connector.WriteTag(tag.PlcAddress, value)
                    match r with
                    | Ok _ -> Ok ()
                    | Error e -> Error (sprintf "%A" e)
                with ex -> Error ex.Message
        }

[<RequireQualifiedAccess>]
module Adapter =
    let create (cfg: PlcConnectionConfig) : IPlcConnectorAdapter =
        match cfg.Vendor with
        | PlcVendor.LsXgi
        | PlcVendor.LsXgk    -> LsAdapter.create cfg
        | PlcVendor.Mitsubishi -> MxAdapter.create cfg
