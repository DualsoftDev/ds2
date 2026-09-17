// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Dapper;

namespace DSPilot.Kpi;

/// <summary>태그 모니터링이 목록에 보여 주는 한 줄. 정의는 AASX 에서 오고 값은 신호 표에서 온다.</summary>
/// <param name="DataType">Bit · Byte · Word · DWord · Int16 · Int32 · Real · String.</param>
/// <param name="SystemGuid">
/// 귀속 System 의 키(<c>system.guid</c>). 주소는 PLC 가 둘이면 겹칠 수 있어, AASX 정의와 맞출 때 이것이 정본 키다.
/// </param>
public sealed record TagInfo(
    long Id,
    string Address,
    string Name,
    string? Label,
    string DataType,
    string? Unit,
    bool IsUserTag,
    string? SystemName,
    string? SystemGuid,
    long? LastAtMs,
    string? LastValue);

/// <summary>추이 한 점. 신호는 변할 때만 기록되므로 점 사이는 계단으로 이어 그린다.</summary>
public sealed record TagPoint(long AtMs, double? Num, string? Text);

/// <summary>
/// 태그 모니터링 조회. doc/30 §9.4.
/// <para>
/// UserTag 는 두 종류로 쓰인다 — <b>로그 레벨</b>이 그 축이다(2026-09-17). Error = 이상알람TAG(조건에 걸리면
/// 발화), Info = 모니터링TAG(값 변화만 기록). 값 종류(Bit·Word·Real·String…)는 종류와 무관하게 여덟 가지
/// 모두 양쪽에 열려 있다. 레벨은 AASX 에만 있으므로 이 저장소는 값만 다루고, 종류 구분은
/// <see cref="Services.TagMonitorService"/> 가 AASX 정의를 얹어 정한다.
/// </para>
/// <para>두 종류 모두 값 변화는 같은 신호 표에 쌓이므로, 이상알람TAG 의 추이도 여기서 그대로 읽힌다.</para>
/// </summary>
public sealed class TagMonitorRepository
{
    private readonly KpiDb _db;
    private readonly ILogger<TagMonitorRepository> _logger;

    public TagMonitorRepository(KpiDb db, ILogger<TagMonitorRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// 태그 목록. 마지막 값과 그 시각을 함께 준다 — 목록만으로 현재 상태가 보이게.
    /// </summary>
    /// <param name="userTagsOnly">true 면 UserTag 만. 화면 기본값.</param>
    public async Task<List<TagInfo>> ListAsync(bool userTagsOnly = true, CancellationToken ct = default)
    {
        await using var conn = _db.OpenRead();
        var sql = """
            SELECT t.id AS Id, t.address AS Address, t.name AS Name, t.label AS Label,
                   t.dataType AS DataType, t.unit AS Unit, t.isUserTag AS IsUserTag,
                   s.name AS SystemName, s.guid AS SystemGuid,
                   (SELECT g.atMs FROM signal g WHERE g.tagId = t.id ORDER BY g.atMs DESC LIMIT 1) AS LastAtMs,
                   (SELECT CAST(g.value AS TEXT) FROM signal g WHERE g.tagId = t.id ORDER BY g.atMs DESC LIMIT 1) AS LastValue
            FROM tag t
            LEFT JOIN system s ON s.id = t.systemId
            WHERE 1=1
            """;
        if (userTagsOnly) sql += " AND t.isUserTag = 1";
        sql += " ORDER BY t.isUserTag DESC, t.address";

        var rows = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        var list = new List<TagInfo>();
        foreach (var r in rows)
        {
            var dataType = (r.DataType as string) ?? "Bit";
            bool isUser = Convert.ToInt64(r.IsUserTag) != 0;
            list.Add(new TagInfo(
                Convert.ToInt64(r.Id),
                (string)r.Address,
                (r.Name as string) ?? (string)r.Address,
                r.Label as string,
                dataType,
                r.Unit as string,
                isUser,
                r.SystemName as string,
                r.SystemGuid as string,
                r.LastAtMs is null ? null : Convert.ToInt64(r.LastAtMs),
                r.LastValue as string));
        }
        return list;
    }

    /// <summary>
    /// 한 태그의 추이. 구간 안의 변화점에 더해 <b>구간 직전의 마지막 값</b> 하나를 앞에 붙인다 —
    /// 신호는 변할 때만 기록되므로 그것이 없으면 차트 왼쪽 끝이 빈다(doc/30 §9.4).
    /// </summary>
    public async Task<List<TagPoint>> TrendAsync(
        long tagId, long fromMs, long toMs, int limit = 5000, CancellationToken ct = default)
    {
        await using var conn = _db.OpenRead();

        var points = new List<TagPoint>();

        var seed = await conn.QueryFirstOrDefaultAsync(new CommandDefinition(
            "SELECT atMs, value FROM signal WHERE tagId=@tagId AND atMs < @fromMs ORDER BY atMs DESC LIMIT 1",
            new { tagId, fromMs }, cancellationToken: ct));
        if (seed is not null) points.Add(ToPoint(fromMs, seed.value));

        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT atMs, value FROM signal WHERE tagId=@tagId AND atMs >= @fromMs AND atMs < @toMs ORDER BY atMs LIMIT @limit",
            new { tagId, fromMs, toMs, limit }, cancellationToken: ct));
        foreach (var r in rows) points.Add(ToPoint(Convert.ToInt64(r.atMs), r.value));

        return points;
    }

    /// <summary>구간 안의 변화 횟수 — 목록에서 "얼마나 자주 움직이는 태그인가"를 보일 때.</summary>
    public async Task<int> ChangeCountAsync(long tagId, long fromMs, long toMs, CancellationToken ct = default)
    {
        await using var conn = _db.OpenRead();
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM signal WHERE tagId=@tagId AND atMs >= @fromMs AND atMs < @toMs",
            new { tagId, fromMs, toMs }, cancellationToken: ct));
    }

    /// <summary>
    /// 저장된 값은 종류에 따라 정수·실수·문자열 중 하나다(칸 하나에 그대로 담긴다).
    /// 숫자로 읽히면 수치 축에, 아니면 문자열로 돌려준다.
    /// </summary>
    private static TagPoint ToPoint(long atMs, object? raw)
    {
        switch (raw)
        {
            case null:
                return new TagPoint(atMs, null, null);
            case long l:
                return new TagPoint(atMs, l, null);
            case int i:
                return new TagPoint(atMs, i, null);
            case double d:
                return new TagPoint(atMs, d, null);
            case string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v):
                return new TagPoint(atMs, v, s);
            case string s:
                return new TagPoint(atMs, null, s);
            default:
                return new TagPoint(atMs, null, raw.ToString());
        }
    }
}
