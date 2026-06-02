using Dapper;
using DSPilot.Infrastructure;
using DSPilot.Models.Oee;
using DSPilot.Repositories;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace DSPilot.Controllers;

/// <summary>
/// P5 OEE / 정지(다운타임) API (격리형 호스팅). 정적 페이지(/app/*.html)가 fetch 로 호출.
///
/// OEE 는 on-demand 계산(별도 daily backfill 생략) — doc/21 §8.
///   availability = 달력근사 (1 - downtime/period)        ⚠ Phase1: 계획시간 데이터 0 → 진짜 가용성은 Phase4
///   performance  = (idealCT × total) / runtime, min(1.0)  idealCT 미설정 시 null
///   quality      = good / total (reject 수동 입력 시)
///   oee          = A × P × Q (한 요소라도 소스 없으면 null + 사유)
///   mtbf/mttr    = isFailure=1 이벤트 기반
/// totalCount 는 dspFlowHistory row count 자동, rejectCount 는 수동, 분류는 수동 PATCH (isFailure 기본 0).
/// 산출 불가 지표는 값 null + *Note 로 정직 표기 (doc/21 §10).
/// </summary>
[ApiController]
[Route("api/oee")]
public class OeeController : ControllerBase
{
    private readonly IOeeRepository _repo;
    private readonly AppSettingsService _settings;
    private readonly DsProjectService _project;
    private readonly IDatabasePathResolver _pathResolver;
    private readonly ILogger<OeeController> _logger;

    public OeeController(
        IOeeRepository repo,
        AppSettingsService settings,
        DsProjectService project,
        IDatabasePathResolver pathResolver,
        ILogger<OeeController> logger)
    {
        _repo = repo;
        _settings = settings;
        _project = project;
        _pathResolver = pathResolver;
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
        return rows.ToList();
    }

    // ── POST /api/oee/downtime/{id}/classify  {reasonCode, category} ──────
    // category=unplanned 일 때만 isFailure=1 (MTBF/MTTR 분모 오염 방지 — doc/21 §2.1).
    [HttpPost("downtime/{id:long}/classify")]
    public async Task<ActionResult<object>> Classify(long id, [FromBody] ClassifyRequest req, CancellationToken ct)
    {
        var category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim().ToLowerInvariant();
        var reasonCode = string.IsNullOrWhiteSpace(req.ReasonCode) ? null : req.ReasonCode.Trim();
        var isFailure = string.Equals(category, "unplanned", StringComparison.OrdinalIgnoreCase);

        var n = await _repo.ClassifyDowntimeAsync(id, reasonCode, category, isFailure, ct);
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

    // ── OEE 계산 코어 ─────────────────────────────────────────────────────

    private async Task<OeeSummaryDto> BuildSummaryAsync(string? flowName, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var periodMs = (toUtc - fromUtc).TotalMilliseconds;
        if (periodMs < 0) periodMs = 0;

        var (downtimeMs, downtimeCount) = await _repo.GetDowntimeAggregateAsync(fromUtc, toUtc, flowName, ct);
        var (failureDurationMs, failureCount) = await _repo.GetFailureAggregateAsync(fromUtc, toUtc, flowName, ct);

        // totalCount 자동: dspFlowHistory row count (기간 내, flow 지정 시 그 flow).
        int? totalCount = await CountFlowHistoryAsync(flowName, fromUtc, toUtc);

        // 생산/품질 (로컬일 버킷). reject 데이터(manual 입력 또는 plc 불량신호)가 있으면 quality 산출.
        var (prodTotal, prodGood, prodReject, hasReject) =
            await _repo.QueryProductionAsync(fromUtc.ToLocalTime(), toUtc.ToLocalTime(), flowName, ct);

        // runtime(달력근사) = period - downtime. 가용성 분모 = period(달력).
        var runtimeMs = Math.Max(0, periodMs - downtimeMs);

        // ── Availability (Phase1 달력근사) ──
        double? availability = null;
        string? availNote;
        if (periodMs > 0)
        {
            availability = Math.Clamp(runtimeMs / periodMs, 0.0, 1.0);
            availNote = "달력근사 (1 - 정지/기간). 계획시간 미반영 — 진짜 가용성은 시프트 설정(Phase4) 후.";
        }
        else
        {
            availNote = "기간이 0 — 가용성 산출 불가.";
        }

        // ── Performance ((idealCT × total) / runtime) ──
        int? idealCT = ResolveIdealCycleTimeMs(flowName);
        double? performance = null;
        string? perfNote;
        if (idealCT is null || idealCT <= 0)
        {
            perfNote = "표준 사이클(idealCT) 미설정 — 성능 산출 불가. /api/oee/ideal-cycle 로 입력 필요.";
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

        // ── Quality (good / total) ──
        double? quality = null;
        string? qualNote;
        int? rejectOut = null;
        int? goodOut = null;
        if (!hasReject)
        {
            qualNote = "불량(reject) 데이터 없음 — 품질 산출 불가. /api/oee/production 입력 또는 OeeSignals 불량신호 설정 필요.";
        }
        else if (prodTotal <= 0)
        {
            qualNote = "생산수 0 — 품질 산출 불가.";
        }
        else
        {
            rejectOut = prodReject;
            goodOut = Math.Max(0, prodTotal - prodReject);
            quality = Math.Clamp((double)goodOut.Value / prodTotal, 0.0, 1.0);
            qualNote = null;
        }

        // ── OEE (A × P × Q) ──
        double? oee = null;
        string? oeeNote = null;
        if (availability is double a && performance is double p && quality is double q)
        {
            oee = a * p * q;
        }
        else
        {
            var missing = new List<string>();
            if (availability is null) missing.Add("가용성");
            if (performance is null) missing.Add("성능");
            if (quality is null) missing.Add("품질");
            oeeNote = $"구성요소 미산출({string.Join(", ", missing)}) — OEE 산출 불가.";
        }

        // ── MTBF / MTTR ──
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
            mtbf = runtimeMs / failureCount;
            mtbfNote = "Σ가동시간(달력근사) / 고장건수.";
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
            Availability: availability,
            AvailabilityNote: availNote,
            Performance: performance,
            PerformanceNote: perfNote,
            Quality: quality,
            QualityNote: qualNote,
            Oee: oee,
            OeeNote: oeeNote,
            FailureCount: failureCount,
            Mtbf: mtbf,
            MtbfNote: mtbfNote,
            Mttr: mttr,
            MttrNote: mttrNote);
    }

    private int? ResolveIdealCycleTimeMs(string? flowName)
    {
        if (string.IsNullOrWhiteSpace(flowName)) return null;
        var ov = _settings.GetFlowCycleOverride(flowName);
        return ov?.IdealCycleTimeMs;
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
public record ProductionRequest(DateTime? Date, string Flow, string? Shift, int Reject);
public record ShiftExceptionRequest(string? Flow, DateTime? StartAt, DateTime? EndAt, string Kind, string? Note);
public record IdealCycleRequest(string Flow, int? IdealCycleTimeMs);
