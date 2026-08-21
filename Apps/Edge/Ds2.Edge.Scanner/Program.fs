module Pi5ScanPoc.Program

// Pi5 엣지 수집 데몬.
//   PoC(read 검증: 20 cycle 후 종료)에서 → 운영 상주 데몬으로 확장.
//   설계 SSOT: samples/research_result.md §10 (store-and-forward + 엔진 replay).
//   구성:
//     Config.fs          — plc.json 파싱/정규화(connections + hub + buffer). 비면 idle.
//     EventLog.fs        — SQLite store-and-forward (event_log seq/OriginTsMs+wall_clock/ack/retention).
//     HubClientPusher.fs — Pi5=SignalR 클라이언트 → Hub.WriteTags push + 재연결 flush.
//     Daemon.fs          — 상주 루프 + config watch + heartbeat(~1s 스로틀).
//   종료: SIGTERM/SIGINT/Ctrl+C → CancellationToken 취소 → 세션 정리 후 종료(systemd 친화).

open System
open System.Runtime.InteropServices
open System.Threading

let private ts () = DateTime.Now.ToString("HH:mm:ss.fff")
let private log (msg: string) = printfn "[%s] %s" (ts ()) msg

let private rss () =
    try
        if RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then
            IO.File.ReadAllLines("/proc/self/status")
            |> Array.tryFind (fun l -> l.StartsWith "VmRSS")
            |> Option.defaultValue (sprintf "GC managed = %d KB" (GC.GetTotalMemory(false) / 1024L))
        else sprintf "GC managed = %d KB" (GC.GetTotalMemory(false) / 1024L)
    with _ -> "rss n/a"

[<EntryPoint>]
let main argv =
    let cfgPath = if argv.Length > 0 then argv.[0] else "plc.json"
    log "==== Pi5 Edge Collector (daemon) ===="
    log (sprintf "[env] OS   = %s" RuntimeInformation.OSDescription)
    log (sprintf "[env] Arch = %A / process %A" RuntimeInformation.OSArchitecture RuntimeInformation.ProcessArchitecture)
    log (sprintf "[env] .NET = %s" (Environment.Version.ToString()))
    log (sprintf "[env] cfg  = %s" cfgPath)
    log (sprintf "[mem] startup %s" (rss ()))

    use cts = new CancellationTokenSource()
    let requestStop (reason: string) =
        if not cts.IsCancellationRequested then
            log (sprintf "[daemon] 종료 신호(%s) — 정리 중..." reason)
            try cts.Cancel() with _ -> ()

    // Ctrl+C — 즉시 종료 막고 graceful.
    Console.CancelKeyPress.Add(fun e ->
        e.Cancel <- true
        requestStop "SIGINT/Ctrl+C")

    // systemd stop → SIGTERM.
    let mutable sigTerm : IDisposable = null
    let mutable sigInt : IDisposable = null
    try
        sigTerm <- PosixSignalRegistration.Create(PosixSignal.SIGTERM, fun ctx ->
            ctx.Cancel <- true
            requestStop "SIGTERM")
        sigInt <- PosixSignalRegistration.Create(PosixSignal.SIGINT, fun ctx ->
            ctx.Cancel <- true
            requestStop "SIGINT")
    with ex ->
        log (sprintf "[daemon] POSIX 시그널 등록 skip: %s" ex.Message)

    try
        Daemon.run cfgPath log cts.Token
        |> Async.AwaitTask
        |> Async.RunSynchronously
    with
    | :? OperationCanceledException -> ()
    | ex -> log (sprintf "[daemon] 치명 예외: %s" ex.Message)

    if not (isNull sigTerm) then sigTerm.Dispose()
    if not (isNull sigInt) then sigInt.Dispose()
    log (sprintf "[mem] end %s" (rss ()))
    log "==== 데몬 종료 ===="
    0
