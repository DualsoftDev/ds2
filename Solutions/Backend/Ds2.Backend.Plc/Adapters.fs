namespace Ds2.Backend.Plc

open System
open System.Threading.Tasks
open Ev2.PLC.Common
open Ev2.PLC.Protocol.LS
open Ev2.PLC.Protocol.MX
open Ev2.Backend.PLC

// LsConnector/MxConnector 의 packetLogger·config 인자는 F# `?param` optional 로 정의돼 있어
// 호출 측에서는 unwrap 된 값을 전달하거나 named-arg 로 omit 한다.

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

/// 한 PLC 인스턴스에 대한 어댑터. 게이트웨이는 이 인터페이스만 본다.
type IPlcConnectorAdapter =
    abstract member Name : string
    abstract member ConnectAsync : unit -> Task<bool>
    abstract member DisconnectAsync : unit -> Task
    abstract member IsConnected : bool
    abstract member ReadTag : tag: PlcTagDef -> Result<CoreDataTypesModule.PlcValue, string>
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
                        let! _ = connector.ConnectAsync()
                        connected <- true
                        return true
                    with ex ->
                        log.Error($"LS [{cfg.Name}] ConnectAsync failed: {ex.Message}")
                        connected <- false
                        return false
                }
            member _.DisconnectAsync () =
                task {
                    try
                        let! _ = connector.DisconnectAsync()
                        connected <- false
                    with ex ->
                        log.Warn($"LS [{cfg.Name}] DisconnectAsync: {ex.Message}")
                }
            member _.ReadTag (tag) =
                try
                    match connector.ReadTag(tag.PlcAddress, tag.DataType) with
                    | Ok v -> Ok v
                    | Error e -> Error (sprintf "%A" e)
                with ex -> Error ex.Message
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
        { new IPlcConnectorAdapter with
            member _.Name = cfg.Name
            member _.IsConnected = connector.IsConnected
            member _.ConnectAsync () =
                task {
                    try
                        connector.Connect()
                        return connector.IsConnected
                    with ex ->
                        log.Error($"MX [{cfg.Name}] Connect failed: {ex.Message}")
                        return false
                }
            member _.DisconnectAsync () =
                task {
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
