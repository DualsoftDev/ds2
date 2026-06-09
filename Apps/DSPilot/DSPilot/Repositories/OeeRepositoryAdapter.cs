// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Text;
using Dapper;
using DSPilot.Infrastructure;
using DSPilot.Models.Oee;
using DSPilot.Services;
using Microsoft.Data.Sqlite;

namespace DSPilot.Repositories;

/// <summary>
/// OEE / 정지 저장소 Dapper 구현 — 별도 oee.db.
/// 경로 = GetSharedDbPath() 의 디렉터리 + "oee.db" (plc.db 와 동일 Shared 폴더, 별도 파일).
/// raw 재구축 대상인 plc.db 와 분리 — 작업자가 입력한 분류/불량 수량을 보존하기 위함(doc/21 §1).
/// 모든 시간은 ISO8601 UTC 문자열 (SqliteDateTimeHelpers). 읽을 때 로컬 변환.
/// </summary>
public sealed class OeeRepositoryAdapter : IOeeRepository
{
    private readonly IDatabasePathResolver _pathResolver;
    private readonly ILogger<OeeRepositoryAdapter> _logger;

    public OeeRepositoryAdapter(IDatabasePathResolver pathResolver, ILogger<OeeRepositoryAdapter> logger)
    {
        _pathResolver = pathResolver;
        _logger = logger;
    }

    /// <summary>plc.db 와 같은 디렉터리의 oee.db 경로.</summary>
    private string OeeDbPath()
    {
        var shared = _pathResolver.GetSharedDbPath();
        var dir = Path.GetDirectoryName(shared);
        return string.IsNullOrEmpty(dir) ? "oee.db" : Path.Combine(dir, "oee.db");
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection($"Data Source={OeeDbPath()};Mode=ReadWriteCreate;Default Timeout=20");
        await conn.OpenAsync();
        return conn;
    }

    private static string Iso(DateTime utc) => SqliteDateTimeHelpers.ToSqliteUtcString(utc);
    private static DateTime ParseIso(string? s) => SqliteDateTimeHelpers.FromSqliteUtcString(s) ?? DateTime.MinValue;
    private static DateTime? ParseIsoNullable(string? s) =>
        string.IsNullOrEmpty(s) ? null : SqliteDateTimeHelpers.FromSqliteUtcString(s);

    // ── 스키마 (doc/21 §2 DDL) ────────────────────────────────────────────

