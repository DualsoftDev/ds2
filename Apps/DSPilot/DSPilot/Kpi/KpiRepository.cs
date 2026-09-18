// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Dapper;
using Microsoft.Data.Sqlite;

namespace DSPilot.Kpi;

/// <summary>
/// 시간 기반 코어 DB 접근. 저장은 "완료된 사이클 1행 + 그 사이클의 work N행" 한 트랜잭션이 전부다.
/// 조회는 구간을 받아 행을 그대로 돌려준다 — 집계·판정은 <see cref="KpiRules"/> 가 한다.
/// </summary>
public sealed class KpiRepository
{
    private readonly KpiDb _db;
    private readonly ILogger<KpiRepository> _logger;

    public KpiRepository(KpiDb db, ILogger<KpiRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── 쓰기 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 사이클 1행 + work N행 저장. 같은 (flow, startMs) 가 이미 있으면 갱신한다(재도출 멱등).
    /// <para>
    /// id 회수는 RETURNING 을 쓴다 — 풀링 커넥션에서 last_insert_rowid() 는 다른 논리 연산의 id 를
    /// 돌려줄 수 있다(reference: sqlite pooling last_insert_rowid 함정).
    /// </para>
    /// </summary>
    public async Task<long> SaveCycleAsync(
        CycleRecord cycle, IReadOnlyList<WorkDuration> works, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var tx = conn.BeginTransaction();
        try
        {
            var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO cycle (flow, branch, startMs, endMs, ctMs, mtMs, wtMs,
                                   rUsedMs, mtMedianUsedMs, worstWork, worstRatio, overflowMs, excludeReason, specVersion)
                VALUES (@Flow, @Branch, @StartMs, @EndMs, @CtMs, @MtMs, @WtMs,
                        @RUsedMs, @MtMedianUsedMs, @WorstWork, @WorstRatio, @OverflowMs, @Exclude, @Spec)
                ON CONFLICT(flow, startMs) DO UPDATE SET
                    branch=excluded.branch, endMs=excluded.endMs, ctMs=excluded.ctMs,
                    mtMs=excluded.mtMs, wtMs=excluded.wtMs,
                    rUsedMs=excluded.rUsedMs, mtMedianUsedMs=excluded.mtMedianUsedMs,
                    worstWork=excluded.worstWork, worstRatio=excluded.worstRatio, overflowMs=excluded.overflowMs,
                    excludeReason=excluded.excludeReason, specVersion=excluded.specVersion
                RETURNING id
                """,
                new
                {
                    cycle.Flow,
                    cycle.Branch,
                    cycle.StartMs,
                    cycle.EndMs,
                    cycle.CtMs,
                    cycle.MtMs,
                    cycle.WtMs,
                    cycle.RUsedMs,
                    cycle.MtMedianUsedMs,
                    cycle.WorstWork,
                    cycle.WorstRatio,
                    cycle.OverflowMs,
                    Exclude = (int)cycle.Exclude,
                    Spec = KpiDb.SpecVersion,
                },
                transaction: tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM cycleWork WHERE cycleId=@id", new { id }, transaction: tx, cancellationToken: ct));

            if (works.Count > 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO cycleWork (cycleId, work, durationMs, wUsedMs, gated)
                    VALUES (@id, @Work, @DurationMs, @WUsedMs, @Gated)
                    """,
                    works.Select(w => new { id, w.Work, w.DurationMs, w.WUsedMs, Gated = w.Gated ? 1 : 0 }),
                    transaction: tx, cancellationToken: ct));
            }

