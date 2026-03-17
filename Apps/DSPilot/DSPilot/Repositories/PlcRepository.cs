using Dapper;
using DSPilot.Models.Plc;
using Microsoft.Data.Sqlite;
using System.Data;

namespace DSPilot.Repositories;

/// <summary>
/// PLC 데이터 저장소 - Dapper 기반 SQLite 구현
/// </summary>
public class PlcRepository : IPlcRepository
{
    private readonly string _connectionString;
    private readonly ILogger<PlcRepository> _logger;

    public PlcRepository(IConfiguration configuration, ILogger<PlcRepository> logger)
    {
        var dbPath = configuration["PlcDatabase:SourceDbPath"]
            ?? throw new InvalidOperationException("PlcDatabase:SourceDbPath is not configured");

        _connectionString = $"Data Source={dbPath};Mode=ReadOnly;";
        _logger = logger;
    }

    /// <summary>
    /// DB 연결 생성
    /// </summary>
    private IDbConnection CreateConnection() => new SqliteConnection(_connectionString);

    /// <inheritdoc />
    public async Task<List<PlcEntity>> GetAllPlcsAsync()
    {
        using var connection = CreateConnection();

        const string sql = @"
            SELECT
                id as Id,
                projectId as ProjectId,
                name as Name,
                connection as Connection
            FROM plc
            ORDER BY id";

        var plcs = await connection.QueryAsync<PlcEntity>(sql);
        return plcs.ToList();
    }

    /// <inheritdoc />
    public async Task<List<PlcTagEntity>> GetAllTagsAsync()
    {
        using var connection = CreateConnection();

        const string sql = @"
            SELECT
                id as Id,
                plcId as PlcId,
                name as Name,
                address as Address,
                dataType as DataType
            FROM plcTag
            ORDER BY plcId, id";

        var tags = await connection.QueryAsync<PlcTagEntity>(sql);
        return tags.ToList();
    }

    /// <inheritdoc />
    public async Task<PlcTagEntity?> GetTagByIdAsync(int tagId)
    {
        using var connection = CreateConnection();

        const string sql = @"
            SELECT
                id as Id,
                plcId as PlcId,
                name as Name,
                address as Address,
                dataType as DataType
            FROM plcTag
            WHERE id = @TagId
            LIMIT 1";

        var tag = await connection.QueryFirstOrDefaultAsync<PlcTagEntity>(sql, new { TagId = tagId });
        return tag;
    }

    /// <inheritdoc />
    public async Task<List<PlcTagLogEntity>> GetNewLogsAsync(DateTime sinceDateTime)
    {
        using var connection = CreateConnection();

        // DB에 UTC 시간으로 저장되어 있으므로 UTC로 변환
        var sinceUtc = sinceDateTime.Kind == DateTimeKind.Local
            ? sinceDateTime.ToUniversalTime()
            : sinceDateTime;

        // ISO 8601 형식으로 문자열 변환 (DB에 "Z" 접미사로 저장되어 있음)
        var sinceStr = sinceUtc.ToString("yyyy-MM-dd HH:mm:ss.fffffff") + "Z";

        const string sql = @"
            SELECT
                id as Id,
                plcTagId as PlcTagId,
                dateTime as DateTime,
                value as Value
            FROM plcTagLog
            WHERE dateTime > @SinceDateTime
            ORDER BY dateTime ASC, id ASC";

        var logs = await connection.QueryAsync<PlcTagLogEntity>(sql, new { SinceDateTime = sinceStr });
        var result = logs.ToList();

        _logger.LogDebug("Retrieved {Count} new logs since {DateTime}", result.Count, sinceDateTime);

        return result;
    }

