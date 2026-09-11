namespace Ds2.Backend.Plc

open System
open System.Collections.Generic
open System.Threading.Tasks
open Ev2.PLC.Common
open Ev2.PLC.Protocol.LS
open Ev2.PLC.Protocol.MX
open Ev2.PLC.Protocol.SX
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

    /// 상위 Backend가 Ev2 PLC 데이터형 어셈블리를 직접 참조하지 않고도 원격 값을 검증하는 경계 helper.
    let canParseTagValue (tag: PlcTagDef) (value: string) =
        not (isNull value) && (parseFromHubString tag.DataType value |> Option.isSome)

[<RequireQualifiedAccess>]
module PlcBatchReadBuffer =
    let private sizeOf (dataType: CoreDataTypesModule.PlcDataType) =
        Math.Max(1, dataType.SizeInBytes)

    let chunkTags maxChunkSize (tags: PlcTagDef list) =
        if maxChunkSize <= 0 then
            invalidArg (nameof maxChunkSize) "Batch chunk size must be positive."

        tags |> List.chunkBySize maxChunkSize

    let private splitAddressTail (address: string) =
        let s = if isNull address then "" else address.Trim().ToUpperInvariant()
        if s = "" then
            "", Int32.MaxValue
        else
            let mutable index = s.Length - 1
            while index >= 0 && Char.IsDigit(s[index]) do
                index <- index - 1

            if index = s.Length - 1 then
                s, Int32.MaxValue
            else
                let prefix = s.Substring(0, index + 1)
                let suffix = s.Substring(index + 1)
                match Int32.TryParse(suffix) with
                | true, value -> prefix, value
                | _ -> prefix, Int32.MaxValue

    let chunkTagsByAddressGroup maxChunkSize (tags: PlcTagDef list) =
        if maxChunkSize <= 0 then
            invalidArg (nameof maxChunkSize) "Batch chunk size must be positive."

        tags
        |> List.groupBy (fun tag -> splitAddressTail tag.PlcAddress |> fst)
        |> List.sortBy fst
        |> List.collect (fun (_, group) ->
            group
            |> List.sortBy (fun tag ->
                let prefix, ordinal = splitAddressTail tag.PlcAddress
                prefix, ordinal, tag.PlcAddress)
            |> List.chunkBySize maxChunkSize)

    let decode (tags: PlcTagDef list) (buffer: byte array) =
        let tagArray = tags |> List.toArray
        let sizes = tagArray |> Array.map (fun tag -> sizeOf tag.DataType)
        let expected = sizes |> Array.sum

        if buffer.Length < expected then
            Error $"Batch read buffer too short: expected={expected}, actual={buffer.Length}"
        else
            let results = ResizeArray<struct (PlcTagDef * CoreDataTypesModule.PlcValue)>()
            let mutable offset = 0
            let mutable error = None
            for i = 0 to tagArray.Length - 1 do
                match error with
                | Some _ -> ()
                | None ->
                    let size = sizes.[i]
                    let bytes = Array.zeroCreate<byte> size
                    Buffer.BlockCopy(buffer, offset, bytes, 0, size)
                    offset <- offset + size
                    match CoreDataTypesModule.PlcValue.FromBytes(bytes, tagArray.[i].DataType) with
                    | Ok value -> results.Add(struct (tagArray.[i], value))
                    | Error msg -> error <- Some (sprintf "%A" msg)

            match error with
            | Some msg -> Error msg
            | None -> Ok (List.ofSeq results)

