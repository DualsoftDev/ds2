// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Dapper;
using DSPilot.Infrastructure;
using DSPilot.Models;
using DSPilot.Models.Oee;
using DSPilot.Repositories;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace DSPilot.Controllers;

/// <summary>
/// P5 OEE / 정지(다운타임) API (격리형 호스팅). 정적 페이지(/app/*.html)가 fetch 로 호출.
///
/// OEE 는 on-demand 계산(별도 daily backfill 생략) — doc/21 §8. 과거 날짜 불량/분류 입력은 재조회 시 즉시 소급 반영.
///   availability = 달력근사 (1 - downtime/period)        ⚠ Phase1: 계획시간 데이터 0 → 진짜 가용성은 Phase4(/shift-summary)
///   performance  = (idealCT × total) / runtime, min(1.0)  idealCT = 수동 입력 또는 실측 자동기입(OeeIdealCycleAutoFillService)
///   quality      = (total − 입력불량) / total              불량 미입력 = 100% 가정(QualitySource="assumed" 명시) — §12
///   oee          = A × P × Q (한 요소라도 소스 없으면 null + 사유)
///   mtbf/mttr    = isFailure=1 이벤트 기반
/// totalCount 는 dspFlowHistory row count 자동, rejectCount 는 수동/PLC 불량신호, 분류는 수동 PATCH (isFailure 기본 0).
/// 산출 불가 지표는 값 null + *Note 로 정직 표기, 가정값은 *Source 로 명시 (doc/21 §10·§12).
/// </summary>
[ApiController]
[Route("api/oee")]
public class OeeController : ControllerBase
{
    private readonly IOeeRepository _repo;
    private readonly AppSettingsService _settings;
    private readonly DsProjectService _project;
    private readonly IDatabasePathResolver _pathResolver;
    private readonly OeeCtStatsService _ctStats;
    private readonly OeeAutoShiftInferenceService _shiftInfer;
    private readonly ILogger<OeeController> _logger;

    public OeeController(
        IOeeRepository repo,
        AppSettingsService settings,
        DsProjectService project,
        IDatabasePathResolver pathResolver,
        OeeCtStatsService ctStats,
        OeeAutoShiftInferenceService shiftInfer,
        ILogger<OeeController> logger)
    {
        _repo = repo;
        _settings = settings;
        _project = project;
        _pathResolver = pathResolver;
        _ctStats = ctStats;
        _shiftInfer = shiftInfer;
        _logger = logger;
    }

    // ── GET /api/oee/summary?from&to&flow ─────────────────────────────────
    [HttpGet("summary")]
    public async Task<ActionResult<OeeSummaryDto>> Summary(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? flow,
        CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var flowName = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();

        var summary = await BuildSummaryAsync(flowName, fromUtc, toUtc, ct);
        return summary;
    }

