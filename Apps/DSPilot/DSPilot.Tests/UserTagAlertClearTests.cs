// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Dapper;
using DSPilot.Models.UserTagAlerts;
using DSPilot.Repositories;
using DSPilot.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// 알람 해소 기록(userTagAlertLog.clearedAt) — "알람이 뜨고 풀리는 순간(Bit 1→0)"을 발생 행에 남기는 규약.
/// 해소를 별도 행으로 만들지 않는 이유: 목록·시계열·Top10 이 모두 같은 행 집합을 세므로 건수가 두 배가 된다.
///
/// 지정 키(UserTagClearKey = 주소 + System + 발생시각 이후)로 좁히는 이유 2가지:
///   ① 멀티 PLC 에서 두 System 이 같은 주소를 정의하면 한쪽의 해소가 남의 알람까지 마감한다.
///   ② 재시작으로 남은 과거 미해소 행이 "지금" 시각으로 소급 마감돼 지속시간이 며칠로 부풀어 오른다.
/// </summary>
public class UserTagAlertClearTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dsp-uta-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTime T10 = new(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc);

    public UserTagAlertClearTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* 임시 폴더 정리는 실패해도 무시 */ }
        GC.SuppressFinalize(this);
    }

    // ── 테스트 대역 ──────────────────────────────────────────────────────────
    private sealed class Paths(string db) : IDatabasePathResolver
    {
        public string GetSharedDbPath() => db;
        public string GetPlcDbPath() => db;
        public string GetDspDbPath() => db;
        public bool IsUnified => true;
    }

    private sealed class Env(string root) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "DSPilot.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
    }

    private string DbPath => Path.Combine(_dir, "plc.db");

    private async Task<UserTagAlertRepository> NewRepoAsync()
    {
        await using (var conn = new SqliteConnection($"Data Source={DbPath}"))
        {
            await conn.OpenAsync();
            // 운영 DDL(DspRepositoryAdapter)과 같은 컬럼 구성 — clearedAt 포함.
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS userTagAlertLog (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurredAt    TEXT     NOT NULL,
                    systemId      TEXT     NOT NULL,
                    systemName    TEXT     NOT NULL,
                    name          TEXT     NOT NULL,
                    logLevel      TEXT     NOT NULL,
                    tagAddress    TEXT     NOT NULL,
                    valueType     TEXT     NOT NULL,
                    matchOp       TEXT     NOT NULL,
                    matchValue    TEXT,
                    actualValue   TEXT     NOT NULL,
                    sourceLogId   INTEGER,
                    clearedAt     TEXT
                )");
        }

        var paths = new Paths(DbPath);
        var settings = new AppSettingsService(new Env(_dir), NullLogger<AppSettingsService>.Instance);
        // 미러 비활성 — 파일 DB 단일 경로로 고정(읽기 폴백·복제 no-op).
        var mirror = new HistoryMirrorService(new HistoryMirrorOptions { Enabled = false }, paths,
            NullLogger<HistoryMirrorService>.Instance);
        return new UserTagAlertRepository(paths, settings, mirror, NullLogger<UserTagAlertRepository>.Instance);
    }

    private static UserTagAlertRecord Alert(DateTime atUtc, string system, string addr) => new(
        Id: 0, OccurredAt: atUtc, SystemId: Guid.Empty, SystemName: system, Name: "누유감지",
        LogLevel: "Error", TagAddress: addr, ValueType: "Bit", MatchOp: "RisingEdge",
        MatchValue: "1", ActualValue: "true", SourceLogId: null);

    private static async Task<Dictionary<long, string?>> ClearedByIdAsync(string dbPath)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        var rows = await conn.QueryAsync<(long Id, string? ClearedAt)>(
            "SELECT id AS Id, clearedAt AS ClearedAt FROM userTagAlertLog");
        return rows.ToDictionary(r => r.Id, r => r.ClearedAt);
    }

    // ── 케이스 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clear_stamps_only_the_targeted_system_and_occurrence()
    {
        var repo = await NewRepoAsync();
        var mine = await repo.InsertAlertAsync(Alert(T10, "SYS-A", "%MX10"));                    // 지금 해소되는 건
        var otherSystem = await repo.InsertAlertAsync(Alert(T10.AddMinutes(1), "SYS-B", "%MX10")); // 같은 주소, 다른 PLC
        var stale = await repo.InsertAlertAsync(Alert(T10.AddHours(-5), "SYS-A", "%MX10"));       // 재시작 전 잔여 미해소

        var n = await repo.MarkClearedAsync([new UserTagClearKey("%MX10", "SYS-A", T10, T10.AddMinutes(5))]);

        var cleared = await ClearedByIdAsync(DbPath);
        Assert.Equal(1, n);
        Assert.NotNull(cleared[mine]);
        Assert.Null(cleared[otherSystem]);
        Assert.Null(cleared[stale]);
    }

    [Fact]
    public async Task Cleared_row_is_not_restamped_by_a_later_clear()
    {
        var repo = await NewRepoAsync();
        var first = await repo.InsertAlertAsync(Alert(T10, "SYS-A", "%MX10"));
        await repo.MarkClearedAsync([new UserTagClearKey("%MX10", "SYS-A", T10, T10.AddMinutes(5))]);
        var firstStamp = (await ClearedByIdAsync(DbPath))[first];

        // 같은 주소가 다시 발화 → 해소. 이전 행의 해소 시각은 그대로여야 한다.
        var second = await repo.InsertAlertAsync(Alert(T10.AddHours(1), "SYS-A", "%MX10"));
        var n = await repo.MarkClearedAsync(
            [new UserTagClearKey("%MX10", "SYS-A", T10.AddHours(1), T10.AddHours(1).AddSeconds(30))]);

        var cleared = await ClearedByIdAsync(DbPath);
        Assert.Equal(1, n);
        Assert.Equal(firstStamp, cleared[first]);
        Assert.NotNull(cleared[second]);
    }

    [Fact]
    public async Task Query_returns_clearedAt_for_the_list_and_excel()
    {
        var repo = await NewRepoAsync();
        await repo.InsertAlertAsync(Alert(T10, "SYS-A", "%MX10"));
        await repo.InsertAlertAsync(Alert(T10.AddMinutes(10), "SYS-A", "%MX20"));   // 미해소로 남는 건
        await repo.MarkClearedAsync([new UserTagClearKey("%MX10", "SYS-A", T10, T10.AddMinutes(2))]);

        var rows = await repo.QueryAlertsAsync(
            T10.AddHours(-1), T10.AddHours(1), null, null, null, null, 100, 0);

        var done = rows.Single(r => r.TagAddress == "%MX10");
        var open = rows.Single(r => r.TagAddress == "%MX20");
        Assert.NotNull(done.ClearedAt);
        // 지속시간(목록/Excel 의 '지속') = 해소 − 발생. tz 규약상 조회 결과는 로컬 Kind 로 돌아온다.
        Assert.Equal(TimeSpan.FromMinutes(2), done.ClearedAt!.Value - done.OccurredAt);
        Assert.Null(open.ClearedAt);
    }
}