    /// <inheritdoc />
    public async Task<List<PlcTagLogEntity>> GetLogsInRangeAsync(DateTime startExclusive, DateTime endInclusive)
    {
        using var connection = CreateConnection();

        // DB에 UTC 시간으로 저장되어 있으므로 UTC로 변환
        var startUtc = startExclusive.Kind == DateTimeKind.Local
            ? startExclusive.ToUniversalTime()
            : startExclusive;
        var endUtc = endInclusive.Kind == DateTimeKind.Local
            ? endInclusive.ToUniversalTime()
            : endInclusive;

        // ISO 8601 형식으로 문자열 변환 (DB에 "Z" 접미사로 저장되어 있음)
        var startStr = startUtc.ToString("yyyy-MM-dd HH:mm:ss.fffffff") + "Z";
        var endStr = endUtc.ToString("yyyy-MM-dd HH:mm:ss.fffffff") + "Z";

        _logger.LogInformation("🔍 GetLogsInRangeAsync: Local ({LocalStart} ~ {LocalEnd}] → UTC ({UtcStart} ~ {UtcEnd}]",
            startExclusive.ToString("HH:mm:ss.fff"),
            endInclusive.ToString("HH:mm:ss.fff"),
            startStr,
            endStr);

        const string sql = @"
            SELECT
                id as Id,
                plcTagId as PlcTagId,
                dateTime as DateTime,
                value as Value
            FROM plcTagLog
            WHERE dateTime > @StartExclusive
              AND dateTime <= @EndInclusive
            ORDER BY dateTime ASC, id ASC";

        var logs = await connection.QueryAsync<PlcTagLogEntity>(sql, new
        {
            StartExclusive = startStr,
            EndInclusive = endStr
        });

        var result = logs.ToList();

        _logger.LogInformation(
            "📊 Retrieved {Count} logs",
            result.Count);

        return result;
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetOldestLogDateTimeAsync()
    {
        using var connection = CreateConnection();

        const string sql = "SELECT MIN(dateTime) FROM plcTagLog";
        var resultStr = await connection.ExecuteScalarAsync<string>(sql);

        if (string.IsNullOrEmpty(resultStr))
        {
            _logger.LogInformation("📅 GetOldestLogDateTimeAsync: NULL");
            return null;
        }

        // DB에서 UTC 문자열로 저장되어 있으므로 UTC로 파싱 후 로컬 시간으로 변환
        var utcDateTime = DateTime.Parse(resultStr, null, System.Globalization.DateTimeStyles.RoundtripKind);
        var localDateTime = utcDateTime.ToLocalTime();

        _logger.LogInformation("📅 GetOldestLogDateTimeAsync: {UtcResult} (UTC) → {LocalResult} (Local)",
            resultStr, localDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        return localDateTime;
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestLogDateTimeAsync()
    {
        using var connection = CreateConnection();

        const string sql = "SELECT MAX(dateTime) FROM plcTagLog";
        var resultStr = await connection.ExecuteScalarAsync<string>(sql);

        if (string.IsNullOrEmpty(resultStr))
        {
            _logger.LogInformation("📅 GetLatestLogDateTimeAsync: NULL");
            return null;
        }

        // DB에서 UTC 문자열로 저장되어 있으므로 UTC로 파싱 후 로컬 시간으로 변환
        var utcDateTime = DateTime.Parse(resultStr, null, System.Globalization.DateTimeStyles.RoundtripKind);
        var localDateTime = utcDateTime.ToLocalTime();

        _logger.LogInformation("📅 GetLatestLogDateTimeAsync: {UtcResult} (UTC) → {LocalResult} (Local)",
            resultStr, localDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        return localDateTime;
    }

    /// <inheritdoc />
    public async Task<PlcTagLogEntity?> GetLatestLogByTagIdAsync(int tagId)
    {
        using var connection = CreateConnection();

        const string sql = @"
            SELECT
                id as Id,
                plcTagId as PlcTagId,
                dateTime as DateTime,
                value as Value
            FROM plcTagLog
            WHERE plcTagId = @TagId
            ORDER BY dateTime DESC
            LIMIT 1";

        var log = await connection.QueryFirstOrDefaultAsync<PlcTagLogEntity>(sql, new { TagId = tagId });
        return log;
    }

    /// <inheritdoc />
    public async Task<int> GetTotalLogCountAsync()
    {
        using var connection = CreateConnection();

        const string sql = "SELECT COUNT(*) FROM plcTagLog";
        var count = await connection.ExecuteScalarAsync<int>(sql);

        return count;
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            // 테이블 존재 확인
            const string sql = @"
                SELECT COUNT(*) FROM sqlite_master
                WHERE type='table' AND name IN ('plc', 'plcTag', 'plcTagLog')";

            var tableCount = await connection.ExecuteScalarAsync<int>(sql);

            if (tableCount != 3)
            {
                _logger.LogWarning("Expected 3 tables, found {Count}", tableCount);
                return false;
            }

            _logger.LogInformation("Database connection test successful");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database connection test failed");
            return false;
        }
    }
}
