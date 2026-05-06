using Dapper;
using DSPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DSPilot.Services;

/// <summary>
/// plcTagLog 의 오래된 행을 주기적으로 purge.
/// 무한 누적되면 plc.db 가 GB 수준까지 증가 — 운영 환경에서 디스크 폭증 방지.
/// 기본: 30일 보관 + 6시간마다 검사.
/// </summary>
public sealed class PlcTagLogRetentionService : BackgroundService
{
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    private readonly IDatabasePathResolver _pathResolver;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlcTagLogRetentionService> _logger;

    public PlcTagLogRetentionService(
        IDatabasePathResolver pathResolver,
        IConfiguration configuration,
        ILogger<PlcTagLogRetentionService> logger)
    {
        _pathResolver = pathResolver;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retention = ResolveRetention();
        _logger.LogInformation(
            "PlcTagLogRetentionService starting (retention={Days}d, check={Hours}h)",
            retention.TotalDays, CheckInterval.TotalHours);

        try
        {
            // 시작 직후엔 부하 피하기
            await Task.Delay(StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await PurgeOldLogsAsync(retention);
                try { await Task.Delay(CheckInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private TimeSpan ResolveRetention()
    {
        var days = _configuration.GetValue<int?>("PlcTagLog:RetentionDays");
        return days is > 0 ? TimeSpan.FromDays(days.Value) : DefaultRetention;
    }

    private async Task PurgeOldLogsAsync(TimeSpan retention)
    {
        try
        {
            var dbPath = _pathResolver.GetSharedDbPath();
            if (!File.Exists(dbPath)) return;

            await using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync();

            var cutoff = DateTime.UtcNow - retention;
            var cutoffStr = SqliteDateTimeHelpers.ToSqliteUtcString(cutoff);

            var rows = await conn.ExecuteAsync(
                "DELETE FROM plcTagLog WHERE dateTime < @Cutoff",
                new { Cutoff = cutoffStr });

            if (rows > 0)
                _logger.LogInformation("[plcTagLog] purged {Count} rows older than {Cutoff}", rows, cutoff);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[plcTagLog] purge failed");
        }
    }
}
