namespace Ds2.Backend.Plc

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Ev2.PLC.Common

/// 게이트웨이 본체. 등록된 모든 PLC 어댑터를 보관하고:
/// - HubAddress -> (adapter, tagDef) 라우팅 테이블 빌드 (ScanOnce / WriteAsync 모두 사용)
/// - 마지막 읽은 값 캐시 (변화분 감지)
/// 라이프사이클: ConnectAllAsync 1회 → ScanOnceAsync 반복 → DisconnectAllAsync.
type PlcGateway(config: PlcGatewayConfig) =

    static let log = log4net.LogManager.GetLogger("PlcGateway")

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

    interface IPlcGateway with

        member _.IsEnabled = not adapters.IsEmpty

        member _.ConnectAllAsync (ct: CancellationToken) =
            task {
                for (cfg, adapter) in adapters do
                    if ct.IsCancellationRequested then () else
                    try
                        let! ok = adapter.ConnectAsync()
                        if ok then log.Info($"PLC connected: {cfg.Name} ({cfg.IpAddress}:{cfg.Port})")
                        else log.Warn($"PLC connect failed: {cfg.Name} — gateway will keep trying on next scan")
                    with ex ->
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
                    if not adapter.IsConnected then
                        // 끊겨 있으면 1회 재연결 시도.
                        let! _ = adapter.ConnectAsync()
                        ()
                    for tag in cfg.Tags do
                        if ct.IsCancellationRequested then () else
                        match adapter.ReadTag tag with
                        | Error msg ->
                            log.Debug($"ReadTag {tag.HubAddress}: {msg}")
                        | Ok value ->
                            let s = PlcValueIo.toHubString value
                            let changed =
                                match lastValues.TryGetValue tag.HubAddress with
                                | true, prev -> prev <> s
                                | false, _ -> true
                            if changed then
                                lastValues.[tag.HubAddress] <- s
                                changes.Add({
                                    HubAddress = tag.HubAddress
                                    Value = s
                                    Source = Ds2.Backend.Common.HubSource.Plc
                                })
                return List.ofSeq changes
            }

        member _.MinScanInterval =
            adapters
            |> List.choose (fun (c, _) -> c.ScanInterval)
            |> function
                | [] -> None
                | xs -> Some (List.min xs)
