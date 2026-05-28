using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// userTagAlertDaily 사전집계 backfill 백그라운드 잡.
/// raw (userTagAlertLog) 는 DB 에 영구 보존 — purge 하지 않음.
/// 기간별 조회는 reader 가 occurredAt 으로 필터링 (BuildFilter).
/// </summary>
public sealed class UserTagAlertAggregationService : BackgroundService
{
    private const int InitialBackfillDays = 60;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UserTagAlertAggregationService> _logger;

    public UserTagAlertAggregationService(
        IServiceScopeFactory scopeFactory,
        ILogger<UserTagAlertAggregationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "UserTagAlertAggregationService starting (check={Hours}h, initial backfill={Days}d)",
            CheckInterval.TotalHours, InitialBackfillDays);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await AggregateAsync(stoppingToken);
                try { await Task.Delay(CheckInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task AggregateAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IUserTagAlertRepository>();
            var lastDate = await repo.GetLastAggregatedDateAsync(ct);
            var today = DateTime.UtcNow.Date;
            // 어제까지만 — 오늘은 계속 들어오는 중이라 다음 잡 사이클에서 집계.
            var endDate = today.AddDays(-1);
            // 마지막 집계 다음 날부터 시작 — 없으면 최근 InitialBackfillDays 만 backfill.
            var startDate = lastDate?.AddDays(1).Date
                         ?? today.AddDays(-InitialBackfillDays);
            if (startDate > endDate) return;

            var rows = await repo.RebuildDailyAggregatesAsync(startDate, endDate, ct);
            _logger.LogInformation(
                "[UserTagAlert] daily aggregation {Start:yyyy-MM-dd}..{End:yyyy-MM-dd} → {Rows} bucket rows",
                startDate, endDate, rows);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[UserTagAlert] aggregation failed");
        }
    }
}