    public async Task<bool> CreateSchemaAsync()
    {
        try
        {
            await using var conn = await OpenAsync();

            // WAL — reader(요약/순위 쿼리)가 상태머신의 write 와 충돌하지 않도록.
            try
            {
                var mode = await conn.ExecuteScalarAsync<string>("PRAGMA journal_mode=WAL");
                await conn.ExecuteAsync("PRAGMA synchronous=NORMAL");
                _logger.LogInformation("oee.db journal_mode={Mode}, synchronous=NORMAL", mode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set WAL pragma on oee.db");
            }

            const string createDowntime = @"
                CREATE TABLE IF NOT EXISTS oeeDowntimeEvent (
                  id           INTEGER PRIMARY KEY AUTOINCREMENT,
                  systemName   TEXT NOT NULL,
                  flowName     TEXT,
                  deviceName   TEXT,
                  startAt      TEXT NOT NULL,
                  endAt        TEXT,
                  durationMs   INTEGER,
                  reasonCode   TEXT,
                  category     TEXT,
                  isFailure    INTEGER NOT NULL DEFAULT 0,
                  detectSource TEXT NOT NULL,
                  sourceLogId  INTEGER,
                  note         TEXT,
                  createdAt    DATETIME DEFAULT (datetime('now'))
                )";
            const string idxDowntimeSystemTime =
                "CREATE INDEX IF NOT EXISTS idx_oeeDowntimeEvent_system_time ON oeeDowntimeEvent(systemName, startAt)";
            const string idxDowntimeFlowTime =
                "CREATE INDEX IF NOT EXISTS idx_oeeDowntimeEvent_flow_time ON oeeDowntimeEvent(flowName, startAt)";
            // onset 멱등 가드 — usertag 동일 sourceLogId 중복 INSERT 방지.
            const string uqDowntimeSrc =
                "CREATE UNIQUE INDEX IF NOT EXISTS uq_oeeDowntimeEvent_src ON oeeDowntimeEvent(detectSource, sourceLogId) WHERE sourceLogId IS NOT NULL";

            const string createProduction = @"
                CREATE TABLE IF NOT EXISTS oeeProductionCount (
                  bucketDate  TEXT NOT NULL,
                  flowName    TEXT NOT NULL,
                  shift       TEXT NOT NULL DEFAULT '',
                  totalCount  INTEGER NOT NULL DEFAULT 0,
                  goodCount   INTEGER NOT NULL DEFAULT 0,
                  rejectCount INTEGER NOT NULL DEFAULT 0,
                  source      TEXT NOT NULL DEFAULT 'cycle',
                  PRIMARY KEY (bucketDate, flowName, shift)
                )";

            const string createShift = @"
                CREATE TABLE IF NOT EXISTS oeeShiftException (
                  id         INTEGER PRIMARY KEY AUTOINCREMENT,
                  flowName   TEXT,
                  startAt    TEXT NOT NULL,
                  endAt      TEXT NOT NULL,
                  kind       TEXT NOT NULL,
                  note       TEXT
                )";
            const string idxShiftTime =
                "CREATE INDEX IF NOT EXISTS idx_oeeShiftException_time ON oeeShiftException(startAt, endAt)";

            await conn.ExecuteAsync(createDowntime);
            await conn.ExecuteAsync(idxDowntimeSystemTime);
            await conn.ExecuteAsync(idxDowntimeFlowTime);
            await conn.ExecuteAsync(uqDowntimeSrc);
            await conn.ExecuteAsync(createProduction);
            await conn.ExecuteAsync(createShift);
            await conn.ExecuteAsync(idxShiftTime);

            _logger.LogInformation("OEE schema ensured (oeeDowntimeEvent / oeeProductionCount / oeeShiftException) at {Path}", OeeDbPath());
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create OEE schema");
            return false;
        }
    }

    // ── 정지(다운타임) ────────────────────────────────────────────────────

    public async Task<long> InsertDowntimeAsync(OeeDowntimeEvent e, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        // sourceLogId 유니크 충돌(usertag 멱등) 시 INSERT 가 안 일어나고 0 반환 → 호출부에서 dedupe 로 해석.
        // ★last_insert_rowid() 를 쓰지 않는다: Microsoft.Data.Sqlite 가 네이티브 핸들을 풀링하면
        //   last_insert_rowid 가 핸들에 sticky 라, ON CONFLICT DO NOTHING(미삽입) 시에도 직전 INSERT 의
        //   rowid 를 돌려줘 "0=중복" 판별이 깨진다. RETURNING 은 실제 삽입된 행만 반환(미삽입=0행)하므로 정확.
        const string sql = @"
            INSERT INTO oeeDowntimeEvent
                (systemName, flowName, deviceName, startAt, endAt, durationMs,
                 reasonCode, category, isFailure, detectSource, sourceLogId, note)
            VALUES
                (@SystemName, @FlowName, @DeviceName, @StartAt, @EndAt, @DurationMs,
                 @ReasonCode, @Category, @IsFailure, @DetectSource, @SourceLogId, @Note)
            ON CONFLICT(detectSource, sourceLogId) WHERE sourceLogId IS NOT NULL DO NOTHING
            RETURNING id;";

        return await conn.ExecuteScalarAsync<long?>(sql, new
        {
            e.SystemName,
            e.FlowName,
            e.DeviceName,
            StartAt = Iso(e.StartAt),
            EndAt = e.EndAt.HasValue ? Iso(e.EndAt.Value) : null,
            e.DurationMs,
            e.ReasonCode,
            e.Category,
            e.IsFailure,
            e.DetectSource,
            e.SourceLogId,
            e.Note,
        }) ?? 0L;
    }

    public async Task<int> CloseDowntimeAsync(long id, DateTime endAtUtc, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        // durationMs = endAt - startAt. SQLite 가 startAt(ISO) 을 다시 파싱하므로 strftime 으로 ms 차 계산.
        // julianday 차(일) × 86400000 = ms. 'Z' suffix 는 julianday 가 UTC 로 해석.
        const string sql = @"
            UPDATE oeeDowntimeEvent
            SET endAt = @EndAt,
                durationMs = CAST((julianday(@EndAt) - julianday(startAt)) * 86400000 AS INTEGER)
            WHERE id = @Id AND endAt IS NULL";
        return await conn.ExecuteAsync(sql, new { Id = id, EndAt = Iso(endAtUtc) });
    }

    public async Task<int> ClassifyDowntimeAsync(long id, string? reasonCode, string? category, bool isFailure, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        const string sql = @"
            UPDATE oeeDowntimeEvent
            SET reasonCode = @ReasonCode,
                category   = @Category,
                isFailure  = @IsFailure
            WHERE id = @Id";
        return await conn.ExecuteAsync(sql, new
        {
            Id = id,
            ReasonCode = reasonCode,
            Category = category,
            IsFailure = isFailure ? 1 : 0,
        });
    }

    public async Task<int> BulkClassifyDowntimeAsync(IReadOnlyList<long> ids, string? reasonCode, string? category, bool isFailure, CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;
        await using var conn = await OpenAsync();
        const string sql = @"
            UPDATE oeeDowntimeEvent
            SET reasonCode = @ReasonCode,
                category   = @Category,
                isFailure  = @IsFailure
            WHERE id IN @Ids";
        return await conn.ExecuteAsync(sql, new
        {
            Ids = ids,
            ReasonCode = reasonCode,
            Category = category,
            IsFailure = isFailure ? 1 : 0,
        });
    }

    public async Task<int> BulkCloseDowntimeAsync(IReadOnlyList<long> ids, DateTime endAtUtc, CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;
        await using var conn = await OpenAsync();
        const string sql = @"
            UPDATE oeeDowntimeEvent
            SET endAt = @EndAt,
                durationMs = CAST((julianday(@EndAt) - julianday(startAt)) * 86400000 AS INTEGER)
            WHERE id IN @Ids AND endAt IS NULL";
        return await conn.ExecuteAsync(sql, new { Ids = ids, EndAt = Iso(endAtUtc) });
    }

    public async Task<int> ClearDowntimeEventsAsync(CancellationToken ct = default)
    {
        // 정지 이벤트만 비운다 — oeeProductionCount / oeeShiftException(수동입력 자산)은 그대로 둔다.
        await using var conn = await OpenAsync();
        return await conn.ExecuteAsync("DELETE FROM oeeDowntimeEvent");
    }

    public async Task<int> DeleteDowntimeEventsBeforeAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteAsync(
            "DELETE FROM oeeDowntimeEvent WHERE startAt < @Cutoff",
            new { Cutoff = Iso(cutoffUtc) });
    }

