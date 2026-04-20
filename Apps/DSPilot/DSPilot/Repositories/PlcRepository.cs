using Dapper;
using DSPilot.Engine;
using DSPilot.Models.Plc;
using DSPilot.Services;
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

    public PlcRepository(IDatabasePathResolver pathResolver, ILogger<PlcRepository> logger)
    {
        var dbPath = pathResolver.GetPlcDbPath();
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new InvalidOperationException("PLC database path is not configured.");
        }

        // ReadOnly 제거 - PlcCaptureService가 DB를 생성하고 쓰기 작업을 수행함
        _connectionString = DatabaseConfig.createConnectionString(dbPath);
        _logger = logger;
        _logger.LogInformation("PLC Database path: {DbPath} (Unified mode)", dbPath);
    }

    /// <summary>
    /// DB 연결 생성
    /// </summary>
    private IDbConnection CreateConnection() => new SqliteConnection(_connectionString);

    /// <summary>
    /// 테이블 존재 여부 확인
    /// </summary>
    private async Task<bool> TableExistsAsync(IDbConnection connection, string tableName)
    {
        const string sql = @"
            SELECT COUNT(*) FROM sqlite_master
            WHERE type='table' AND name = @TableName";

        var count = await connection.ExecuteScalarAsync<int>(sql, new { TableName = tableName });
        return count > 0;
    }

    /// <summary>
    /// 필요한 모든 테이블이 존재하는지 확인
    /// </summary>
    private async Task<bool> RequiredTablesExistAsync(IDbConnection connection)
    {
        return await TableExistsAsync(connection, "plc") &&
               await TableExistsAsync(connection, "plcTag") &&
               await TableExistsAsync(connection, "plcTagLog");
    }

    /// <inheritdoc />
    public async Task<List<PlcEntity>> GetAllPlcsAsync()
    {
        try
        {
            using var connection = CreateConnection();

            if (!await TableExistsAsync(connection, "plc"))
            {
                _logger.LogWarning("Table 'plc' does not exist. Returning empty list.");
                return new List<PlcEntity>();
            }

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
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "SQLite error in GetAllPlcsAsync");
            return new List<PlcEntity>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in GetAllPlcsAsync");
            return new List<PlcEntity>();
        }
    }

    /// <inheritdoc />
    public async Task<List<PlcTagEntity>> GetAllTagsAsync()
    {
        try
        {
            using var connection = CreateConnection();

            if (!await TableExistsAsync(connection, "plcTag"))
            {
                _logger.LogWarning("Table 'plcTag' does not exist. Returning empty list.");
                return new List<PlcTagEntity>();
            }

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
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "SQLite error in GetAllTagsAsync");
            return new List<PlcTagEntity>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in GetAllTagsAsync");
            return new List<PlcTagEntity>();
        }
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
    public async Task<List<PlcTagLogEntity>> GetLatestLogsByAddressesBeforeAsync(
        List<string> addresses, DateTime atOrBefore)
    {
        if (addresses.Count == 0) return new List<PlcTagLogEntity>();

        using var connection = CreateConnection();
        var atOrBeforeStr = QueryHelpers.toSqliteUtcString(atOrBefore);

        const string sql = @"
WITH ranked AS (
    SELECT
        l.id AS Id,
        l.plcTagId AS PlcTagId,
        l.dateTime AS DateTime,
        l.value AS Value,
        ROW_NUMBER() OVER (
            PARTITION BY l.plcTagId
            ORDER BY l.dateTime DESC, l.id DESC
        ) AS RowNum
    FROM plcTagLog l
    INNER JOIN plcTag t ON l.plcTagId = t.id
    WHERE t.address IN @Addresses
      AND l.dateTime <= @AtOrBefore
)
SELECT
    Id,
    PlcTagId,
    DateTime,
    Value
FROM ranked
WHERE RowNum = 1";

        var logs = await connection.QueryAsync<PlcTagLogEntity>(sql, new
        {
            Addresses = addresses,
            AtOrBefore = atOrBeforeStr
        });

        return logs.ToList();
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

            // 인덱스 자동 생성 (이미 존재하면 무시)
            await EnsureIndexesAsync(connection);

            _logger.LogInformation("Database connection test successful");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database connection test failed");
            return false;
        }
    }

    /// <summary>
    /// plcTagLog 성능 최적화 인덱스 생성 (IF NOT EXISTS로 안전)
    /// </summary>
    private async Task EnsureIndexesAsync(IDbConnection connection)
    {
        var indexes = new[]
        {
            "CREATE INDEX IF NOT EXISTS idx_plcTagLog_dateTime_id ON plcTagLog(dateTime, id)",
            "CREATE INDEX IF NOT EXISTS idx_plcTagLog_tagId_id ON plcTagLog(plcTagId, id DESC)",
            "CREATE INDEX IF NOT EXISTS idx_plcTag_address ON plcTag(address)",
        };

        foreach (var ddl in indexes)
        {
            try
            {
                await connection.ExecuteAsync(ddl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create index: {Sql}", ddl);
            }
        }

        _logger.LogInformation("Database indexes ensured");
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


    public async Task<List<DateTime>> FindRecentRisingEdgesAsync(string address, int count)
    {
        using var connection = CreateConnection();

        // 최신 로그부터 역순으로 스캔하여 최근 N개 rising edge만 빠르게 조회
        // 서브쿼리로 해당 태그의 최근 로그만 제한적으로 읽어 전체 테이블 스캔 방지
        var sql = $@"
WITH recent_logs AS (
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
        ) OVER (ORDER BY l.DateTime ASC, l.Id ASC) AS PreviousNormalizedValue
    FROM plcTagLog l
    INNER JOIN plcTag t ON l.PlcTagId = t.Id
    WHERE t.Address = @Address
),
edges AS (
    SELECT DateTime
    FROM recent_logs
    WHERE coalesce(PreviousNormalizedValue, '0') = '0'
      AND NormalizedValue = '1'
    ORDER BY DateTime DESC
    LIMIT @Count
)
SELECT DateTime FROM edges ORDER BY DateTime ASC";

        var rows = await connection.QueryAsync<PlcTagDateTimeRow>(sql, new
        {
            Address = address,
            Count = count
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

    /// <inheritdoc />
    public async Task<List<PlcTagLogEntity>> GetTagLogsAsync(string tagAddress, int count)
    {
        using var connection = CreateConnection();

        const string sql = @"
            SELECT
                l.id as Id,
                l.plcTagId as PlcTagId,
                l.dateTime as DateTime,
                l.value as Value,
                t.name as TagName,
                t.address as Address
            FROM plcTagLog l
            INNER JOIN plcTag t ON l.plcTagId = t.id
            WHERE t.address = @Address
            ORDER BY l.id DESC
            LIMIT @Count";

        var rows = await connection.QueryAsync<PlcTagLogAddressRow>(sql, new
        {
            Address = tagAddress,
            Count = count
        });

        return rows.Select(row => new PlcTagLogEntity
        {
            Id = row.Id,
            PlcTagId = row.PlcTagId,
            DateTime = ParseSqliteDateTime(row.DateTime),
            Value = row.Value,
            TagName = row.TagName,
            Address = row.Address
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<List<PlcTagLogEntity>> GetTagLogsByTimeRangeAsync(
        string tagAddress, DateTime startTime, DateTime endTime)
    {
        try
        {
            using var connection = CreateConnection();

            if (!await RequiredTablesExistAsync(connection))
            {
                _logger.LogWarning("Required tables do not exist. Returning empty list.");
                return new List<PlcTagLogEntity>();
            }

            var startStr = QueryHelpers.toSqliteUtcString(startTime);
            var endStr = QueryHelpers.toSqliteUtcString(endTime);

            const string sql = @"
                SELECT
                    l.id as Id,
                    l.plcTagId as PlcTagId,
                    l.dateTime as DateTime,
                    l.value as Value,
                    t.name as TagName,
                    t.address as Address
                FROM plcTagLog l
                INNER JOIN plcTag t ON l.plcTagId = t.id
                WHERE t.address = @Address
                  AND l.dateTime >= @StartTime
                  AND l.dateTime <= @EndTime
                ORDER BY l.id ASC";

            var rows = await connection.QueryAsync<PlcTagLogAddressRow>(sql, new
            {
                Address = tagAddress,
                StartTime = startStr,
                EndTime = endStr
            });

            return rows.Select(row => new PlcTagLogEntity
            {
                Id = row.Id,
                PlcTagId = row.PlcTagId,
                DateTime = ParseSqliteDateTime(row.DateTime),
                Value = row.Value,
                TagName = row.TagName,
                Address = row.Address
            }).ToList();
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "SQLite error in GetTagLogsByTimeRangeAsync for address {Address}", tagAddress);
            return new List<PlcTagLogEntity>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in GetTagLogsByTimeRangeAsync for address {Address}", tagAddress);
            return new List<PlcTagLogEntity>();
        }
    }

    /// <inheritdoc />
    public async Task<(Dictionary<string, string> TagValues, long MaxLogId)> GetLatestValuePerTagAsync()
    {
        using var connection = CreateConnection();

        if (!await RequiredTablesExistAsync(connection))
        {
            _logger.LogDebug("Required tables (plcTag/plcTagLog) do not exist yet. Returning empty state.");
            return (new Dictionary<string, string>(), 0L);
        }

        // 모든 태그의 최신 로그 값을 단일 쿼리로 조회
        const string sql = @"
            SELECT t.address AS Address, l.value AS Value
            FROM plcTag t
            INNER JOIN plcTagLog l ON l.id = (
                SELECT MAX(l2.id) FROM plcTagLog l2 WHERE l2.plcTagId = t.id
            )
            WHERE t.address IS NOT NULL AND t.address != ''";

        var rows = await connection.QueryAsync<(string Address, string? Value)>(sql);
        var dict = new Dictionary<string, string>();
        foreach (var row in rows)
        {
            dict[row.Address] = row.Value ?? "0";
        }

        // 현재 최대 log ID 조회 (델타 폴링 시작점)
        var maxId = await connection.ExecuteScalarAsync<long?>("SELECT MAX(id) FROM plcTagLog") ?? 0L;

        return (dict, maxId);
    }

    /// <inheritdoc />
    public async Task<List<PlcTagLogEntity>> GetLogsAfterIdAsync(long afterId)
    {
        using var connection = CreateConnection();

        if (!await RequiredTablesExistAsync(connection))
        {
            return new List<PlcTagLogEntity>();
        }

        const string sql = @"
            SELECT
                l.id AS Id,
                l.plcTagId AS PlcTagId,
                l.dateTime AS DateTime,
                l.value AS Value,
                t.name AS TagName,
                t.address AS Address
            FROM plcTagLog l
            INNER JOIN plcTag t ON l.plcTagId = t.id
            WHERE l.id > @AfterId
            ORDER BY l.id ASC";

        var rows = await connection.QueryAsync<PlcTagLogAddressRow>(sql, new { AfterId = afterId });
        return rows.Select(row => new PlcTagLogEntity
        {
            Id = row.Id,
            PlcTagId = row.PlcTagId,
            DateTime = ParseSqliteDateTime(row.DateTime),
            Value = row.Value,
            TagName = row.TagName,
            Address = row.Address
        }).ToList();
    }
}