    // ── GET /api/oee/downtime?from&to&status&reason&flow ──────────────────
    [HttpGet("downtime")]
    public async Task<ActionResult<List<OeeDowntimeDto>>> Downtime(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] string? status, [FromQuery] string? reason, [FromQuery] string? flow,
        CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var rows = await _repo.QueryDowntimeAsync(fromUtc, toUtc, status, reason,
            string.IsNullOrWhiteSpace(flow) ? null : flow.Trim(), ct);
        // abnormal/usertag 시간겹침 단서(읽기전용 표시 — 건수·MTBF 미반영, doc/21 §4) 부착.
        return await AttachCluesAsync(rows, fromUtc, toUtc, ct);
    }

    /// <summary>
    /// 정지 행 [startAt, endAt|now] 에 시간이 겹치는 abnormal/usertag 점 이벤트를 단서로 붙인다(표시 전용).
    /// abnormal = valueType='Abnormal' AND matchOp='AbnormalDetect'(matchValue=Kind), usertag = logLevel='Error' 일반 행.
    /// userTagAlertLog 는 flowName 컬럼이 없어 abnormal 은 tagAddress 첫 경로 세그먼트(FLOW), 그 외는 systemName 으로 스코프 매칭.
    /// ★건수·길이·MTBF 에는 절대 반영하지 않는다 — Downtime/Summary 의 집계는 oeeDowntimeEvent 만 본다(doc/21 §4 정직성).
    /// </summary>
    private async Task<List<OeeDowntimeDto>> AttachCluesAsync(
        IReadOnlyList<OeeDowntimeDto> rows, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var list = rows.ToList();
        if (list.Count == 0) return list;
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!System.IO.File.Exists(dbPath)) return list;

        var clues = new List<(string? Flow, string? System, DateTime At, string Label, string Src)>();
        try
        {
            await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync();
            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='userTagAlertLog'");
            if (exists == 0) return list;

            var endBound = toUtc > DateTime.UtcNow ? toUtc : DateTime.UtcNow; // open 이벤트 진행분(now 까지) 포함
            const string sql = @"
                SELECT occurredAt AS OccurredAt, systemName AS SystemName, name AS Name,
                       tagAddress AS TagAddress, valueType AS ValueType, matchOp AS MatchOp, matchValue AS MatchValue
                FROM userTagAlertLog
                WHERE occurredAt >= @From AND occurredAt <= @To
                  AND ((matchOp = 'AbnormalDetect' AND valueType = 'Abnormal') OR logLevel = 'Error')";
            var alerts = await conn.QueryAsync<AlertRow>(sql, new
            {
                From = SqliteDateTimeHelpers.ToSqliteUtcString(fromUtc),
                To = SqliteDateTimeHelpers.ToSqliteUtcString(endBound),
            });
            foreach (var a in alerts)
            {
                var at = SqliteDateTimeHelpers.FromSqliteUtcString(a.OccurredAt);
                if (at is null) continue;
                var isAbn = string.Equals(a.MatchOp, "AbnormalDetect", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(a.ValueType, "Abnormal", StringComparison.OrdinalIgnoreCase);
                string? cFlow = null;
                if (isAbn && !string.IsNullOrEmpty(a.TagAddress))
                {
                    var ix = a.TagAddress.IndexOf(" / ", StringComparison.Ordinal); // "FLOW / WORK / CALL" → FLOW
                    cFlow = ix > 0 ? a.TagAddress[..ix].Trim() : null;
                }
                var label = isAbn
                    ? AbnormalKindLabel(a.MatchValue)
                    : (string.IsNullOrWhiteSpace(a.Name) ? "이상 신호" : a.Name!.Trim());
                clues.Add((cFlow, a.SystemName, at.Value, label, isAbn ? "abnormal" : "usertag"));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[OEE] downtime clue join failed"); return list; }

        if (clues.Count == 0) return list;
        var nowLocal = DateTime.Now; // d.StartAt/EndAt 는 FromSqliteUtcString → Kind=Local 벽시계 → 로컬끼리 비교.
        for (var idx = 0; idx < list.Count; idx++)
        {
            var d = list[idx];
            var spanEnd = d.EndAt ?? nowLocal;
            (string Label, string Src)? best = null;
            var bestAt = DateTime.MinValue;
            foreach (var c in clues)
            {
                if (c.At < d.StartAt || c.At > spanEnd) continue;
                var scope = c.Flow is not null
                    ? string.Equals(c.Flow, d.FlowName, StringComparison.OrdinalIgnoreCase)
                    : (string.Equals(c.System, d.SystemName, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(c.System, d.FlowName, StringComparison.OrdinalIgnoreCase));
                if (!scope) continue;
                if (c.At >= bestAt) { bestAt = c.At; best = (c.Label, c.Src); } // 가장 최근 신호를 단서로
            }
            if (best is not null)
                list[idx] = d with { Clue = new OeeDowntimeClue(best.Value.Label, best.Value.Src) };
        }
        return list;
    }

    private static string AbnormalKindLabel(string? kind) => kind switch
    {
        "ActionOver" => "동작지연",
        "ActionUnder" => "동작빠름",
        "SensorShort" => "조기완료",
        "SensorOpen" => "센서끊김",
        _ => string.IsNullOrWhiteSpace(kind) ? "이상감지" : kind!,
    };

    private sealed class AlertRow
    {
        public string? OccurredAt { get; set; }
        public string? SystemName { get; set; }
        public string? Name { get; set; }
        public string? TagAddress { get; set; }
        public string? ValueType { get; set; }
        public string? MatchOp { get; set; }
        public string? MatchValue { get; set; }
    }

    // ── POST /api/oee/downtime/{id}/classify  {reasonCode, category} ──────
    // category=unplanned 일 때만 isFailure=1 (MTBF/MTTR 분모 오염 방지 — doc/21 §2.1).
    [HttpPost("downtime/{id:long}/classify")]
    public async Task<ActionResult<object>> Classify(long id, [FromBody] ClassifyRequest req, CancellationToken ct)
    {
        var category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim().ToLowerInvariant();
        var reasonCode = string.IsNullOrWhiteSpace(req.ReasonCode) ? null : req.ReasonCode.Trim();
        var isFailure = string.Equals(category, "unplanned", StringComparison.OrdinalIgnoreCase);

        var n = await _repo.ClassifyDowntimeAsync(id, reasonCode, category, isFailure, classifySource: "manual", ct);
        if (n == 0) return NotFound(new { error = "downtime event not found", id });
        return new { ok = true, id, reasonCode, category, isFailure };
    }

    // ── POST /api/oee/downtime/{id}/close  {endAt} ────────────────────────
    // 자동 clear 미감지 시 수동 마감. endAt 미지정 시 now. durationMs 무한증가/MTTR 폭주 방지(doc/21 §7).
    [HttpPost("downtime/{id:long}/close")]
    public async Task<ActionResult<object>> Close(long id, [FromBody] CloseRequest? req, CancellationToken ct)
    {
        var endAtUtc = (req?.EndAt) is DateTime e ? ToUtc(e) : DateTime.UtcNow;
        var n = await _repo.CloseDowntimeAsync(id, endAtUtc, ct);
        if (n == 0) return NotFound(new { error = "open downtime event not found", id });
        return new { ok = true, id, endAt = endAtUtc };
    }

    // ── POST /api/oee/downtime/bulk-classify  {ids, reasonCode, category} ──
    // 복수 id 일괄 분류. ids 최대 500개 제한.
    [HttpPost("downtime/bulk-classify")]
    public async Task<ActionResult<object>> BulkClassify([FromBody] BulkClassifyRequest req, CancellationToken ct)
    {
        if (req.Ids == null || req.Ids.Count == 0)
            return BadRequest(new { error = "ids is required" });
        if (req.Ids.Count > 500)
            return BadRequest(new { error = "too many ids (max 500)" });

        var category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim().ToLowerInvariant();
        var reasonCode = string.IsNullOrWhiteSpace(req.ReasonCode) ? null : req.ReasonCode.Trim();
        var isFailure = string.Equals(category, "unplanned", StringComparison.OrdinalIgnoreCase);

        var n = await _repo.BulkClassifyDowntimeAsync(req.Ids, reasonCode, category, isFailure, classifySource: "manual", ct);
        return new { ok = true, count = n, reasonCode, category, isFailure };
    }

    // ── POST /api/oee/downtime/bulk-close  {ids, endAt?} ─────────────────
    // open 상태인 항목만 수동 마감. endAt 미지정 시 now.
    [HttpPost("downtime/bulk-close")]
    public async Task<ActionResult<object>> BulkClose([FromBody] BulkCloseRequest req, CancellationToken ct)
    {
        if (req.Ids == null || req.Ids.Count == 0)
            return BadRequest(new { error = "ids is required" });
        if (req.Ids.Count > 500)
            return BadRequest(new { error = "too many ids (max 500)" });

        var endAtUtc = req.EndAt is DateTime e ? ToUtc(e) : DateTime.UtcNow;
        var n = await _repo.BulkCloseDowntimeAsync(req.Ids, endAtUtc, ct);
        return new { ok = true, count = n, endAt = endAtUtc };
    }

    // ── POST /api/oee/production  {date, flow, shift, reject} ─────────────
    // total 은 dspFlowHistory 자동, reject 만 수동. good = total - reject (clamp >= 0).
    [HttpPost("production")]
    public async Task<ActionResult<object>> Production([FromBody] ProductionRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Flow))
            return BadRequest(new { error = "flow is required" });

        var bucketDate = (req.Date ?? DateTime.Now).Date;
        var bucketStr = bucketDate.ToString("yyyy-MM-dd");
        var shift = req.Shift ?? "";

        // 해당 로컬일의 dspFlowHistory row count 로 total 자동 산출.
        var dayStartUtc = DateTime.SpecifyKind(bucketDate, DateTimeKind.Local).ToUniversalTime();
        var dayEndUtc = DateTime.SpecifyKind(bucketDate.AddDays(1), DateTimeKind.Local).ToUniversalTime();
        var total = await CountFlowHistoryAsync(req.Flow.Trim(), dayStartUtc, dayEndUtc);

        var reject = Math.Max(0, req.Reject);
        var good = Math.Max(0, total - reject);

        await _repo.UpsertProductionAsync(new OeeProductionCount
        {
            BucketDate = bucketStr,
            FlowName = req.Flow.Trim(),
            Shift = shift,
            TotalCount = total,
            GoodCount = good,
            RejectCount = reject,
            Source = "manual",
        }, ct);

        return new { ok = true, date = bucketStr, flow = req.Flow.Trim(), shift, total, good, reject };
    }

    // ── GET /api/oee/shift-exception?from&to&flow ─────────────────────────
    [HttpGet("shift-exception")]
    public async Task<ActionResult<List<OeeShiftException>>> GetShiftExceptions(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? flow, CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var rows = await _repo.QueryShiftExceptionsAsync(fromUtc, toUtc,
            string.IsNullOrWhiteSpace(flow) ? null : flow.Trim(), ct);
        return rows.ToList();
    }

    // ── POST /api/oee/shift-exception  {flow?, startAt, endAt, kind, note?} ─
    [HttpPost("shift-exception")]
    public async Task<ActionResult<object>> AddShiftException([FromBody] ShiftExceptionRequest req, CancellationToken ct)
    {
        if (req.StartAt is null || req.EndAt is null)
            return BadRequest(new { error = "startAt and endAt are required" });
        if (req.EndAt <= req.StartAt)
            return BadRequest(new { error = "endAt must be after startAt" });
        if (string.IsNullOrWhiteSpace(req.Kind))
            return BadRequest(new { error = "kind is required (planned_maint | planned_stop | non_production)" });

        var id = await _repo.InsertShiftExceptionAsync(new OeeShiftException
        {
            FlowName = string.IsNullOrWhiteSpace(req.Flow) ? null : req.Flow.Trim(),
            StartAt = ToUtc(req.StartAt.Value),
            EndAt = ToUtc(req.EndAt.Value),
            Kind = req.Kind.Trim(),
            Note = req.Note,
        }, ct);
        return new { ok = true, id };
    }

    // ── DELETE-via-POST /api/oee/shift-exception/{id}/delete ──────────────
    [HttpPost("shift-exception/{id:long}/delete")]
    public async Task<ActionResult<object>> DeleteShiftException(long id, CancellationToken ct)
    {
        var n = await _repo.DeleteShiftExceptionAsync(id, ct);
        if (n == 0) return NotFound(new { error = "shift exception not found", id });
        return new { ok = true, id };
    }

    // ── POST /api/oee/ideal-cycle  {flow, idealCycleTimeMs?} ──────────────
    // idealCT 엔지니어 입력(Performance 단일 소스). null/<=0 이면 해제.
    [HttpPost("ideal-cycle")]
    public ActionResult<object> SetIdealCycle([FromBody] IdealCycleRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Flow))
            return BadRequest(new { error = "flow is required" });
        _settings.SaveFlowIdealCycleTime(req.Flow.Trim(), req.IdealCycleTimeMs);
        return new { ok = true, flow = req.Flow.Trim(), idealCycleTimeMs = req.IdealCycleTimeMs is > 0 ? req.IdealCycleTimeMs : null };
    }

    // ── POST /api/oee/ideal-cycle/batch  {items:[{flow, idealCycleTimeMs?}]} ─
    // 여러 Flow 의 표준CT 를 한 번에 적용(설정 파일 1회 쓰기). null/0 = 해제.
    [HttpPost("ideal-cycle/batch")]
    public ActionResult<object> SetIdealCycleBatch([FromBody] IdealCycleBatchRequest req)
    {
        var items = (req?.Items ?? new List<IdealCycleRequest>())
            .Where(i => !string.IsNullOrWhiteSpace(i.Flow))
            .Select(i => (i.Flow.Trim(), i.IdealCycleTimeMs, i.Mode))
            .ToList();
        if (items.Count == 0)
            return BadRequest(new { error = "items is empty" });

        _settings.SaveFlowIdealCycleTimesBatch(items);
        return new { ok = true, count = items.Count };
    }

    // ── GET /api/oee/ideal-cycle/table?percentile&sampleLimit ─────────────
    // Flow별 현재 표준CT(설정값) + 실측 추천값 + CT 통계 테이블. uptime 표준CT 일괄 편집 화면용.
    // 추천값 = 이상치 제외(IsIdle=0, 통합 유효범위) 사이클 CT 의 best-demonstrated 분위수(기본 p10).
    //   평균이 아니라 "가장 빠른 반복가능 CT"를 기준으로 삼아 Performance 가 속도손실을 정직하게 잡도록 한다(순환정의 방지).
    [HttpGet("ideal-cycle/table")]
    public async Task<ActionResult<List<IdealCycleRowDto>>> IdealCycleTable(
        [FromQuery] double percentile = 10, [FromQuery] int sampleLimit = 2000)
    {
        var p = Math.Clamp(percentile, 0, 100);
        var limit = sampleLimit <= 0 ? 2000 : Math.Min(sampleLimit, 100000);
        var stats = await _ctStats.ComputeAsync(limit, p);

        // 설정은 1회만 로드해 행마다 디스크 재로드를 피한다. Source = "auto"(자동기입) / null(수동 또는 미설정).
        var settings = _settings.LoadSettings();
        var overrideByFlow = settings.FlowCycle.Overrides
            .Where(o => !string.IsNullOrWhiteSpace(o.FlowName))
            .GroupBy(o => o.FlowName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var rows = stats
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                var s = kv.Value;
                var has = s.SampleCount > 0;
                overrideByFlow.TryGetValue(kv.Key, out var ov);
                var ideal = ov?.IdealCycleTimeMs is > 0 ? ov.IdealCycleTimeMs : null;
                return new IdealCycleRowDto(
                    FlowName: kv.Key,
                    IdealCycleTimeMs: ideal,
                    Source: ideal is not null ? ov!.IdealCycleTimeSource : null,
                    RecommendedMs: has ? s.Recommended : null,
                    SampleCount: s.SampleCount,
                    MinCt: has ? s.Min : null,
                    MedianCt: has ? s.Median : null,
                    AvgCt: has ? s.Avg : null);
            })
            .ToList();
        return rows;
    }

    // ── GET /api/oee/ranking?from&to ──────────────────────────────────────
    [HttpGet("ranking")]
    public async Task<ActionResult<List<OeeRankingDto>>> Ranking(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var byFlow = await _repo.GetDowntimeByFlowAsync(fromUtc, toUtc, ct);

        var result = new List<OeeRankingDto>(byFlow.Count);
        foreach (var (flowName, downtimeMs, count) in byFlow)
        {
            var s = await BuildSummaryAsync(flowName, fromUtc, toUtc, ct);
            result.Add(new OeeRankingDto(
                flowName, downtimeMs, count, s.TotalCount,
                s.Availability, s.Performance, s.Quality, s.Oee));
        }
        // OEE 산출 가능한 것 우선 내림차순, 그 외 정지시간 내림차순.
        return result
            .OrderByDescending(r => r.Oee ?? -1)
            .ThenByDescending(r => r.DowntimeMs)
            .ToList();
    }

    // ── GET /api/oee/shift-summary?from&to&flow ───────────────────────────
    // 시프트 기반 "진짜" 가용성(doc/21 §8 Phase4). 달력근사(Summary)와 달리 계획생산시간(PPT)을 분모로 쓴다.
    //   계획시간(Scheduled) = 시프트 창(Start/End, 야간 자정넘김 포함) ∩ 조회기간
    //   PPT                = 계획시간 − 계획정지(시프트 예외: planned_maint/planned_stop/non_production)
    //   가용성 Availability = 가동시간(PPT − 비계획정지) / PPT       (구간 교집합/차집합으로 정밀 산출)
    //   성능/품질/OEE       = Summary 와 동일 정의(단 성능 분모는 시프트 가동시간)
    // 정지(다운타임)는 category='planned' 만 제외(계획정지는 PPT 에서 이미 빠지므로 가용성손실 아님).
    [HttpGet("shift-summary")]
    public async Task<ActionResult<OeeShiftSummaryDto>> ShiftSummary(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? flow,
        CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var flowName = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();
        var summary = await BuildShiftSummaryAsync(flowName, fromUtc, toUtc, ct);
        return summary;
    }

    private async Task<OeeShiftSummaryDto> BuildShiftSummaryAsync(
        string? flowName, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var periodMs = Math.Max(0, (toUtc - fromUtc).TotalMilliseconds);
        var shift = _settings.LoadSettings().Shift;
        var scheduled = BuildScheduledIntervals(shift, fromUtc, toUtc);

        var av = await ComputeShiftAvailabilityAsync(flowName, fromUtc, toUtc, scheduled, ct);
        var (failureDurationMs, failureCount) = await _repo.GetFailureAggregateAsync(fromUtc, toUtc, flowName, ct);
        int? totalCount = await CountFlowHistoryAsync(flowName, fromUtc, toUtc);
        var (_, _, prodReject, hasReject) =
            await _repo.QueryProductionAsync(fromUtc.ToLocalTime(), toUtc.ToLocalTime(), flowName, ct);

        // ── Availability (시프트 기반) ──
        double? availability = null;
        string? availNote;
        if (av.ScheduledMs <= 0)
            availNote = "조회기간이 시프트 창과 겹치지 않음 — 시프트(Start/End) 설정을 확인하세요.";
        else if (av.PlannedProductionMs <= 0)
            availNote = "계획생산시간(PPT) 0 — 계획정지(시프트 예외)가 시프트 전체를 덮고 있습니다.";
        else
        {
            availability = Math.Clamp(av.RunTimeMs / av.PlannedProductionMs, 0.0, 1.0);
            availNote = "가동시간 ÷ 계획생산시간(PPT). PPT = 시프트 ∩ 기간 − 계획정지.";
        }

        // ── Performance ((idealCT × total) / 시프트 가동시간) ──
        var (idealCT, idealCtSource) = ResolveIdealCycle(flowName);
        double? performance = null;
        string? perfNote;
        if (string.IsNullOrWhiteSpace(flowName))
        {
            (performance, perfNote) = await ComputeShiftLinePerformanceAsync(fromUtc, toUtc, scheduled, ct);
        }
        else if (idealCT is null || idealCT <= 0)
            perfNote = "표준 사이클(idealCT) 미설정 — 성능 산출 불가. 클린사이클이 모이면 자동 기입됩니다(또는 표준CT 직접 입력).";
        else if (totalCount is null || totalCount <= 0)
            perfNote = "기간 내 생산 사이클 0 — 성능 산출 불가.";
        else if (av.RunTimeMs <= 0)
            perfNote = "시프트 가동시간 0 — 성능 산출 불가.";
        else
        {
            performance = Math.Min(1.0, (idealCT.Value * (double)totalCount.Value) / av.RunTimeMs);
            perfNote = null;
        }

        // ── Quality ── Summary 와 동일: 기본 100% 가정, 불량 입력 시 실측 (§12 개정)
        var (quality, qualNote, qualitySource, rejectOut, goodOut) =
            OeeMath.ComputeQuality(totalCount, prodReject, hasReject);

        // ── OEE = A × P × Q ──
        double? oee = null;
        string? oeeNote = null;
        if (availability is double a && performance is double p && quality is double q)
        {
            oee = a * p * q;
            if (qualitySource == "assumed")
                oeeNote = "품질 100% 가정 포함(불량 미입력).";
        }
        else
        {
            var missing = new List<string>();
            if (availability is null) missing.Add("가용성");
            if (performance is null) missing.Add("성능");
            if (quality is null) missing.Add("품질");
            oeeNote = $"구성요소 미산출({string.Join(", ", missing)}) — OEE 산출 불가.";
        }

        // ── MTBF / MTTR (시프트 가동시간 기준 MTBF) ──
        double? mtbf = null;
        string? mtbfNote;
        double? mttr = null;
        string? mttrNote;
        if (failureCount <= 0)
        {
            mtbfNote = "고장(분류 unplanned) 건수 0 — MTBF 산출 불가.";
            mttrNote = "고장(분류 unplanned, 마감됨) 건수 0 — MTTR 산출 불가.";
        }
        else
        {
            mtbf = av.RunTimeMs / failureCount;
            mtbfNote = "시프트 가동시간 / 고장건수.";
            mttr = (double)failureDurationMs / failureCount;
            mttrNote = "Σ고장 지속시간(마감 이벤트만) / 고장건수.";
        }

        var shiftLabel = $"{shift.Start}–{shift.End}";
        return new OeeShiftSummaryDto(
            FlowName: flowName,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            PeriodMs: periodMs,
            ScheduledMs: av.ScheduledMs,
            PlannedStopMs: av.PlannedStopMs,
            PlannedProductionMs: av.PlannedProductionMs,
            DowntimeMs: av.DowntimeMs,
            DowntimeCount: av.DowntimeCount,
            RunTimeMs: av.RunTimeMs,
            TotalCount: totalCount,
            RejectCount: rejectOut,
            GoodCount: goodOut,
            IdealCycleTimeMs: idealCT,
            IdealCycleTimeSource: idealCtSource,
            Availability: availability,
            AvailabilityNote: availNote,
            Performance: performance,
            PerformanceNote: perfNote,
            Quality: quality,
            QualityNote: qualNote,
            QualitySource: qualitySource,
            Oee: oee,
            OeeNote: oeeNote,
            ShiftStart: shift.Start,
            ShiftEnd: shift.End,
            ShiftType: shift.ShiftType,
            ShiftLabel: shiftLabel,
            FailureCount: failureCount,
            Mtbf: mtbf,
            MtbfNote: mtbfNote,
            Mttr: mttr,
            MttrNote: mttrNote);
    }

    private readonly record struct ShiftAvail(
        double ScheduledMs, double PlannedStopMs, double PlannedProductionMs,
        double DowntimeMs, double RunTimeMs, int DowntimeCount);

    /// <summary>
    /// 한 스코프(flow 또는 라인=null)의 시프트 가용성 구간 산출. scheduled(시프트 창)는 라인 공통이라 외부에서 1회 산출해 전달.
    /// 계획정지(시프트 예외)는 scheduled 와 교집합 → PPT = scheduled − 예외. 비계획정지(category≠planned)는 PPT 와 교집합.
    /// </summary>
    private async Task<ShiftAvail> ComputeShiftAvailabilityAsync(
        string? flowName, DateTime fromUtc, DateTime toUtc, List<(double S, double E)> scheduled, CancellationToken ct)
    {
        var scheduledMs = Intervals.Total(scheduled);

        var exc = await _repo.QueryShiftExceptionsAsync(fromUtc, toUtc, flowName, ct);
        var excSegs = exc.Select(x => (ToMs(x.StartAt), ToMs(x.EndAt)));
        var plannedStop = Intervals.Intersect(scheduled, excSegs);
        var ppt = Intervals.Subtract(scheduled, excSegs);
        var pptMs = Intervals.Total(ppt);

        var dt = await _repo.QueryDowntimeAsync(fromUtc, toUtc, null, null, flowName, ct);
        var nowMs = ToMs(DateTime.UtcNow);
        var dtSegs = dt
            .Where(e => !string.Equals(e.Category, "planned", StringComparison.OrdinalIgnoreCase))
            .Select(e => (ToMs(e.StartAt), e.EndAt.HasValue ? ToMs(e.EndAt.Value) : nowMs));
        var dtInPpt = Intervals.Intersect(ppt, dtSegs);
        var downtimeMs = Intervals.Total(dtInPpt);
        var runTimeMs = Math.Max(0, pptMs - downtimeMs);

        return new ShiftAvail(scheduledMs, Intervals.Total(plannedStop), pptMs, downtimeMs, runTimeMs, dt.Count);
    }

    /// <summary>라인 전체 성능 = idealCT 설정 flow 들의 시프트 가동시간 기반 성능을 생산수 가중평균.</summary>
    private async Task<(double? Perf, string? Note)> ComputeShiftLinePerformanceAsync(
        DateTime fromUtc, DateTime toUtc, List<(double S, double E)> scheduled, CancellationToken ct)
    {
        var flows = _settings.GetFlowsWithIdealCycleTime();
        if (flows.Count == 0)
            return (null, "표준 사이클(idealCT) 설정된 Flow 없음 — 성능 산출 불가. 표준CT 입력 필요.");

        double weightedPerf = 0;
        long weight = 0;
        int usedFlows = 0;
        foreach (var (flow, ideal) in flows)
        {
            var count = await CountFlowHistoryAsync(flow, fromUtc, toUtc);
            if (count <= 0) continue;
            var av = await ComputeShiftAvailabilityAsync(flow, fromUtc, toUtc, scheduled, ct);
            if (av.RunTimeMs <= 0) continue;
            var perf = Math.Min(1.0, (ideal * (double)count) / av.RunTimeMs);
            weightedPerf += perf * count;
            weight += count;
            usedFlows++;
        }
        if (weight <= 0)
            return (null, "표준CT 설정된 Flow 의 시프트 가동시간 내 생산 사이클 0 — 성능 산출 불가.");
        return (weightedPerf / weight, $"Flow {usedFlows}개 성능의 생산수 가중평균 (시프트 가동시간 기준).");
    }

    /// <summary>
    /// 시프트 창(Start/End, 로컬 "HH:mm")을 조회기간 [from,to] 에 맞춰 UTC 구간 리스트로. End≤Start 면 자정 넘김(야간),
    /// Start==End 면 24h 연속. 로컬일을 하루 앞뒤로 넉넉히 훑어 경계 시프트도 포함한 뒤 기간으로 클립.
    /// </summary>
    private static List<(double S, double E)> BuildScheduledIntervals(ShiftSettings shift, DateTime fromUtc, DateTime toUtc)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!TimeSpan.TryParseExact(shift.Start, "hh\\:mm", inv, out var startT)) startT = new TimeSpan(8, 0, 0);
        if (!TimeSpan.TryParseExact(shift.End, "hh\\:mm", inv, out var endT)) endT = new TimeSpan(17, 0, 0);
        bool crosses = endT <= startT; // 야간(자정넘김) 또는 24h(==)

        var fromMs = ToMs(fromUtc);
        var toMs = ToMs(toUtc);
        var fromLocalDate = fromUtc.ToLocalTime().Date;
        var toLocalDate = toUtc.ToLocalTime().Date;

        var segs = new List<(double S, double E)>();
        for (var d = fromLocalDate.AddDays(-1); d <= toLocalDate.AddDays(1); d = d.AddDays(1))
        {
            var sLocal = d + startT;
            var eLocal = crosses ? d.AddDays(1) + endT : d + endT;
            var sUtc = DateTime.SpecifyKind(sLocal, DateTimeKind.Local).ToUniversalTime();
            var eUtc = DateTime.SpecifyKind(eLocal, DateTimeKind.Local).ToUniversalTime();
            var s = Math.Max(ToMs(sUtc), fromMs);
            var e = Math.Min(ToMs(eUtc), toMs);
            if (e > s) segs.Add((s, e));
        }
        return Intervals.Union(segs);
    }

    // DateTime → epoch-ms (UTC 절대시각 축). ★Kind 정규화 필수:
    //   repo 의 ParseIso(FromSqliteUtcString)는 .ToLocalTime() 으로 Kind=Local(로컬 벽시계)을 돌려주고,
    //   BuildScheduledIntervals 는 Kind=Utc 를 만든다 — Kind 무시 값 빼기를 하면 두 축이 어긋나 교집합이 0이 된다.
    //   Local 은 UTC 로 환산, Unspecified 는 UTC 간주(DB 컨벤션), Utc 는 그대로 → 모두 동일 UTC 축.
    private static readonly DateTime _epochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static double ToMs(DateTime dt)
    {
        var utc = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt;
        return (utc - _epochUtc).TotalMilliseconds;
    }

    /// <summary>구간(반열린 [S,E), ms) 합집합/교집합/차집합/합계. 가용성 정밀 산출용(중복 정지 이중계상 방지).</summary>
    private static class Intervals
    {
        public static List<(double S, double E)> Union(IEnumerable<(double S, double E)> segs)
        {
            var xs = segs.Where(x => x.E > x.S).OrderBy(x => x.S).ToList();
            var res = new List<(double S, double E)>();
            foreach (var s in xs)
            {
                if (res.Count > 0 && s.S <= res[^1].E)
                    res[^1] = (res[^1].S, Math.Max(res[^1].E, s.E));
                else
                    res.Add(s);
            }
            return res;
        }

        public static double Total(IEnumerable<(double S, double E)> segs)
            => segs.Sum(s => Math.Max(0, s.E - s.S));

        public static List<(double S, double E)> Intersect(
            IEnumerable<(double S, double E)> a, IEnumerable<(double S, double E)> b)
        {
            var aa = Union(a);
            var bb = Union(b);
            var res = new List<(double S, double E)>();
            int i = 0, j = 0;
            while (i < aa.Count && j < bb.Count)
            {
                var s = Math.Max(aa[i].S, bb[j].S);
                var e = Math.Min(aa[i].E, bb[j].E);
                if (e > s) res.Add((s, e));
                if (aa[i].E < bb[j].E) i++; else j++;
            }
            return res;
        }

        public static List<(double S, double E)> Subtract(
            IEnumerable<(double S, double E)> a, IEnumerable<(double S, double E)> b)
        {
            var aa = Union(a);
            var bb = Union(b);
            var res = new List<(double S, double E)>();
            foreach (var seg in aa)
            {
                var cur = seg.S;
                foreach (var x in bb)
                {
                    if (x.E <= cur || x.S >= seg.E) continue;
                    if (x.S > cur) res.Add((cur, Math.Min(x.S, seg.E)));
                    cur = Math.Max(cur, x.E);
                    if (cur >= seg.E) break;
                }
                if (cur < seg.E) res.Add((cur, seg.E));
            }
            return res;
        }
    }

    // ── OEE 계산 코어 ─────────────────────────────────────────────────────

    private async Task<OeeSummaryDto> BuildSummaryAsync(string? flowName, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var periodMs = (toUtc - fromUtc).TotalMilliseconds;
        if (periodMs < 0) periodMs = 0;

        var (downtimeMs, downtimeCount) = await _repo.GetDowntimeAggregateAsync(fromUtc, toUtc, flowName, ct);
        var (failureDurationMs, failureCount) = await _repo.GetFailureAggregateAsync(fromUtc, toUtc, flowName, ct);

        // totalCount 자동: dspFlowHistory row count (기간 내, flow 지정 시 그 flow).
        int? totalCount = await CountFlowHistoryAsync(flowName, fromUtc, toUtc);

        // 생산/품질 (로컬일 버킷). 입력 불량(manual 또는 plc 불량신호) 합만 소비 — 품질 분모는 기간 사이클수(§12).
        var (_, _, prodReject, hasReject) =
            await _repo.QueryProductionAsync(fromUtc.ToLocalTime(), toUtc.ToLocalTime(), flowName, ct);

        // ── Availability (계획시간 폴백 체인: UserSet 시프트 ▸ 14일 자동추정 ▸ 달력근사 — doc/21 §12) ──
        // runtime(가동시간)도 폴백 체인 산출값을 쓴다 → 성능/MTBF 분모가 가용성과 일관(혼합 분모 방지).
        var av = await ResolveAvailabilityAsync(flowName, fromUtc, toUtc, downtimeMs, periodMs, ct);
        double? availability = av.Availability;
        string? availNote = av.Note;
        string? availabilitySource = av.Source;
        var runtimeMs = av.RuntimeMs;

        // ── Performance ((idealCT × total) / runtime) ──
        // per-flow: 해당 flow 의 idealCT 사용. 라인 전체(flowName=null): idealCT 설정된 flow 들의
        //   per-flow 성능을 생산수 가중평균(각 flow 는 자기 정지/가동 기준) — 라인 전체에도 성능이 뜨도록.
        var (idealCT, idealCtSource) = ResolveIdealCycle(flowName);
        double? performance = null;
        string? perfNote;
        if (string.IsNullOrWhiteSpace(flowName))
        {
            (performance, perfNote) = await ComputeLinePerformanceAsync(fromUtc, toUtc, periodMs, ct);
        }
        else if (idealCT is null || idealCT <= 0)
        {
            perfNote = "표준 사이클(idealCT) 미설정 — 성능 산출 불가. 클린사이클이 모이면 자동 기입됩니다(또는 표준CT 직접 입력).";
        }
        else if (totalCount is null || totalCount <= 0)
        {
            perfNote = "기간 내 생산 사이클 0 — 성능 산출 불가.";
        }
        else if (runtimeMs <= 0)
        {
            perfNote = "가동시간 0 — 성능 산출 불가.";
        }
        else
        {
            performance = Math.Min(1.0, (idealCT.Value * (double)totalCount.Value) / runtimeMs);
            perfNote = null;
        }

        // ── Quality ── 기본 100% 가정, 불량 입력 시 실측 (§12 개정)
        var (quality, qualNote, qualitySource, rejectOut, goodOut) =
            OeeMath.ComputeQuality(totalCount, prodReject, hasReject);

        // ── OEE (A × P × Q) — 순수 함수 단일 소스 ──
        var (oee, oeeNote) = OeeMath.ComputeOee(availability, performance, quality, qualitySource);

        // ── MTBF (무고장=null+배지) / MTTR ──
        var (mtbf, mtbfNote, _) = OeeMath.ComputeMtbf(runtimeMs, failureCount);
        double? mttr = null;
        string? mttrNote;
        if (failureCount <= 0)
            mttrNote = "고장(분류 unplanned, 마감됨) 건수 0 — MTTR 산출 불가.";
        else
        {
            mttr = (double)failureDurationMs / failureCount;
            mttrNote = "Σ고장 지속시간(마감 이벤트만) / 고장건수.";
        }

        return new OeeSummaryDto(
            FlowName: flowName,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            PeriodMs: periodMs,
            DowntimeMs: downtimeMs,
            DowntimeCount: downtimeCount,
            TotalCount: totalCount,
            RejectCount: rejectOut,
            GoodCount: goodOut,
            IdealCycleTimeMs: idealCT,
            IdealCycleTimeSource: idealCtSource,
            Availability: availability,
            AvailabilityNote: availNote,
            AvailabilitySource: availabilitySource,
            Performance: performance,
            PerformanceNote: perfNote,
            Quality: quality,
            QualityNote: qualNote,
            QualitySource: qualitySource,
            Oee: oee,
            OeeNote: oeeNote,
            FailureCount: failureCount,
            Mtbf: mtbf,
            MtbfNote: mtbfNote,
            Mttr: mttr,
            MttrNote: mttrNote);
    }

    /// <summary>flow 의 유효 idealCT(ms)와 출처("auto"=실측 자동기입 / null=수동). 라인(flow=null)은 (null, null).</summary>
    private (int? Ms, string? Source) ResolveIdealCycle(string? flowName)
    {
        if (string.IsNullOrWhiteSpace(flowName)) return (null, null);
        var ov = _settings.GetFlowCycleOverride(flowName);
        return ov?.IdealCycleTimeMs is > 0 ? (ov.IdealCycleTimeMs, ov.IdealCycleTimeSource) : (null, null);
    }

    /// <summary>
    /// 라인 전체 성능 = idealCT 설정된 flow 들의 per-flow 성능(min(1, idealCT×count/runtime_f))을 생산수로 가중평균.
    /// runtime_f 는 flow별 정지(달력근사)로 산출 — 단일 idealCT 로 라인 totalCount 를 나누는 비정합을 피한다.
    /// </summary>
    private async Task<(double? Perf, string? Note)> ComputeLinePerformanceAsync(
        DateTime fromUtc, DateTime toUtc, double periodMs, CancellationToken ct)
    {
        var flows = _settings.GetFlowsWithIdealCycleTime();
        if (flows.Count == 0)
            return (null, "표준 사이클(idealCT) 설정된 Flow 없음 — 성능 산출 불가. 표준CT 입력 필요.");

        double weightedPerf = 0;
        long weight = 0;
        int usedFlows = 0;
        foreach (var (flow, ideal) in flows)
        {
            var count = await CountFlowHistoryAsync(flow, fromUtc, toUtc);
            if (count <= 0) continue;
            // 분모 = 그 flow 의 가동시간(가용성 폴백 체인과 동일 산출) → 성능이 가용성과 일관(혼합 분모 방지).
            var (dMs, _) = await _repo.GetDowntimeAggregateAsync(fromUtc, toUtc, flow, ct);
            var avf = await ResolveAvailabilityAsync(flow, fromUtc, toUtc, dMs, periodMs, ct);
            var runtimeMs = avf.RuntimeMs;
            if (runtimeMs <= 0) continue;
            var perf = Math.Min(1.0, (ideal * (double)count) / runtimeMs);
            weightedPerf += perf * count;
            weight += count;
            usedFlows++;
        }
        if (weight <= 0)
            return (null, "표준CT 설정된 Flow 의 기간 내 생산 사이클 0 — 성능 산출 불가.");
        return (weightedPerf / weight, $"Flow {usedFlows}개 성능의 생산수 가중평균 (per-flow idealCT 기반).");
    }

    /// <summary>
    /// plc.db dspFlowHistory 의 사이클 row 수 (기간 내, 비가동 제외). flowName=null → 전체 flow 합.
    /// recordedAt 은 UTC(Z 없는 DATETIME) 로 저장 — UTC 범위 문자열로 비교.
    /// </summary>
    private async Task<int> CountFlowHistoryAsync(string? flowName, DateTime fromUtc, DateTime toUtc)
    {
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!System.IO.File.Exists(dbPath)) return 0;
        try
        {
            await using var conn = new SqliteConnection(
                $"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync();

            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (exists == 0) return 0;

            var p = new DynamicParameters();
            // recordedAt 저장 포맷("yyyy-MM-dd HH:mm:ss(.fffffff)", Z 없음)과 동일하게 비교 문자열 구성.
            p.Add("From", fromUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            p.Add("To", toUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            var flowClause = "";
            if (!string.IsNullOrWhiteSpace(flowName))
            {
                flowClause = " AND flowName = @Flow ";
                p.Add("Flow", flowName.Trim());
            }
            var sql = $@"
                SELECT COUNT(*) FROM dspFlowHistory
                WHERE COALESCE(IsIdle,0) = 0
                  AND recordedAt >= @From AND recordedAt < @To {flowClause}";
            return await conn.ExecuteScalarAsync<int>(sql, p);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] dspFlowHistory count failed");
            return 0;
        }
    }

    // ── 가용성 분모 폴백 체인 (계획시간: UserSet 시프트 ▸ 14일 자동추정 ▸ 달력근사) ──────────────

    private readonly record struct AvailabilityResult(double? Availability, string? Note, string Source, double PlannedMs, double RuntimeMs);

    /// <summary>
    /// 가용성 = 가동시간 / 계획시간. 계획시간을 폴백 체인으로 정한다(doc/21 §12):
    ///   ① UserSet 시프트(권위적): 시프트 창 ∩ 기간 − 계획정지 = PPT.
    ///   ② 14일 자동추정: 활동 시간창 × 조회기간 활동일수 − 계획정비(category='planned') = PPT.
    ///   ③ 달력근사: 기간 전체 − 정지(최후 폴백).
    /// RuntimeMs 도 함께 돌려줘 성능/MTBF 분모로 재사용(분모 일관).
    /// </summary>
    private async Task<AvailabilityResult> ResolveAvailabilityAsync(
        string? flowName, DateTime fromUtc, DateTime toUtc, long downtimeMs, double periodMs, CancellationToken ct)
    {
        // ① UserSet 시프트
        var shift = _settings.LoadSettings().Shift;
        if (shift.UserSet)
        {
            var scheduled = BuildScheduledIntervals(shift, fromUtc, toUtc);
            var sav = await ComputeShiftAvailabilityAsync(flowName, fromUtc, toUtc, scheduled, ct);
            if (sav.PlannedProductionMs > 0)
                return new AvailabilityResult(
                    Math.Clamp(sav.RunTimeMs / sav.PlannedProductionMs, 0, 1),
                    "가동시간 ÷ 계획생산시간(사용자 시프트 ∩ 기간 − 계획정지).", "shift", sav.PlannedProductionMs, sav.RunTimeMs);
        }

        // ② 14일 자동추정
        var win = _shiftInfer.Get(flowName);
        if (win is not null)
        {
            var (pptMs, runtimeMs, ok) = await ComputeAutoAvailabilityAsync(flowName, win, fromUtc, toUtc, ct);
            if (ok && pptMs > 0)
                return new AvailabilityResult(
                    Math.Clamp(runtimeMs / pptMs, 0, 1),
                    "가동시간 ÷ 자동추정 계획시간(14일 활동 시간창 × 활동일수 − 계획정비).", "auto", pptMs, runtimeMs);
        }

        // ③ 달력근사
        if (periodMs > 0)
        {
            var rt = Math.Max(0, periodMs - downtimeMs);
            return new AvailabilityResult(
                Math.Clamp(rt / periodMs, 0, 1),
                "달력근사 (1 − 정지/기간). 시프트 미설정·활동 데이터 부족 시 폴백.", "calendar", periodMs, rt);
        }
        return new AvailabilityResult(null, "기간이 0 — 가용성 산출 불가.", "calendar", 0, 0);
    }

    /// <summary>
    /// 자동추정 가용성: 활동 시간창(win.InBand)을 조회기간 내 활동일마다 시간슬롯으로 펼쳐 계획시간을 만든다.
    /// 계획정비(category='planned')는 계획시간에서 차감(가용성손실 아님), 비계획 정지는 가동시간에서 차감.
    /// </summary>
    private async Task<(double PptMs, double RuntimeMs, bool Ok)> ComputeAutoAvailabilityAsync(
        string? flowName, ShiftWindow win, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var activeDates = await GetActiveLocalDatesAsync(flowName, fromUtc, toUtc);
        if (activeDates.Count == 0) return (0, 0, false);

        var fromMs = ToMs(fromUtc);
        var toMs = ToMs(toUtc);
        var segs = new List<(double S, double E)>();
        foreach (var d in activeDates)
        {
            for (int h = 0; h < 24; h++)
            {
                if (!win.InBand[h]) continue;
                var sLocal = d.AddHours(h);
                var sUtc = DateTime.SpecifyKind(sLocal, DateTimeKind.Local).ToUniversalTime();
                var eUtc = DateTime.SpecifyKind(sLocal.AddHours(1), DateTimeKind.Local).ToUniversalTime();
                var s = Math.Max(ToMs(sUtc), fromMs);
                var e = Math.Min(ToMs(eUtc), toMs);
                if (e > s) segs.Add((s, e));
            }
        }
        var planned = Intervals.Union(segs);
        if (Intervals.Total(planned) <= 0) return (0, 0, false);

        var dt = await _repo.QueryDowntimeAsync(fromUtc, toUtc, null, null, flowName, ct);
        var nowMs = ToMs(DateTime.UtcNow);
        var plannedStop = dt
            .Where(e => string.Equals(e.Category, "planned", StringComparison.OrdinalIgnoreCase))
            .Select(e => (ToMs(e.StartAt), e.EndAt.HasValue ? ToMs(e.EndAt.Value) : nowMs));
        var ppt = Intervals.Subtract(planned, plannedStop);
        var pptMs = Intervals.Total(ppt);
        if (pptMs <= 0) return (0, 0, false);

        var nonPlanned = dt
            .Where(e => !string.Equals(e.Category, "planned", StringComparison.OrdinalIgnoreCase))
            .Select(e => (ToMs(e.StartAt), e.EndAt.HasValue ? ToMs(e.EndAt.Value) : nowMs));
        var downInPpt = Intervals.Total(Intervals.Intersect(ppt, nonPlanned));
        var runtimeMs = Math.Max(0, pptMs - downInPpt);
        return (pptMs, runtimeMs, true);
    }

    /// <summary>조회기간 [from,to] 안에서 사이클이 1건이라도 있은 로컬 날짜들(무활동일 제외). flowName=null → 전체.</summary>
    private async Task<List<DateTime>> GetActiveLocalDatesAsync(string? flowName, DateTime fromUtc, DateTime toUtc)
    {
        var result = new List<DateTime>();
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!System.IO.File.Exists(dbPath)) return result;
        try
        {
            await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync();
            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (exists == 0) return result;

            var p = new DynamicParameters();
            p.Add("From", fromUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            p.Add("To", toUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            var flowClause = "";
            if (!string.IsNullOrWhiteSpace(flowName)) { flowClause = " AND flowName = @Flow "; p.Add("Flow", flowName.Trim()); }
            // substr(...,1,19): 7자리 소수 제거 후 localtime → 로컬 날짜. (recordedAt = UTC·Z없는 문자열)
            var sql = $@"
                SELECT DISTINCT strftime('%Y-%m-%d', substr(recordedAt,1,19), 'localtime') AS D
                FROM dspFlowHistory
                WHERE COALESCE(IsIdle,0) = 0 AND recordedAt >= @From AND recordedAt < @To {flowClause}";
            var dates = await conn.QueryAsync<string>(sql, p);
            foreach (var s in dates)
                if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var d))
                    result.Add(DateTime.SpecifyKind(d.Date, DateTimeKind.Local));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[OEE] active local dates query failed"); }
        return result;
    }

    // ── GET /api/oee/plan-time?from&to&flow — 가용성 폴백 체인 + 14일 히스토그램 (목업 계획시간 카드용) ──
    [HttpGet("plan-time")]
    public async Task<ActionResult<OeePlanTimeDto>> PlanTime(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? flow, CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var flowName = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();
        var periodMs = Math.Max(0, (toUtc - fromUtc).TotalMilliseconds);
        var (downtimeMs, _) = await _repo.GetDowntimeAggregateAsync(fromUtc, toUtc, flowName, ct);

        var avr = await ResolveAvailabilityAsync(flowName, fromUtc, toUtc, downtimeMs, periodMs, ct);
        var shift = _settings.LoadSettings().Shift;
        var win = _shiftInfer.Get(flowName);
        var activeDays = (await GetActiveLocalDatesAsync(flowName, fromUtc, toUtc)).Count;

        return new OeePlanTimeDto(
            Source: avr.Source,
            PlannedMs: avr.PlannedMs,
            RuntimeMs: avr.RuntimeMs,
            ShiftUserSet: shift.UserSet,
            ShiftLabel: $"{shift.Start}–{shift.End}",
            AutoAvailable: win is not null,
            AutoStartHour: win?.StartHour,
            AutoEndHour: win?.EndHour,
            AutoCrosses: win?.Crosses ?? false,
            AutoSampleCycles: win?.SampleCycles ?? 0,
            AutoSampleDays: win?.SampleDays ?? 0,
            ActiveDays: activeDays,
            Histogram: win?.Histogram ?? new int[24]);
    }

    // ── GET /api/oee/daily?from&to&flow ──────────────────────────────────
    // 일자별(스팬>2일) 또는 시간별(≤2일) 가동·정지·점검 버킷.
    // 가동 = slotMs - unplannedMs - plannedMs (달력근사).
    [HttpGet("daily")]
    public async Task<ActionResult<OeeDailyResponse>> Daily(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? flow,
        CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var flowName = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();
        var spanDays = (toUtc - fromUtc).TotalDays;
        var hourly = spanDays <= 2.0;
        var gran = hourly ? "hour" : "day";

        // DB에서 정지 이벤트를 버킷별로 집계
        var dbBuckets = await _repo.GetDowntimeBySlotsAsync(fromUtc, toUtc, flowName, hourly, ct);
        var lookup = dbBuckets.ToDictionary(r => r.Slot, r => r, StringComparer.Ordinal);

        // 전체 슬롯 목록 생성 (달력 기준)
        var slots = new List<OeeDailySlotDto>();
        if (hourly)
        {
            var cur = fromUtc.ToLocalTime();
            cur = new DateTime(cur.Year, cur.Month, cur.Day, cur.Hour, 0, 0, DateTimeKind.Local);
            while (cur.ToUniversalTime() < toUtc)
            {
                var next = cur.AddHours(1);
                var label = cur.ToString("yyyy-MM-dd HH:00");
                var slotStart = cur.ToUniversalTime();
                var slotEnd = next.ToUniversalTime();
                var slotMs = (long)Math.Max(0, (Min(toUtc, slotEnd) - Max(fromUtc, slotStart)).TotalMilliseconds);
                lookup.TryGetValue(label, out var b);
                slots.Add(BuildDailySlot(label, slotMs, b));
                cur = next;
            }
        }
        else
        {
            var curLocal = fromUtc.ToLocalTime().Date;
            while (curLocal.ToUniversalTime() < toUtc)
            {
                var nextLocal = curLocal.AddDays(1);
                var label = curLocal.ToString("yyyy-MM-dd");
                var slotStart = curLocal.ToUniversalTime();
                var slotEnd = nextLocal.ToUniversalTime();
                var slotMs = (long)Math.Max(0, (Min(toUtc, slotEnd) - Max(fromUtc, slotStart)).TotalMilliseconds);
                lookup.TryGetValue(label, out var b);
                slots.Add(BuildDailySlot(label, slotMs, b));
                curLocal = nextLocal;
            }
        }

        return new OeeDailyResponse(gran, slots);
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>
    /// 정지 5분해(failure/other/unclassified/planned)를 슬롯 달력시간(slotMs) 예산 안으로 캡해 가동이 음수가 되지 않게 한다.
    /// 우선순위(비계획 먼저 ▸ 계획)는 구 동작과 동일. 가동 = SlotMs − (4분해 합)은 클라이언트가 차감.
    /// </summary>
    private static OeeDailySlotDto BuildDailySlot(
        string label, long slotMs,
        (string Slot, long PlannedMs, long FailureMs, long OtherMs, long UnclassifiedMs) b)
    {
        var budget = slotMs;
        long Take(long v) { var t = Math.Min(Math.Max(0, v), Math.Max(0, budget)); budget -= t; return t; }
        var failure = Take(b.FailureMs);
        var other = Take(b.OtherMs);
        var unclass = Take(b.UnclassifiedMs);
        var planned = Take(b.PlannedMs);
        var unplanned = failure + other + unclass;
        return new OeeDailySlotDto(label, slotMs, unplanned, planned, failure, other, unclass);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    // 기본 기간: 미지정 시 최근 24h.
    private static (DateTime FromUtc, DateTime ToUtc) ResolveRange(DateTime? from, DateTime? to)
    {
        var toUtc = to.HasValue ? ToUtc(to.Value) : DateTime.UtcNow;
        var fromUtc = from.HasValue ? ToUtc(from.Value) : toUtc.AddHours(-24);
        if (fromUtc > toUtc) (fromUtc, toUtc) = (toUtc, fromUtc);
        return (fromUtc, toUtc);
    }

    private static DateTime ToUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
    };
}

// ── 요청 DTO (camelCase JSON: reasonCode, category, endAt, idealCycleTimeMs ...) ──

public record ClassifyRequest(string? ReasonCode, string? Category);
public record CloseRequest(DateTime? EndAt);
public record BulkClassifyRequest(List<long> Ids, string? ReasonCode, string? Category);
public record BulkCloseRequest(List<long> Ids, DateTime? EndAt);
public record ProductionRequest(DateTime? Date, string Flow, string? Shift, int Reject);
public record ShiftExceptionRequest(string? Flow, DateTime? StartAt, DateTime? EndAt, string Kind, string? Note);
// Mode: "manual"=사용자 직접 입력(자동이 안 덮음, 값 동일해도 수동 잠금) / "auto"=자동 관리로 해제(수동값 비움→자동기입) / null=레거시(값 변경 시 수동).
public record IdealCycleRequest(string Flow, int? IdealCycleTimeMs, string? Mode = null);
public record IdealCycleBatchRequest(List<IdealCycleRequest> Items);

// idealCT 일괄 편집 테이블 1행: 현재 설정값(+출처) + 실측 추천/통계(이상치 제외).
// Source: "auto" = 실측 자동기입(OeeIdealCycleAutoFillService) / null = 수동 입력(값 있을 때) 또는 미설정.
public record IdealCycleRowDto(
    string FlowName,
    int? IdealCycleTimeMs,
    string? Source,
    int? RecommendedMs,
    int SampleCount,
    int? MinCt,
    int? MedianCt,
    int? AvgCt);

/// <summary>
/// 가용성 분모(계획시간) 폴백 체인 상태 + 14일 활동 히스토그램 (uptime 계획시간 카드용).
/// Source = 현재 활성 단계(shift/auto/calendar). Histogram = 14일 시간대별(0~23) 사이클 수(표시용).
/// </summary>
public record OeePlanTimeDto(
    string Source,
    double PlannedMs,
    double RuntimeMs,
    bool ShiftUserSet,
    string ShiftLabel,
    bool AutoAvailable,
    int? AutoStartHour,
    int? AutoEndHour,
    bool AutoCrosses,
    int AutoSampleCycles,
    int AutoSampleDays,
    int ActiveDays,
    int[] Histogram);

/// <summary>
/// 시프트 기반 OEE 요약 (Phase4 진짜 가용성). Summary 와 달리 분모가 계획생산시간(PPT)이다.
/// 시간값(*Ms)은 구간합(ms). 산출 불가 지표는 null + *Note 정직 표기.
/// 워터폴: 완전생산 = PPT×OEE, 가용성손실 = PPT×(1−A), 성능손실 = PPT×A×(1−P), 품질손실 = PPT×A×P×(1−Q).
/// </summary>
public sealed record OeeShiftSummaryDto(
    string? FlowName,
    DateTime FromUtc,
    DateTime ToUtc,
    double PeriodMs,              // 달력 기간 (to-from)
    double ScheduledMs,          // 시프트 창 ∩ 기간
    double PlannedStopMs,        // 계획정지(시프트 예외) ∩ 계획시간
    double PlannedProductionMs,  // PPT = 계획시간 − 계획정지
    double DowntimeMs,           // 비계획정지 ∩ PPT
    int DowntimeCount,           // 기간 내 정지 이벤트 수(맥락)
    double RunTimeMs,            // 가동시간 = PPT − 비계획정지
    int? TotalCount,
    int? RejectCount,
    int? GoodCount,
    int? IdealCycleTimeMs,
    string? IdealCycleTimeSource, // "auto" = 실측 자동기입 / null = 수동(또는 미설정)
    double? Availability,        // 가동시간 / PPT
    string? AvailabilityNote,
    double? Performance,
    string? PerformanceNote,
    double? Quality,             // (사이클수 − 입력불량) / 사이클수 — 불량 미입력 시 100% 가정(§12)
    string? QualityNote,
    string? QualitySource,       // "measured" / "assumed"(불량 0 가정) / null(산출 불가)
    double? Oee,
    string? OeeNote,
    string ShiftStart,           // "HH:mm" 로컬
    string ShiftEnd,
    string ShiftType,
    string ShiftLabel,           // "08:00–17:00"
    int FailureCount,
    double? Mtbf,
    string? MtbfNote,
    double? Mttr,
    string? MttrNote);