[<RequireQualifiedAccess>]
module LsPackRead =
    let private tagName index (tag: PlcTagDef) =
        if String.IsNullOrWhiteSpace tag.HubAddress then $"tag{index}"
        else tag.HubAddress

    let toTagSpecs (tags: PlcTagDef list) =
        tags
        |> List.mapi (fun index tag -> TagSpec(tagName index tag, tag.PlcAddress, tag.DataType))
        |> Array.ofList

    let scanAddressesForTags isLocalEthernet (tags: PlcTagDef list) =
        let tagSpecs = toTagSpecs tags
        // 새 Ev2 pack API: CPU 모델은 접속이 아니라 태그 주소별로 판별하므로 plcType="LS" 만 넘긴다.
        let packs = PackModule.packTagSpecsForType("LS", tagSpecs, isLocalEthernet)
        // 4번째=LS-USB 전용 청크 상한, 5번째 cpuKey=MX-USB CPU 프로파일 축 — 둘 다 LS Ethernet 경로선 무시.
        PackModule.getScanAddressesForPacks("LS", packs, isLocalEthernet, 240, "")

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
    let private maxReadBatchSize = 16

    let create (cfg: PlcConnectionConfig) : IPlcConnectorAdapter =
        // 새 Ev2(LS)는 CPU 모델(XGK/XGI)을 접속 config 가 아니라 태그 주소별로 판별한다
        // (XgtUtil.resolveTagInfo 의 XGI↔XGK 폴백). 따라서 LsEthernetConnectionConfig 에는
        // 모델 필드가 없다. vendor 구분은 어댑터 선택(LsAdapter vs MxAdapter) 단계에서 이미 끝났다.
        let lsConnCfg : LsEthernetConnectionConfig =
            { IpAddress = cfg.IpAddress
              Name = cfg.Name
              Port = cfg.Port
              EnableScan = true
              // ThreadId: 병렬 스캐너의 스레드 배정 축. 엣지 스캐너는 단일 접속이라 0(기본 스레드).
              ThreadId = 0
              LocalEthernet = cfg.LocalEthernet
              Timeout = TimeSpan.FromMilliseconds(float cfg.TimeoutMs)
              ScanInterval = TimeSpan.FromMilliseconds 100.0 }
        let connector =
            new LsConnector(
                cfg.IpAddress,
                cfg.Port,
                cfg.TimeoutMs,
                cfg.LocalEthernet,
                config = lsConnCfg)
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

