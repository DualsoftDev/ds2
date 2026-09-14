namespace Ds2.Backend.Plc

open System
open System.Collections.Generic
open System.Threading.Tasks
open Ev2.PLC.Common
open Ev2.PLC.Protocol.LS
open Ev2.PLC.Protocol.LS.Usb
open Ev2.PLC.Protocol.LS.UsbLoader

/// 수집 호스트에 붙은 LS PLC USB 장치의 선택 키 해석.
///
/// dsev2 의 transport(XgtUsbClientBase)는 장치를 `bus:addr` 로만 고른다. 그런데 bus:addr 는 재삽입·재부팅에
/// 바뀌므로 AID 에는 serial 같은 안정적인 키도 적을 수 있게 두고, dsev2 의 UsbEnumerator.trySelectByKey 가
/// 목록번호·serial·bus:addr·product 부분일치를 해석하므로 접속 직전에 열거해 bus:addr 로 바꿔 넘긴다.
///
/// Promaker 는 이 키를 <b>만들지 않는다</b>(항상 "" = 첫 장치). 장치를 고르려면 열거를 수집 호스트에서
/// 해야 하는데 Promaker 는 자기 PC 만 볼 수 있고, LS USB 는 VID/PID 가 기종 공통이라 개체를 가리키는
/// 안정적인 키도 없기 때문이다. 비어 있지 않은 키는 손으로 적은 AASX 에서만 들어온다.
[<RequireQualifiedAccess>]
module LsUsbDevices =
    /// VID/PID allow-list 는 dsev2 LS USB 기본값(0x1109 / 0x1004·0x1104)을 그대로 쓴다 — AID 에 노출하지 않는다.
    let private defaults = LsUsbConnectionConfig.Default 1000

    let private describe (d: UsbDeviceInfo) =
        let product = if String.IsNullOrWhiteSpace d.Product then "LS PLC" else d.Product
        let serial = if String.IsNullOrWhiteSpace d.Serial then "" else $" · S/N {d.Serial}"
        $"{product} · bus {d.Bus} addr {d.Address}{serial}"

    /// 선택 키 → dsev2 transport 가 요구하는 `bus:addr`. 빈 키는 "첫 매칭 장치"라 그대로 통과.
    let tryResolveSelector (selector: string) : Result<string, string> =
        let key = if isNull selector then "" else selector.Trim()
        if key = "" then Ok ""
        else
            let devices = LsUsbConnector.ListDevices(defaults.Vid, defaults.Pids)
            match UsbEnumerator.trySelectByKey devices key with
            | Some d -> Ok d.Selector
            | None when devices.Length = 0 ->
                Error $"연결된 LS PLC USB 장치가 없습니다 (VID 0x{defaults.Vid:X4}) — 케이블·전원·libusb 드라이버를 확인하세요."
            | None ->
                let known = devices |> Array.map describe |> String.concat "; "
                Error $"USB 장치 선택 키 '{key}' 에 맞는 장치가 없습니다. 연결된 장치: {known}"

