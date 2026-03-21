using System.Data;
using System.Globalization;
using DSPilot.Models.Plc;
using Microsoft.Data.Sqlite;

namespace DSPilot.Services;

/// <summary>
/// PLC 디버그용 서비스 - 빅데이터 수준의 PLC 로그 분석
/// </summary>
public class PlcDebugService
{
    private readonly ILogger<PlcDebugService> _logger;
    private string? _currentDbPath;

    public PlcDebugService(ILogger<PlcDebugService> logger)
    {
        _logger = logger;
    }

    public string? CurrentDbPath => _currentDbPath;

    /// <summary>
    /// DB 파일 연결
    /// </summary>
    public bool SetDatabasePath(string dbPath)
    {
        if (!File.Exists(dbPath))
        {
            _logger.LogWarning("DB 파일을 찾을 수 없습니다: {DbPath}", dbPath);
            return false;
        }

        _currentDbPath = dbPath;
        _logger.LogInformation("PLC 디버그 DB 연결: {DbPath}", dbPath);
        return true;
    }

    /// <summary>
    /// 모든 태그 목록 조회
    /// </summary>
    public async Task<List<PlcTagEntity>> GetAllTagsAsync()
    {
        if (string.IsNullOrEmpty(_currentDbPath))
            return new List<PlcTagEntity>();

        var tags = new List<PlcTagEntity>();

        try
        {
            using var conn = new SqliteConnection($"Data Source={_currentDbPath};Mode=ReadOnly");
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, PlcId, Address, Name, DataType FROM plcTag ORDER BY Address";

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tags.Add(new PlcTagEntity
                {
                    Id = reader.GetInt32(0),
                    PlcId = reader.GetInt32(1),
                    Address = reader.GetString(2),
                    Name = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    DataType = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "태그 목록 조회 실패");
        }

        return tags;
    }

    /// <summary>
    /// 태그별 샘플링된 로그 데이터 조회 (빅데이터 최적화)
    /// </summary>
    /// <param name="tagIds">조회할 태그 ID 목록</param>
    /// <param name="startTime">시작 시간</param>
    /// <param name="endTime">종료 시간</param>
    /// <param name="maxPointsPerTag">태그당 최대 포인트 수 (기본: 1000)</param>
    public async Task<Dictionary<int, List<PlcTagLogEntity>>> GetSampledLogsAsync(
        List<int> tagIds,
        DateTime? startTime = null,
        DateTime? endTime = null,
        int maxPointsPerTag = 1000)
    {
        var result = new Dictionary<int, List<PlcTagLogEntity>>();

        if (string.IsNullOrEmpty(_currentDbPath) || tagIds.Count == 0)
            return result;

        try
        {
            using var conn = new SqliteConnection($"Data Source={_currentDbPath};Mode=ReadOnly");
            await conn.OpenAsync();

            foreach (var tagId in tagIds)
            {
                // 1. 총 로그 개수 조회
                var totalCount = await GetLogCountAsync(conn, tagId, startTime, endTime);

                if (totalCount == 0)
                {
                    result[tagId] = new List<PlcTagLogEntity>();
                    continue;
                }

                // 2. 샘플링 간격 계산
                var samplingInterval = totalCount > maxPointsPerTag
                    ? (int)Math.Ceiling((double)totalCount / maxPointsPerTag)
                    : 1;

                // 3. 샘플링된 데이터 조회
                var logs = await GetSampledTagLogsAsync(conn, tagId, startTime, endTime, samplingInterval);
                result[tagId] = logs;

                _logger.LogInformation(
                    "태그 {TagId}: 전체 {Total}개 → 샘플링 {Sampled}개 (간격: {Interval})",
                    tagId, totalCount, logs.Count, samplingInterval);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "샘플링된 로그 조회 실패");
        }

        return result;
    }

    /// <summary>
    /// 로그 개수 조회
    /// </summary>
    private async Task<int> GetLogCountAsync(
        SqliteConnection conn,
        int tagId,
        DateTime? startTime,
        DateTime? endTime)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM plcTagLog WHERE PlcTagId = @tagId";

        if (startTime.HasValue)
        {
            cmd.CommandText += " AND julianday(DateTime) >= julianday(@startTime)";
            cmd.Parameters.AddWithValue("@startTime", ToSqliteDateTime(startTime.Value));
        }

        if (endTime.HasValue)
        {
            cmd.CommandText += " AND julianday(DateTime) <= julianday(@endTime)";
            cmd.Parameters.AddWithValue("@endTime", ToSqliteDateTime(endTime.Value));
        }

        cmd.Parameters.AddWithValue("@tagId", tagId);

        var result = await cmd.ExecuteScalarAsync();
        return result != null ? Convert.ToInt32(result) : 0;
    }

