using Dapper;
using DSPilot.Engine;
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

        // 환경 변수 확장 (%APPDATA% 등)
        dbPath = Environment.ExpandEnvironmentVariables(dbPath);

        // Windows 경로 구분자 정규화 (/ → \)
        dbPath = dbPath.Replace('/', Path.DirectorySeparatorChar);

        // 상대 경로를 절대 경로로 변환
        if (!Path.IsPathRooted(dbPath))
        {
            dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dbPath);
        }

        // ReadOnly 제거 - PlcCaptureService가 DB를 생성하고 쓰기 작업을 수행함
        _connectionString = $"Data Source={dbPath};";
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

        // F# QueryHelpers 사용
        var sinceStr = QueryHelpers.toSqliteUtcString(sinceDateTime);

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

        // F# QueryHelpers 사용
        var startStr = QueryHelpers.toSqliteUtcString(startExclusive);
        var endStr = QueryHelpers.toSqliteUtcString(endInclusive);

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

        // F# QueryHelpers 사용 (FSharpOption → nullable)
        var fsharpOption = QueryHelpers.fromSqliteUtcString(resultStr);
        var localDateTime = Microsoft.FSharp.Core.FSharpOption<DateTime>.get_IsSome(fsharpOption)
            ? fsharpOption.Value
            : (DateTime?)null;

        _logger.LogInformation("📅 GetOldestLogDateTimeAsync: {UtcResult} (UTC) → {LocalResult} (Local)",
            resultStr, localDateTime?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "NULL");

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

        // F# QueryHelpers 사용 (FSharpOption → nullable)
        var fsharpOption = QueryHelpers.fromSqliteUtcString(resultStr);
        var localDateTime = Microsoft.FSharp.Core.FSharpOption<DateTime>.get_IsSome(fsharpOption)
            ? fsharpOption.Value
            : (DateTime?)null;

        _logger.LogInformation("📅 GetLatestLogDateTimeAsync: {UtcResult} (UTC) → {LocalResult} (Local)",
            resultStr, localDateTime?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "NULL");

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

    public async Task<List<PlcTagLogEntity>> GetTagLogsByAddressInRangeAsync(
        string address, DateTime startTime, DateTime endTime)
    {
        using var connection = CreateConnection();
        var startStr = QueryHelpers.toSqliteUtcString(startTime);
        var endStr = QueryHelpers.toSqliteUtcString(endTime);

        const string sql = @"
SELECT l.Id, l.PlcTagId, l.DateTime, l.Value
FROM plcTagLog l
INNER JOIN plcTag t ON l.PlcTagId = t.Id
WHERE t.Address = @Address
  AND l.DateTime >= @StartTime
  AND l.DateTime <= @EndTime
ORDER BY l.DateTime ASC";

        var logs = await connection.QueryAsync<PlcTagLogEntity>(sql, new
        {
            Address = address,
            StartTime = startStr,
            EndTime = endStr
        });

        return logs.ToList();
    }

    public async Task<List<PlcTagLogEntity>> GetMultipleTagLogsInRangeAsync(
        List<string> addresses, DateTime startTime, DateTime endTime)
    {
        if (addresses.Count == 0) return new List<PlcTagLogEntity>();

        using var connection = CreateConnection();
        var startStr = QueryHelpers.toSqliteUtcString(startTime);
        var endStr = QueryHelpers.toSqliteUtcString(endTime);

        const string sql = @"
SELECT
    l.Id AS Id,
    l.PlcTagId AS PlcTagId,
    l.DateTime AS DateTime,
    l.Value AS Value,
    t.Name AS TagName,
    t.Address AS Address
FROM plcTagLog l
INNER JOIN plcTag t ON l.PlcTagId = t.Id
WHERE t.Address IN @Addresses
  AND l.DateTime >= @StartTime
  AND l.DateTime <= @EndTime
ORDER BY l.DateTime ASC";

        var rows = await connection.QueryAsync<PlcTagLogAddressRow>(sql, new
        {
            Addresses = addresses,
            StartTime = startStr,
            EndTime = endStr
        });

        var logs = rows.Select(row => new PlcTagLogEntity
        {
            Id = row.Id,
            PlcTagId = row.PlcTagId,
            DateTime = ParseSqliteDateTime(row.DateTime),
            Value = row.Value,
            PlcTag = new PlcTagEntity
            {
                Name = row.TagName,
                Address = row.Address
            }
        }).ToList();

        return logs;
    }

    public async Task<List<PlcTagLogEntity>> GetMultipleTagRisingEdgesInRangeAsync(
        List<string> addresses, DateTime startTime, DateTime endTime)
    {
        if (addresses.Count == 0) return new List<PlcTagLogEntity>();

        using var connection = CreateConnection();
        var startStr = QueryHelpers.toSqliteUtcString(startTime);
        var endStr = QueryHelpers.toSqliteUtcString(endTime);

        const string sql = @"
WITH ordered_logs AS (
    SELECT
        l.Id AS Id,
        l.PlcTagId AS PlcTagId,
        l.DateTime AS DateTime,
        l.Value AS Value,
        t.Name AS TagName,
        t.Address AS Address,
        CASE
            WHEN lower(trim(coalesce(l.Value, ''))) IN ('1', 'true', 'on') THEN '1'
            ELSE '0'
        END AS NormalizedValue,
        LAG(
            CASE
                WHEN lower(trim(coalesce(l.Value, ''))) IN ('1', 'true', 'on') THEN '1'
                ELSE '0'
            END
        ) OVER (PARTITION BY l.PlcTagId ORDER BY l.DateTime ASC, l.Id ASC) AS PreviousNormalizedValue
    FROM plcTagLog l
    INNER JOIN plcTag t ON l.PlcTagId = t.Id
    WHERE t.Address IN @Addresses
      AND l.DateTime >= @StartTime
      AND l.DateTime <= @EndTime
)
SELECT
    Id,
    PlcTagId,
    DateTime,
    Value,
    TagName,
    Address
FROM ordered_logs
WHERE coalesce(PreviousNormalizedValue, '0') = '0'
  AND NormalizedValue = '1'
ORDER BY DateTime ASC, Id ASC";

        var rows = await connection.QueryAsync<PlcTagLogAddressRow>(sql, new
        {
            Addresses = addresses,
            StartTime = startStr,
            EndTime = endStr
        });

        return rows.Select(row => new PlcTagLogEntity
        {
            Id = row.Id,
            PlcTagId = row.PlcTagId,
            DateTime = ParseSqliteDateTime(row.DateTime),
            Value = row.Value,
            PlcTag = new PlcTagEntity
            {
                Name = row.TagName,
                Address = row.Address
            }
        }).ToList();
    }

    public async Task<List<DateTime>> FindRisingEdgesAsync(
        string address, DateTime startTime, DateTime endTime)
    {
        using var connection = CreateConnection();
        var startStr = QueryHelpers.toSqliteUtcString(startTime);
        var endStr = QueryHelpers.toSqliteUtcString(endTime);

        const string sql = @"
WITH ordered_logs AS (
    SELECT
        l.Id AS Id,
        l.DateTime AS DateTime,
        CASE
            WHEN lower(trim(coalesce(l.Value, ''))) IN ('1', 'true', 'on') THEN '1'
            ELSE '0'
        END AS NormalizedValue,
        LAG(
            CASE
                WHEN lower(trim(coalesce(l.Value, ''))) IN ('1', 'true', 'on') THEN '1'
                ELSE '0'
            END
        ) OVER (PARTITION BY l.PlcTagId ORDER BY l.DateTime ASC, l.Id ASC) AS PreviousNormalizedValue
    FROM plcTagLog l
    INNER JOIN plcTag t ON l.PlcTagId = t.Id
    WHERE t.Address = @Address
      AND l.DateTime >= @StartTime
      AND l.DateTime <= @EndTime
)
SELECT
    Id,
    DateTime
FROM ordered_logs
WHERE coalesce(PreviousNormalizedValue, '0') = '0'
  AND NormalizedValue = '1'
ORDER BY DateTime ASC, Id ASC";

        var rows = await connection.QueryAsync<PlcTagDateTimeRow>(sql, new
        {
            Address = address,
            StartTime = startStr,
            EndTime = endStr
        });

        return rows.Select(row => ParseSqliteDateTime(row.DateTime)).ToList();
    }


    private static DateTime ParseSqliteDateTime(string value)
    {
        var fsharpOption = QueryHelpers.fromSqliteUtcString(value);
        if (Microsoft.FSharp.Core.FSharpOption<DateTime>.get_IsSome(fsharpOption))
            return fsharpOption.Value;

        return DateTime.Parse(value);
    }

    private sealed class PlcTagLogAddressRow
    {
        public int Id { get; set; }
        public int PlcTagId { get; set; }
        public string DateTime { get; set; } = string.Empty;
        public string? Value { get; set; }
        public string TagName { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
    }

    private sealed class PlcTagLogValueRow
    {
        public int Id { get; set; }
        public string DateTime { get; set; } = string.Empty;
        public string? Value { get; set; }
    }

    private sealed class PlcTagDateTimeRow
    {
        public int Id { get; set; }
        public string DateTime { get; set; } = string.Empty;
    }
}
