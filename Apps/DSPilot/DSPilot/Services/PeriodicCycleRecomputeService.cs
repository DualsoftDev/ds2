// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// 주기적으로 전체-이력 재계산을 돌려, 라이브 기록기(FlowMetricsService)가 실시간에 tail 완료를 놓쳐
/// WT 가 부풀려진 사이클을 원시 plcTagLog 엣지 기준으로 self-heal 하는 백그라운드 서비스.
/// 사용자가 사이클 분석에서 "저장"을 누르는 것과 동일한 재도출 경로(<see cref="CycleRecomputeService.RecomputeAllTrackedFlowsAsync"/>)를 자동화한다.
///
/// <para>간격은 <c>HistoryView.AutoRecomputeIntervalMinutes</c>(분, 0=비활성)로 매 루프마다 라이브 조회 →
/// 재시작 없이 설정 변경 반영. 새 로그가 들어오지 않았으면(라인 유휴) 재계산을 스킵해 불필요한
/// 전체 재도출/UI 재빌드("DatabaseRebuilt")를 피한다.</para>
/// </summary>
public sealed class PeriodicCycleRecomputeService : BackgroundService
{
    private readonly CycleRecomputeService _recompute;
    private readonly IFlowMetricsService _flowMetrics;
    private readonly AppSettingsService _settings;
    private readonly IPlcRepository _plc;
    private readonly HeatmapService _heatmap;
    private readonly SimulationEngineService _engine;
    private readonly ILogger<PeriodicCycleRecomputeService> _logger;

    // 동작편차 통계 1회성 self-heal 완료 여부 — 라이브 누산기가 캡 적용 전 누적한 오염을 부팅 후 한 번 청소.
    private bool _callStatsHealed;

    // 비활성/설정 재확인 주기 — 간격을 0 으로 둔 동안 짧게 폴링해 켜짐을 감지.
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromMinutes(1);
    // 부팅 직후 첫 재계산까지의 워밍업 — 초기화/첫 사이클 기록과 겹쳐 churn 나지 않게.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    // 증분 재계산 overlap(분) — 워터마크에서 이만큼 뒤로 빼서 재도출. 경계에 걸친(straddle) 사이클이
    // 누락되지 않도록 "가장 긴 사이클"보다 충분히 커야 한다(이 라인 CT 수분 << 30분이라 안전).
    private const int IncrementalOverlapMinutes = 30;

    // 마지막으로 재계산에 반영한 최신 로그 시각 — 이후 새 로그가 없으면 스킵 + 증분 하한 산출용.
    private DateTime? _lastRecomputedLatest;

    public PeriodicCycleRecomputeService(
        CycleRecomputeService recompute,
        IFlowMetricsService flowMetrics,
        AppSettingsService settings,
        IPlcRepository plc,
        HeatmapService heatmap,
        SimulationEngineService engine,
        ILogger<PeriodicCycleRecomputeService> logger)
    {
        _recompute = recompute;
        _flowMetrics = flowMetrics;
        _settings = settings;
        _plc = plc;
        _heatmap = heatmap;
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await DelayAsync(StartupDelay, stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            // 동작편차 통계 1회성 self-heal — 모델/로그가 준비되면 한 번 청소(캡 재도출). AutoRecompute 간격
            // 설정(0=비활성)과 무관하게 시도하며, 준비 전이면 다음 루프에서 재시도.
            if (!_callStatsHealed)
                _callStatsHealed = await TryHealCallGoingStatsAsync(stoppingToken);

            int intervalMin = ReadIntervalMinutes();

            if (intervalMin <= 0)
            {
                // 비활성 — 설정이 켜지는지 짧게 폴링.
                if (!await DelayAsync(DisabledPollInterval, stoppingToken)) return;
                continue;
            }

            if (!await DelayAsync(TimeSpan.FromMinutes(intervalMin), stoppingToken)) return;

            await TryRecomputeAsync(stoppingToken);
        }
    }

