// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using DSPilot.Models.Plc;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// signal 표(정수 epoch atMs · 타입 없는 value) → PlcTagLogEntity 매핑 회귀.
/// <para>
/// 2026-09-18 현장 사고: 1차 이동에서 plcTagLog(텍스트 시각) → signal(정수 epoch) 로 바꾸면서 일부 조회가
/// <c>atMs AS DateTime</c> 을 <see cref="PlcTagLogEntity"/> 에 <b>직접</b> 매핑한 채 남았다. Dapper 는
/// Int64 → DateTime 변환에서 던지는데, <b>행이 0건이면 역직렬화가 아예 돌지 않아 조용히 통과</b>한다.
/// 그래서 "조회 창이 첫 신호보다 앞서면 200, 뒤면 500" 이라는 기묘한 증상이 됐다(가동시간 분석 전 flow).
/// </para>
/// <para>이 테스트는 그 함정을 고정한다 — 직접 매핑은 던지고, 행 타입(long) 경유는 통과해야 한다.</para>
/// </summary>
public class SignalRowMappingTests
{
    private const long AtMs = 1_789_700_000_000; // 2026-09-18 근방

    private static SqliteConnection NewDb()
    {
        var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        // KpiDb 의 signal DDL 과 같은 모양 — value 에 타입을 선언하지 않는 것이 요점(비트는 정수로 들어온다).
        c.Execute("""
            CREATE TABLE signal (
                id    INTEGER PRIMARY KEY AUTOINCREMENT,
                tagId INTEGER NOT NULL,
                atMs  INTEGER NOT NULL,
                value         NOT NULL
            )
            """);
        c.Execute("INSERT INTO signal (tagId, atMs, value) VALUES (1, @a, 1), (1, @b, 0)",
                  new { a = AtMs, b = AtMs + 1000 });
        return c;
    }

    /// <summary>행 타입(DateTime 이 long) 경유 = 지금 저장소가 쓰는 경로. 값이 정수여도 안전하다.</summary>
    private sealed class Row
    {
        public int Id { get; set; }
        public int PlcTagId { get; set; }
        public long DateTime { get; set; }
        public string? Value { get; set; }
    }

    private const string Sql =
        "SELECT id AS Id, tagId AS PlcTagId, atMs AS DateTime, CAST(value AS TEXT) AS Value FROM signal ORDER BY atMs";

    [Fact]
    public void RowType_Then_Convert_Works()
    {
        using var c = NewDb();
        var rows = c.Query<Row>(Sql).ToList();
        Assert.Equal(2, rows.Count);

        var entity = new PlcTagLogEntity
        {
            Id = rows[0].Id,
            PlcTagId = rows[0].PlcTagId,
            DateTime = DSPilot.Kpi.KpiTime.ToLocal(rows[0].DateTime),
            Value = rows[0].Value,
        };
        Assert.Equal(DSPilot.Kpi.KpiTime.ToLocal(AtMs), entity.DateTime);
        Assert.Equal("1", entity.Value);   // 비트가 정수로 들어와도 CAST 로 문자열이 된다
    }

    /// <summary>
    /// 엔티티에 직접 매핑하면 던진다 — 이게 현장 HTTP 500 의 정체다. 새 조회를 추가할 때 이 경로를 쓰면 안 된다.
    /// </summary>
    [Fact]
    public void DirectMapping_To_DateTime_Property_Throws()
    {
        using var c = NewDb();
        var ex = Assert.ThrowsAny<Exception>(() => c.Query<PlcTagLogEntity>(Sql).ToList());
        Assert.Contains("DateTime", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>행이 0건이면 같은 잘못된 매핑도 통과한다 — 이 비대칭이 버그를 오래 숨겼다.</summary>
    [Fact]
    public void DirectMapping_Is_Silent_When_No_Rows()
    {
        using var c = NewDb();
        c.Execute("DELETE FROM signal");
        var rows = c.Query<PlcTagLogEntity>(Sql).ToList();
        Assert.Empty(rows);
    }

    /// <summary>MIN/MAX(atMs) 는 정수다. 문자열로 읽어 UTC 문자열로 파싱하면 예외 없이 null 이 되어 "데이터 없음" 으로 둔갑한다.</summary>
    [Fact]
    public void MinMax_AtMs_Must_Be_Read_As_Integer()
    {
        using var c = NewDb();
        var asLong = c.ExecuteScalar<long?>("SELECT MIN(atMs) FROM signal");
        Assert.Equal(AtMs, asLong);

        var asString = c.ExecuteScalar<string>("SELECT MIN(atMs) FROM signal");
        Assert.Null(SqliteDateTimeProbe.FromUtcString(asString));   // 구 경로는 조용히 null
    }

    /// <summary>구 파서 재현 — 정수 문자열은 DATETIME 문자열이 아니므로 파싱되지 않는다.</summary>
    private static class SqliteDateTimeProbe
    {
        public static DateTime? FromUtcString(string? s) =>
            DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                              System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }
}
