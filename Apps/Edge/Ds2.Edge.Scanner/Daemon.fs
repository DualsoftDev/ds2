module Pi5ScanPoc.Daemon

// 상주 데몬 루프 — PoC 의 "20 cycle 후 종료"를 무한 상주(취소 토큰 정리)로 대체.
//   설계: research_result.md §10.2 / §10.5 / §10.7.
//   - config 없음/비어있음/connections=0 → idle (파일 채워지면 재개).
//   - config 유효 → gateway connect + 주기 scan → event_log append + (연결 시) 실시간 push.
//   - heartbeat : 스캔 성공 시 ~1s 스로틀로 발행(PlcScanService.OnScanHeartbeat 패턴).
//   - config watch : plc.json 변경 감지 → 세션 취소 후 재구성.

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Ds2.Backend.Plc
open Pi5ScanPoc.Config
open Pi5ScanPoc.EventLog
open Pi5ScanPoc.HubClientPusher

/// plc.json 변경 감지 → onChange 콜백(디바운스). 상주 루프가 idle wake + 세션 취소에 쓴다.
type private ConfigWatcher(cfgPath: string, onChange: unit -> unit) =
    let full = Path.GetFullPath cfgPath
    let dir =
        let d = Path.GetDirectoryName full
        if String.IsNullOrEmpty d then "." else d
    let name = Path.GetFileName full
    let fsw = new FileSystemWatcher(dir, name)
    let mutable lastFire = DateTime.MinValue
    let handler () =
        let now = DateTime.UtcNow
        if (now - lastFire).TotalMilliseconds > 300.0 then
            lastFire <- now
            onChange ()
    do
        fsw.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName ||| NotifyFilters.Size
        fsw.Changed.Add(fun _ -> handler ())
        fsw.Created.Add(fun _ -> handler ())
        fsw.Deleted.Add(fun _ -> handler ())
        fsw.Renamed.Add(fun _ -> handler ())
        fsw.EnableRaisingEvents <- true
    interface IDisposable with
        member _.Dispose() = try fsw.Dispose() with _ -> ()

