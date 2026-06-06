namespace DSPilot.Services;

/// <summary>
/// 라이브 flow/Call 상태 self-heal 백그라운드 서비스. 주기적으로
/// <see cref="SimulationEngineService.ReconcileStuckStatesAsync"/> 를 호출해, 엔진 in-memory 상태(정본)와
/// DB 가 발산(DB=Going, 엔진=non-Going)한 행을 교정한다.
///
/// <para>배경: Going→Ready 다운그레이드는 이벤트 경로가 직접 안 내리고 폴링에 위임하는데, 폴링은
/// dspFlow.State 를 읽기만 한다. 그래서 라이브 상태 쓰기가 한 번 유실되면(예: 주기적 전체-이력 재계산의
/// 무거운 트랜잭션과 SQLite 단일-writer 락 경합) 해당 flow 가 'Going'에 latch 된다. 이 서비스가 매 tick
/// 엔진 정본과 대조해 그런 발산을 흡수한다(eventually-consistent).</para>
///
/// <para>간격은 <c>HistoryView.StateReconcileIntervalSeconds</c>(초, 0=비활성)로 매 루프 라이브 조회 →
/// 재시작 없이 설정 변경/비활성 반영.</para>
/// </summary>
public sealed class StateReconcileService : BackgroundService
{
    private readonly SimulationEngineService _engine;
    private readonly AppSettingsService _settings;
    private readonly ILogger<StateReconcileService> _logger;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromSeconds(30);
    private const int DefaultIntervalSeconds = 5;

    public StateReconcileService(
        SimulationEngineService engine,
        AppSettingsService settings,
        ILogger<StateReconcileService> logger)
    {
        _engine = engine;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await DelayAsync(StartupDelay, stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            int sec = ReadIntervalSeconds();

            if (sec <= 0)
            {
                if (!await DelayAsync(DisabledPollInterval, stoppingToken)) return;
                continue;
            }

            if (!await DelayAsync(TimeSpan.FromSeconds(sec), stoppingToken)) return;

            try
            {
                if (_engine.IsInitialized)
                    await _engine.ReconcileStuckStatesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StateReconcile] 틱 실패");
            }
        }
    }

    private int ReadIntervalSeconds()
    {
        try { return _settings.LoadSettings().HistoryView.StateReconcileIntervalSeconds; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[StateReconcile] 설정 읽기 실패 — 기본값({Sec}s) 사용", DefaultIntervalSeconds);
            return DefaultIntervalSeconds;
        }
    }

    /// <summary>취소 시 false 반환(루프 종료용). OperationCanceledException 을 흡수한다.</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
