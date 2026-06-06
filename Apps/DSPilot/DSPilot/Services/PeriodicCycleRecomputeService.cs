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
    private readonly ILogger<PeriodicCycleRecomputeService> _logger;

    // 비활성/설정 재확인 주기 — 간격을 0 으로 둔 동안 짧게 폴링해 켜짐을 감지.
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromMinutes(1);
    // 부팅 직후 첫 재계산까지의 워밍업 — 초기화/첫 사이클 기록과 겹쳐 churn 나지 않게.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    // 마지막으로 재계산에 반영한 최신 로그 시각 — 이후 새 로그가 없으면 스킵.
    private DateTime? _lastRecomputedLatest;

    public PeriodicCycleRecomputeService(
        CycleRecomputeService recompute,
        IFlowMetricsService flowMetrics,
        AppSettingsService settings,
        IPlcRepository plc,
        ILogger<PeriodicCycleRecomputeService> logger)
    {
        _recompute = recompute;
        _flowMetrics = flowMetrics;
        _settings = settings;
        _plc = plc;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await DelayAsync(StartupDelay, stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
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
            if (_lastRecomputedLatest.HasValue && latest <= _lastRecomputedLatest.Value) return;

            var recomputed = await _recompute.RecomputeAllTrackedFlowsAsync(ct);
            if (recomputed > 0)
            {
                _lastRecomputedLatest = latest;
                _logger.LogInformation("[AutoRecompute] 주기 재계산 완료 — flows={Count}", recomputed);
            }
            // recomputed == 0: 게이트 점유(수동 잡 진행 중) 등 — _lastRecomputedLatest 유지하여 다음 주기 재시도.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AutoRecompute] 주기 재계산 중 오류");
        }
    }

    /// <summary>취소 시 false 반환(루프 종료용). OperationCanceledException 을 흡수한다.</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
