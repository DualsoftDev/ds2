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

            // 자동 비생산 감지 로그 (10×CT, doc/22 §3.3) — ComputeCycleAggregateAsync 가 조회 시 UPSERT(materialize).
            // flowName: 라인 스코프 감지는 ""(빈문자열)로 저장 — SQLite UNIQUE 에서 NULL 은 서로 distinct 라 dedup 이 깨지기 때문.
            const string createNonProdLog = @"
                CREATE TABLE IF NOT EXISTS oeeNonProdDetectionLog (
                  id              INTEGER PRIMARY KEY AUTOINCREMENT,
                  flowName        TEXT NOT NULL DEFAULT '',
                  onsetAt         TEXT NOT NULL,
                  clearAt         TEXT,
                  durationMs      INTEGER NOT NULL DEFAULT 0,
                  detectionSource TEXT NOT NULL DEFAULT 'auto-10xct',
                  detectionReason TEXT NOT NULL,
                  ctThresholdMs   REAL NOT NULL DEFAULT 0,
                  ctMultiplier    REAL NOT NULL DEFAULT 10,
                  createdAt       DATETIME DEFAULT (datetime('now'))
                )";
            // dedup 멱등 키 — 같은 (flow, 시작시각, 사유)면 재조회 시 재삽입 대신 끝/지속/임계 갱신(UPSERT).
            const string uqNonProdLog =
                "CREATE UNIQUE INDEX IF NOT EXISTS uq_oeeNonProdLog_key ON oeeNonProdDetectionLog(flowName, onsetAt, detectionReason)";
            const string idxNonProdLogTime =
                "CREATE INDEX IF NOT EXISTS idx_oeeNonProdLog_time ON oeeNonProdDetectionLog(onsetAt)";

            await conn.ExecuteAsync(createDowntime);
            await conn.ExecuteAsync(idxDowntimeSystemTime);
            await conn.ExecuteAsync(idxDowntimeFlowTime);
            await conn.ExecuteAsync(uqDowntimeSrc);
            await conn.ExecuteAsync(createProduction);
            await conn.ExecuteAsync(createShift);
            await conn.ExecuteAsync(idxShiftTime);
            await conn.ExecuteAsync(createNonProdLog);
            await conn.ExecuteAsync(uqNonProdLog);
            await conn.ExecuteAsync(idxNonProdLogTime);

            // classifySource — 기존 oee.db 마이그레이션(이 어댑터는 CREATE TABLE IF NOT EXISTS 만 쓰고 ALTER 인프라가
            // 없음). detectSource(감지 출처)와 의미 구분: 분류가 어떻게 정해졌는지(manual/auto-bit/auto-heuristic/NULL).
            await EnsureColumnAsync(conn, "oeeDowntimeEvent", "classifySource", "TEXT");

            // 2026-06-15: MTBF '고장' 정의를 설비고장(reasonCode='equipment_fault')만으로 변경(OeeMath.IsFailureReason).
            // 기존 분류 이벤트(reasonCode 있는)의 isFailure 를 새 규칙에 재정렬 — 자재대기·작업자대기 등 비-설비고장을 MTBF에서 제외.
            // 미분류(reasonCode NULL) / 고장비트 onset(reasonCode NULL, 감지기반 isFailure=1)은 보존. 멱등(불일치 행만 갱신).
            var realigned = await conn.ExecuteAsync(@"
                UPDATE oeeDowntimeEvent
                SET isFailure = CASE WHEN reasonCode = 'equipment_fault' THEN 1 ELSE 0 END
                WHERE reasonCode IS NOT NULL
                  AND isFailure <> CASE WHEN reasonCode = 'equipment_fault' THEN 1 ELSE 0 END");
            if (realigned > 0)
                _logger.LogInformation("[OEE] isFailure 재정렬(설비고장만): {N}건 — 비-설비고장 분류는 MTBF 고장에서 제외", realigned);

            // 2026-06-23: 비가동 기본값 = 고장(isFailure=1). 기존 nocycle 미분류(reasonCode NULL, isFailure=0)를 고장으로 업그레이드.
            // 고장비트 onset은 이미 1이므로 영향 없음. 사용자가 '유지보수'로 해제한 것(reasonCode='planned_maint')은 IS NOT NULL 가드로 보존.
            var upgraded = await conn.ExecuteAsync(@"
                UPDATE oeeDowntimeEvent
                SET isFailure = 1
                WHERE reasonCode IS NULL AND isFailure = 0");
            if (upgraded > 0)
                _logger.LogInformation("[OEE] isFailure 기본값 업그레이드(고장): {N}건 — nocycle 미분류 → isFailure=1", upgraded);

            _logger.LogInformation("OEE schema ensured (oeeDowntimeEvent / oeeProductionCount / oeeShiftException / oeeNonProdDetectionLog) at {Path}", OeeDbPath());
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create OEE schema");
            return false;
        }
    }

    /// <summary>
    /// 컬럼이 없으면 ALTER TABLE ADD COLUMN (기존 DB 마이그레이션). PRAGMA table_info 가드 — 이미 있으면 no-op.
    /// SQLite ADD COLUMN 은 NULL 기본의 nullable 컬럼만 안전하게 추가(기존 행은 NULL).
    /// </summary>
    private static async Task EnsureColumnAsync(SqliteConnection conn, string table, string column, string type)
    {
        // PRAGMA table_info 는 (cid,name,type,...) 행을 돌려준다 — name 컬럼만 매핑해 존재 여부 확인.
        var names = await conn.QueryAsync<PragmaCol>($"PRAGMA table_info({table})");
        if (names.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))) return;
        await conn.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    private sealed class PragmaCol
    {
        public string? Name { get; set; }
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

    public async Task<int> ClassifyDowntimeAsync(long id, string? reasonCode, string? category, bool isFailure, string? classifySource = "manual", CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        const string sql = @"
            UPDATE oeeDowntimeEvent
            SET reasonCode     = @ReasonCode,
                category       = @Category,
                isFailure      = @IsFailure,
                classifySource = @ClassifySource
            WHERE id = @Id";
        return await conn.ExecuteAsync(sql, new
        {
            Id = id,
            ReasonCode = reasonCode,
            Category = category,
            IsFailure = isFailure ? 1 : 0,
            ClassifySource = classifySource,
        });
    }

    public async Task<int> BulkClassifyDowntimeAsync(IReadOnlyList<long> ids, string? reasonCode, string? category, bool isFailure, string? classifySource = "manual", CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;
        await using var conn = await OpenAsync();
        const string sql = @"
            UPDATE oeeDowntimeEvent
            SET reasonCode     = @ReasonCode,
                category       = @Category,
                isFailure      = @IsFailure,
                classifySource = @ClassifySource
            WHERE id IN @Ids";
        return await conn.ExecuteAsync(sql, new
        {
            Ids = ids,
            ReasonCode = reasonCode,
            Category = category,
            IsFailure = isFailure ? 1 : 0,
            ClassifySource = classifySource,
        });
    }

    public async Task<int> AutoClassifyHeuristicAsync(long id, string? reasonCode, string? category, bool isFailure, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        // 휴리스틱 자동분류 — 수동 분류는 절대 덮지 않는다(classifySource='manual' 가드 = 수동 우선, doc/21 §12).
        // 미분류(category IS NULL) 인 행만 채운다 — 이미 분류된(수동·비트) 건은 건드리지 않음.
        const string sql = @"
            UPDATE oeeDowntimeEvent
            SET reasonCode     = @ReasonCode,
                category       = @Category,
                isFailure      = @IsFailure,
                classifySource = 'auto-heuristic'
            WHERE id = @Id
              AND category IS NULL
              AND (classifySource IS NULL OR classifySource <> 'manual')";
        return await conn.ExecuteAsync(sql, new
        {
            Id = id,
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
        // 비생산 감지 로그도 동반 초기화 — plc.db 사이클에서 파생되므로 정지 이벤트와 동일 수명(전체 초기화 시 stale 방지).
        await conn.ExecuteAsync("DELETE FROM oeeNonProdDetectionLog");
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
                   reasonCode, category, isFailure, detectSource, classifySource, sourceLogId, note
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
                   reasonCode, category, isFailure, detectSource, classifySource, sourceLogId, note
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
        var capUtc = DateTime.UtcNow < toUtc ? DateTime.UtcNow : toUtc;
        var p = new DynamicParameters();
        p.Add("From", Iso(fromUtc));
        p.Add("To", Iso(toUtc));
        p.Add("Cap", Iso(capUtc));
        var flowClause = "";
        if (!string.IsNullOrWhiteSpace(flowName))
        {
            flowClause = " AND flowName = @Flow ";
            p.Add("Flow", flowName.Trim());
        }
        // SUM 단순 합산 대신 Interval Union — 겹치는 open 이벤트가 같은 시간대를
        // 이중 계상해 downtimeMs > periodMs 가 되는 문제를 방지.
        var sql = $@"
            SELECT
              CAST((julianday(startAt)                      - julianday(@From)) * 86400000 AS INTEGER) AS S,
              CAST((julianday(COALESCE(endAt, @Cap))        - julianday(@From)) * 86400000 AS INTEGER) AS E
            FROM oeeDowntimeEvent
            WHERE startAt >= @From AND startAt <= @To {flowClause}";
        var rows = await conn.QueryAsync<SegRow>(sql, p);
        var list = rows.ToList();
        var periodMs = (long)(toUtc - fromUtc).TotalMilliseconds;
        var segs = list.Select(r => (S: Math.Max(0L, r.S), E: Math.Min(periodMs, r.E)));
        return (UnionMs(segs), list.Count);
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
        var capUtc = DateTime.UtcNow < toUtc ? DateTime.UtcNow : toUtc;
        var sql = @"
            SELECT flowName AS FlowName,
              CAST((julianday(startAt)                - julianday(@From)) * 86400000 AS INTEGER) AS S,
              CAST((julianday(COALESCE(endAt, @Cap))  - julianday(@From)) * 86400000 AS INTEGER) AS E
            FROM oeeDowntimeEvent
            WHERE startAt >= @From AND startAt <= @To AND flowName IS NOT NULL";
        var rows = await conn.QueryAsync<FlowSegRow>(sql, new
        {
            From = Iso(fromUtc),
            To = Iso(toUtc),
            Cap = Iso(capUtc),
        });
        var periodMs = (long)(toUtc - fromUtc).TotalMilliseconds;
        return rows
            .GroupBy(r => r.FlowName ?? "")
            .Select(g =>
            {
                var segs = g.Select(r => (S: Math.Max(0L, r.S), E: Math.Min(periodMs, r.E)));
                return (g.Key, UnionMs(segs), g.Count());
            })
            .OrderByDescending(x => x.Item2)
            .ToList();
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

    public async Task<IReadOnlyList<(long StartMs, long EndMs, int Kind, bool IsAuto)>> GetDowntimeIntervalsAsync(
        DateTime fromUtc, DateTime toUtc, string? flowName, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var capUtc = DateTime.UtcNow < toUtc ? DateTime.UtcNow : toUtc;
        var p = new DynamicParameters();
        p.Add("From", Iso(fromUtc));
        p.Add("To", Iso(toUtc));
        p.Add("Cap", Iso(capUtc));
        var flowClause = "";
        if (!string.IsNullOrWhiteSpace(flowName))
        {
            flowClause = " AND flowName = @Flow ";
            p.Add("Flow", flowName.Trim());
        }
        // UTC epoch ms 로 반환(로컬 변환/파싱 모호성 회피): (julianday(x) - 2440587.5) * 86400000.
        // open(endAt NULL) 은 @Cap(min(now,to)) 로 마감. 기간과 겹치는 이벤트 전부 포함(startAt < to AND effEnd > from)
        //   → 시작일 몰빵 대신 컨트롤러가 실제 겹친 슬롯마다 분배(다일·장시간 정지 정확 표현).
        // Kind(상호배타): 0=계획정비 / 1=고장 / 2=기타 비계획 / 3=미분류.
        // IsAuto: detectSource='nocycle' → 자동 파생 무사이클 정지(사이클 모델의 비생산과 동일 유휴). 비생산 카빙 우선 판정에 사용.
        const string startMs = "CAST((julianday(startAt) - 2440587.5) * 86400000 AS INTEGER)";
        const string endMs = "CAST((julianday(COALESCE(endAt, @Cap)) - 2440587.5) * 86400000 AS INTEGER)";
        var sql = $@"
            SELECT
              {startMs} AS StartMs,
              {endMs}   AS EndMs,
              CASE
                WHEN category = 'planned' THEN 0
                WHEN category IS NULL THEN 3
                WHEN isFailure = 1 THEN 1
                ELSE 2
              END AS Kind,
              CASE WHEN detectSource = 'nocycle' THEN 1 ELSE 0 END AS IsAuto
            FROM oeeDowntimeEvent
            WHERE startAt <= @To AND COALESCE(endAt, @Cap) >= @From {flowClause}";
        var rows = await conn.QueryAsync<IntervalRow>(sql, p);
        return rows.Select(r => (r.StartMs, r.EndMs, r.Kind, r.IsAuto != 0)).ToList();
    }

    private sealed class IntervalRow
    {
        public long StartMs { get; set; }
        public long EndMs { get; set; }
        public int Kind { get; set; }
        public int IsAuto { get; set; }
    }

    // ── 자동 비생산 감지 로그 (10×CT, doc/22 §3.3) ────────────────────────

    public async Task<int> UpsertNonProdDetectionsAsync(
        IReadOnlyList<OeeNonProdDetectionLog> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return 0;
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(ct);
        // 조회마다 같은 구간이 다시 감지되므로 (flowName, onsetAt, detectionReason) 멱등 UPSERT — 재삽입 대신 끝/지속/임계 갱신.
        const string sql = @"
            INSERT INTO oeeNonProdDetectionLog
                (flowName, onsetAt, clearAt, durationMs, detectionSource, detectionReason, ctThresholdMs, ctMultiplier)
            VALUES
                (@FlowName, @OnsetAt, @ClearAt, @DurationMs, @DetectionSource, @DetectionReason, @CtThresholdMs, @CtMultiplier)
            ON CONFLICT(flowName, onsetAt, detectionReason) DO UPDATE SET
                clearAt       = excluded.clearAt,
                durationMs    = excluded.durationMs,
                ctThresholdMs = excluded.ctThresholdMs,
                ctMultiplier  = excluded.ctMultiplier";
        int n = 0;
        foreach (var e in entries)
        {
            n += await conn.ExecuteAsync(sql, new
            {
                FlowName = e.FlowName ?? "",                 // 라인 스코프 감지는 "" 로 정규화(UNIQUE NULL footgun 회피)
                OnsetAt = Iso(e.OnsetAt),
                ClearAt = e.ClearAt.HasValue ? Iso(e.ClearAt.Value) : null,
                e.DurationMs,
                e.DetectionSource,
                e.DetectionReason,
                e.CtThresholdMs,
                e.CtMultiplier,
            }, tx);
        }
        await tx.CommitAsync(ct);
        return n;
    }

    public async Task<IReadOnlyList<(double S, double E)>> GetNonProdIntervalsFromLogAsync(
        DateTime fromUtc, DateTime toUtc, string? flowName, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        var capUtc = DateTime.UtcNow < toUtc ? DateTime.UtcNow : toUtc;
        var p = new DynamicParameters();
        p.Add("From", Iso(fromUtc));
        p.Add("To", Iso(toUtc));
        p.Add("Cap", Iso(capUtc));
        var flowClause = "";
        if (!string.IsNullOrWhiteSpace(flowName))
        {
            flowClause = " AND flowName = @Flow ";
            p.Add("Flow", flowName.Trim());
        }
        // UTC epoch ms (GetDowntimeIntervalsAsync 와 동일 변환). open(clearAt NULL)은 @Cap(min(now,to))로 마감.
        // onset 이 기간에 든 감지만(materialize 가 기간창 단위로 이뤄지므로 일치). 라인(flow=null)은 전체 반환 → 호출측 Union.
        const string startMs = "CAST((julianday(onsetAt) - 2440587.5) * 86400000 AS INTEGER)";
        const string endMs = "CAST((julianday(COALESCE(clearAt, @Cap)) - 2440587.5) * 86400000 AS INTEGER)";
        var sql = $@"
            SELECT {startMs} AS S, {endMs} AS E
            FROM oeeNonProdDetectionLog
            WHERE onsetAt >= @From AND onsetAt < @To {flowClause}
            ORDER BY onsetAt";
        var rows = await conn.QueryAsync<IntervalMsRow>(sql, p);
        return rows.Select(r => (S: (double)r.S, E: (double)r.E)).Where(iv => iv.E > iv.S).ToList();
    }

    private sealed class IntervalMsRow { public long S { get; set; } public long E { get; set; } }

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
        Status: string.IsNullOrEmpty(r.EndAt) ? "open" : "recovered",
        ClassifySource: r.ClassifySource);

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
        ClassifySource = r.ClassifySource,
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
        public string? ClassifySource { get; set; }
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

    private sealed class SegRow { public long S { get; set; } public long E { get; set; } }
    private sealed class FlowSegRow { public string? FlowName { get; set; } public long S { get; set; } public long E { get; set; } }

    // 겹치는 구간을 병합한 뒤 합산 — downtimeMs > periodMs 이중계상 방지.
    private static long UnionMs(IEnumerable<(long S, long E)> intervals)
    {
        var xs = intervals.Where(x => x.E > x.S).OrderBy(x => x.S).ToList();
        if (xs.Count == 0) return 0;
        long total = 0, s = xs[0].S, e = xs[0].E;
        foreach (var seg in xs.Skip(1))
        {
            if (seg.S <= e) e = Math.Max(e, seg.E);
            else { total += e - s; s = seg.S; e = seg.E; }
        }
        return total + (e - s);
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
