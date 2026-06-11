namespace Ds2.Backend.Plc

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Ds2.Backend.Common
open Ev2.PLC.Common

/// 게이트웨이 본체. 등록된 모든 PLC 어댑터를 보관하고:
/// - HubAddress -> (adapter, tagDef) 라우팅 테이블 빌드 (ScanOnce / WriteAsync 모두 사용)
/// - 마지막 읽은 값 캐시 (변화분 감지)
/// 라이프사이클: ConnectAllAsync 1회 → ScanOnceAsync 반복 → DisconnectAllAsync.
type PlcGateway(config: PlcGatewayConfig) =

    static let log = log4net.LogManager.GetLogger("PlcGateway")
    static let rawLog = log4net.LogManager.GetLogger("PlcGateway.Raw")

    /// PlcVendor → contract string. Promaker/DSPilot UI 에 그대로 노출되는 라벨이라 안정 문자열로 고정.
    /// 본 파일은 `open Ev2.PLC.Common` 이 위에 있어 `PlcVendor` 가 Ev2 의 동명 타입으로 resolve 된다.
    /// Ds2.Backend.Plc 의 union 으로 명시 qualify.
    let vendorLabel (v: Ds2.Backend.Plc.PlcVendor) : string =
        match v with
        | Ds2.Backend.Plc.PlcVendor.LsXgi      -> "LsXgi"
        | Ds2.Backend.Plc.PlcVendor.LsXgk      -> "LsXgk"
        | Ds2.Backend.Plc.PlcVendor.Mitsubishi -> "Mitsubishi"

    let adapters : (PlcConnectionConfig * IPlcConnectorAdapter) list =
        config.Connections
        |> List.map (fun c -> c, Adapter.create c)

    /// HubAddress -> (adapter, tagDef). 같은 주소를 두 PLC 가 서로 다르게 보유하면 마지막 등록만 살아남는다.
    let routing : ConcurrentDictionary<string, IPlcConnectorAdapter * PlcTagDef> =
        let d = ConcurrentDictionary<_, _>(StringComparer.OrdinalIgnoreCase)
        for (cfg, adapter) in adapters do
            for tag in cfg.Tags do
                if not (d.TryAdd(tag.HubAddress, (adapter, tag))) then
                    log.Warn($"Duplicate HubAddress {tag.HubAddress} — last wins")
                    d.[tag.HubAddress] <- (adapter, tag)
        d

    /// 변화분 감지용 last-value 캐시.
    let lastValues = ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase)

    /// adapter 별 "PLC 에 존재하지 않는 주소" skip-list.
    /// 운영 PLC 와 테스트 PLC 의 디바이스 영역 크기가 달라 일부 user tag 주소가 테스트 PLC 에는
    /// 존재 안 하는 경우, MELSEC 은 0xC056 같은 protocol error 로 응답한다. 같은 주소를 매 scan 마다
    /// 다시 시도하면 PLC 부하 + 로그 노이즈가 되므로, 연속 N회 protocol error 받으면 영구 skip.
    /// down→up 재연결 전이 시점에 reset — PLC parameter 가 바뀌어 영역이 확장됐을 수 있어 재검증.
    let unreachableAddresses =
        ConcurrentDictionary<string, ConcurrentDictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
    let protocolErrorThreshold = 3
    let getUnreachable (adapterName: string) =
        unreachableAddresses.GetOrAdd(
            adapterName,
            fun _ -> ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase))

    let isSkippedAddress (skipSet: ConcurrentDictionary<string, int>) (address: string) =
        match skipSet.TryGetValue address with
        | true, count when count >= protocolErrorThreshold -> true
        | _ -> false

    let clearTransientProtocolFailure (skipSet: ConcurrentDictionary<string, int>) (address: string) =
        match skipSet.TryGetValue address with
        | true, count when count > 0 && count < protocolErrorThreshold ->
            let mutable removed = 0
            skipSet.TryRemove(address, &removed) |> ignore
        | _ -> ()

    /// adapter 별 재연결 백오프 상태 — 잘못된 PLC 설정일 때 ConnectAsync 가 매 sweep timeout 만큼 blocking
    /// 하는 걸 막기 위해 연속 실패 횟수에 따라 다음 시도까지 대기 시간을 키운다.
    let reconnectState =
        ConcurrentDictionary<string, struct (DateTime * int)>(StringComparer.OrdinalIgnoreCase)

    /// 어댑터별 현재 상태 스냅샷 — Promaker/DSPilot 가 "PLC 통신 실패" 를 표시하기 위한 contract.
    /// 전이(up↔down) 외에 실패가 *지속되는 동안에도* 매번 broadcast — 재시도 중임을 클라이언트가
    /// 인지할 수 있어야 한다(사용자 요구). 성공 지속은 noise 라 broadcast 안 함.
    /// LastError 는 마지막 ConnectAsync 실패 사유(또는 ""). 등록 직후엔 IsConnected=false, LastError="".
    let statuses : ConcurrentDictionary<string, PlcConnectionStatus> =
        let d = ConcurrentDictionary<_, _>(StringComparer.OrdinalIgnoreCase)
        for (cfg, _) in adapters do
            d.[cfg.Name] <- {
                Name           = cfg.Name
                Vendor         = vendorLabel cfg.Vendor
                IpAddress      = cfg.IpAddress
                Port           = cfg.Port
                IsConnected    = false
                LastError      = ""
                FailedAttempts = 0
                AtUtc          = DateTime.UtcNow
            }
        d

    let connectionStatusEvent = Event<PlcConnectionStatus>()

    let backoffDelay (failedAttempts: int) : TimeSpan =
        match failedAttempts with
        | n when n <= 0 -> TimeSpan.Zero
        | n when n <  3 -> TimeSpan.FromSeconds 1.0
        | n when n < 10 -> TimeSpan.FromSeconds 5.0
        | _             -> TimeSpan.FromSeconds 30.0

    let shouldAttemptReconnect (name: string) : bool =
        match reconnectState.TryGetValue name with
        | false, _ -> true
        | true, struct (lastAttempt, fails) ->
            DateTime.UtcNow - lastAttempt >= backoffDelay fails

    /// 상태 갱신 + IsConnected 전이가 일어났을 때만 이벤트 발화.
    /// errorMessage 는 success=false 일 때 사유 라벨. success=true 면 "" 로 reset.
    let markConnectResult (name: string) (success: bool) (errorMessage: string) =
        let fails =
            match reconnectState.TryGetValue name with
            | true, struct (_, f) -> f
            | _ -> 0
        let nextFails = if success then 0 else fails + 1
        reconnectState.[name] <- struct (DateTime.UtcNow, nextFails)

        match statuses.TryGetValue name with
        | false, _ -> ()
        | true, prev ->
            // down → up 재연결 전이 시점에 unreachable skip-list reset.
            // PLC parameter 가 바뀌어 디바이스 영역이 확장된 경우 자동 재검증되도록.
            if success && not prev.IsConnected then
                match unreachableAddresses.TryGetValue name with
                | true, m when not m.IsEmpty ->
                    log.Info($"PLC [{name}] reconnected — clearing {m.Count} address(es) from unreachable skip-list for revalidation")
                    m.Clear()
                | _ -> ()
            let next = {
                prev with
                    IsConnected    = success
                    LastError      = if success then "" else (if isNull errorMessage then "" else errorMessage)
                    FailedAttempts = nextFails
                    AtUtc          = DateTime.UtcNow
            }
            statuses.[name] <- next
            // 전이(up↔down) 는 무조건 broadcast. 실패가 지속되는 동안에도 매 시도마다 broadcast 해서
            // 재시도 중임을 클라이언트가 인지할 수 있게 한다 (FailedAttempts 가 증가하므로 payload 도 매번 다름).
            // 성공 지속은 broadcast 안 함 — 잦은 정상 ping 으로 hub 부하 증가 방지.
            let isTransition    = prev.IsConnected <> next.IsConnected
            let failureOngoing  = not success
            if isTransition || failureOngoing then
                connectionStatusEvent.Trigger(next)

    // 런타임 스캔 주기 오버라이드(ms) — scan loop 가 매 iteration 읽어 재시작 없이 반영.
    let mutable scanIntervalOverrideMs : int option = None

    interface IPlcGateway with

        member _.IsEnabled = not adapters.IsEmpty

        member _.ScanIntervalOverrideMs
            with get () = scanIntervalOverrideMs
            and set value = scanIntervalOverrideMs <- value

        member _.ConnectAllAsync (ct: CancellationToken) =
            task {
                for (cfg, adapter) in adapters do
                    if ct.IsCancellationRequested then () else
                    try
                        let! ok = adapter.ConnectAsync()
                        // 사유 라벨: ConnectAsync 가 false 만 반환하면 구체 사유는 어댑터 로그에 있음.
                        // contract 의 LastError 에는 사용자에게 보일 수 있는 1줄 요약을 채운다.
                        let err =
                            if ok then ""
                            else $"connect failed (vendor={vendorLabel cfg.Vendor}, ip={cfg.IpAddress}:{cfg.Port})"
                        markConnectResult cfg.Name ok err
                        if ok then log.Info($"PLC connected: {cfg.Name} ({cfg.IpAddress}:{cfg.Port})")
                        else log.Warn($"PLC connect failed: {cfg.Name} — gateway will retry with backoff")
                    with ex ->
                        markConnectResult cfg.Name false ex.Message
                        log.Error($"PLC connect threw for {cfg.Name}: {ex.Message}")
            } :> Task

        member _.DisconnectAllAsync () =
            task {
                for (_, adapter) in adapters do
                    do! adapter.DisconnectAsync()
            } :> Task

        member _.WriteAsync (address: string, value: string) =
            task {
                match routing.TryGetValue address with
                | false, _ ->
                    // 진단용: 라우팅 테이블에 없는 주소 — IO map auto-import 가 빠뜨렸거나
                    // 엔진이 IO map 에 없는 주소로 OUT 을 쏘고 있는 케이스.
                    log.Warn($"WriteAsync: address '{address}' not in routing table (size={routing.Count}) — drop value={value}")
                    return false
                | true, (adapter, tag) ->
                    match PlcValueIo.parseFromHubString tag.DataType value with
                    | None ->
                        log.Warn($"WriteAsync: cannot parse '{value}' as {tag.DataType} for {address}")
                        return false
                    | Some plcValue ->
                        match adapter.WriteTag(tag, plcValue) with
                        | Ok () ->
                            log.Info($"WriteAsync OK: {adapter.Name} {address}={value}")
                            // 우리가 방금 쓴 값을 캐시에 반영해 self-echo 변화 감지를 막는다.
                            lastValues.[address] <- value
                            return true
                        | Error msg ->
                            log.Warn($"WriteAsync FAIL: {adapter.Name} {address}={value} — {msg}")
                            return false
            }

        member _.ScanOnceAsync (ct: CancellationToken) =
            task {
                let changes = ResizeArray<PlcTagChange>()
                for (cfg, adapter) in adapters do
                    if ct.IsCancellationRequested then () else
                    // 끊겨 있으면 backoff 안에서만 재연결 시도 — 잘못된 PLC 설정 시 매 sweep 마다
                    // timeout 만큼 blocking 되는 걸 막는다.
                    if not adapter.IsConnected && shouldAttemptReconnect cfg.Name then
                        try
                            let! ok = adapter.ConnectAsync()
                            let err =
                                if ok then ""
                                else $"reconnect failed (vendor={vendorLabel cfg.Vendor}, ip={cfg.IpAddress}:{cfg.Port})"
                            markConnectResult cfg.Name ok err
                        with ex ->
                            markConnectResult cfg.Name false ex.Message
                            log.Debug($"PLC reconnect {cfg.Name} threw: {ex.Message}")
                    // 연결 확정된 경우에만 ReadTag 진행 — 끊긴 상태에서 N 개 태그 × per-read timeout
                    // 누적으로 broadcast 가 지연되는 걸 차단.
                    // 한 스캔에서 *모든* 태그가 실패하면 좀비 connected 상태로 판단하고 disconnect →
                    // 다음 sweep 에서 backoff 후 재연결 흐름으로 진입. connect-time 에 잡지 못한
                    // 통신 단절(케이블 분리, PLC 전원 OFF 등) 도 이 경로로 가시화된다.
                    if adapter.IsConnected then
                        let adapterScanStartedAt = DateTime.UtcNow
                        let skipSet = getUnreachable cfg.Name
                        let mutable attempted = 0
                        let mutable failed = 0
                        let mutable firstReadError = ""
                        let mutable batchReads = 0
                        let mutable singleReads = 0
                        let mutable batches = 0
                        let mutable changesDetected = 0
                        let mutable protocolErrors = 0
                        let mutable skippedReads = 0

                        let recordRead (tag: PlcTagDef) (value: CoreDataTypesModule.PlcValue) =
                            let s = PlcValueIo.toHubString value
                            let changed =
                                match lastValues.TryGetValue tag.HubAddress with
                                | true, prev -> prev <> s
                                | false, _ -> true
                            if changed then
                                lastValues.[tag.HubAddress] <- s
                                changesDetected <- changesDetected + 1
                                rawLog.Info($"scan-change plc={cfg.Name} hub={tag.HubAddress} plc={tag.PlcAddress} value={s}")
                                changes.Add({
                                    HubAddress = tag.HubAddress
                                    Value = s
                                    Source = Ds2.Backend.Common.HubSource.Plc
                                })

                        let readTagNoSkip (tag: PlcTagDef) =
                            attempted <- attempted + 1
                            singleReads <- singleReads + 1
                            match adapter.ReadTag tag with
                            | Error msg ->
                                // protocol error (0xC0xx 등) = PLC 가 응답한 신호. 통신은 정상이고
                                // 요청 자체가 부적합. 누적해 임계치 넘으면 skip-list 영구 등록.
                                // non-protocol error = 진짜 통신 실패. all-fail 판정에 카운트.
                                if PlcErrorClassifier.isProtocolError msg then
                                    protocolErrors <- protocolErrors + 1
                                    let cur =
                                        skipSet.AddOrUpdate(
                                            tag.HubAddress,
                                            1,
                                            fun _ old -> old + 1)
                                    if cur = protocolErrorThreshold then
                                        log.Warn($"PLC [{cfg.Name}] address {tag.HubAddress} ({tag.PlcAddress}) repeatedly returned protocol error '{msg}' — skipping until reconnect (likely missing in PLC device range)")
                                    elif cur < protocolErrorThreshold then
                                        log.Debug($"ReadTag {tag.HubAddress} protocol error #{cur}: {msg}")
                                else
                                    failed <- failed + 1
                                    // 첫 에러만 보존 — 최초 단절 사유(타임아웃/connection lost)가
                                    // 후속 가드 메시지("not established")에 덮이지 않게 한다.
                                    if firstReadError = "" then firstReadError <- msg
                                    log.Debug($"ReadTag {tag.HubAddress}: {msg}")
                            | Ok value ->
                                clearTransientProtocolFailure skipSet tag.HubAddress
                                recordRead tag value

                        let readTags (tags: PlcTagDef list) =
                            if ct.IsCancellationRequested then () else
                            let readable = ResizeArray<PlcTagDef>()
                            for tag in tags do
                                if isSkippedAddress skipSet tag.HubAddress then
                                    skippedReads <- skippedReads + 1
                                else
                                    readable.Add(tag)

                            let readableTags = List.ofSeq readable
                            if not readableTags.IsEmpty then
                                match adapter.ReadTags readableTags with
                                | Some (Ok values) ->
                                    attempted <- attempted + readableTags.Length
                                    batchReads <- batchReads + readableTags.Length
                                    batches <- batches + 1

                                    for struct (tag, value) in values do
                                        clearTransientProtocolFailure skipSet tag.HubAddress
                                        recordRead tag value
                                | Some (Error msg) ->
                                    let batchError = $"PLC [{cfg.Name}] batch read count={readableTags.Length} failed: {msg}; falling back to single reads"
                                    log.Warn(batchError)
                                    rawLog.Warn(batchError)
                                    for tag in readableTags do
                                        readTagNoSkip tag
                                | None ->
                                    for tag in readableTags do
                                        readTagNoSkip tag

                        readTags cfg.Tags

                        let elapsedMs = (DateTime.UtcNow - adapterScanStartedAt).TotalMilliseconds
                        let elapsedText = elapsedMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                        rawLog.Info($"scan-summary plc={cfg.Name} durationMs={elapsedText} batchReads={batchReads} singleReads={singleReads} batches={batches} changes={changesDetected} failed={failed} protocolErrors={protocolErrors} skipped={skippedReads}")

                        if attempted > 0 && failed = attempted then
                            log.Warn($"PLC [{cfg.Name}] all {attempted} tag reads failed — forcing disconnect for reconnect cycle")
                            try do! adapter.DisconnectAsync() with _ -> ()
                            let err = $"all reads failed ({firstReadError})"
                            markConnectResult cfg.Name false err
                return List.ofSeq changes
            }

        member _.MinScanInterval =
            adapters
            |> List.choose (fun (c, _) -> c.ScanInterval)
            |> function
                | [] -> None
                | xs -> Some (List.min xs)

        member _.GetConnectionStatuses () =
            statuses.Values |> Seq.toList

        member _.ConnectionStatusChanged = connectionStatusEvent.Publish