    private static (string Where, DynamicParameters Params) BuildDowntimeFilter(
        DateTime fromUtc, DateTime toUtc, string? status, string? reasonCode, string? flowName)
    {
        var sb = new StringBuilder(" WHERE startAt >= @From AND startAt <= @To ");
        var p = new DynamicParameters();
        p.Add("From", Iso(fromUtc));
        p.Add("To", Iso(toUtc));

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (status.Equals("open", StringComparison.OrdinalIgnoreCase))
                sb.Append(" AND endAt IS NULL ");
            else if (status.Equals("recovered", StringComparison.OrdinalIgnoreCase))
                sb.Append(" AND endAt IS NOT NULL ");
        }
        if (!string.IsNullOrWhiteSpace(reasonCode))
        {
            sb.Append(" AND reasonCode = @Reason ");
            p.Add("Reason", reasonCode.Trim());
        }
        if (!string.IsNullOrWhiteSpace(flowName))
        {
            sb.Append(" AND flowName = @Flow ");
            p.Add("Flow", flowName.Trim());
        }
        return (sb.ToString(), p);
    }

    public async Task<IReadOnlyList<OeeDowntimeDto>> QueryDowntimeAsync(
        DateTime fromUtc, DateTime toUtc,
        string? status, string? reasonCode, string? flowName,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var (where, p) = BuildDowntimeFilter(fromUtc, toUtc, status, reasonCode, flowName);
        var sql = $@"
            SELECT id, systemName, flowName, deviceName, startAt, endAt, durationMs,
                   reasonCode, category, isFailure, detectSource, sourceLogId, note
            FROM oeeDowntimeEvent
            {where}
            ORDER BY startAt DESC, id DESC";
        var rows = await conn.QueryAsync<DowntimeRow>(sql, p);
        return rows.Select(MapDowntimeDto).ToList();
    }

    public async Task<IReadOnlyList<OeeDowntimeEvent>> GetOpenEventsAsync(string? flowName = null, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var sql = @"
            SELECT id, systemName, flowName, deviceName, startAt, endAt, durationMs,
                   reasonCode, category, isFailure, detectSource, sourceLogId, note
            FROM oeeDowntimeEvent
            WHERE endAt IS NULL";
        if (!string.IsNullOrWhiteSpace(flowName))
            sql += " AND flowName = @Flow";
        var rows = await conn.QueryAsync<DowntimeRow>(sql, new { Flow = flowName });
        return rows.Select(MapEntity).ToList();
    }

    public async Task<(long DowntimeMs, int Count)> GetDowntimeAggregateAsync(
        DateTime fromUtc, DateTime toUtc, string? flowName, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var p = new DynamicParameters();
        p.Add("From", Iso(fromUtc));
        p.Add("To", Iso(toUtc));
        p.Add("Now", Iso(DateTime.UtcNow));
        var flowClause = "";
        if (!string.IsNullOrWhiteSpace(flowName))
        {
            flowClause = " AND flowName = @Flow ";
            p.Add("Flow", flowName.Trim());
        }
        // open 이벤트는 durationMs 가 NULL 이므로 now 까지 진행분을 보정해 합산.
        var sql = $@"
            SELECT
              COALESCE(SUM(
                CASE WHEN durationMs IS NOT NULL THEN durationMs
                     ELSE CAST((julianday(@Now) - julianday(startAt)) * 86400000 AS INTEGER)
                END), 0) AS DowntimeMs,
              COUNT(*) AS Cnt
            FROM oeeDowntimeEvent
            WHERE startAt >= @From AND startAt <= @To {flowClause}";
        var row = await conn.QueryFirstAsync<AggRow>(sql, p);
        return (row.DowntimeMs, row.Cnt);
    }

    public async Task<(long FailureDurationMs, int FailureCount)> GetFailureAggregateAsync(
        DateTime fromUtc, DateTime toUtc, string? flowName, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var p = new DynamicParameters();
        p.Add("From", Iso(fromUtc));
        p.Add("To", Iso(toUtc));
        var flowClause = "";
        if (!string.IsNullOrWhiteSpace(flowName))
        {
            flowClause = " AND flowName = @Flow ";
            p.Add("Flow", flowName.Trim());
        }
        // 마감된(durationMs IS NOT NULL) 고장만 MTTR 분자에 — open 은 durationMs 무한증가 위험 제외.
        var sql = $@"
            SELECT COALESCE(SUM(durationMs), 0) AS DowntimeMs, COUNT(*) AS Cnt
            FROM oeeDowntimeEvent
            WHERE isFailure = 1 AND durationMs IS NOT NULL
              AND startAt >= @From AND startAt <= @To {flowClause}";
        var row = await conn.QueryFirstAsync<AggRow>(sql, p);
        return (row.DowntimeMs, row.Cnt);
    }

    public async Task<IReadOnlyList<(string FlowName, long DowntimeMs, int Count)>> GetDowntimeByFlowAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var sql = @"
            SELECT flowName AS FlowName,
              COALESCE(SUM(
                CASE WHEN durationMs IS NOT NULL THEN durationMs
                     ELSE CAST((julianday(@Now) - julianday(startAt)) * 86400000 AS INTEGER)
                END), 0) AS DowntimeMs,
              COUNT(*) AS Cnt
            FROM oeeDowntimeEvent
            WHERE startAt >= @From AND startAt <= @To AND flowName IS NOT NULL
            GROUP BY flowName
            ORDER BY DowntimeMs DESC";
        var rows = await conn.QueryAsync<FlowAggRow>(sql, new
        {
            From = Iso(fromUtc),
            To = Iso(toUtc),
            Now = Iso(DateTime.UtcNow),
        });
        return rows.Select(r => (r.FlowName ?? "", r.DowntimeMs, r.Cnt)).ToList();
    }

    // ── 생산/품질 ─────────────────────────────────────────────────────────

    public async Task<int> UpsertProductionAsync(OeeProductionCount r, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        const string sql = @"
            INSERT INTO oeeProductionCount (bucketDate, flowName, shift, totalCount, goodCount, rejectCount, source)
            VALUES (@BucketDate, @FlowName, @Shift, @TotalCount, @GoodCount, @RejectCount, @Source)
            ON CONFLICT(bucketDate, flowName, shift) DO UPDATE SET
                totalCount  = excluded.totalCount,
                goodCount   = excluded.goodCount,
                rejectCount = excluded.rejectCount,
                source      = excluded.source";
        return await conn.ExecuteAsync(sql, new
        {
            r.BucketDate,
            r.FlowName,
            Shift = r.Shift ?? "",
            r.TotalCount,
            r.GoodCount,
            r.RejectCount,
            Source = string.IsNullOrEmpty(r.Source) ? "manual" : r.Source,
        });
    }

    public async Task<int> UpsertProductionFromPlcAsync(
        string bucketDate, string flowName, string shift, int? total, int? reject, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();

        var insTotal = total ?? 0;
        var insReject = reject ?? 0;
        var insGood = Math.Max(0, insTotal - insReject);

        // 신규 INSERT: 가진 신호값(없으면 0)으로 생성.
        // 충돌(기존 행) UPDATE: total/reject 는 신호가 있을 때만 덮고(@HasTotal/@HasReject), 없으면 기존값 유지
        //   → 수동 reject 가 plc total 갱신에 휩쓸려 사라지지 않게 한다(plc > manual, 단 미수집 필드는 보존).
        // good 은 항상 (적용된 total - 적용된 reject) 로 재계산.
        const string sql = @"
            INSERT INTO oeeProductionCount (bucketDate, flowName, shift, totalCount, goodCount, rejectCount, source)
            VALUES (@BucketDate, @FlowName, @Shift, @InsTotal, @InsGood, @InsReject, 'plc')
            ON CONFLICT(bucketDate, flowName, shift) DO UPDATE SET
                totalCount  = CASE WHEN @HasTotal = 1 THEN @Total ELSE oeeProductionCount.totalCount END,
                rejectCount = CASE WHEN @HasReject = 1 THEN @Reject ELSE oeeProductionCount.rejectCount END,
                goodCount   = MAX(0,
                    (CASE WHEN @HasTotal  = 1 THEN @Total  ELSE oeeProductionCount.totalCount  END) -
                    (CASE WHEN @HasReject = 1 THEN @Reject ELSE oeeProductionCount.rejectCount END)),
                source      = 'plc'";

        return await conn.ExecuteAsync(sql, new
        {
            BucketDate = bucketDate,
            FlowName = flowName,
            Shift = shift ?? "",
            InsTotal = insTotal,
            InsGood = insGood,
            InsReject = insReject,
            HasTotal = total.HasValue ? 1 : 0,
            Total = total ?? 0,
            HasReject = reject.HasValue ? 1 : 0,
            Reject = reject ?? 0,
        });
    }

    public async Task<(int Total, int Good, int Reject, bool HasReject)> QueryProductionAsync(
        DateTime fromLocal, DateTime toLocal, string? flowName, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var p = new DynamicParameters();
        p.Add("From", fromLocal.ToString("yyyy-MM-dd"));
        p.Add("To", toLocal.ToString("yyyy-MM-dd"));
        var flowClause = "";
        if (!string.IsNullOrWhiteSpace(flowName))
        {
            flowClause = " AND flowName = @Flow ";
            p.Add("Flow", flowName.Trim());
        }
        // 소스 우선규칙(plc > manual): 같은 (기간,flow) 에 plc 행이 하나라도 있으면 plc 행만 합산하고
        // manual 행은 제외한다. plc 폴러는 shift="" 버킷에 쓰고 manual 은 명명 shift 를 쓸 수 있어,
        // shift 분산으로 두 소스가 동시에 합산되면 생산/불량이 이중계상되기 때문(소스 단일화로 방지).
        // plc 행이 없으면 manual 행(여러 shift) 을 정상 합산 → 기존 수동 멀티시프트 동작 보존.
        var sql = $@"
            SELECT COALESCE(SUM(totalCount),0)  AS Total,
                   COALESCE(SUM(goodCount),0)   AS Good,
                   COALESCE(SUM(rejectCount),0) AS Reject,
                   COUNT(*)                     AS Rows
            FROM oeeProductionCount
            WHERE bucketDate >= @From AND bucketDate <= @To {flowClause}
              AND source = (CASE WHEN EXISTS (
                    SELECT 1 FROM oeeProductionCount
                    WHERE bucketDate >= @From AND bucketDate <= @To {flowClause} AND source = 'plc'
              ) THEN 'plc' ELSE source END)";
        var row = await conn.QueryFirstAsync<ProdRow>(sql, p);
        return (row.Total, row.Good, row.Reject, row.Rows > 0);
    }

    // ── 일자별/시간별 정지 버킷 ───────────────────────────────────────────

    public async Task<IReadOnlyList<(string Slot, long PlannedMs, long UnplannedMs)>> GetDowntimeBySlotsAsync(
        DateTime fromUtc, DateTime toUtc, string? flowName, bool hourly, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var fmt = hourly ? "%Y-%m-%d %H:00" : "%Y-%m-%d";
        var p = new DynamicParameters();
        p.Add("From", Iso(fromUtc));
        p.Add("To", Iso(toUtc));
        p.Add("Now", Iso(DateTime.UtcNow));
        var flowClause = "";
        if (!string.IsNullOrWhiteSpace(flowName))
        {
            flowClause = " AND flowName = @Flow ";
            p.Add("Flow", flowName.Trim());
        }
        var sql = $@"
            SELECT
              strftime('{fmt}', startAt, 'localtime') AS Slot,
              COALESCE(SUM(CASE WHEN category = 'planned'
                THEN COALESCE(durationMs, CAST((julianday(@Now) - julianday(startAt)) * 86400000 AS INTEGER))
                ELSE 0 END), 0) AS PlannedMs,
              COALESCE(SUM(CASE WHEN category IS NULL OR category != 'planned'
                THEN COALESCE(durationMs, CAST((julianday(@Now) - julianday(startAt)) * 86400000 AS INTEGER))
                ELSE 0 END), 0) AS UnplannedMs
            FROM oeeDowntimeEvent
            WHERE startAt >= @From AND startAt <= @To {flowClause}
            GROUP BY strftime('{fmt}', startAt, 'localtime')
            ORDER BY Slot";
        var rows = await conn.QueryAsync<SlotRow>(sql, p);
        return rows.Select(r => (r.Slot ?? "", r.PlannedMs, r.UnplannedMs)).ToList();
    }

    private sealed class SlotRow
    {
        public string? Slot { get; set; }
        public long PlannedMs { get; set; }
        public long UnplannedMs { get; set; }
    }

    // ── 시프트 예외 ───────────────────────────────────────────────────────

    public async Task<long> InsertShiftExceptionAsync(OeeShiftException r, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        const string sql = @"
            INSERT INTO oeeShiftException (flowName, startAt, endAt, kind, note)
            VALUES (@FlowName, @StartAt, @EndAt, @Kind, @Note);
            SELECT last_insert_rowid();";
        return await conn.ExecuteScalarAsync<long>(sql, new
        {
            r.FlowName,
            StartAt = Iso(r.StartAt),
            EndAt = Iso(r.EndAt),
            r.Kind,
            r.Note,
        });
    }

    public async Task<IReadOnlyList<OeeShiftException>> QueryShiftExceptionsAsync(
        DateTime fromUtc, DateTime toUtc, string? flowName, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var p = new DynamicParameters();
        p.Add("From", Iso(fromUtc));
        p.Add("To", Iso(toUtc));
        // flowName=NULL(전체 라인) 행은 항상 포함, 특정 flow 필터 시 그 flow + 전체 라인.
        var flowClause = "";
        if (!string.IsNullOrWhiteSpace(flowName))
        {
            flowClause = " AND (flowName = @Flow OR flowName IS NULL) ";
            p.Add("Flow", flowName.Trim());
        }
        var sql = $@"
            SELECT id, flowName, startAt, endAt, kind, note
            FROM oeeShiftException
            WHERE startAt <= @To AND endAt >= @From {flowClause}
            ORDER BY startAt DESC";
        var rows = await conn.QueryAsync<ShiftRow>(sql, p);
        return rows.Select(r => new OeeShiftException
        {
            Id = r.Id,
            FlowName = r.FlowName,
            StartAt = ParseIso(r.StartAt),
            EndAt = ParseIso(r.EndAt),
            Kind = r.Kind ?? "",
            Note = r.Note,
        }).ToList();
    }

    public async Task<int> DeleteShiftExceptionAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteAsync("DELETE FROM oeeShiftException WHERE id = @Id", new { Id = id });
    }

    // ── 매핑 ──────────────────────────────────────────────────────────────

    private static OeeDowntimeDto MapDowntimeDto(DowntimeRow r) => new(
        Id: r.Id,
        SystemName: r.SystemName ?? "",
        FlowName: r.FlowName,
        DeviceName: r.DeviceName,
        StartAt: ParseIso(r.StartAt),
        EndAt: ParseIsoNullable(r.EndAt),
        DurationMs: r.DurationMs,
        ReasonCode: r.ReasonCode,
        Category: r.Category,
        IsFailure: r.IsFailure != 0,
        DetectSource: r.DetectSource ?? "nocycle",
        SourceLogId: r.SourceLogId,
        Note: r.Note,
        Status: string.IsNullOrEmpty(r.EndAt) ? "open" : "recovered");

    private static OeeDowntimeEvent MapEntity(DowntimeRow r) => new()
    {
        Id = r.Id,
        SystemName = r.SystemName ?? "",
        FlowName = r.FlowName,
        DeviceName = r.DeviceName,
        StartAt = ParseIso(r.StartAt),
        EndAt = ParseIsoNullable(r.EndAt),
        DurationMs = r.DurationMs,
        ReasonCode = r.ReasonCode,
        Category = r.Category,
        IsFailure = r.IsFailure,
        DetectSource = r.DetectSource ?? "nocycle",
        SourceLogId = r.SourceLogId,
        Note = r.Note,
    };

    private sealed class DowntimeRow
    {
        public long Id { get; set; }
        public string? SystemName { get; set; }
        public string? FlowName { get; set; }
        public string? DeviceName { get; set; }
        public string? StartAt { get; set; }
        public string? EndAt { get; set; }
        public long? DurationMs { get; set; }
        public string? ReasonCode { get; set; }
        public string? Category { get; set; }
        public int IsFailure { get; set; }
        public string? DetectSource { get; set; }
        public long? SourceLogId { get; set; }
        public string? Note { get; set; }
    }

    private sealed class AggRow
    {
        public long DowntimeMs { get; set; }
        public int Cnt { get; set; }
    }

    private sealed class FlowAggRow
    {
        public string? FlowName { get; set; }
        public long DowntimeMs { get; set; }
        public int Cnt { get; set; }
    }

    private sealed class ProdRow
    {
        public int Total { get; set; }
        public int Good { get; set; }
        public int Reject { get; set; }
        public int Rows { get; set; }
    }

    private sealed class ShiftRow
    {
        public long Id { get; set; }
        public string? FlowName { get; set; }
        public string? StartAt { get; set; }
        public string? EndAt { get; set; }
        public string? Kind { get; set; }
        public string? Note { get; set; }
    }
}
