// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
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
/// 모든 조회(목록/카운트/버킷/Top/레벨/최신)는 디바이스별 이상감지 차단 규칙
/// (AbnormalAlarm.DeviceFilters)에 걸린 Abnormal 행을 제외한다 — uptime/oee 통계·사이드바 피드·배지가
/// 한 곳에서 일관되게 숨겨지도록 SQL WHERE 레벨에서 거른다(규칙 해제 시 다시 표시).
/// </summary>
public sealed class UserTagAlertRepository : IUserTagAlertRepository
{
    private readonly IDatabasePathResolver _pathResolver;
    private readonly AppSettingsService _appSettings;
    private readonly ILogger<UserTagAlertRepository> _logger;

    public UserTagAlertRepository(IDatabasePathResolver pathResolver, AppSettingsService appSettings, ILogger<UserTagAlertRepository> logger)
    {
        _pathResolver = pathResolver;
        _appSettings = appSettings;
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

    // 구분(카테고리) 판별 SSOT — abnormal 행은 valueType='Abnormal'(AbnormalEventService.PersistToLogAsync),
    // 그 외는 사용자정의(usertag). 스택 막대 그룹키로도 쓴다.
    private const string CategoryCase = "CASE WHEN valueType = 'Abnormal' THEN 'ABNORMAL' ELSE 'USERTAG' END";

    private (string Where, DynamicParameters Params) BuildFilter(
        DateTime startUtc, DateTime endUtc,
        string? name, string? level, string? system, string? category = null, string? flow = null)
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
        // 설비(Flow)별 필터 — tagAddress 의 맨 앞 세그먼트가 FLOW(AbnormalEventService.PersistToLogAsync).
        // UserTag 는 Flow 에 속하지 않으므로 이 필터는 자동감지(valueType='Abnormal') 행만 남긴다
        // (= flow 선택 시 사용자정의 알람은 자연히 제외). 구 이력("WORK / CALL", Flow 누락)은 매칭되지 않는다.
        if (!string.IsNullOrWhiteSpace(flow))
        {
            var f = flow.Trim();
            sb.Append(@" AND valueType = 'Abnormal' AND (tagAddress = @Flow OR tagAddress LIKE @FlowPre ESCAPE '\') ");
            p.Add("Flow", f);
            p.Add("FlowPre", EscapeLike(f) + " / %");
        }
        // 구분 필터 — abnormal: valueType='Abnormal', usertag: 그 외(포함 NULL).
        var cat = category?.Trim().ToLowerInvariant();
        if (cat == "abnormal")
            sb.Append(" AND valueType = 'Abnormal' ");
        else if (cat == "usertag")
            sb.Append(" AND (valueType IS NULL OR valueType <> 'Abnormal') ");
        AppendDeviceFilterExclusion(sb, p);
        AppendUserTagFilterExclusion(sb, p);
        return (sb.ToString(), p);
    }