    private int ReadIntervalMinutes()
    {
        try { return _settings.LoadSettings().HistoryView.AutoRecomputeIntervalMinutes; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AutoRecompute] 설정 읽기 실패 — 이번 주기 비활성 처리");
            return 0;
        }
    }

    private async Task TryRecomputeAsync(CancellationToken ct)
    {
        try
        {
            if (!_flowMetrics.IsInitialized) return;

            // 새 로그가 없으면(라인 유휴) 스킵 — 같은 데이터 재도출/UI 재빌드 방지.
            var latest = await _plc.GetLatestLogDateTimeAsync();
            if (latest is null) return;

            // 수집기 store-and-forward replay 로 들어온 backfill 구간(도착보다 과거인 원천시각)의 하한.
            // ★두 곳을 모두 통과하지 못하는 사각지대라 별도 신호가 필요하다:
            //   ① 아래 "새 로그 없음" 스킵 — replay 후 라인이 유휴면 MAX(dateTime) 가 워터마크를 안 넘을 수 있다.
            //   ② 증분 창 하한(워터마크 − 30분) — 30분 넘는 두절의 replay 는 이 창보다 과거에 꽂힌다.
            // 넓히지 않으면 그 구간이 plcTagLog 에만 복원되고 사이클 이력엔 안 들어가, 무사이클 정지가
            // 정상가동을 계속 삼킨다(그래프와 OEE 가 서로 모순되는 상태로 남는다).
            var backfillFloor = _engine.PeekBackfillFloorLocal();

            // ★상시 시계 스큐(송신기 시계가 통째로 뒤진 경우)는 replay 가 아니다 — 그때는 MAX(dateTime) 도 같은
            // 만큼 밀려 있어 평소 창 [latest−overlap, latest] 이 그 데이터를 그대로 덮는다(창이 데이터 자신의
            // 시계 기준이라 상수 오프셋은 상쇄된다). 그래서 "평소 창 밖으로 벗어난" 깊은 replay 만 신호로 인정한다.
            // 이 구분이 없으면 스큐 있는 현장에서 아래 유휴 스킵이 상시 무력화돼 매 주기 재도출 + "DatabaseRebuilt"
            // 푸시가 돌아, 그 스킵이 원래 막으려던 churn 이 되살아난다.
            var deepBackfill = backfillFloor.HasValue
                && backfillFloor.Value < latest.Value.AddMinutes(-IncrementalOverlapMinutes)
                    ? backfillFloor
                    : null;

            if (deepBackfill is null && _lastRecomputedLatest.HasValue && latest <= _lastRecomputedLatest.Value)
                return;

            // 첫 실행(워터마크 없음)은 전 구간, 이후는 증분(워터마크 − overlap)만 재도출 → 쓰기 락 점유 최소화.
            DateTime? since = _lastRecomputedLatest.HasValue
                ? _lastRecomputedLatest.Value.AddMinutes(-IncrementalOverlapMinutes)
                : (DateTime?)null;

            // 깊은 replay 면 하한을 그 지점까지 내린다. overlap 을 동일하게 적용하는 이유는 replay 경계에
            // 걸친(straddle) 사이클도 온전히 재도출되어야 하기 때문 — 창 안쪽 절반만 보면 head 없는 tail 이 된다.
            if (since.HasValue && deepBackfill.HasValue)
            {
                var backfillSince = deepBackfill.Value.AddMinutes(-IncrementalOverlapMinutes);
                if (backfillSince < since.Value) since = backfillSince;
            }

            var recomputed = await _recompute.RecomputeAllTrackedFlowsAsync(since, ct);
            if (recomputed > 0)
            {
                // 워터마크는 단조 — backfill 재도출은 latest 가 워터마크보다 과거인 상태에서도 돌 수 있어,
                // 그대로 대입하면 워터마크가 되돌아가 증분 창이 영구히 넓어진다.
                if (!_lastRecomputedLatest.HasValue || latest.Value > _lastRecomputedLatest.Value)
                    _lastRecomputedLatest = latest;

                // 소비 확정 — 창 안이라 무시했던(deepBackfill 아님) 하한도 이번 재도출이 덮었으므로 함께 비운다.
                // 재도출 중에 더 오래된 backfill 이 들어왔다면 CAS 실패로 남아 다음 주기가 처리한다.
                if (backfillFloor.HasValue) _engine.ClearBackfillFloor(backfillFloor.Value);

                _logger.LogInformation(
                    "[AutoRecompute] {Mode} 재계산 완료 — flows={Count}, since={Since}{Backfill}",
                    since.HasValue ? "증분" : "전체", recomputed,
                    since?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(전 구간)",
                    deepBackfill.HasValue ? $" ★replay 하한 {deepBackfill.Value:MM-dd HH:mm:ss} 포함" : "");
            }
            // recomputed == 0: 게이트 점유(수동 잡 진행 중) 등 — _lastRecomputedLatest 유지하여 다음 주기 재시도.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AutoRecompute] 주기 재계산 중 오류");
        }
    }

    /// <summary>
    /// 라이브 누산기가 캡 적용 전 누적한 동작편차 통계 오염을 원시 엣지에서 캡 기준으로 재도출해 청소하고
    /// 엔진 누산기를 재시드한다. 매핑/로그 미준비(반환 0)면 false 를 돌려 다음 루프에서 재시도.
    /// </summary>
    /// <returns>1회성 청소 완료 여부(true 면 재시도 중단).</returns>
    private async Task<bool> TryHealCallGoingStatsAsync(CancellationToken ct)
    {
        try
        {
            var healed = await _heatmap.RecomputeAllCallGoingStatisticsAsync(ct);
            if (healed <= 0) return false; // 매핑/로그 미준비 — 다음 주기 재시도.

            _engine.ReseedCallStatsFromDb();
            _logger.LogInformation("[AutoRecompute] 동작편차 통계 캡 재도출 self-heal 완료 — calls={Count}", healed);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AutoRecompute] 동작편차 통계 self-heal 실패 — 다음 주기 재시도");
            return false;
        }
    }

    /// <summary>취소 시 false 반환(루프 종료용). OperationCanceledException 을 흡수한다.</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