            tx.Commit();
            return id;
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { /* 이미 닫힘 */ }
            _logger.LogError(ex, "[Kpi] SaveCycle failed — flow={Flow} start={Start}", cycle.Flow, cycle.StartMs);
            return 0;
        }
    }

    /// <summary>구간의 사이클 행을 지운다(재도출 전 단계). cycleWork 는 FK CASCADE 로 함께 지워진다.</summary>
    public async Task<int> DeleteCyclesAsync(string flow, long fromMs, long toMs, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition("PRAGMA foreign_keys=ON;", cancellationToken: ct));
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM cycle WHERE flow=@flow AND startMs >= @fromMs AND startMs < @toMs",
            new { flow, fromMs, toMs }, cancellationToken: ct));
    }

    /// <summary>기준선 일별 스냅샷 기록(같은 날 재계산이면 덮어쓴다).</summary>
    public async Task UpsertBaselineAsync(IReadOnlyList<BaselineRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO baseline (scope, flow, branch, work, asOfDate, valueMs, sampleCount, q1Ms, q3Ms)
            VALUES (@Scope, @Flow, @Branch, @Work, @AsOfDate, @ValueMs, @SampleCount, @Q1Ms, @Q3Ms)
            ON CONFLICT(scope, flow, branch, work, asOfDate) DO UPDATE SET
                valueMs=excluded.valueMs, sampleCount=excluded.sampleCount,
                q1Ms=excluded.q1Ms, q3Ms=excluded.q3Ms
            """,
            rows, cancellationToken: ct));
    }

    /// <summary>접속 전이·심박 공백 기록.</summary>
    public async Task InsertLinkEventAsync(LinkEventRecord e, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO linkEvent (system, atMs, endMs, isConnected, kind, detail, source)
            VALUES (@System, @AtMs, @EndMs, @IsConnected, @Kind, @Detail, @Source)
            """,
            new
            {
                e.System,
                e.AtMs,
                e.EndMs,
                IsConnected = e.IsConnected ? 1 : 0,
                e.Kind,
                e.Detail,
                e.Source,
            },
            cancellationToken: ct));
    }

    /// <summary>열린 공백(endMs IS NULL)을 닫는다 — 심박이 돌아온 순간.</summary>
    public async Task<int> CloseOpenGapAsync(string system, long endMs, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE linkEvent SET endMs=@endMs WHERE system=@system AND kind=@kind AND endMs IS NULL",
            new { system, endMs, kind = LinkEventRecord.KindGap }, cancellationToken: ct));
    }

    // ── 조회 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 구간과 겹치는 사이클 행. 구간 경계에 걸친 행은 <see cref="ExcludeReason.Cut"/> 으로 표시해 돌려준다 —
    /// 연표에는 잘라 그리되 개수·합산에서는 빠진다(doc/30 §3 · §6).
    /// </summary>
    public async Task<List<CycleRow>> QueryCyclesAsync(
        long fromMs, long toMs, string? flow = null, string? branch = null, CancellationToken ct = default)
    {
        await using var conn = _db.OpenRead();
        var sql = """
            SELECT id, flow, branch, startMs, endMs, ctMs, mtMs, wtMs,
                   rUsedMs, mtMedianUsedMs, worstWork, worstRatio, overflowMs, excludeReason
            FROM cycle
            WHERE endMs > @fromMs AND startMs < @toMs
            """;
        if (!string.IsNullOrWhiteSpace(flow)) sql += " AND flow = @flow";
        if (!string.IsNullOrWhiteSpace(branch)) sql += " AND branch = @branch";
        sql += " ORDER BY startMs";

        var raw = await conn.QueryAsync(new CommandDefinition(
            sql, new { fromMs, toMs, flow, branch }, cancellationToken: ct));

        var rows = new List<CycleRow>();
        foreach (var r in raw)
        {
            long start = (long)r.startMs;
            long end = (long)r.endMs;
            var stored = (ExcludeReason)(int)(long)r.excludeReason;
            // 저장된 사유가 우선(진행 중·기준 없음·미분류). 그렇지 않으면 경계 잘림 여부로 판단.
            var exclude = stored != ExcludeReason.None
                ? stored
                : (start < fromMs || end > toMs ? ExcludeReason.Cut : ExcludeReason.None);

            rows.Add(new CycleRow(
                (long)r.id,
                (string)r.flow,
                r.branch as string,
                start,
                end,
                (long)r.ctMs,
                r.mtMs is null ? (long?)null : (long)r.mtMs,
                r.wtMs is null ? (long?)null : (long)r.wtMs,
                Convert.ToDouble(r.rUsedMs),
                Convert.ToDouble(r.mtMedianUsedMs),
                r.worstWork as string,
                Convert.ToDouble(r.worstRatio),
                (long)r.overflowMs,
                exclude));
        }
        return rows;
    }

    /// <summary>한 사이클의 work 지속시간 — 세그먼트 툴팁·간트 상세용. 게이트에 걸린 work 도 함께 준다(표시용).</summary>
    public async Task<List<WorkDuration>> GetCycleWorksAsync(long cycleId, CancellationToken ct = default)
    {
        await using var conn = _db.OpenRead();
        var rows = await conn.QueryAsync<(string Work, long DurationMs, double WUsedMs, long Gated)>(new CommandDefinition(
            """
            SELECT work AS Work, durationMs AS DurationMs, wUsedMs AS WUsedMs, gated AS Gated
            FROM cycleWork WHERE cycleId=@cycleId ORDER BY durationMs DESC
            """,
            new { cycleId }, cancellationToken: ct));
        return rows.Select(r => new WorkDuration(r.Work, r.DurationMs, r.WUsedMs, r.Gated != 0)).ToList();
    }

    /// <summary>
    /// R 표본 — 최근 창의 완료 CT. 제외 행(진행 중·기준 없음·미분류)은 빼고, 비생산으로 보일 만큼 긴 행도
    /// 중앙값 특성상 자연히 밀려나므로 따로 거르지 않는다(원본 스펙 §3 의 중앙값 채택 이유).
    /// </summary>
    public async Task<List<long>> GetCtSamplesAsync(
        string flow, string? branch, long sinceMs, CancellationToken ct = default)
    {
        await using var conn = _db.OpenRead();
        var sql = "SELECT ctMs FROM cycle WHERE flow=@flow AND startMs >= @sinceMs AND ctMs > 0 AND excludeReason = 0";
        sql += branch is null ? " AND branch IS NULL" : " AND branch = @branch";
        var rows = await conn.QueryAsync<long>(new CommandDefinition(
            sql, new { flow, branch, sinceMs }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>MT중앙 표본 — 최근 창에서 work 가 하나라도 잡힌 완료 사이클의 MT.</summary>
    public async Task<List<long>> GetMtSamplesAsync(
        string flow, string? branch, long sinceMs, CancellationToken ct = default)
    {
        await using var conn = _db.OpenRead();
        var sql = "SELECT mtMs FROM cycle WHERE flow=@flow AND startMs >= @sinceMs AND mtMs > 0 AND excludeReason = 0";
        sql += branch is null ? " AND branch IS NULL" : " AND branch = @branch";
        var rows = await conn.QueryAsync<long>(new CommandDefinition(
            sql, new { flow, branch, sinceMs }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>W 표본 — 최근 창에서 그 work 가 실제 실행된 사이클의 지속시간. 분기가 있으면 분기별(doc/30 §4).</summary>
    public async Task<Dictionary<string, List<long>>> GetWorkSamplesAsync(
        string flow, string? branch, long sinceMs, CancellationToken ct = default)
    {
        await using var conn = _db.OpenRead();
        var sql = """
            SELECT w.work AS Work, w.durationMs AS DurationMs
            FROM cycleWork w JOIN cycle c ON c.id = w.cycleId
            WHERE c.flow=@flow AND c.startMs >= @sinceMs AND c.excludeReason = 0 AND w.durationMs > 0
            """;
        sql += branch is null ? " AND c.branch IS NULL" : " AND c.branch = @branch";
        var rows = await conn.QueryAsync<(string Work, long DurationMs)>(new CommandDefinition(
            sql, new { flow, branch, sinceMs }, cancellationToken: ct));

        var map = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!map.TryGetValue(r.Work, out var list)) map[r.Work] = list = [];
            list.Add(r.DurationMs);
        }
        return map;
    }

    /// <summary>구간·flow 에서 게이트에 걸린(판정 제외) work 이름 — 간트 Work 헤더 표시용(doc/30 §4.1 · §9.2).</summary>
    public async Task<List<string>> GetGatedWorksAsync(
        long fromMs, long toMs, string? flow, CancellationToken ct = default)
    {
        await using var conn = _db.OpenRead();
        var sql = """
            SELECT DISTINCT w.work
            FROM cycleWork w JOIN cycle c ON c.id = w.cycleId
            WHERE w.gated = 1 AND c.endMs > @fromMs AND c.startMs < @toMs
            """;
        if (!string.IsNullOrWhiteSpace(flow)) sql += " AND c.flow = @flow";
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            sql, new { fromMs, toMs, flow }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>수집 중인 flow·분기 목록 — 기준선 계산 대상.</summary>
    public async Task<List<(string Flow, string? Branch)>> GetActiveScopesAsync(
        long sinceMs, CancellationToken ct = default)
    {
        await using var conn = _db.OpenRead();
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT DISTINCT flow, branch FROM cycle WHERE startMs >= @sinceMs",
            new { sinceMs }, cancellationToken: ct));
        return rows.Select(r => ((string)r.flow, r.branch as string)).ToList();
    }

    /// <summary>
    /// 아직 기준선을 못 받은 행(표본 K 미달 시점에 적재된 행). 설치 직후 첫 사이클들이 여기 해당하며,
    /// 표본이 쌓여 기준선이 생기면 <see cref="StampBaselineAsync"/> 로 뒤늦게 찍어 준다 — 안 그러면
    /// 첫 묶음이 영원히 계산 밖에 남는다.
    /// </summary>
    public async Task<List<(long Id, string Flow, string? Branch)>> GetPendingBaselineCyclesAsync(
        int limit, CancellationToken ct = default)
    {
        await using var conn = _db.OpenRead();
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT id, flow, branch FROM cycle WHERE excludeReason = @r ORDER BY startMs LIMIT @limit",
            new { r = (int)ExcludeReason.NoBaseline, limit }, cancellationToken: ct));
        return rows.Select(x => ((long)x.id, (string)x.flow, x.branch as string)).ToList();
    }

    /// <summary>
    /// 뒤늦게 기준선을 박제한다. 행의 R·MT중앙·최악 work·배율과 각 work 의 W·게이트를 갱신하고 제외 표시를 푼다.
    /// 이후에는 다른 행과 똑같이 현재 κ 로 판정된다.
    /// </summary>
    public async Task<bool> StampBaselineAsync(
        long cycleId, double rMs, double mtMedianMs, string? worstWork, double worstRatio,
        IReadOnlyList<WorkDuration> works, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var tx = conn.BeginTransaction();
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE cycle SET rUsedMs=@rMs, mtMedianUsedMs=@mtMedianMs,
                                 worstWork=@worstWork, worstRatio=@worstRatio, excludeReason=0
                WHERE id=@cycleId
                """,
                new { cycleId, rMs, mtMedianMs, worstWork, worstRatio }, transaction: tx, cancellationToken: ct));

            if (works.Count > 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE cycleWork SET wUsedMs=@WUsedMs, gated=@Gated WHERE cycleId=@cycleId AND work=@Work",
                    works.Select(w => new { cycleId, w.Work, w.WUsedMs, Gated = w.Gated ? 1 : 0 }),
                    transaction: tx, cancellationToken: ct));
            }
            tx.Commit();
            return true;
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { /* 이미 닫힘 */ }
            _logger.LogWarning(ex, "[Kpi] StampBaseline failed — cycle={Id}", cycleId);
            return false;
        }
    }

    /// <summary>flow 별 마지막 적재 지점(= 저장된 사이클 끝의 최댓값). 적재 서비스의 워터마크.</summary>
    public async Task<Dictionary<string, long>> GetCycleWatermarksAsync(CancellationToken ct = default)
    {
        await using var conn = _db.OpenRead();
        var rows = await conn.QueryAsync<(string Flow, long MaxEnd)>(new CommandDefinition(
            "SELECT flow AS Flow, MAX(endMs) AS MaxEnd FROM cycle GROUP BY flow", cancellationToken: ct));
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var r in rows) map[r.Flow] = r.MaxEnd;
        return map;
    }

    /// <summary>구간과 겹치는 접속 공백·전이 — 연표 오버레이용(계산 인자 아님).</summary>
    public async Task<List<LinkEventRecord>> QueryLinkEventsAsync(
        long fromMs, long toMs, CancellationToken ct = default)
    {
        await using var conn = _db.OpenRead();
        var rows = await conn.QueryAsync(new CommandDefinition(
            """
            SELECT system, atMs, endMs, isConnected, kind, detail, source
            FROM linkEvent
            WHERE atMs < @toMs AND (endMs IS NULL OR endMs > @fromMs)
            ORDER BY atMs
            """,
            new { fromMs, toMs }, cancellationToken: ct));

        return rows.Select(r => new LinkEventRecord(
            (string)r.system,
            (long)r.atMs,
            r.endMs is null ? (long?)null : (long)r.endMs,
            (long)r.isConnected != 0,
            (string)r.kind,
            r.detail as string,
            r.source as string)).ToList();
    }

    /// <summary>
    /// 롤링 보존 — 원시 신호와 알람에서 기준 시각 이전 행을 지운다.
    /// 원시 표는 아직 기존 이름(plcTagLog · userTagAlertLog)이고 시각이 텍스트라 문자열 경계로 비교한다.
    /// 3차에서 표를 정수 epoch 로 바꾸면 이 비교도 정수로 바뀐다.
    /// </summary>
    public async Task<int> PruneRawBeforeAsync(long beforeMs, CancellationToken ct = default)
    {
        var boundary = KpiTime.ToUtc(beforeMs).ToString("yyyy-MM-dd HH:mm:ss.fffffff") + "Z";
        await using var conn = _db.Open();
        int n = 0;
        foreach (var (table, column) in new[] { ("plcTagLog", "dateTime"), ("userTagAlertLog", "occurredAt") })
        {
            try
            {
                n += await conn.ExecuteAsync(new CommandDefinition(
                    $"DELETE FROM {table} WHERE {column} < @boundary", new { boundary }, cancellationToken: ct));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Kpi] prune skipped — {Table}", table);
            }
        }
        return n;
    }
}
