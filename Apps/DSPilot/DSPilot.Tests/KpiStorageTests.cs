// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Kpi;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// 시간 기반 코어 DB(doc/30 §9) 왕복 — 스키마 v1 생성, 사이클·work 저장, 구간 조회의 잘림 표시,
/// 워터마크, 접속 공백 열고 닫기. 임시 파일에 만들고 지운다.
/// </summary>
public sealed class KpiStorageTests : IDisposable
{
    private readonly string _dir;
    private readonly KpiDb _db;
    private readonly KpiRepository _repo;

    public KpiStorageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dspilot-kpi-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = KpiDb.ForPath(NullLogger<KpiDb>.Instance, Path.Combine(_dir, "test.db"));
        _repo = new KpiRepository(_db, NullLogger<KpiRepository>.Instance);
        Assert.True(_db.EnsureSchemaAsync().GetAwaiter().GetResult());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 임시 폴더 — 남아도 무해 */ }
    }

    private const long T0 = 1_800_000_000_000;   // 고정 기준 시각(epoch ms)

    private static CycleRecord Cycle(long startMs, long ctMs, double r = 10_000, double worst = 0, long? mt = null) =>
        new("FlowA", null, startMs, startMs + ctMs, mt, r, 0, worst > 0 ? "w1" : null, worst);

    private static CycleRecord NoBaseline(long startMs, long ctMs) =>
        new("FlowA", null, startMs, startMs + ctMs, null, 0, 0, null, 0, 0, ExcludeReason.NoBaseline);

    [Fact]
    public async Task 스키마는_멱등이다()
    {
        Assert.True(await _db.EnsureSchemaAsync());
        Assert.True(await _db.EnsureSchemaAsync());
    }

    [Fact]
    public async Task 사이클과_work_가_함께_저장되고_조회된다()
    {
        var id = await _repo.SaveCycleAsync(
            Cycle(T0, 12_000, worst: 3.0, mt: 9_500),
            [new WorkDuration("w1", 9_000, 3_000), new WorkDuration("w2", 1_000, 2_000, Gated: true)]);
        Assert.True(id > 0);

        var rows = await _repo.QueryCyclesAsync(T0 - 1000, T0 + 60_000);
        var row = Assert.Single(rows);
        Assert.Equal(12_000, row.CtMs);
        Assert.Equal(9_500, row.MtMs);
        Assert.Equal(2_500, row.WtMs);
        Assert.Equal("w1", row.WorstWork);
        Assert.Equal(ExcludeReason.None, row.Exclude);

        var works = await _repo.GetCycleWorksAsync(id);
        Assert.Equal(2, works.Count);
        Assert.Equal(3.0, works[0].Ratio, 6);   // 9000/3000, durationMs 내림차순 정렬
        Assert.False(works[0].Gated);
        Assert.True(works[1].Gated);            // 게이트 표시가 왕복한다
    }

    [Fact]
    public async Task MT중앙과_경계초과가_박제되어_돌아온다()
    {
        await _repo.SaveCycleAsync(
            new CycleRecord("FlowA", null, T0, T0 + 12_000, 9_000, 10_000, 8_000, null, 0, OverflowMs: 3_100), []);
        var row = Assert.Single(await _repo.QueryCyclesAsync(T0 - 1, T0 + 60_000));
        Assert.Equal(8_000, row.MtMedianUsedMs, 6);
        Assert.Equal(3_100, row.OverflowMs);

        var f = row.ToFact();
        Assert.Equal(1.125, f.MtRatio, 6);   // 9000/8000
        Assert.Equal(CycleState.Run, KpiRules.Classify(f, KpiKappa.Default));            // 3.1초 초과는 허용
        Assert.Equal(CycleState.Excluded, KpiRules.Classify(f, KpiKappa.Default with { OverflowMs = 1_000 }));
    }

    [Fact]
    public async Task 같은_시작시각_재저장은_갱신이다()
    {
        var first = await _repo.SaveCycleAsync(Cycle(T0, 12_000), [new WorkDuration("w1", 5_000, 2_000)]);
        var again = await _repo.SaveCycleAsync(Cycle(T0, 15_000), [new WorkDuration("w1", 6_000, 2_000)]);

        Assert.Equal(first, again);                       // 같은 행
        var rows = await _repo.QueryCyclesAsync(T0 - 1, T0 + 60_000);
        Assert.Single(rows);
        Assert.Equal(15_000, rows[0].CtMs);               // 마지막 값으로 갱신

        var works = await _repo.GetCycleWorksAsync(again);
        Assert.Equal(6_000, Assert.Single(works).DurationMs);   // work 도 교체(중복 누적 없음)
    }

    [Fact]
    public async Task 구간_경계에_걸친_행은_잘림으로_표시된다()
    {
        await _repo.SaveCycleAsync(Cycle(T0, 10_000), []);              // 완전히 안쪽
        await _repo.SaveCycleAsync(Cycle(T0 - 5_000, 6_000), []);       // 왼쪽 걸침
        await _repo.SaveCycleAsync(Cycle(T0 + 15_000, 10_000), []);     // 오른쪽 걸침

        var rows = await _repo.QueryCyclesAsync(T0 - 1_000, T0 + 20_000);
        Assert.Equal(3, rows.Count);
        Assert.Equal(1, rows.Count(r => r.Exclude == ExcludeReason.None));
        Assert.Equal(2, rows.Count(r => r.Exclude == ExcludeReason.Cut));
    }

    [Fact]
    public async Task 저장된_제외사유가_잘림보다_우선한다()
    {
        await _repo.SaveCycleAsync(NoBaseline(T0, 10_000), []);
        var row = Assert.Single(await _repo.QueryCyclesAsync(T0 - 1, T0 + 60_000));
        Assert.Equal(ExcludeReason.NoBaseline, row.Exclude);
    }

    [Fact]
    public async Task 표본_조회는_제외행을_빼고_돌려준다()
    {
        for (int i = 0; i < 5; i++)
            await _repo.SaveCycleAsync(Cycle(T0 + i * 20_000, 10_000, mt: 7_000 + i), []);
        await _repo.SaveCycleAsync(NoBaseline(T0 + 500_000, 10_000), []);

        var samples = await _repo.GetCtSamplesAsync("FlowA", null, T0 - 1);
        Assert.Equal(5, samples.Count);

        var mts = await _repo.GetMtSamplesAsync("FlowA", null, T0 - 1);
        Assert.Equal(5, mts.Count);
        Assert.Equal(7_000, mts.Min());
    }

    [Fact]
    public async Task work_표본은_work_별_분기별로_모인다()
    {
        for (int i = 0; i < 3; i++)
        {
            await _repo.SaveCycleAsync(
                Cycle(T0 + i * 20_000, 10_000),
                [new WorkDuration("w1", 3_000 + i, 0), new WorkDuration("w2", 500, 0)]);
        }
        // 다른 분기의 같은 work 는 섞이지 않는다(doc/30 §4 — W 는 분기별).
        await _repo.SaveCycleAsync(
            new CycleRecord("FlowA", "Y450", T0 + 100_000, T0 + 110_000, null, 10_000, 0, null, 0),
            [new WorkDuration("w1", 99_000, 0)]);

        var map = await _repo.GetWorkSamplesAsync("FlowA", null, T0 - 1);
        Assert.Equal(2, map.Count);
        Assert.Equal(3, map["w1"].Count);
        Assert.Equal(3, map["w2"].Count);

        var branched = await _repo.GetWorkSamplesAsync("FlowA", "Y450", T0 - 1);
        Assert.Equal(99_000, Assert.Single(branched["w1"]));
    }

    [Fact]
    public async Task 워터마크는_flow_별_마지막_끝이다()
    {
        await _repo.SaveCycleAsync(Cycle(T0, 10_000), []);
        await _repo.SaveCycleAsync(Cycle(T0 + 30_000, 10_000), []);
        var marks = await _repo.GetCycleWatermarksAsync();
        Assert.Equal(T0 + 40_000, marks["FlowA"]);
    }

    [Fact]
    public async Task 사이클을_지우면_work_도_함께_지워진다()
    {
        var id = await _repo.SaveCycleAsync(Cycle(T0, 10_000), [new WorkDuration("w1", 5_000, 2_000)]);
        Assert.Single(await _repo.GetCycleWorksAsync(id));

        await _repo.DeleteCyclesAsync("FlowA", T0 - 1, T0 + 60_000);
        Assert.Empty(await _repo.QueryCyclesAsync(T0 - 1, T0 + 60_000));
        Assert.Empty(await _repo.GetCycleWorksAsync(id));
    }

    [Fact]
    public async Task 접속_공백은_열고_닫힌다()
    {
        await _repo.InsertLinkEventAsync(new LinkEventRecord(
            "*", T0, null, false, LinkEventRecord.KindGap, "silent", null));

        var open = Assert.Single(await _repo.QueryLinkEventsAsync(T0 - 1_000, T0 + 60_000));
        Assert.Null(open.EndMs);

        Assert.Equal(1, await _repo.CloseOpenGapAsync("*", T0 + 30_000));
        var closed = Assert.Single(await _repo.QueryLinkEventsAsync(T0 - 1_000, T0 + 60_000));
        Assert.Equal(T0 + 30_000, closed.EndMs);
    }

    [Fact]
    public async Task 기준선_없이_들어온_행은_나중에_찍어_줄_수_있다()
    {
        // 설치 직후: 표본이 없어 R 을 못 박제한 채 들어온 행.
        var id = await _repo.SaveCycleAsync(
            new CycleRecord("FlowA", null, T0, T0 + 30_000, 26_000, 0, 0, null, 0, 0, ExcludeReason.NoBaseline),
            [new WorkDuration("w1", 24_000, 0)]);

        var pending = await _repo.GetPendingBaselineCyclesAsync(10);
        Assert.Equal(id, Assert.Single(pending).Id);

        // 표본이 쌓여 R=10초 · MT중앙=8초 · W(w1)=3초 가 생긴 뒤 뒤늦게 박제.
        Assert.True(await _repo.StampBaselineAsync(
            id, 10_000, 8_000, "w1", 8.0, [new WorkDuration("w1", 24_000, 3_000)]));

        Assert.Empty(await _repo.GetPendingBaselineCyclesAsync(10));

        var row = Assert.Single(await _repo.QueryCyclesAsync(T0 - 1, T0 + 60_000));
        Assert.Equal(ExcludeReason.None, row.Exclude);
        Assert.Equal(10_000, row.RUsedMs, 6);
        Assert.Equal(8_000, row.MtMedianUsedMs, 6);
        Assert.Equal(8.0, row.WorstRatio, 6);

        // 이제 다른 행과 똑같이 판정된다 — work 가 W 의 8배라 비가동(work 축이 MT 3.25배보다 임계 대비 크다).
        Assert.Equal(CycleState.Down, KpiRules.Classify(row.ToFact(), KpiKappa.Default));
        Assert.Equal(DownAxis.Work, KpiRules.Axis(row.ToFact(), KpiKappa.Default));

        var works = await _repo.GetCycleWorksAsync(id);
        Assert.Equal(3_000, Assert.Single(works).WUsedMs, 6);
    }

    [Fact]
    public async Task 기준선_스냅샷은_같은_날이면_덮어쓴다()
    {
        await _repo.UpsertBaselineAsync([new BaselineRow("R", "FlowA", "", "", "2026-09-17", 10_000, 12)]);
        await _repo.UpsertBaselineAsync([new BaselineRow("R", "FlowA", "", "", "2026-09-17", 11_000, 13)]);
        await _repo.UpsertBaselineAsync([new BaselineRow("W", "FlowA", "", "w1", "2026-09-17", 3_000, 20, 2_500, 3_600)]);
        await _repo.UpsertBaselineAsync([new BaselineRow("MT", "FlowA", "", "", "2026-09-17", 8_000, 12)]);
        // 예외 없이 통과하면 PK 충돌이 갱신으로 흡수된 것.
    }

    [Fact]
    public async Task 보존_삭제는_기준시각_이전만_지운다()
    {
        // 원시 신호 표는 아직 기존 이름·텍스트 시각이다(7단계에서 정수 epoch 로 교체).
        static string Iso(long ms) => KpiTime.ToUtc(ms).ToString("yyyy-MM-dd HH:mm:ss.fffffff") + "Z";
        await using (var conn = _db.Open())
        {
            await Dapper.SqlMapper.ExecuteAsync(conn,
                """
                CREATE TABLE plcTagLog (id INTEGER PRIMARY KEY AUTOINCREMENT, plcTagId INTEGER NOT NULL,
                                        dateTime DATETIME NOT NULL, value TEXT NOT NULL)
                """);
            await Dapper.SqlMapper.ExecuteAsync(conn,
                "INSERT INTO plcTagLog (plcTagId, dateTime, value) VALUES (1, @a, 'true'), (1, @b, 'false')",
                new { a = Iso(T0 - 10_000), b = Iso(T0 + 10_000) });
        }
        Assert.Equal(1, await _repo.PruneRawBeforeAsync(T0));
    }
}
