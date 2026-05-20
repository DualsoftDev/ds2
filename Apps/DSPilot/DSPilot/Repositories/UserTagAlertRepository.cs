using System.Data;
using System.Text;
using Dapper;
using DSPilot.Infrastructure;
using DSPilot.Models.UserTagAlerts;
using DSPilot.Services;
using Microsoft.Data.Sqlite;

namespace DSPilot.Repositories;

/// <summary>
/// UserTagAlert SQLite Dapper 저장소.
/// 모든 시간은 plcTagLog 와 동일한 ISO8601 UTC 문자열 (yyyy-MM-dd HH:mm:ss.fffffffZ).
/// </summary>
public sealed class UserTagAlertRepository : IUserTagAlertRepository
{
    private readonly IDatabasePathResolver _pathResolver;
    private readonly ILogger<UserTagAlertRepository> _logger;

    public UserTagAlertRepository(IDatabasePathResolver pathResolver, ILogger<UserTagAlertRepository> logger)
    {
        _pathResolver = pathResolver;
        _logger = logger;
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection($"Data Source={_pathResolver.GetSharedDbPath()};Mode=ReadWriteCreate;Default Timeout=20");
        await conn.OpenAsync();
        return conn;
    }

    private static string Iso(DateTime utc) => SqliteDateTimeHelpers.ToSqliteUtcString(utc);

    private static DateTime ParseIso(string s)
        => SqliteDateTimeHelpers.FromSqliteUtcString(s) ?? DateTime.MinValue;

    public async Task<long> InsertAlertAsync(UserTagAlertRecord r, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        const string sql = @"
            INSERT INTO userTagAlertLog
                (occurredAt, systemId, systemName, name, logLevel, tagAddress, valueType, matchOp, matchValue, actualValue, sourceLogId)
            VALUES
                (@OccurredAt, @SystemId, @SystemName, @Name, @LogLevel, @TagAddress, @ValueType, @MatchOp, @MatchValue, @ActualValue, @SourceLogId);
            SELECT last_insert_rowid();";

        return await conn.ExecuteScalarAsync<long>(sql, new
        {
            OccurredAt = Iso(r.OccurredAt),
            SystemId = r.SystemId.ToString(),
            r.SystemName,
            r.Name,
            r.LogLevel,
            r.TagAddress,
            r.ValueType,
            r.MatchOp,
            r.MatchValue,
            r.ActualValue,
            r.SourceLogId,
        });
    }

    private static (string Where, DynamicParameters Params) BuildFilter(
        DateTime startUtc, DateTime endUtc,
        string? name, string? level, string? system)
    {
        var sb = new StringBuilder(" WHERE occurredAt >= @Start AND occurredAt <= @End ");
        var p = new DynamicParameters();
        p.Add("Start", Iso(startUtc));
        p.Add("End", Iso(endUtc));
        if (!string.IsNullOrWhiteSpace(name))
        {
            sb.Append(" AND name LIKE @Name ");
            p.Add("Name", "%" + name.Trim() + "%");
        }
        if (!string.IsNullOrWhiteSpace(level))
        {
            sb.Append(" AND logLevel = @Level ");
            p.Add("Level", level.Trim());
        }
        if (!string.IsNullOrWhiteSpace(system))
        {
            sb.Append(" AND systemName = @System ");
            p.Add("System", system.Trim());
        }
        return (sb.ToString(), p);
    }

    public async Task<IReadOnlyList<UserTagAlertRecord>> QueryAlertsAsync(
        DateTime startUtc, DateTime endUtc,
        string? nameFilter, string? levelFilter, string? systemFilter,
        int limit, int offset,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var (where, p) = BuildFilter(startUtc, endUtc, nameFilter, levelFilter, systemFilter);
        p.Add("Limit", limit);
        p.Add("Offset", offset);

        var sql = $@"
            SELECT id, occurredAt, systemId, systemName, name, logLevel, tagAddress, valueType, matchOp, matchValue, actualValue, sourceLogId
            FROM userTagAlertLog
            {where}
            ORDER BY occurredAt DESC, id DESC
            LIMIT @Limit OFFSET @Offset";

        var rows = await conn.QueryAsync<Row>(sql, p);
        return rows.Select(MapRow).ToList();
    }

