namespace Ds2.LightHouseService

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting

/// Phase S3 idle TTL sweep BackgroundService (done-lighthouse-kb-server.md §3.8 L2-3 backstop).
///
/// `sessionIdleTtlMinutes` 마다 깨어나서 `LastUsedAt < now - idleAfter` 인 session 들을 일괄 Delete.
/// service kill / network drop / panel close hook 누락 모두 backstop — chat 회복 불가능한 session 누수 차단.
///
/// L2 cleanup 의 3차 — 1차 (panel close DELETE) / 2차 (process exit DELETE) / 3차 (본 sweep) 의 마지막 안전망.
type SessionSweepService(registry: ISessionRegistry, cfg: ServiceConfig) =
    inherit BackgroundService()

    /// sweep 주기 = idle TTL 의 1/4. (예: TTL 240분 → 60분 마다 sweep) 너무 빈번하면 lock contention, 너무 드물면 누수 지연.
    /// 최소 1분 가드 — 테스트/짧은 TTL 환경 대응.
    let sweepIntervalMs =
        let quarter = cfg.SessionIdleTtlMinutes * 60 * 1000 / 4
        max 60000 quarter

    let idleAfter = TimeSpan.FromMinutes(float cfg.SessionIdleTtlMinutes)

    override _.ExecuteAsync(stoppingToken: CancellationToken) : Task =
        task {
            Log.service.Info(
                sprintf "SessionSweepService 시작 — idleAfterMinutes=%d sweepIntervalMs=%d"
                    cfg.SessionIdleTtlMinutes sweepIntervalMs)
            // 첫 sweep 즉시 (service restart 직후 stale session 정리 — in-memory 라 사실 영향 0 이지만 패턴 유지)
            let mutable initial = true
            try
                while not stoppingToken.IsCancellationRequested do
                    if initial then
                        initial <- false
                    else
                        do! Task.Delay(sweepIntervalMs, stoppingToken)
                    if not stoppingToken.IsCancellationRequested then
                        try
                            let now = DateTime.UtcNow
                            let swept = registry.SweepIdle(now, idleAfter)
                            if swept > 0 then
                                Log.service.Info(
                                    sprintf "SessionSweepService — %d session 정리 (idleAfterMinutes=%d)"
                                        swept cfg.SessionIdleTtlMinutes)
                        with ex ->
                            // sweep 자체 실패 (예: KB Dispose 예외 누적) 는 service 중단 사유 아님 — log + 다음 주기 재시도.
                            Log.service.Warn(sprintf "SessionSweepService sweep 실패 — %s" ex.Message)
            with
            | :? OperationCanceledException -> ()
            Log.service.Info "SessionSweepService 중지"
        } :> Task

    override this.StopAsync(cancellationToken: CancellationToken) : Task =
        // F# 의 `base.X` 호출 제약: computation expression (task {}) 안에서는 escape 되어 거부됨 (FS0405).
        // 따라서 method body 에서 직접 호출 — DisposeAll 먼저, 그 후 base.StopAsync 반환.
        Log.service.Info "SessionSweepService StopAsync — 모든 session KB Dispose"
        try registry.DisposeAll () with ex ->
            Log.service.Warn(sprintf "SessionSweepService StopAsync DisposeAll 실패 — %s" ex.Message)
        base.StopAsync(cancellationToken)
