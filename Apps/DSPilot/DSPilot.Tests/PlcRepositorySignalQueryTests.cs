// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using DSPilot.Kpi;
using DSPilot.Repositories;
using DSPilot.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// PlcRepository 가 새 signal 표(정수 epoch atMs · 타입 없는 value)를 실제로 읽어 내는지 — 진짜 DB 파일로 검증.
/// <para>
/// 2026-09-18 현장 사고 회귀: 1차 이동 뒤에도 일부 조회가 구 컬럼명(dateTime · plcTagId)을 쓰거나 atMs 를
/// DateTime 속성에 직접 매핑한 채 남아 있었다. 행이 0건이면 조용히 통과해 단위 테스트로는 안 잡혔고,
/// 현장에서 "조회 창이 첫 신호보다 뒤면 HTTP 500" 과 "전체 이력 재계산 실패: no such column: dateTime" 로 터졌다.
/// 그래서 이 테스트는 <b>반드시 행이 1건 이상 나오는</b> 조건으로 부른다.
/// </para>
/// </summary>
public sealed class PlcRepositorySignalQueryTests : IDisposable
{
    private const string Addr = "%QW1291.1";
    private static readonly Guid SystemGuid = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly PlcRepository _repo;
    private readonly long _t0 = KpiTime.ToMs(new DateTime(2026, 9, 18, 5, 0, 0, DateTimeKind.Utc));

    private sealed class FixedPath(string p) : IDatabasePathResolver
    {
        public string GetSharedDbPath() => p;
        public string GetPlcDbPath() => p;
        public string GetDspDbPath() => p;
        public bool IsUnified => true;
    }

    public PlcRepositorySignalQueryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dsp-plcrepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "plc.db");

        var db = KpiDb.ForPath(NullLogger<KpiDb>.Instance, _dbPath);
        Assert.True(db.EnsureSchemaAsync().GetAwaiter().GetResult());
        using (var conn = db.Open())
        {
            conn.Execute("INSERT INTO system (guid, name) VALUES (@g, 'S1')", new { g = SystemGuid.ToString() });
            conn.Execute("INSERT INTO tag (systemId, address, name) VALUES (1, @a, 'T1')", new { a = Addr });
            // 비트는 정수로 들어간다 — 구 plcTagLog.value(TEXT) 와 다른 점이고, 이게 매핑 함정의 절반이었다.
            conn.Execute("INSERT INTO signal (tagId, atMs, value) VALUES (1, @a, 1), (1, @b, 0), (1, @c, 1)",
                         new { a = _t0, b = _t0 + 10_000, c = _t0 + 20_000 });
        }

        _repo = new PlcRepository(new FixedPath(_dbPath), NullLogger<PlcRepository>.Instance);
    }

    /// <summary>현장 HTTP 500 의 직접 원인 — 창 시작 이전에 신호가 있으면 이 조회가 행을 반환하며 터졌다.</summary>
    [Fact]
    public async Task 창시작_이전_초기값_조회가_행을_반환한다()
    {
        var atOrBefore = KpiTime.ToUtc(_t0 + 15_000);
        var rows = await _repo.GetLatestLogsByAddressesBeforeAsync([Addr], atOrBefore, SystemGuid);

        Assert.Single(rows);
        Assert.Equal(KpiTime.ToLocal(_t0 + 10_000), rows[0].DateTime);
        Assert.Equal("0", rows[0].Value);
    }

    /// <summary>재계산 범위의 두 끝 — 정수로 읽어야 한다. 문자열로 읽던 구 경로는 예외 없이 null 을 냈다.</summary>
    [Fact]
    public async Task 최초_최종_신호시각이_정수_epoch_로_읽힌다()
    {
        Assert.Equal(KpiTime.ToLocal(_t0), await _repo.GetOldestLogDateTimeAsync());
        Assert.Equal(KpiTime.ToLocal(_t0 + 20_000), await _repo.GetLatestLogDateTimeAsync());
    }

    [Fact]
    public async Task 주소_구간_조회가_행을_반환한다()
    {
        var rows = await _repo.GetTagLogsByAddressInRangeAsync(
            Addr, KpiTime.ToUtc(_t0 - 1000), KpiTime.ToUtc(_t0 + 30_000), SystemGuid);

        Assert.Equal(3, rows.Count);
        Assert.Equal(KpiTime.ToLocal(_t0), rows[0].DateTime);
    }

    /// <summary>구 컬럼명(dateTime · plcTagId)이 남아 있으면 여기서 'no such column' 으로 터진다.</summary>
    [Fact]
    public async Task 구컬럼명을_쓰던_조회들이_동작한다()
    {
        var since = await _repo.GetNewLogsAsync(KpiTime.ToUtc(_t0 + 5_000));
        Assert.Equal(2, since.Count);

        var ranged = await _repo.GetLogsInRangeAsync(KpiTime.ToUtc(_t0 - 1), KpiTime.ToUtc(_t0 + 10_000));
        Assert.Equal(2, ranged.Count);

        var latest = await _repo.GetLatestLogByTagIdAsync(1);
        Assert.NotNull(latest);
        Assert.Equal(KpiTime.ToLocal(_t0 + 20_000), latest!.DateTime);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 임시 폴더 — 남아도 무해 */ }
    }
}
