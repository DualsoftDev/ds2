namespace Ds2.Backend.Plc

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.AspNetCore.SignalR
open Ds2.Backend.Common

/// SignalHub broadcaster 시그니처 — Ds2.Backend 가 own SignalHub 타입을 가지고 있어
/// 여기서 직접 import 하면 순환참조가 된다. broadcaster 람다를 DI 로 주입받는다.
type IPlcHubBroadcaster =
    abstract member BroadcastTagChanged : address: string * value: string * source: string -> Task
    /// PLC 어댑터 1개의 연결 상태 변화. PlcScanService 가 IPlcGateway.ConnectionStatusChanged 를
    /// 받아 위임 호출 — SignalHub broadcaster 가 모든 클라이언트로 fan-out + 캐시 갱신.
    abstract member BroadcastPlcConnectionStatus : status: PlcConnectionStatus -> Task
    /// v12 — Control/Monitoring abnormal 감지 결과를 모든 클라이언트로 fan-out (server-origin).
    abstract member BroadcastAbnormal : payload: AbnormalPayload -> Task

/// 주기적으로 PlcGateway.ScanOnceAsync 를 호출해 OnTagChanged broadcast.
/// StartAsync 에서 connect + first scan 을 *동기적으로* 완료시켜, BackendHost.start 가 반환되는 시점에
/// Hub tagCache 에 모든 IN/OUT 의 진짜 PLC 값이 들어있음을 보장한다.
/// 이 보장이 없으면 SyncRuntimeBootstrapStateFromHub 가 빈 cache 를 query 해 모든 Work 를
/// Ready(=home)로 잘못 추론하여 원위치가 빈 plan 으로 빠지는 race 가 발생한다.
type PlcScanService(gateway: IPlcGateway, broadcaster: IPlcHubBroadcaster) =

    static let log = log4net.LogManager.GetLogger("PlcScanService")

    /// Monitoring(read-only) 모드에서는 초기 동기 스캔을 건너뛴다.
    /// 이유: 초기 스캔은 Control 의 원위치 추론용 cache populate 가 목적이라 Monitoring 에 불필요.
    /// PLC 가 응답 없을 때 수백 태그 × per-tag timeout 만큼 UI 가 freeze 되는 문제 방지.
    /// 백그라운드 scan loop 가 자체 재연결 + cache 채움.
    static let mutable skipInitialScan = false

    let stoppingCts = new CancellationTokenSource()
    let mutable loopTask : Task = null
    /// gateway.ConnectionStatusChanged 구독 핸들 — StopAsync 시 dispose 해 누수/이중 발화 방지.
    let mutable statusSubscription : IDisposable = null

    /// PLC 어댑터 상태 변화를 SignalR fan-out + Agent 로그로 동시 가시화.
    /// fire-and-forget: SignalR send 실패가 scan loop 를 멈추면 안 됨.
    /// PlcGateway 는 실패가 지속되는 동안 매 재시도마다 status 를 발화 — broadcast 는 계속 보내되
    /// (DSPilot/Promaker 가 "현재 실패중" 을 인지해야 함) 로그는 FailedAttempts 로 노이즈 컨트롤:
    /// - 0: 연결 성공 (Info, 전이 시점)
    /// - 1: 첫 실패 (Warn)
    /// - 2~: 재시도 지속 (Debug — log4net 설정으로 끌 수 있음)
    let onConnectionStatusChanged (status: PlcConnectionStatus) =
        if status.IsConnected then
            log.Info($"PLC connection up: {status.Name} ({status.Vendor} {status.IpAddress}:{status.Port})")
        elif status.FailedAttempts <= 1 then
            log.Warn($"PLC connection down: {status.Name} ({status.Vendor} {status.IpAddress}:{status.Port}) — {status.LastError}")
        else
            log.Debug($"PLC retry #{status.FailedAttempts}: {status.Name} ({status.Vendor} {status.IpAddress}:{status.Port}) — {status.LastError}")
        try
            broadcaster.BroadcastPlcConnectionStatus(status) |> ignore
        with ex ->
            log.Warn($"BroadcastPlcConnectionStatus threw for {status.Name}: {ex.Message}")

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

    /// BackendHost.startWithPlc 에서 readOnly(=Monitoring) 진입 시 true 로 set — 초기 동기 스캔 생략.
    static member SetSkipInitialScan(value: bool) =
        skipInitialScan <- value

    interface IHostedService with
        /// app.StartAsync 가 이 task 의 완료까지 기다림 → initial scan 끝난 후에야
        /// BackendHost.start 가 반환되어, 이후 Hub client 가 query 하면 cache hit.
        /// skipInitialScan=true (Monitoring) 인 경우 초기 동기 스캔을 생략하고 즉시 background loop 시작.
        member _.StartAsync (cancellationToken: CancellationToken) =
            task {
                // gateway.ConnectionStatusChanged 구독 — Connect/Reconnect 시도 시 IsConnected 전이를 SignalR fan-out.
                // ConnectAllAsync 호출 전에 걸어야 첫 connect 결과를 놓치지 않는다.
                statusSubscription <- gateway.ConnectionStatusChanged.Subscribe(onConnectionStatusChanged)

                if skipInitialScan then
                    log.Info("PLC initial scan skipped (read-only / monitoring) — background loop will populate cache asynchronously.")
                else
                    do! initialConnectAndScan cancellationToken
                // initial scan 완료(또는 skip) 후 background 로 주기 scan loop 시작.
                loopTask <- Task.Run(fun () -> runScanLoop stoppingCts.Token)
            } :> Task

        member _.StopAsync (cancellationToken: CancellationToken) =
            task {
                try stoppingCts.Cancel() with _ -> ()
                try
                    if not (isNull statusSubscription) then
                        statusSubscription.Dispose()
                        statusSubscription <- null
                with _ -> ()
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
            try
                if not (isNull statusSubscription) then
                    statusSubscription.Dispose()
                    statusSubscription <- null
            with _ -> ()
            stoppingCts.Dispose()
