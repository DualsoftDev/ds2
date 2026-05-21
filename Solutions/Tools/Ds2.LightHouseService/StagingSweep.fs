namespace Ds2.LightHouseService

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting

/// `Staging\` 의 incomplete upload sweep (todo-lighthouse-kb-server.md §3.10).
///
/// 정책:
/// - 명시 cancel (`DELETE /uploads/{stagingId}`) 시 즉시 해당 entry 제거 — `removeStaging`
/// - 주기 sweep (`stagingSweepIntervalMinutes`) — 일정 age 이상 .tmp / 빈 디렉토리 일괄 정리 (backstop, MA14)
///
/// stagingId = 디렉토리/파일 이름 (`<guid>.tmp` 또는 디렉토리). caller 가 path traversal 방지 의무.
[<RequireQualifiedAccess>]
module StagingSweep =

    /// stagingId 검증 — guid v4 형식만 허용 (32 hex + 4 dash). traversal / 절대경로 가드.
    let isValidStagingId (id: string) : bool =
        if String.IsNullOrWhiteSpace id then false
        elif id.Contains '/' || id.Contains '\\' || id.Contains ".." then false
        else
            match Guid.TryParseExact(id, "D") with
            | true, _ -> true
            | _ ->
                match Guid.TryParseExact(id, "N") with
                | true, _ -> true
                | _ -> false

    /// 즉시 cancel — stagingId 의 디렉토리 또는 `.tmp` 파일 제거. 미존재 시 false.
    let removeStaging (storageRoot: string) (stagingId: string) : bool =
        if not (isValidStagingId stagingId) then
            Log.audit.Warn(sprintf "staging: invalid stagingId — %s" stagingId)
            false
        else
            let stagingDir = Storage.stagingDir storageRoot
            let asDir = Path.Combine(stagingDir, stagingId)
            let asTmp = Path.Combine(stagingDir, stagingId + ".tmp")
            let mutable removed = false
            if Directory.Exists asDir then
                Directory.Delete(asDir, true)
                removed <- true
            if File.Exists asTmp then
                File.Delete asTmp
                removed <- true
            if removed then
                Log.audit.Info(sprintf "staging: cancelled — id=%s" stagingId)
            removed

    /// 주기 sweep — `maxAge` 이상 오래된 entry 일괄 제거.
    /// 디렉토리 / 파일 모두 LastWriteTimeUtc 기준. 첫 진입 (service restart 직후) 도 통과.
    /// **B11 (s6-r88, 15-reviewer Major)** — 디렉토리 mtime 의존 회귀 차단.
    /// 이전 박제는 staging 디렉토리 LastWriteTime 만 검사 — Linux ext4 의 partial content write
    /// 가 staging dir mtime 갱신 안 함 → 장시간 chunked upload 가 silent strangle. 디렉토리 안 *모든* 파일
    /// 의 max(LastWriteTimeUtc) 를 effective mtime 으로 사용 (resumable upload 의 partial.bin 또는 meta.json
    /// 갱신 시점 정합). 빈 디렉토리는 DirectoryInfo.LastWriteTimeUtc fallback.
    let private effectiveLastWriteUtc (dir: string) : DateTime =
        let dirInfo = DirectoryInfo dir
        let fileMax =
            Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            |> Seq.map (fun f -> FileInfo(f).LastWriteTimeUtc)
            |> Seq.fold max DateTime.MinValue
        if fileMax = DateTime.MinValue then dirInfo.LastWriteTimeUtc else fileMax

    let sweepStale (storageRoot: string) (maxAge: TimeSpan) : int =
        let stagingDir = Storage.stagingDir storageRoot
        if not (Directory.Exists stagingDir) then 0
        else
            let threshold = DateTime.UtcNow - maxAge
            let mutable removed = 0
            for d in Directory.EnumerateDirectories stagingDir do
                if effectiveLastWriteUtc d < threshold then
                    try
                        Directory.Delete(d, true)
                        removed <- removed + 1
                    with ex ->
                        Log.service.Warn(sprintf "staging sweep: dir 삭제 실패 — %s (%s)" d ex.Message)
            for f in Directory.EnumerateFiles stagingDir do
                let info = FileInfo f
                if info.LastWriteTimeUtc < threshold then
                    try
                        File.Delete f
                        removed <- removed + 1
                    with ex ->
                        Log.service.Warn(sprintf "staging sweep: file 삭제 실패 — %s (%s)" f ex.Message)
            if removed > 0 then
                Log.audit.Info(sprintf "staging sweep: removed=%d threshold=%s" removed (threshold.ToString("o")))
            removed


/// IHostedService — 주기 sweep BackgroundService. `stagingSweepIntervalMinutes` 마다 호출.
/// service start 시점에 한 번 즉시 실행 (이전 process 중단 잔재 정리).
type StagingSweepService(cfg: ServiceConfig) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) : Task =
        task {
            let storageRoot = Config.expandEnv cfg.StorageRoot
            let interval = TimeSpan.FromMinutes(float cfg.StagingSweepIntervalMinutes)
            // 첫 sweep — 이전 process 잔재 즉시 정리.
            let initialMaxAge =
                // 첫 진입은 1시간 이상 오래된 staging 만 정리 (다른 service instance 동시 가동 가능성 회피).
                TimeSpan.FromMinutes(60.0)
            try
                StagingSweep.sweepStale storageRoot initialMaxAge |> ignore
            with ex ->
                Log.service.Warn(sprintf "초기 staging sweep 실패 — %s" ex.Message)

            while not stoppingToken.IsCancellationRequested do
                try
                    do! Task.Delay(interval, stoppingToken)
                    // 주기 sweep — interval × 2 이상 오래된 entry 정리.
                    let maxAge = TimeSpan.FromMinutes(float cfg.StagingSweepIntervalMinutes * 2.0)
                    StagingSweep.sweepStale storageRoot maxAge |> ignore
                with
                | :? OperationCanceledException -> ()
                | ex ->
                    Log.service.Warn(sprintf "주기 staging sweep 실패 — %s" ex.Message)
        } :> Task