    public async Task<int> CountAlertsAsync(
        DateTime startUtc, DateTime endUtc,
        string? nameFilter, string? levelFilter, string? systemFilter,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var (where, p) = BuildFilter(startUtc, endUtc, nameFilter, levelFilter, systemFilter);
        var sql = $"SELECT COUNT(*) FROM userTagAlertLog {where}";
        return await conn.ExecuteScalarAsync<int>(sql, p);
    }

    public async Task<IReadOnlyList<UserTagAlertBucket>> GetBucketCountsAsync(
        DateTime startUtc, DateTime endUtc,
        string bucketGranularity,
        string? nameFilter, string? levelFilter, string? systemFilter,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var (where, p) = BuildFilter(startUtc, endUtc, nameFilter, levelFilter, systemFilter);

        // SQLite strftime — UTC 기반 버킷 시작 시각 문자열 (UI 측에서 다시 DateTime 으로 파싱).
        // occurredAt 은 "yyyy-MM-dd HH:mm:ss.fffffffZ" 형식. strftime 은 'T' 없는 형식도 인식.
        // week: %W = 그 해의 주 번호 (0~53). 월요일 시작 — 그룹키로만 사용하고 buckStart 도 함께 select.
        var bucketSql = bucketGranularity switch
        {
            "hour"  => "strftime('%Y-%m-%d %H:00:00', occurredAt)",
            "day"   => "strftime('%Y-%m-%d 00:00:00', occurredAt)",
            // ISO 주: 같은 주의 월요일 날짜 — strftime 의 weekday(%w: 0=Sun) 로 보정.
            "week"  => "strftime('%Y-%m-%d 00:00:00', occurredAt, '-' || ((strftime('%w', occurredAt) + 6) % 7) || ' days')",
            "month" => "strftime('%Y-%m-01 00:00:00', occurredAt)",
            _        => "strftime('%Y-%m-%d 00:00:00', occurredAt)",
        };

        var sql = $@"
            SELECT {bucketSql} AS BucketStartStr, logLevel AS LogLevel, COUNT(*) AS Count
            FROM userTagAlertLog
            {where}
            GROUP BY BucketStartStr, logLevel
            ORDER BY BucketStartStr ASC";

        var rows = await conn.QueryAsync<(string BucketStartStr, string LogLevel, int Count)>(sql, p);
        return rows.Select(r => new UserTagAlertBucket(
            DateTime.SpecifyKind(DateTime.Parse(r.BucketStartStr, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc),
            r.LogLevel ?? "Info",
            r.Count)).ToList();
    }

    public async Task<IReadOnlyList<UserTagAlertTopRow>> GetTopByNameAsync(
        DateTime startUtc, DateTime endUtc,
        int topN,
        string? levelFilter, string? systemFilter,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var (where, p) = BuildFilter(startUtc, endUtc, null, levelFilter, systemFilter);
        p.Add("TopN", topN);
        var sql = $@"
            SELECT name AS Name, logLevel AS LogLevel, COUNT(*) AS Count
            FROM userTagAlertLog
            {where}
            GROUP BY name, logLevel
            ORDER BY Count DESC
            LIMIT @TopN";
        var rows = await conn.QueryAsync<UserTagAlertTopRow>(sql, p);
        return rows.ToList();
    }

    public async Task<IReadOnlyDictionary<string, int>> GetLevelCountsAsync(
        DateTime startUtc, DateTime endUtc,
        string? nameFilter, string? systemFilter,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var (where, p) = BuildFilter(startUtc, endUtc, nameFilter, null, systemFilter);
        var sql = $@"
            SELECT logLevel AS LogLevel, COUNT(*) AS Count
            FROM userTagAlertLog
            {where}
            GROUP BY logLevel";
        var rows = await conn.QueryAsync<(string LogLevel, int Count)>(sql, p);
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (lvl, cnt) in rows) d[lvl ?? "Info"] = cnt;
        return d;
    }