/// 유효 config 1개에 대한 scan 세션 — 세션 토큰이 취소될 때까지 상주.
/// (세션 취소 = 전역 종료 or config 변경.)
let private runSession (cfgPath: string) (cfg: DaemonConfig) (log: string -> unit) (sct: CancellationToken) : Task =
    task {
        use buffer = new EventLog(cfg.Buffer.DbPath, cfg.Buffer.RetentionMs, cfg.Buffer.MaxRows, log)
        let gw = new PlcGateway(cfg.Plc) :> IPlcGateway
        // Agent 가 push 하는 수집기 config → plc.json 병합(태그=Agent/접속=로컬) → watcher 가 재구성.
        let onCollectorConfig payload = Config.applyCollectorConfig cfgPath payload log
        let pusher =
            cfg.Hub |> Option.map (fun h ->
                new HubClientPusher(h, buffer, cfg.Buffer.ChunkSize, onCollectorConfig, log))

        let onConnectionStatus (status: Ds2.Backend.Common.PlcConnectionStatus) =
            log $"[plc] conn {status.Name} connected={status.IsConnected} err={status.LastError}"
            match pusher with
            | Some p -> p.ReportConnectionStatus(status) |> ignore
            | None -> ()

        use statusSubscription = gw.ConnectionStatusChanged.Subscribe(onConnectionStatus)

        let totalTags = cfg.Plc.Connections |> List.sumBy (fun c -> c.Tags.Length)
        log $"[scan] 세션 시작 — connections={cfg.Plc.Connections.Length} totalTags={totalTags} buffer={cfg.Buffer.DbPath}"

        let effectiveInterval =
            gw.MinScanInterval |> Option.defaultValue (TimeSpan.FromMilliseconds 100.0)

        // 백그라운드 flush/heartbeat 펌프 핸들(finally 에서 정리). 스캔 루프와 네트워크 전송 분리용.
        let mutable pumpTask : Task = Task.CompletedTask

        try
            try
                do! gw.ConnectAllAsync(sct)
                for s in gw.GetConnectionStatuses() do
                    // Ensure the current snapshot is cached even if the initial gateway
                    // event happened before SignalR finished connecting.
                    onConnectionStatus s

                match pusher with
                | Some p -> do! p.StartAsync(sct)
                | None -> log "[hub] url 없음 — push 비활성(로컬 버퍼링만)"

                // ── flush/heartbeat 를 스캔 루프에서 분리(백그라운드 펌프) ─────────────────
                //  flush(버퍼→Hub push+ack)·heartbeat 는 불안정 터널로 InvokeAsync(왕복 대기)한다.
                //  예전엔 이걸 스캔 루프 안에서 await → 핑 튀면 스캔이 그만큼 멈춰 PLC 샘플 유실(그래프 깨짐).
                //  → 스캔 루프는 로컬만(scan→append→sleep)으로 100ms 고정 유지, 전송/heartbeat 는 이 펌프가
                //    독립적으로 수행(터널 죽으면 펌프만 밀리고 스캔은 계속 버퍼링 → 복구 시 밀린 것 따라잡음).
                //  둘 다 그대로 필요/유지 — 위치만 스캔 밖으로 옮긴 것. EventLog 는 메서드마다 lock 직렬화라
                //  append(scan)·read/ack(pump) 동시 접근 안전.
                match pusher with
                | Some p ->
                    pumpTask <-
                        Task.Run(Func<Task>(fun () ->
                            task {
                                let mutable lastHeartbeat = DateTime.MinValue
                                while not sct.IsCancellationRequested do
                                    try
                                        do! p.FlushBuffer sct
                                        // heartbeat 스로틀 — 스캔 성공(LastSuccessfulScanUtc 전진) 시만, 최소 1s.
                                        // 실패 중이면 LastSuccessfulScanUtc 가 멎어 heartbeat 도 멎음 → 두절 오판 차단.
                                        match gw.LastSuccessfulScanUtc with
                                        | Some scanAt when
                                            scanAt > lastHeartbeat
                                            && (DateTime.UtcNow - lastHeartbeat).TotalMilliseconds >= 1000.0 ->
                                            lastHeartbeat <- DateTime.UtcNow
                                            do! p.SendHeartbeat()
                                        | _ -> ()
                                    with
                                    | :? OperationCanceledException -> ()
                                    | ex -> log $"[hub] 펌프 예외: {ex.Message}"
                                    try do! Task.Delay(50, sct) with :? OperationCanceledException -> ()
                            } :> Task))
                | None -> ()

                let mutable lastRetention = DateTime.UtcNow

                // 스캔 루프 — 로컬만(scan→append→retention→sleep). 네트워크 대기 없음 → 핑 무관 100ms 유지.
                while not sct.IsCancellationRequested do
                    let started = Stopwatch.GetTimestamp()
                    try
                        let! changes = gw.ScanOnceAsync(sct)
                        // event_log append(순서=seq). 전송은 펌프가 seq 순서로 push(§10.7.1, 직송 없음).
                        buffer.Append changes
                    with
                    | :? OperationCanceledException -> ()
                    | ex -> log $"[scan] iteration 예외: {ex.Message}"

                    // 주기 retention(시간/개수) — ack 무관하게 30s 마다.
                    if (DateTime.UtcNow - lastRetention).TotalSeconds >= 30.0 then
                        lastRetention <- DateTime.UtcNow
                        try buffer.ApplyRetention() with ex -> log $"[buffer] retention 예외: {ex.Message}"

                    let remaining = effectiveInterval - Stopwatch.GetElapsedTime(started)
                    if remaining > TimeSpan.Zero && not sct.IsCancellationRequested then
                        try do! Task.Delay(remaining, sct)
                        with :? OperationCanceledException -> ()
            with
            | :? OperationCanceledException -> ()
            | ex -> log $"[scan] 세션 예외: {ex.Message}"
        finally
            // teardown — 동기 정리(finally 안에서는 do! 불가).
            // 펌프 먼저 정리(세션 취소로 곧 종료) → 그 다음 hub dispose.
            try pumpTask.GetAwaiter().GetResult() with _ -> ()
            match pusher with
            | Some p -> try (p :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult() with _ -> ()
            | None -> ()
            try gw.DisconnectAllAsync().GetAwaiter().GetResult() with _ -> ()
            log "[scan] 세션 종료(정리 완료)"
    } :> Task

/// 상주 루프 진입점. ct 취소까지 무한 상주.
let run (cfgPath: string) (log: string -> unit) (ct: CancellationToken) : Task =
    task {
        let mutable sessionCts : CancellationTokenSource = null
        use wake = new SemaphoreSlim(0)
        let onChange () =
            log "[cfg] plc.json 변경 감지 → 재로드"
            (try wake.Release() |> ignore with _ -> ())
            match sessionCts with
            | null -> ()
            | c -> (try c.Cancel() with _ -> ())
        let watcher =
            try Some (new ConfigWatcher(cfgPath, onChange))
            with ex ->
                log $"[cfg] watcher 시작 실패(폴링만): {ex.Message}"
                None

        let waitIdle () =
            task {
                try do! wake.WaitAsync(2000, ct) :> Task
                with :? OperationCanceledException -> ()
            }

        try
            while not ct.IsCancellationRequested do
                match Config.tryLoad cfgPath with
                | None ->
                    log "[idle] config 없음/비어있음 — 대기(config 채워지면 재개)"
                    do! waitIdle ()
                | Some cfg when not cfg.HasConnections && cfg.Hub.IsNone ->
                    // connections 도 hub.url 도 없으면 할 일 없음 → idle.
                    log "[idle] connections=0 且 hub.url 없음 — 대기"
                    do! waitIdle ()
                | Some cfg ->
                    // connections 가 있으면 직접 scan. 없어도 hub.url 있으면 세션을 띄워 Hub 에 접속하고
                    // Agent 의 OnCollectorConfig(접속+태그)를 받아 plc.json 을 채운다 → watcher 재구성으로 scan 시작.
                    // (분리 아키텍처: 수집기는 hub.url 만 있으면 붙고, 접속/태그는 Agent 가 내려준다.)
                    use linked = CancellationTokenSource.CreateLinkedTokenSource(ct)
                    sessionCts <- linked
                    try do! runSession cfgPath cfg log linked.Token
                    finally sessionCts <- null

            log "[daemon] 종료 요청 수신 — 상주 루프 종료"
        finally
            match watcher with
            | Some w -> (w :> IDisposable).Dispose()
            | None -> ()
    } :> Task