/// Fuji MICREX-SX (SPH2000 계열) 어댑터.
///
/// ## 왜 ReadTags 를 반드시 구현하는가
///
/// SX 는 태그 하나마다 프레임 하나를 보내지 않는다. `CompilePlans` 가 태그를
/// **영역별로 묶고 인접 워드를 한 프레임으로 병합**한다(한 프레임 최대 243워드).
/// 실측: 229개 태그가 프레임 **1개**로 줄었다(`IO[0..55] x56`).
/// per-tag 로 읽으면 같은 일을 229 왕복으로 하게 된다 — 스캔 주기가 100ms 인
/// 게이트웨이에서는 성립하지 않는다.
///
/// 그래서 `ReadTags` 에서 계획을 캐시해 쓴다. `IPlcConnectorAdapter.ReadTags` 가
/// `option` 인 것은 "배치를 지원하지 않으면 None" 이라는 뜻이고, SX 는 지원한다.
///
/// ## 쓰기
///
/// SX 커넥터는 쓰기를 **타입 수준에서** 막아 둔다 — `SxSession.Write` 는 `Grant` 만
///받고 `Grant` 는 private 생성자라 `WriteGrant.authorize` 로만 나온다.
/// `SxWritableAreas` 가 비어 있으면 그 발급이 거부되므로 쓰기가 원천 차단된다.
/// 여기서는 그 상태를 **미리 알아채고 무엇을 설정해야 하는지 말해 준다** —
/// 그러지 않으면 Promaker 가 코일을 못 쓰는 이유를 프로토콜 오류로만 보게 된다.
[<RequireQualifiedAccess>]
module SxAdapter =
    let private log = log4net.LogManager.GetLogger("SxAdapter")

    let create (cfg: PlcConnectionConfig) : IPlcConnectorAdapter =
        let sxCfg =
            { SxConnectionConfig.Default cfg.IpAddress with
                Name = cfg.Name
                Port = cfg.Port
                Timeout = TimeSpan.FromMilliseconds(float cfg.TimeoutMs)
                ScanInterval = defaultArg cfg.ScanInterval (TimeSpan.FromMilliseconds 100.0)
                IoMapPath = cfg.SxIoMapPath
                WritableAreas = cfg.SxWritableAreas
                // **목록에 이름을 넣는 것이 곧 여는 행위다.** 라이브러리는 M10/IO 를
                // 별도 불리언으로도 막지만, ds2 설정 표면에서는 목록에 적는 것 자체가
                // 명시적 의사표시이므로 그대로 반영한다.
                // (예전 주석은 "한 번 더 명시해야 열린다" 고 했는데 코드는 그러지 않았다 —
                //  없는 안전장치를 있다고 적어 두면 그것을 믿고 M10 을 적게 된다.)
                // 대신 위험한 두 영역은 기동 시 경고로 남긴다.
                AllowSystemMemoryWrite = cfg.SxWritableAreas |> List.contains "M10"
                AllowIoImageWrite = cfg.SxWritableAreas |> List.contains "IO" }

        let connector = new SxConnector(sxCfg)
        let writeLocked = List.isEmpty cfg.SxWritableAreas

        /// **배열 주소를 거절한다.**
        ///
        /// SX 주소 문법만 배열을 표현할 수 있다 — `M1.400:WORD*16`. 그런데 허브는
        /// 주소 하나에 값 하나이고 `PlcTagDef.DataType` 도 스칼라다. 그대로 두면
        /// 읽기는 성공하고 `PlcValue[]` 가 돌아오는데, `PlcValueIo.toHubString` 이
        /// `Convert.ToString(array)` 로 떨어져 허브에 **"Ev2.PLC.Common.CoreDataTypesModule+PlcValue[]"**
        /// 라는 타입 이름 문자열이 들어간다(실측). 값이 아니라 쓰레기가 조용히 쌓인다.
        ///
        /// 읽기 전에 막고 무엇을 하라고 말한다.
        let rejectArray (tag: PlcTagDef) =
            let a = if isNull tag.PlcAddress then "" else tag.PlcAddress

            if a.Contains "*" then
                Some(
                    sprintf
                        "SX [%s] 배열 주소는 지원하지 않습니다 (%s). 허브는 주소 하나에 값 하나입니다 — 워드마다 태그를 나눠 적으세요 (M1.400, M1.401, …)."
                        cfg.Name
                        a)
            else
                None

        /// 계획 캐시. 태그 목록이 바뀌면 다시 만든다.
        let mutable planKey : string = ""
        let mutable plans : ReadPlan list = []

        if writeLocked then
            log.Info(
                $"SX [{cfg.Name}] 쓰기 잠김 (읽기 전용). 열려면 SxWritableAreas 에 영역을 넣으세요 — 예: [\"M1\"]")
        else
            let areaList = String.Join(", ", cfg.SxWritableAreas)
            log.Warn($"SX [{cfg.Name}] 쓰기 허용 영역: {areaList}")

            if cfg.SxWritableAreas |> List.contains "IO" then
                log.Warn($"SX [{cfg.Name}] **I/O 이미지 쓰기가 열렸다** — 출력 코일을 직접 움직인다")

            if cfg.SxWritableAreas |> List.contains "M10" then
                log.Warn($"SX [{cfg.Name}] **시스템 메모리(M10) 쓰기가 열렸다** — CPU 동작에 영향을 줄 수 있다")

        if cfg.SxIoMapPath = "" then
            log.Info($"SX [{cfg.Name}] 매핑표 없음 — 네이티브 주소만 사용 (M1.2000.0, IO.42.4)")
        else
            log.Info($"SX [{cfg.Name}] 매핑표 {cfg.SxIoMapPath} — IEC 원격 주소(%%QX…) 사용 가능")

        { new IPlcConnectorAdapter with
            member _.Name = cfg.Name
            member _.IsConnected = (connector :> IPLCConnector).IsConnected

            member _.ConnectAsync () =
                task {
                    try
                        // TryConnect 는 소켓만 여는 게 아니라 **매핑표 적재까지** 한다.
                        // 실패를 삼키면 주소가 전부 해석 불가가 되고, 그 원인이
                        // "연결됨" 뒤에 숨는다.
                        match (connector :> IPLCConnector).TryConnect() with
                        | Ok () ->
                            connector.LogIdentityOnce()
                            return true
                        | Error e ->
                            log.Error($"SX [{cfg.Name}] Connect 실패: {e}")
                            return false
                    with ex ->
                        log.Error($"SX [{cfg.Name}] Connect 예외: {ex.Message}")
                        return false
                }

            member _.DisconnectAsync () =
                task {
                    try (connector :> IPLCConnector).ForceDisconnect()
                    with ex -> log.Warn($"SX [{cfg.Name}] Disconnect: {ex.Message}")
                }

            member _.ReadTag (tag) =
                match rejectArray tag with
                | Some e -> Error e
                | None ->
                    try connector.ReadTag tag.PlcAddress
                    with ex -> Error ex.Message

            member _.ReadTags (tags) =
                // 태그 구성이 그대로면 계획을 다시 만들지 않는다.
                // 계획 수립은 주소 파싱 + 영역별 병합이라 매 사이클 돌릴 일이 아니다.
                let key = tags |> List.map (fun t -> t.PlcAddress) |> String.concat "|"

                let ensurePlans () =
                    match tags |> List.tryPick rejectArray with
                    | Some e -> Error e
                    | None ->

                    if key <> planKey then
                        let specs = tags |> List.map (fun t -> TagSpec(t.HubAddress, t.PlcAddress, t.DataType)) |> Array.ofList

                        match connector.CompilePlans specs with
                        | Ok ps ->
                            plans <- ps
                            planKey <- key
                            log.Info($"SX [{cfg.Name}] 읽기 계획 {ps.Length}프레임 / 태그 {tags.Length}개")
                            Ok ()
                        | Error e ->
                            // 계획이 서면 안 되는 주소가 하나라도 있으면 전체가 실패한다.
                            // 어느 주소인지는 ValidateTagSpecs 가 알려주므로 함께 남긴다.
                            let _, rejected = connector.ValidateTagSpecs specs

                            for (ts, why) in rejected |> Array.truncate 5 do
                                log.Error($"SX [{cfg.Name}] 태그 거부 — {ts.Name} ({ts.Address}): {why}")

                            Error (sprintf "%A" e)
                    else
                        Ok ()

                try
                    match ensurePlans () with
                    | Error e -> Some (Error e)
                    | Ok () ->
                        let results = connector.ReadPlanned plans
                        let tagArray = tags |> Array.ofList
                        let acc = ResizeArray<struct (PlcTagDef * CoreDataTypesModule.PlcValue)>()
                        let mutable firstError = None

                        for (idx, r) in results do
                            if idx >= 0 && idx < tagArray.Length then
                                match r with
                                | Ok v -> acc.Add(struct (tagArray.[idx], v))
                                | Error e ->
                                    if firstError.IsNone then
                                        let t = tagArray.[idx]
                                        firstError <- Some(sprintf "tag=%s plc=%s: %s" t.HubAddress t.PlcAddress e)
                            else
                                // 계획의 TagIndex 가 태그 배열을 벗어났다 = 계획과 태그가
                                // 어긋난 것이다. 조용히 넘기면 값이 밀려 들어간다.
                                if firstError.IsNone then
                                    firstError <- Some(sprintf "읽기 계획 인덱스 %d 가 태그 %d개를 벗어났다" idx tagArray.Length)

                        // **하나라도 실패하면 전체를 Error 로 올린다.**
                        //
                        // 게이트웨이는 Ok 를 받으면 `attempted` 에 **요청한 태그 수
                        // 전체**를 더하고 돌려받은 것만 기록한다(PlcGateway.fs).
                        // 즉 부분 리스트를 Ok 로 주면 빠진 태그는 실패로 세지도, 스킵
                        // 되지도 않고 **그냥 사라진다** — 허브에는 옛 값이 그대로 남고
                        // 스캔은 정상으로 보고된다.
                        //
                        // LS 어댑터(LsPackRead.readTags)도 같은 이유로 all-or-nothing 이다.
                        // Error 를 주면 게이트웨이가 단건 읽기로 폴백해 어느 태그가
                        // 실패했는지 태그별로 기록한다.
                        match firstError with
                        | Some e -> Some (Error e)
                        | None -> Some (Ok (List.ofSeq acc))
                with ex ->
                    Some (Error ex.Message)

            member _.WriteTag (tag, value) =
                if writeLocked then
                    // 프로토콜 오류로 흘려보내지 않는다. 설정 문제라고 말한다.
                    Error
                        (sprintf
                            "SX [%s] 는 읽기 전용입니다 (%s). 쓰려면 연결 설정의 SxWritableAreas 에 영역을 넣으세요 — 예: [\"M1\"]. I/O 이미지는 \"IO\", 시스템 메모리는 \"M10\"."
                            cfg.Name
                            tag.PlcAddress)
                else
                    try connector.WriteTag(tag.PlcAddress, value)
                    with ex -> Error ex.Message
        }

[<RequireQualifiedAccess>]
module Adapter =
    let create (cfg: PlcConnectionConfig) : IPlcConnectorAdapter =
        match cfg.Vendor with
        | PlcVendor.LsXgi
        | PlcVendor.LsXgk
        | PlcVendor.LsXgb    -> LsAdapter.create cfg
        | PlcVendor.Mitsubishi -> MxAdapter.create cfg
        | PlcVendor.MicrexSx   -> SxAdapter.create cfg
