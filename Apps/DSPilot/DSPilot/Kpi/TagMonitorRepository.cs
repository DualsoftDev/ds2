// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Dapper;

namespace DSPilot.Kpi;

/// <summary>태그 모니터링이 목록에 보여 주는 한 줄. 정의는 AASX 에서 오고 값은 신호 표에서 온다.</summary>
/// <param name="DataType">Bit · Byte · Word · DWord · Int16 · Int32 · Real · String.</param>
/// <param name="IsAlarmSource">
/// 알람(에러) 대상 여부. DSPilot 규칙상 <b>Bit UserTag 만</b> 알람이 된다.
/// 나머지 종류는 정보용이라 여기 모니터링에서 값과 추이로만 본다.
/// </param>
public sealed record TagInfo(
    long Id,
    string Address,
    string Name,
    string? Label,
    string DataType,
    string? Unit,
    bool IsUserTag,
    bool IsAlarmSource,
    string? SystemName,
    long? LastAtMs,
    string? LastValue);

/// <summary>추이 한 점. 신호는 변할 때만 기록되므로 점 사이는 계단으로 이어 그린다.</summary>
public sealed record TagPoint(long AtMs, double? Num, string? Text);

/// <summary>
/// 태그 모니터링 조회. doc/30 §9.4.
/// <para>
/// UserTag 는 두 갈래로 쓰인다. 값 종류가 Bit 인 것은 알람(에러)이 되고, 나머지(Word·Real·String 등)는
/// 정보용이라 이 기능이 값과 추이를 보여 준다. 두 갈래 모두 값 변화는 같은 신호 표에 쌓인다.
/// </para>
/// </summary>
public sealed class TagMonitorRepository
{
    /// <summary>알람이 될 수 있는 유일한 값 종류. 나머지는 모니터링 전용이다.</summary>
    public const string AlarmValueType = "Bit";

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
    /// <param name="excludeBit">true 면 Bit 를 뺀다 — 정보용 UserTag 만 보고 싶을 때.</param>
    public async Task<List<TagInfo>> ListAsync(
        bool userTagsOnly = true, bool excludeBit = false, CancellationToken ct = default)
    {
        await using var conn = _db.OpenRead();
        var sql = """
            SELECT t.id AS Id, t.address AS Address, t.name AS Name, t.label AS Label,
                   t.dataType AS DataType, t.unit AS Unit, t.isUserTag AS IsUserTag,
                   s.name AS SystemName,
                   (SELECT g.atMs FROM signal g WHERE g.tagId = t.id ORDER BY g.atMs DESC LIMIT 1) AS LastAtMs,
                   (SELECT CAST(g.value AS TEXT) FROM signal g WHERE g.tagId = t.id ORDER BY g.atMs DESC LIMIT 1) AS LastValue
            FROM tag t
            LEFT JOIN system s ON s.id = t.systemId
            WHERE 1=1
            """;
        if (userTagsOnly) sql += " AND t.isUserTag = 1";
        if (excludeBit) sql += " AND t.dataType <> 'Bit'";
        sql += " ORDER BY t.isUserTag DESC, t.address";

        var rows = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        var list = new List<TagInfo>();
        foreach (var r in rows)
        {
            var dataType = (r.DataType as string) ?? AlarmValueType;
            bool isUser = Convert.ToInt64(r.IsUserTag) != 0;
            list.Add(new TagInfo(
                Convert.ToInt64(r.Id),
                (string)r.Address,
                (r.Name as string) ?? (string)r.Address,
                r.Label as string,
                dataType,
                r.Unit as string,
                isUser,
                isUser && string.Equals(dataType, AlarmValueType, StringComparison.Ordinal),
                r.SystemName as string,
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