    /// <summary>
    /// 샘플링된 태그 로그 조회
    /// </summary>
    private async Task<List<PlcTagLogEntity>> GetSampledTagLogsAsync(
        SqliteConnection conn,
        int tagId,
        DateTime? startTime,
        DateTime? endTime,
        int samplingInterval)
    {
        var logs = new List<PlcTagLogEntity>();

        using var cmd = conn.CreateCommand();

        // ROW_NUMBER를 사용한 샘플링 쿼리
        cmd.CommandText = @"
            WITH numbered AS (
                SELECT
                    Id, PlcTagId, DateTime, Value,
                    ROW_NUMBER() OVER (ORDER BY DateTime) as rn
                FROM plcTagLog
                WHERE PlcTagId = @tagId";

        if (startTime.HasValue)
        {
            cmd.CommandText += " AND julianday(DateTime) >= julianday(@startTime)";
            cmd.Parameters.AddWithValue("@startTime", ToSqliteDateTime(startTime.Value));
        }

        if (endTime.HasValue)
        {
            cmd.CommandText += " AND julianday(DateTime) <= julianday(@endTime)";
            cmd.Parameters.AddWithValue("@endTime", ToSqliteDateTime(endTime.Value));
        }

        cmd.CommandText += @"
            )
            SELECT Id, PlcTagId, DateTime, Value
            FROM numbered
            WHERE (rn - 1) % @interval = 0
            ORDER BY DateTime";

        cmd.Parameters.AddWithValue("@tagId", tagId);
        cmd.Parameters.AddWithValue("@interval", samplingInterval);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            logs.Add(new PlcTagLogEntity
            {
                Id = reader.GetInt32(0),
                PlcTagId = reader.GetInt32(1),
                DateTime = ParseSqliteDateTime(reader.GetString(2)),
                Value = reader.IsDBNull(3) ? null : reader.GetString(3)
            });
        }

        return logs;
    }

    private static string ToSqliteDateTime(DateTime value)
    {
        var utcValue = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
        };

        return utcValue.ToString("yyyy-MM-dd HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    }

    private static DateTime ParseSqliteDateTime(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            return dto.LocalDateTime;
        }

        return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
    }

    /// <summary>
    /// DB 통계 정보 조회
    /// </summary>
    public async Task<(int TotalTags, long TotalLogs)> GetStatisticsAsync()
    {
        if (string.IsNullOrEmpty(_currentDbPath))
            return (0, 0);

        try
        {
            using var conn = new SqliteConnection($"Data Source={_currentDbPath};Mode=ReadOnly");
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    (SELECT COUNT(*) FROM plcTag) as TotalTags,
                    (SELECT COUNT(*) FROM plcTagLog) as TotalLogs";

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return (reader.GetInt32(0), reader.GetInt64(1));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "통계 정보 조회 실패");
        }

        return (0, 0);
    }

    /// <summary>
    /// DB 파일의 로그 시간 범위 조회
    /// </summary>
    public async Task<(DateTime? Oldest, DateTime? Latest)> GetLogTimeRangeAsync()
    {
        if (string.IsNullOrEmpty(_currentDbPath))
            return (null, null);

        try
        {
            using var conn = new SqliteConnection($"Data Source={_currentDbPath};Mode=ReadOnly");
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    MIN(DateTime) as Oldest,
                    MAX(DateTime) as Latest
                FROM plcTagLog";

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var oldest = reader.IsDBNull(0) ? (DateTime?)null : ParseSqliteDateTime(reader.GetString(0));
                var latest = reader.IsDBNull(1) ? (DateTime?)null : ParseSqliteDateTime(reader.GetString(1));
                return (oldest, latest);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "로그 시간 범위 조회 실패");
        }

        return (null, null);
    }

    /// <summary>
    /// 태그별 로그 개수 조회
    /// </summary>
    public async Task<Dictionary<int, int>> GetLogCountsByTagAsync(List<int> tagIds)
    {
        var result = new Dictionary<int, int>();

        if (string.IsNullOrEmpty(_currentDbPath) || tagIds.Count == 0)
            return result;

        try
        {
            using var conn = new SqliteConnection($"Data Source={_currentDbPath};Mode=ReadOnly");
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            var tagIdParams = string.Join(",", tagIds.Select((_, i) => $"@tagId{i}"));
            cmd.CommandText = $@"
                SELECT PlcTagId, COUNT(*) as LogCount
                FROM plcTagLog
                WHERE PlcTagId IN ({tagIdParams})
                GROUP BY PlcTagId";

            for (int i = 0; i < tagIds.Count; i++)
            {
                cmd.Parameters.AddWithValue($"@tagId{i}", tagIds[i]);
            }

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result[reader.GetInt32(0)] = reader.GetInt32(1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "태그별 로그 개수 조회 실패");
        }

        return result;
    }
}