    public async Task<IReadOnlyList<UserTagAlertRecord>> GetLatestAlertsAsync(int maxCount, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var sql = @"
            SELECT id, occurredAt, systemId, systemName, name, logLevel, tagAddress, valueType, matchOp, matchValue, actualValue, sourceLogId
            FROM userTagAlertLog
            ORDER BY id DESC
            LIMIT @Limit";
        var rows = await conn.QueryAsync<Row>(sql, new { Limit = maxCount });
        return rows.Select(MapRow).ToList();
    }

    public async Task<long> GetMaxAlertIdAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteScalarAsync<long?>("SELECT MAX(id) FROM userTagAlertLog") ?? 0L;
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteAsync(
            "DELETE FROM userTagAlertLog WHERE occurredAt < @Cutoff",
            new { Cutoff = Iso(cutoffUtc) });
    }

    public async Task<int> RebuildDailyAggregatesAsync(DateTime fromDateUtc, DateTime toDateUtc, CancellationToken ct = default)
    {
        if (toDateUtc < fromDateUtc) return 0;
        await using var conn = await OpenAsync();

        // 해당 범위의 daily 행 제거 후 다시 채움 — backfill 시점에 중복 안전.
        var fromDate = fromDateUtc.Date.ToString("yyyy-MM-dd");
        var toDate = toDateUtc.Date.ToString("yyyy-MM-dd");

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(
                "DELETE FROM userTagAlertDaily WHERE bucketDate >= @From AND bucketDate <= @To",
                new { From = fromDate, To = toDate }, tx);

            const string aggSql = @"
                INSERT INTO userTagAlertDaily (bucketDate, systemName, name, logLevel, count)
                SELECT strftime('%Y-%m-%d', occurredAt) AS bucketDate,
                       systemName, name, logLevel,
                       COUNT(*) AS count
                FROM userTagAlertLog
                WHERE strftime('%Y-%m-%d', occurredAt) BETWEEN @From AND @To
                GROUP BY bucketDate, systemName, name, logLevel";

            var inserted = await conn.ExecuteAsync(aggSql, new { From = fromDate, To = toDate }, tx);
            await tx.CommitAsync(ct);
            return inserted;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<DateTime?> GetLastAggregatedDateAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var s = await conn.ExecuteScalarAsync<string?>("SELECT MAX(bucketDate) FROM userTagAlertDaily");
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var d))
            return d.Date;
        return null;
    }

    private static UserTagAlertRecord MapRow(Row r) => new(
        Id: r.Id,
        OccurredAt: ParseIso(r.OccurredAt),
        SystemId: Guid.TryParse(r.SystemId, out var g) ? g : Guid.Empty,
        SystemName: r.SystemName ?? string.Empty,
        Name: r.Name ?? string.Empty,
        LogLevel: r.LogLevel ?? "Info",
        TagAddress: r.TagAddress ?? string.Empty,
        ValueType: r.ValueType ?? "Bit",
        MatchOp: r.MatchOp ?? "RisingEdge",
        MatchValue: r.MatchValue,
        ActualValue: r.ActualValue ?? string.Empty,
        SourceLogId: r.SourceLogId);

    private sealed class Row
    {
        public long Id { get; set; }
        public string OccurredAt { get; set; } = string.Empty;
        public string SystemId { get; set; } = string.Empty;
        public string? SystemName { get; set; }
        public string? Name { get; set; }
        public string? LogLevel { get; set; }
        public string? TagAddress { get; set; }
        public string? ValueType { get; set; }
        public string? MatchOp { get; set; }
        public string? MatchValue { get; set; }
        public string? ActualValue { get; set; }
        public long? SourceLogId { get; set; }
    }
}
