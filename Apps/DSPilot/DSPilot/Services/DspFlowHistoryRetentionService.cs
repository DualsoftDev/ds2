using Dapper;
using DSPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DSPilot.Services;

/// <summary>
/// dspFlowHistory 의 오래된 행을 주기적으로 purge.
/// /flow 페이지의 "지난 60일" 추이를 보장하면서 무한 누적은 방지.
/// 기본: 60일 보관 + 6시간마다 검사 (UserTagAlertRetentionService 와 동일 정책).
/// </summary>
public sealed class DspFlowHistoryRetentionService : BackgroundService
{
    private const int DefaultRetentionDays = 60;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    private readonly IDatabasePathResolver _pathResolver;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DspFlowHistoryRetentionService> _logger;

    public DspFlowHistoryRetentionService(
        IDatabasePathResolver pathResolver,
        IConfiguration configuration,
        ILogger<DspFlowHistoryRetentionService> logger)
    {
        _pathResolver = pathResolver;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retention = ResolveRetention();
        _logger.LogInformation(
            "DspFlowHistoryRetentionService starting (retention={Days}d, check={Hours}h)",
            retention.TotalDays, CheckInterval.TotalHours);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await PurgeOldHistoryAsync(retention);
                try { await Task.Delay(CheckInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private TimeSpan ResolveRetention()
    {
        var days = _configuration.GetValue<int?>("FlowHistory:RetentionDays");
        return TimeSpan.FromDays(days is > 0 ? days.Value : DefaultRetentionDays);
    }

    private async Task PurgeOldHistoryAsync(TimeSpan retention)
    {
        try
        {
            var dbPath = _pathResolver.GetSharedDbPath();
            if (!File.Exists(dbPath)) return;

            await using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync();

            var cutoff = DateTime.UtcNow - retention;

            var rows = await conn.ExecuteAsync(
                "DELETE FROM dspFlowHistory WHERE recordedAt < @Cutoff",
                new { Cutoff = cutoff });

            if (rows > 0)
                _logger.LogInformation("[dspFlowHistory] purged {Count} rows older than {Cutoff:u}", rows, cutoff);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[dspFlowHistory] purge failed");
        }
    }
}
