namespace Ds2.Backend.Plc

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.AspNetCore.SignalR

/// SignalHub broadcaster 시그니처 — Ds2.Backend 가 own SignalHub 타입을 가지고 있어
/// 여기서 직접 import 하면 순환참조가 된다. broadcaster 람다를 DI 로 주입받는다.
type IPlcHubBroadcaster =
    abstract member BroadcastTagChanged : address: string * value: string * source: string -> Task

/// 주기적으로 PlcGateway.ScanOnceAsync 를 호출해 OnTagChanged broadcast.
/// StartAsync 에서 connect + first scan 을 *동기적으로* 완료시켜, BackendHost.start 가 반환되는 시점에
/// Hub tagCache 에 모든 IN/OUT 의 진짜 PLC 값이 들어있음을 보장한다.
/// 이 보장이 없으면 SyncRuntimeBootstrapStateFromHub 가 빈 cache 를 query 해 모든 Work 를
/// Ready(=home)로 잘못 추론하여 원위치가 빈 plan 으로 빠지는 race 가 발생한다.
type PlcScanService(gateway: IPlcGateway, broadcaster: IPlcHubBroadcaster) =

    static let log = log4net.LogManager.GetLogger("PlcScanService")

    let stoppingCts = new CancellationTokenSource()
    let mutable loopTask : Task = null

    /// host 시작 시점에 동기적으로 호출 — connect + 1회 전체 scan + cache populate.
    let initialConnectAndScan (ct: CancellationToken) =
        task {
            if not gateway.IsEnabled then
                log.Info("PLC gateway disabled — initial scan skipped")
            else
                log.Info("PLC initial connect + first scan starting (synchronous)...")
                try do! gateway.ConnectAllAsync(ct)
                with ex -> log.Error($"Initial connect threw: {ex.Message}")

                try
                    let! changes = gateway.ScanOnceAsync(ct)
                    log.Info($"PLC initial scan complete — {changes.Length} address(es) populated to hub cache")
                    for change in changes do
                        try
                            do! broadcaster.BroadcastTagChanged(
                                    change.HubAddress, change.Value, change.Source)
                        with ex ->
                            log.Warn($"Initial broadcast {change.HubAddress}={change.Value}: {ex.Message}")
                with ex ->
                    log.Error($"Initial scan threw: {ex.Message}")
        }

    let runScanLoop (stoppingToken: CancellationToken) =
        task {
            if not gateway.IsEnabled then
                return ()
            else
                let interval =
                    gateway.MinScanInterval
                    |> Option.defaultValue (TimeSpan.FromMilliseconds 100.0)

                log.Info($"PLC scan loop entering (interval={interval.TotalMilliseconds}ms)")

                while not stoppingToken.IsCancellationRequested do
                    try do! Task.Delay(interval, stoppingToken)
                    with :? OperationCanceledException -> ()

                    if stoppingToken.IsCancellationRequested then () else
                    try
                        let! changes = gateway.ScanOnceAsync(stoppingToken)
                        for change in changes do
                            try
                                do! broadcaster.BroadcastTagChanged(
                                        change.HubAddress, change.Value, change.Source)
                            with ex ->
                                log.Warn($"Broadcast failed {change.HubAddress}={change.Value}: {ex.Message}")
                    with
                    | :? OperationCanceledException -> ()
                    | ex -> log.Error($"Scan iteration threw: {ex.Message}")

                try do! gateway.DisconnectAllAsync()
                with ex -> log.Warn($"PLC disconnect on shutdown: {ex.Message}")

                return ()
        } :> Task

    interface IHostedService with
        /// app.StartAsync 가 이 task 의 완료까지 기다림 → initial scan 끝난 후에야
        /// BackendHost.start 가 반환되어, 이후 Hub client 가 query 하면 cache hit.
        member _.StartAsync (cancellationToken: CancellationToken) =
            task {
                do! initialConnectAndScan cancellationToken
                // initial scan 완료 후 background 로 주기 scan loop 시작.
                loopTask <- Task.Run(fun () -> runScanLoop stoppingCts.Token)
            } :> Task

        member _.StopAsync (cancellationToken: CancellationToken) =
            task {
                try stoppingCts.Cancel() with _ -> ()
                if not (isNull loopTask) then
                    try
                        // shutdown 이 너무 오래 걸리지 않도록 타임아웃.
                        let timeout = Task.Delay(2000, cancellationToken)
                        let! _ = Task.WhenAny(loopTask, timeout)
                        return ()
                    with _ -> return ()
                else return ()
            } :> Task

    interface IDisposable with
        member _.Dispose() =
            try stoppingCts.Cancel() with _ -> ()
            stoppingCts.Dispose()