/// LS XGT USB 로더 어댑터. 게이트웨이는 이더넷 어댑터와 같은 IPlcConnectorAdapter 로만 본다.
///
/// 접속마다 장치를 새로 열거해 커넥터를 다시 만든다 — bus:addr 가 바뀌는 재삽입 뒤에도 같은 selector 로
/// 다시 붙는다. 읽기는 dsev2 BlockPlanner 가 세운 240B 블록 계획을 캐시해 매 사이클 그대로 읽고
/// (XG5000 도 같은 계획을 반복 폴링한다), 하나라도 실패하면 전체를 Error 로 올린다 — LS 이더넷·SX 와 같은
/// all-or-nothing 계약이라 게이트웨이가 단건 폴백으로 어느 태그가 실패했는지 기록한다.
[<RequireQualifiedAccess>]
module LsUsbAdapter =
    let private log = log4net.LogManager.GetLogger("LsUsbAdapter")

    let create (cfg: PlcConnectionConfig) : IPlcConnectorAdapter =
        let usbCfg =
            { LsUsbConnectionConfig.Default cfg.TimeoutMs with
                Name = cfg.Name
                ScanInterval = defaultArg cfg.ScanInterval (TimeSpan.FromMilliseconds 100.0) }

        let planCache = ReadPlanCache<UsbReadPlan list>()
        let mutable connector : LsUsbConnector option = None

        let disposeConnector () =
            match connector with
            | Some c ->
                connector <- None
                try (c :> IDisposable).Dispose() with _ -> ()
            | None -> ()

        let refOf (tag: PlcTagDef) : UsbTagRef =
            { Name = tag.HubAddress; Address = tag.PlcAddress; DataType = tag.DataType }

        let requireConnector () =
            match connector with
            | Some c -> Ok c
            | None -> Error $"LS USB [{cfg.Name}] 미연결"

        let compilePlans (tags: PlcTagDef list) =
            try Ok (BlockPlanner.plan usbCfg.MaxChunkBytes (tags |> List.map refOf |> Array.ofList))
            with ex -> Error ex.Message

        { new IPlcConnectorAdapter with
            member _.Name = cfg.Name
            member _.IsConnected =
                connector |> Option.exists (fun c -> (c :> IPLCConnector).IsConnected)

            member _.ConnectAsync () =
                task {
                    try
                        disposeConnector ()
                        match LsUsbDevices.tryResolveSelector cfg.UsbDeviceSelector with
                        | Error message ->
                            log.Warn($"LS USB [{cfg.Name}] 장치 선택 실패: {message}")
                            return false
                        | Ok busAddr ->
                            let c = new LsUsbConnector({ usbCfg with DeviceSelector = busAddr })
                            match (c :> IPLCConnector).TryConnect() with
                            | Ok () ->
                                connector <- Some c
                                let device = if busAddr = "" then "첫 매칭 장치" else $"bus:addr {busAddr}"
                                log.Info($"LS USB [{cfg.Name}] connected — model={c.Model}, {device}")
                                return true
                            | Error message ->
                                (c :> IDisposable).Dispose()
                                // BUSY 는 XG5000 이 온라인으로 포트를 점유한 경우가 대부분 — 메시지에 그대로 실린다.
                                log.Warn($"LS USB [{cfg.Name}] connect 실패: {message}")
                                return false
                    with ex ->
                        // DllNotFoundException = libusb-1.0 네이티브가 실행 폴더에 없다(dsev2 install-libusb 참조).
                        log.Error($"LS USB [{cfg.Name}] connect 예외: {ex.Message}")
                        return false
                }

            member _.DisconnectAsync () =
                task { disposeConnector () }

            member _.ReadTag (tag) =
                requireConnector ()
                |> Result.bind (fun c ->
                    try
                        match c.ReadTags [| refOf tag |] with
                        | [| (_, value) |] -> Ok value
                        | _ -> Error $"tag={tag.HubAddress} plc={tag.PlcAddress}: USB 응답 없음"
                    with ex -> Error ex.Message)

            member _.ReadTags (tags) =
                Some (
                    requireConnector ()
                    |> Result.bind (fun c ->
                        planCache.GetOrCompile tags compilePlans
                        |> Result.bind (fun plans ->
                            try
                                // 계획은 영역·주소순으로 재배열되므로 응답을 HubAddress(=UsbTagRef.Name)로 되찾는다.
                                let byName = Dictionary<string, PlcTagDef>(StringComparer.Ordinal)
                                for tag in tags do byName.TryAdd(tag.HubAddress, tag) |> ignore
                                let results = c.ReadPlanned plans
                                let values = ResizeArray<struct (PlcTagDef * CoreDataTypesModule.PlcValue)>()
                                let mutable missing = None
                                for (r, value) in results do
                                    match byName.TryGetValue r.Name with
                                    | true, tag -> values.Add(struct (tag, value))
                                    | _ -> if missing.IsNone then missing <- Some r.Name
                                match missing with
                                | Some name -> Error $"LS USB [{cfg.Name}] 계획에 없는 태그 응답: {name}"
                                | None when values.Count <> tags.Length ->
                                    Error $"LS USB [{cfg.Name}] 블록 읽기 응답 {values.Count}개 / 태그 {tags.Length}개 — 누락 태그가 있어 전체를 실패로 봅니다"
                                | None -> Ok (List.ofSeq values)
                            with ex -> Error ex.Message)))

            member _.WriteTag (tag, value) =
                requireConnector ()
                |> Result.bind (fun c ->
                    try
                        c.WriteTag(tag.PlcAddress, tag.DataType, value)
                        Ok ()
                    with ex -> Error ex.Message)
        }