    /// <summary>
    /// 디바이스별 이상감지 차단 규칙을 WHERE 절에 반영. Abnormal 행의 디바이스는 tagAddress 의
    /// 마지막 경로 세그먼트("WORK / DEVICE.API" 또는 "DEVICE.API")의 "DEVICE." 접두로 식별한다
    /// (AbnormalEventService.PersistToLogAsync 의 BuildPath(WorkName, CallName) 형식과 일치).
    /// kind 매칭은 matchValue = AbnormalKind 이름(KindName) — usertag 행(valueType != 'Abnormal')은 건드리지 않는다.
    /// </summary>
    private void AppendDeviceFilterExclusion(StringBuilder sb, DynamicParameters p)
    {
        List<DSPilot.Models.AbnormalDeviceFilter> rules;
        try { rules = _appSettings.LoadSettings().AbnormalAlarm.DeviceFilters; }
        catch { return; } // 설정 로드 실패 시 필터 없이 진행(조회 자체를 막지 않는다)

        var i = 0;
        foreach (var rule in rules ?? [])
        {
            if (string.IsNullOrWhiteSpace(rule.Device) || rule.Kinds is not { Count: > 0 }) continue;

            var device = EscapeLike(rule.Device.Trim());
            foreach (var kind in rule.Kinds.Distinct())
            {
                sb.Append($@" AND NOT (valueType = 'Abnormal' AND matchValue = @AbnFltKind{i}
                    AND (tagAddress LIKE @AbnFltMid{i} ESCAPE '\'
                         OR (instr(tagAddress, ' / ') = 0 AND tagAddress LIKE @AbnFltPre{i} ESCAPE '\'))) ");
                p.Add($"AbnFltKind{i}", ((Ds2.Core.AbnormalKind)kind).ToString());
                p.Add($"AbnFltMid{i}", "% / " + device + ".%");
                p.Add($"AbnFltPre{i}", device + ".%");
                i++;
            }
        }
    }

    /// <summary>
    /// 사용자정의(UserTag) 알람 차단 목록을 WHERE 절에 반영. usertag 행(valueType != 'Abnormal')의
    /// tagAddress 가 차단 목록(AbnormalAlarm.UserTagFilters)의 UserTag 정의 주소와 정확히 일치하면 제외한다.
    /// 자동감지(Abnormal) 행은 건드리지 않는다(디바이스 차단이 소유).
    /// </summary>
    private void AppendUserTagFilterExclusion(StringBuilder sb, DynamicParameters p)
    {
        List<string> addrs;
        try { addrs = _appSettings.LoadSettings().AbnormalAlarm.UserTagFilters; }
        catch { return; } // 설정 로드 실패 시 필터 없이 진행

        var i = 0;
        foreach (var addr in addrs ?? [])
        {
            if (string.IsNullOrWhiteSpace(addr)) continue;
            sb.Append($" AND NOT ((valueType IS NULL OR valueType <> 'Abnormal') AND tagAddress = @UtFltAddr{i}) ");
            p.Add($"UtFltAddr{i}", addr.Trim());
            i++;
        }
    }

    // SQLite LIKE 패턴 이스케이프 (ESCAPE '\' 전제) — 디바이스명에 %/_ 가 섞여도 리터럴 매칭.
    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public async Task<IReadOnlyList<UserTagAlertRecord>> QueryAlertsAsync(
        DateTime startUtc, DateTime endUtc,
        string? nameFilter, string? levelFilter, string? systemFilter, string? categoryFilter,
        int limit, int offset,
        CancellationToken ct = default, string? flowFilter = null,
        string? sortColumn = null, bool sortDesc = true)
    {
        await using var conn = await OpenAsync();
        var (where, p) = BuildFilter(startUtc, endUtc, nameFilter, levelFilter, systemFilter, categoryFilter, flowFilter);
        p.Add("Limit", limit);
        p.Add("Offset", offset);

        // 정렬 컬럼 화이트리스트 — 외부 입력을 SQL 에 직접 넣지 않도록 허용 컬럼만 매핑(SQL 인젝션 방지).
        var col = sortColumn switch
        {
            "name"       => "name",
            "systemName" => "systemName",
            "matchOp"    => "matchOp",
            "valueType"  => "valueType",
            _            => "occurredAt",
        };
        var dir = sortDesc ? "DESC" : "ASC";

        var sql = $@"
            SELECT id, occurredAt, systemId, systemName, name, logLevel, tagAddress, valueType, matchOp, matchValue, actualValue, sourceLogId
            FROM userTagAlertLog
            {where}
            ORDER BY {col} {dir}, id DESC
            LIMIT @Limit OFFSET @Offset";

        var rows = await conn.QueryAsync<Row>(sql, p);
        return rows.Select(MapRow).ToList();
    }

    public async Task<int> CountAlertsAsync(
        DateTime startUtc, DateTime endUtc,
        string? nameFilter, string? levelFilter, string? systemFilter, string? categoryFilter = null,
        CancellationToken ct = default, string? flowFilter = null)
    {
        await using var conn = await OpenAsync();
        var (where, p) = BuildFilter(startUtc, endUtc, nameFilter, levelFilter, systemFilter, categoryFilter, flowFilter);
        var sql = $"SELECT COUNT(*) FROM userTagAlertLog {where}";
        return await conn.ExecuteScalarAsync<int>(sql, p);
    }

    public async Task<IReadOnlyList<UserTagAlertBucket>> GetBucketCountsAsync(
        DateTime startUtc, DateTime endUtc,
        string bucketGranularity,
        string? nameFilter, string? levelFilter, string? systemFilter, string? categoryFilter,
        CancellationToken ct = default, string? flowFilter = null)
    {
        await using var conn = await OpenAsync();
        var (where, p) = BuildFilter(startUtc, endUtc, nameFilter, levelFilter, systemFilter, categoryFilter, flowFilter);

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

        // 스택 키 = 구분(ABNORMAL/USERTAG). 레벨이 Error 단일로 통일돼 레벨 스택은 무의미하므로 구분으로 스택한다.
        // (UserTagAlertBucket.LogLevel 슬롯에 구분 문자열을 담아 DTO 형상 유지.)
        var sql = $@"
            SELECT {bucketSql} AS BucketStartStr, {CategoryCase} AS LogLevel, COUNT(*) AS Count
            FROM userTagAlertLog
            {where}
            GROUP BY BucketStartStr, LogLevel
            ORDER BY BucketStartStr ASC";

        var rows = await conn.QueryAsync<BucketRow>(sql, p);
        return rows.Select(r => new UserTagAlertBucket(
            DateTime.SpecifyKind(DateTime.Parse(r.BucketStartStr ?? "1970-01-01", System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc),
            r.LogLevel ?? "USERTAG",
            r.Count)).ToList();
    }

    public async Task<IReadOnlyList<UserTagAlertTopRow>> GetTopByNameAsync(
        DateTime startUtc, DateTime endUtc,
        int topN,
        string? levelFilter, string? systemFilter, string? categoryFilter,
        string groupBy = "name",
        CancellationToken ct = default, string? flowFilter = null)
    {
        await using var conn = await OpenAsync();
        var (where, p) = BuildFilter(startUtc, endUtc, null, levelFilter, systemFilter, categoryFilter, flowFilter);
        p.Add("TopN", topN);
        // 그룹키: name(기본) | tagAddress(경로). SQL 삽입값이라 화이트리스트로만 결정(주입 방지).
        var keyCol = string.Equals(groupBy, "path", StringComparison.OrdinalIgnoreCase) ? "tagAddress" : "name";
        var sql = $@"
            SELECT {keyCol} AS Name, logLevel AS LogLevel, COUNT(*) AS Count
            FROM userTagAlertLog
            {where}
            GROUP BY {keyCol}, logLevel
            ORDER BY Count DESC
            LIMIT @TopN";
        var rows = await conn.QueryAsync<TopRow>(sql, p);
        return rows.Select(r => new UserTagAlertTopRow(r.Name ?? "", r.LogLevel ?? "Info", r.Count)).ToList();
    }

    public async Task<IReadOnlyDictionary<string, int>> GetCategoryCountsAsync(
        DateTime startUtc, DateTime endUtc,
        string? nameFilter, string? levelFilter, string? systemFilter,
        CancellationToken ct = default, string? flowFilter = null)
    {
        await using var conn = await OpenAsync();
        // 구분 도넛은 항상 두 구분을 함께 보여준다 → category 필터는 걸지 않는다(name/level/system 만).
        // 단 flow 필터가 걸리면(설비별 보기) tagAddress 로 자동감지만 남으므로 도넛도 ABNORMAL 단일이 된다.
        var (where, p) = BuildFilter(startUtc, endUtc, nameFilter, levelFilter, systemFilter, null, flowFilter);
        var sql = $@"
            SELECT {CategoryCase} AS Category, COUNT(*) AS Count
            FROM userTagAlertLog
            {where}
            GROUP BY Category";
        var rows = await conn.QueryAsync<CategoryCountRow>(sql, p);
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) d[r.Category ?? "USERTAG"] = r.Count;
        return d;
    }

    public async Task<IReadOnlyList<UserTagAlertRecord>> GetLatestAlertsAsync(int maxCount, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var sb = new StringBuilder(" WHERE 1 = 1 ");
        var p = new DynamicParameters();
        p.Add("Limit", maxCount);
        AppendDeviceFilterExclusion(sb, p);
        AppendUserTagFilterExclusion(sb, p);
        var sql = $@"
            SELECT id, occurredAt, systemId, systemName, name, logLevel, tagAddress, valueType, matchOp, matchValue, actualValue, sourceLogId
            FROM userTagAlertLog
            {sb}
            ORDER BY id DESC
            LIMIT @Limit";
        var rows = await conn.QueryAsync<Row>(sql, p);
        return rows.Select(MapRow).ToList();
    }

    public async Task<long> GetMaxAlertIdAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteScalarAsync<long?>("SELECT MAX(id) FROM userTagAlertLog") ?? 0L;
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

    // Dapper 가 ValueTuple 을 매핑하지 못해 (TupleElementNamesAttribute 는 컴파일타임만 살아있음 → Item1/Item2 로만 인식),
    // 컬럼 alias 기반 매핑이 silently null 을 반환. 명시적 DTO 로 안정화.
    private sealed class BucketRow
    {
        public string? BucketStartStr { get; set; }
        public string? LogLevel { get; set; }
        public int Count { get; set; }
    }

    private sealed class TopRow
    {
        public string? Name { get; set; }
        public string? LogLevel { get; set; }
        public int Count { get; set; }
    }

    private sealed class CategoryCountRow
    {
        public string? Category { get; set; }
        public int Count { get; set; }
    }
}
