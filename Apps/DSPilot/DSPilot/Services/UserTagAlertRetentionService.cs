using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// userTagAlertLog 의 보관 정리 + userTagAlertDaily 사전집계 백그라운드 잡.
/// - 기본 보관 60일 (UserTagAlert:RetentionDays appsettings)
/// - 6시간마다 cutoff 보다 오래된 raw 삭제
/// - 마지막 집계 다음 날 ~ 어제 까지 daily 집계 backfill
/// </summary>
public sealed class UserTagAlertRetentionService : BackgroundService
{
    private const int DefaultRetentionDays = 60;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UserTagAlertRetentionService> _logger;

    public UserTagAlertRetentionService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<UserTagAlertRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retention = ResolveRetention();
        _logger.LogInformation(
            "UserTagAlertRetentionService starting (retention={Days}d, check={Hours}h)",
            retention.TotalDays, CheckInterval.TotalHours);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await PurgeAsync(retention, stoppingToken);
                await AggregateAsync(stoppingToken);
                try { await Task.Delay(CheckInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private TimeSpan ResolveRetention()
    {
        var days = _configuration.GetValue<int?>("UserTagAlert:RetentionDays");
        return TimeSpan.FromDays(days is > 0 ? days.Value : DefaultRetentionDays);
    }

    private async Task PurgeAsync(TimeSpan retention, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IUserTagAlertRepository>();
            var cutoff = DateTime.UtcNow - retention;
            var deleted = await repo.PurgeOlderThanAsync(cutoff, ct);
            if (deleted > 0)
                _logger.LogInformation("[UserTagAlert] purged {Count} rows older than {Cutoff:u}", deleted, cutoff);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[UserTagAlert] purge failed");
        }
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
            // 마지막 집계 다음 날부터 시작 — 없으면 retention 시작 시점 부근부터.
            var startDate = lastDate?.AddDays(1).Date
                         ?? today.AddDays(-DefaultRetentionDays);
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
