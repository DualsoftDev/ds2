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
    private readonly OeeCommHealthService _commHealth;
    private readonly OeeNonProdPatternService _nonProdPattern;
    private readonly ILogger<OeeController> _logger;

    public OeeController(
        IOeeRepository repo,
        AppSettingsService settings,
        DsProjectService project,
        IDatabasePathResolver pathResolver,
        OeeCtStatsService ctStats,
        OeeAutoShiftInferenceService shiftInfer,
        OeeCommHealthService commHealth,
        OeeNonProdPatternService nonProdPattern,
        ILogger<OeeController> logger)
    {
        _repo = repo;
        _settings = settings;
        _project = project;
        _pathResolver = pathResolver;
        _ctStats = ctStats;
        _shiftInfer = shiftInfer;
        _commHealth = commHealth;
        _nonProdPattern = nonProdPattern;
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

    // ── GET /api/oee/teep?from&to&flow ────────────────────────────────────
    // 생산효율(TEEP) = 가동(Σ실측CT) ÷ 캘린더(전체, 비생산 포함). 단순 가동형(P·Q 미반영 — 설비효율 탭이 A·P·Q 담당).
    // 가동/정지/비생산은 같은 사이클 집계(ComputeCycleAggregateAsync)에서 조달 — 그 호출이 자동 10× 비생산을 로그로도 materialize.
    // 라인(flow 미지정)은 flow별 합산이라 캘린더=기간×임계보유flow수 로 스케일(병렬 flow 과다계상 방지).
    [HttpGet("teep")]
    public async Task<ActionResult<OeeTeepDto>> Teep(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? flow,
        CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var flowName = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();

        var periodMs = (toUtc - fromUtc).TotalMilliseconds;
        if (periodMs < 0) periodMs = 0;

        var thresholds = await ResolveCtThresholdsAsync();
        var (plannedWindows, _, applyLongStop) = ResolvePlannedWindows();
        var agg = await ComputeCycleAggregateAsync(flowName, fromUtc, toUtc, thresholds, plannedWindows, applyLongStop, ct);

        // 캘린더 배수 = 대상 flow 수(가동/정지/비생산이 flow별 합산이므로 분모도 배수).
        int flowCount = flowName is not null
            ? (thresholds.TryGetValue(flowName, out var t) && t.AvgMs > 0 ? 1 : 0)
            : thresholds.Count(kv => kv.Value.AvgMs > 0);

        double calendarMs = periodMs * flowCount;
        double runningMs = agg.NormalCtMs;
        double downMs = agg.IdleCtMs;
        double nonProdMs = agg.PlannedCtMs;
        // 미계측(§3.4)은 flow별 합산 축에 맞춰 flowCount 배수 — 정지/비생산과 같은 단위로 잔여에서 분리.
        double teepUnmeasuredMs = agg.UnmeasuredMs * flowCount;
        double residualMs = Math.Max(0, calendarMs - runningMs - downMs - nonProdMs - teepUnmeasuredMs);

        var teep = OeeMath.ComputeTeep(runningMs, calendarMs);
        var util = OeeMath.ComputeUtilization(calendarMs, nonProdMs);
        var teepNote = flowCount == 0
            ? "표준 CT(14일 평균) 보유 flow 없음 — TEEP 산출 불가."
            : "가동(Σ실측CT) ÷ 캘린더(전체, 비생산 포함) — 달력 대비 진짜 가동.";

        return new OeeTeepDto(
            FlowName: flowName,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            FlowCount: flowCount,
            CalendarMs: calendarMs,
            RunningMs: runningMs,
            DownMs: downMs,
            NonProdMs: nonProdMs,
            ResidualMs: residualMs,
            Teep: teep,
            TeepNote: teepNote,
            Utilization: util,
            CtThresholdMs: agg.CtThresholdMs,
            UnmeasuredMs: teepUnmeasuredMs);
    }

    // ── GET /api/oee/teep/matrix?from&to&flow ─────────────────────────────
    // 생산효율 매트릭스(P6 L0) — flow × 시간버킷별 TEEP·OEE. /uptime-teep 차트 데이터(라인=3D 아이소, 설비=2D 막대).
    // 버킷·granularity 는 daily 와 동일 규칙(≤2일=시간, 초과=일, 로컬 달력 클립). 셀 산출은 OeeMath.BuildTeepMatrixCells.
    // flow별 ComputeCycleAggregateAsync 1회씩(ranking 의 flow별 BuildSummary 와 같은 허용 패턴) — 버킷별 재집계 아님.
    [HttpGet("teep/matrix")]
    public async Task<ActionResult<OeeTeepMatrixDto>> TeepMatrix(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? flow,
        CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var flowName = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();

        var thresholds = await ResolveCtThresholdsAsync();
        var (plannedWindows, _, applyLongStop) = ResolvePlannedWindows();

        // 대상 flow = 임계(14일 평균) 보유 flow — /api/oee/teep 의 FlowCount 와 동일 모집단. 이름순 고정(차트 축 안정).
        var targetFlows = (flowName is not null
                ? thresholds.Where(kv => kv.Key == flowName)
                : thresholds.AsEnumerable())
            .Where(kv => kv.Value.AvgMs > 0)
            .Select(kv => kv.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        // 시간버킷(로컬 달력, [from,to] 클립) — daily 의 AddSlot 루프와 동일 경계.
        var hourly = (toUtc - fromUtc).TotalDays <= 2.0;
        var buckets = new List<OeeTeepMatrixBucketDto>();
        if (hourly)
        {
            var cur = fromUtc.ToLocalTime();
            cur = new DateTime(cur.Year, cur.Month, cur.Day, cur.Hour, 0, 0, DateTimeKind.Local);
            while (cur.ToUniversalTime() < toUtc)
            {
                var next = cur.AddHours(1);
                buckets.Add(new OeeTeepMatrixBucketDto(
                    cur.ToString("yyyy-MM-dd HH:00"), Max(fromUtc, cur.ToUniversalTime()), Min(toUtc, next.ToUniversalTime())));
                cur = next;
            }
        }
        else
        {
            var curLocal = fromUtc.ToLocalTime().Date;
            while (curLocal.ToUniversalTime() < toUtc)
            {
                var nextLocal = curLocal.AddDays(1);
                buckets.Add(new OeeTeepMatrixBucketDto(
                    curLocal.ToString("yyyy-MM-dd"), Max(fromUtc, curLocal.ToUniversalTime()), Min(toUtc, nextLocal.ToUniversalTime())));
                curLocal = nextLocal;
            }
        }

        // Q = 수기 전역값(기본 100% 가정) — 버킷별 불량 데이터가 없어 매트릭스는 전역 Q 단일 적용(P6 원칙: Q 수기).
        var manualQualityPct = _settings.LoadSettings().OeeManual.QualityPercent;
        var quality = manualQualityPct is double qp ? Math.Clamp(qp / 100.0, 0.0, 1.0) : 1.0;
        var qualitySource = manualQualityPct is not null ? "manual" : "assumed";

        var bucketRanges = buckets.Select(b => (ToMs(b.StartUtc), ToMs(b.EndUtc))).ToList();
        var flowRows = new List<OeeTeepMatrixFlowDto>(targetFlows.Count);
        foreach (var f in targetFlows)
        {
            var agg = await ComputeCycleAggregateAsync(f, fromUtc, toUtc, thresholds, plannedWindows, applyLongStop, ct,
                collectNormalCycles: true);
            var thr = thresholds[f].AvgMs;
            var cells = OeeMath.BuildTeepMatrixCells(
                bucketRanges,
                agg.NormalCycles ?? [],
                agg.IdleIntervals ?? [],
                agg.NonProdIntervals ?? [],
                thr, quality);
            flowRows.Add(new OeeTeepMatrixFlowDto(f, thr, cells));
        }

        return new OeeTeepMatrixDto(fromUtc, toUtc, hourly ? "hour" : "day", quality, qualitySource, buckets, flowRows);
    }

    // ── GET /api/oee/output-count?from&to ─────────────────────────────────
    /// <summary>
    /// 대시보드 "가동횟수" 카드 값. flow별 사이클수를 그냥 합치면 직렬 공정에서 과다 계상되므로:
    ///   · 출력 Flow 지정(OeeManual.OutputFlows) 있음 → 그 flow들의 사이클수 합(산출량).
    ///   · 지정 없음(자동) → 전체 사이클수 합 ÷ 기간 내 가동한 flow 수(정수 평균) — 직렬이면 완제품 수, 병렬이면 라인 평균에 근사.
    /// </summary>
    [HttpGet("output-count")]
    public async Task<ActionResult<OutputCountDto>> OutputCount(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var selected = _settings.LoadSettings().OeeManual.OutputFlows ?? [];

        if (selected.Count > 0)
        {
            int sum = 0;
            foreach (var f in selected)
                sum += await CountFlowHistoryAsync(f, fromUtc, toUtc);
            return new OutputCountDto(sum, "designated");
        }

        var total = await CountFlowHistoryAsync(null, fromUtc, toUtc);
        var flowCount = await CountDistinctActiveFlowsAsync(fromUtc, toUtc);
        var avg = flowCount > 0
            ? (int)Math.Round((double)total / flowCount, MidpointRounding.AwayFromZero)
            : 0;
        return new OutputCountDto(avg, "auto");
    }

    // ── GET /api/oee/output-flows ─────────────────────────────────────────
    /// <summary>출력 Flow 지정 모달용 — 후보 flow 목록(프로젝트 flow ∪ 히스토리 flowName) + 현재 지정.</summary>
    [HttpGet("output-flows")]
    public async Task<ActionResult<OutputFlowStateDto>> GetOutputFlows()
    {
        var selected = _settings.LoadSettings().OeeManual.OutputFlows ?? [];
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // "*_Flow" 접미사 = device 레벨/합성 플로우(실제 제조 Flow 아님) → 후보에서 제외.
            // DspDatabaseServiceAdapter/FlowMetricsService/DatabaseLifecycleService 와 동일 규약.
            // (이 필터가 없으면 1IN_CYL_Flow 등 히스토리에 안 남는 플로우가 모달에 떠 선택해도 가동횟수 0)
            if (_project.IsLoaded)
                foreach (var f in _project.GetAllFlows())
                    if (!string.IsNullOrWhiteSpace(f.Name)
                        && !f.Name.EndsWith("_Flow", StringComparison.OrdinalIgnoreCase))
                        names.Add(f.Name.Trim());
        }
        catch (Exception ex) { _logger.LogDebug(ex, "[OEE] output-flows: project flow 수집 실패 (non-critical)"); }

        foreach (var n in await GetDistinctFlowNamesAsync())
            names.Add(n);

        return new OutputFlowStateDto([.. names], [.. selected]);
    }

    // ── POST /api/oee/output-flows ────────────────────────────────────────
    /// <summary>출력 Flow 지정 저장(전체 교체). 빈 목록 = 자동(평균) 모드.</summary>
    [HttpPost("output-flows")]
    public ActionResult<SaveResultDto> SaveOutputFlows([FromBody] OutputFlowSaveDto? req)
    {
        try
        {
            var flows = (req?.Flows ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _settings.Update(m => m.OeeManual.OutputFlows = flows);
            return new SaveResultDto(true,
                flows.Count == 0 ? "자동(평균) 모드로 설정되었습니다." : $"출력 Flow {flows.Count}개가 지정되었습니다.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OEE] SaveOutputFlows failed");
            return new SaveResultDto(false, $"출력 Flow 저장 실패: {ex.Message}");
        }
    }

    // ── POST /api/oee/export-excel ────────────────────────────────────────
    /// <summary>
    /// Excel(.xlsx) 내보내기 — WYSIWYG. 서버가 OEE 를 다시 계산하지 않고, 클라이언트(uptime-oee.html)가
    /// 화면에 그린 현재 상태(<see cref="OeeExcelModel"/>: 종합 지표 + 가용성 분해 + 정지 구성 + 설비별 순위
    /// + 정지 이벤트 로그 + 일자별 추이 차트 캔버스 캡처)를 그대로 받아 <see cref="OeeExcelExporter.BuildOeeExcel"/> 로 렌더.
    /// 파일명 = OEE_&lt;title&gt;_&lt;yyyyMMdd_HHmmss&gt;.xlsx. antiforgery 미적용 평범한 POST.
    /// </summary>
    [HttpPost("export-excel")]
    public IActionResult ExportExcel([FromBody] OeeExcelModel req)
    {
        if (req is null)
            return BadRequest("model required");

        var bytes = OeeExcelExporter.BuildOeeExcel(req);
        var title = string.IsNullOrWhiteSpace(req.Title) ? "라인전체" : SanitizeFileName(req.Title);
        var fileName = $"OEE_{title}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        return File(bytes, OeeExcelExporter.XlsxMimeType, fileName);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
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
    // isFailure(MTBF 고장) = 설비고장(reasonCode='equipment_fault')만 — OeeMath.IsFailureReason 단일 규칙(2026-06-15).
    // category(계획/계획외)는 가용성·일자스택용으로 별도 유지(자재대기=계획외지만 고장 아님).
    [HttpPost("downtime/{id:long}/classify")]
    public async Task<ActionResult<object>> Classify(long id, [FromBody] ClassifyRequest req, CancellationToken ct)
    {
        var category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim().ToLowerInvariant();
        var reasonCode = string.IsNullOrWhiteSpace(req.ReasonCode) ? null : req.ReasonCode.Trim();
        var isFailure = OeeMath.IsFailureReason(reasonCode); // MTBF 고장 = 설비고장(equipment_fault)만. category(계획외)와 분리.

        var n = await _repo.ClassifyDowntimeAsync(id, reasonCode, category, isFailure, classifySource: "manual", ct);
        if (n == 0) return NotFound(new { error = "downtime event not found", id });
        return new { ok = true, id, reasonCode, category, isFailure };
    }

    // ── POST /api/oee/downtime/{id}/set-fault  {isFault} ─────────────────
    // 단순 고장/유지보수 2-상태 토글. isFault=true→equipment_fault(isFailure=1), false→planned_maint(isFailure=0).
    [HttpPost("downtime/{id:long}/set-fault")]
    public async Task<ActionResult<object>> SetFault(long id, [FromBody] SetFaultRequest req, CancellationToken ct)
    {
        var (reasonCode, category, isFailure) = req.IsFault
            ? ("equipment_fault", "unplanned", true)
            : ("planned_maint", "planned", false);
        var n = await _repo.ClassifyDowntimeAsync(id, reasonCode, category, isFailure, classifySource: "manual", ct);
        if (n == 0) return NotFound(new { error = "downtime event not found", id });
        return new { ok = true, id, isFault = req.IsFault };
    }

    // ── POST /api/oee/downtime/bulk-set-fault  {ids, isFault} ────────────
    [HttpPost("downtime/bulk-set-fault")]
    public async Task<ActionResult<object>> BulkSetFault([FromBody] BulkSetFaultRequest req, CancellationToken ct)
    {
        if (req.Ids == null || req.Ids.Count == 0) return BadRequest(new { error = "ids is required" });
        if (req.Ids.Count > 500) return BadRequest(new { error = "too many ids (max 500)" });
        var (reasonCode, category, isFailure) = req.IsFault
            ? ("equipment_fault", "unplanned", true)
            : ("planned_maint", "planned", false);
        var n = await _repo.BulkClassifyDowntimeAsync(req.Ids, reasonCode, category, isFailure, classifySource: "manual", ct);
        return new { ok = true, count = n, isFault = req.IsFault };
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
        var isFailure = OeeMath.IsFailureReason(reasonCode); // MTBF 고장 = 설비고장(equipment_fault)만. category(계획외)와 분리.

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

    // ── POST /api/oee/quality  {qualityPercent?} ─────────────────────────
    // 사용자가 직접 설정하는 "전반 품질(양품률) %" (전역). null/빈값이면 해제 → 불량 입력 기반/100% 가정 폴백.
    // 불량 카운트(production)와 별개의 단순 오버라이드 — 설정 시 라인·전 설비 OEE 품질에 그대로 적용(QualitySource="manual").
    [HttpPost("quality")]
    public ActionResult<object> SetManualQuality([FromBody] ManualQualityRequest req)
    {
        _settings.SaveManualQualityPercent(req?.QualityPercent);
        var saved = _settings.LoadSettings().OeeManual.QualityPercent;
        return new { ok = true, qualityPercent = saved };
    }

    // ── GET /api/oee/planned-stops ────────────────────────────────────────
    // 비생산 시간대 상태(라인 전체). auto=true → 10×(14일 평균 CT) 장시간 무변화 정지를 비생산으로 자동 분류(시각대 윈도 없음).
    // auto=false → 사용자 수동 시간대(windows)만 적용. source = auto / manual / none. ctMultiplier = 자동판정 배수(10).
    [HttpGet("planned-stops")]
    public ActionResult<PlannedStopsDto> GetPlannedStops()
    {
        var oee = _settings.LoadSettings().OeeManual;
        var auto = oee.PlannedStopsAutoEffective;
        var manual = oee.PlannedStops ?? new List<PlannedStopWindow>();
        var windows = manual.Select(w => new PlannedStopWindowDto(w.StartMinutes, w.EndMinutes, w.Label)).ToList();
        var source = auto ? "auto" : (windows.Count > 0 ? "manual" : "none");
        return new PlannedStopsDto(source, windows, auto, (int)OeeMath.NonProductionCtMultiplier);
    }

    // ── GET /api/oee/planned-stops/auto-pattern ──────────────────────────────
    // 자동 비생산 14일 시간대 패턴 — 일별 샘플 투표제 학습(doc/22 §3.5, OeeNonProdPatternService).
    // 참고 표시 전용(KPI 판정 미적용 — Phase 1 섀도). 라인 레벨(flow 미지정)은 24h 캐시(자동 전환 시 즉시 갱신).
    [HttpGet("planned-stops/auto-pattern")]
    public async Task<ActionResult<PlannedAutoPatternDto>> GetPlannedAutoPattern(
        [FromQuery] string? flow, CancellationToken ct)
    {
        var flowName = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();
        var thresholds = await ResolveCtThresholdsAsync();
        return await _nonProdPattern.GetOrComputeAsync(flowName, thresholds, forceRefresh: false, ct);
    }

    // ── GET /api/oee/planned-stops/actual?from&to&flow ───────────────────────
    // 조회기간에 "실제로 A 분모에서 제외된" 비생산 구간(NonProdIntervals)을 로컬 시:분(hour-of-day)으로
    // 접어 병합 windows 로 반환. 14일 평균 패턴(auto-pattern)과 달리 실제 적용분 — 시간별 추이의 남색(비생산 제외)과
    // 동일 소스. 단 24h 연표 표시는 기간 마지막 날(로컬)만 접는다(여러 날 union 은 전체 채움으로 퇴화 — 본문 참조).
    // DaysAnalyzed=0 = "실제 적용분" 신호.
    [HttpGet("planned-stops/actual")]
    public async Task<ActionResult<PlannedAutoPatternDto>> GetActualNonProduction(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? flow, CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var flowName = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();

        var thresholds = await ResolveCtThresholdsAsync();
        var (plannedWindows, _, applyLongStop) = ResolvePlannedWindows();
        // 자동 비생산은 로그(SSOT, 감지시점 임계 스냅샷)에서, 수동 비생산 시간대는 설정에서 조달해 병합.
        // ComputeCycleAggregateAsync 호출이 이번 기간 자동 감지를 로그에 materialize(UPSERT)하므로 로그가 최신.
        var agg = await ComputeCycleAggregateAsync(flowName, fromUtc, toUtc, thresholds, plannedWindows, applyLongStop, ct);
        var merged = new List<(double S, double E)>();
        merged.AddRange(await _repo.GetNonProdIntervalsFromLogAsync(fromUtc, toUtc, flowName, ct)); // 자동(10×) — 로그
        merged.AddRange(ExpandPlannedIntervalsMs(plannedWindows, fromUtc, toUtc));                  // 수동 비생산 시간대 — 설정
        // 로그가 비면(자동 OFF·기록 실패 등) 방금 계산한 값으로 폴백해 표시 공백 방지.
        List<(double S, double E)> intervals = merged.Count > 0
            ? Intervals.Union(merged)
            : (agg.NonProdIntervals ?? new List<(double S, double E)>());
        // 미계측(수신 공백, §3.4) — 데이터로는 비생산과 분리하되(별도 필드·학습 §3.5 차집합·A 별도 제외),
        // 화면 표시는 비생산에 합친다(사용자 결정 2026-07-04): 사용자 눈에는 "제외된 시간" 하나로 보이고,
        // 14일 이동평균 학습과 KPI 카빙에는 절대 안 들어간다. displayIv = 비생산 ∪ 미계측.
        var unmeasuredIv = agg.UnmeasuredIntervals ?? new List<(double S, double E)>();
        if (unmeasuredIv.Count > 0)
            intervals = Intervals.Subtract(intervals, unmeasuredIv);   // 순수 비생산(데이터)
        var displayIv = unmeasuredIv.Count > 0
            ? Intervals.Union(intervals.Concat(unmeasuredIv))
            : intervals;

        // NonProdIntervals(UTC epoch ms) → 로컬 시:분(minute-of-day) 커버리지.
        // 여러 날 union 접기는 주말 정지 등 ≥24h 구간 하나로 1440분 전체가 덮여 무의미(전체 채움 버그)
        // → 24h 연표는 '하루 일과' 뷰이므로 기간 마지막 날(로컬)로 클립해 접는다.
        //   오늘로 끝나는 기간(오늘/7일/30일 등)이면 '오늘' 조회와 동일 화면.
        var lastDayProbe = toUtc > fromUtc ? toUtc.AddSeconds(-1) : toUtc; // to=자정 정각이면 전날이 마지막 날
        var dayStartUtc = DateTime.SpecifyKind(lastDayProbe.ToLocalTime().Date, DateTimeKind.Local).ToUniversalTime();
        if (dayStartUtc < fromUtc) dayStartUtc = fromUtc;
        double clipS = ToMs(dayStartUtc), clipE = ToMs(toUtc);

        static List<PlannedStopWindowDto> FoldToDay(IEnumerable<(double S, double E)> ivs, double clipS, double clipE)
            => OeeMath.FoldIntervalsToMinuteOfDay(ivs, clipS, clipE, OeeMath.LocalMinuteOfDay);

        var windows = FoldToDay(displayIv, clipS, clipE);              // 표시 = 비생산 ∪ 미계측(합쳐 보임)
        var unmeasuredWindows = FoldToDay(unmeasuredIv, clipS, clipE); // 미계측(§3.4) — 데이터 보존(진단·후속 소비자용)

        // 날짜별 접기 — TEEP "날짜별 비생산 패턴" 행(오늘=1행, 7일=7행 …). 각 날을 그 날의 로컬 자정
        // 경계로 클립해 독립 접기하므로 union 접기의 ≥24h 전체 채움 퇴화가 없다(PlannedStopDayDto 주석).
        // custom 초장기 범위 가드 — 최근 MaxPatternDays 일만(행 폭주 방지), 잘리면 DaysClipped 로 정직 표기.
        const int MaxPatternDays = 92;
        var firstDayLocal = fromUtc.ToLocalTime().Date;
        var lastDayLocal = lastDayProbe.ToLocalTime().Date;
        var daysClipped = (lastDayLocal - firstDayLocal).Days + 1 > MaxPatternDays;
        if (daysClipped) firstDayLocal = lastDayLocal.AddDays(-(MaxPatternDays - 1));
        var days = new List<PlannedStopDayDto>();
        for (var day = firstDayLocal; day <= lastDayLocal; day = day.AddDays(1))
        {
            var dS = Math.Max(ToMs(DateTime.SpecifyKind(day, DateTimeKind.Local).ToUniversalTime()), ToMs(fromUtc));
            var dE = Math.Min(ToMs(DateTime.SpecifyKind(day.AddDays(1), DateTimeKind.Local).ToUniversalTime()), ToMs(toUtc));
            if (dE <= dS) continue;
            days.Add(new PlannedStopDayDto(day, FoldToDay(displayIv, dS, dE), FoldToDay(unmeasuredIv, dS, dE)));
        }

        // 현재 비생산 상태 — 조회범위가 실시간(현재 포함)이고 지금 이 순간이 비생산 raw 구간에 속하는가.
        // 진행 중 정지는 구간 끝이 now 근처까지 이어지므로 [S, E) 에 now(1분 여유) 포함 여부로 판정.
        var nowUtc = DateTime.UtcNow;
        var isLive = toUtc >= nowUtc.AddMinutes(-5);
        var probeMs = ToMs(nowUtc);
        // 표시 정책: 지금이 미계측이어도 배지는 '비생산 중'으로 — displayIv(비생산 ∪ 미계측) 기준 판정.
        var currentlyNonProd = isLive && displayIv.Any(iv => iv.S <= probeMs && iv.E >= probeMs - 60000);
        var currentlyUnmeasured = isLive && unmeasuredIv.Any(iv => iv.S <= probeMs && iv.E >= probeMs - 60000); // 데이터 보존

        return new PlannedAutoPatternDto(windows, fromUtc.ToLocalTime(), toUtc.ToLocalTime(), 0, currentlyNonProd,
            UnmeasuredWindows: unmeasuredWindows, CurrentlyUnmeasured: currentlyUnmeasured,
            Days: days, DaysClipped: daysClipped);
    }

    // ── PUT /api/oee/planned-stops  {windows:[{startMinutes,endMinutes,label?}]} ──
    // 사용자가 비생산 시간대를 직접 설정(수동 적용) → 자동 계산 OFF(요청 사양). 빈 배열도 수동(=비생산 시간대 없음).
    [HttpPut("planned-stops")]
    public ActionResult<PlannedStopsDto> SetPlannedStops([FromBody] PlannedStopsRequest? req)
    {
        var windows = (req?.Windows ?? new List<PlannedStopWindowDto>())
            .Select(w => new PlannedStopWindow { StartMinutes = w.StartMinutes, EndMinutes = w.EndMinutes, Label = w.Label })
            .ToList();
        _settings.SavePlannedStops(windows); // 수동 적용 = 자동 계산 OFF
        return GetPlannedStops();
    }

    // ── POST /api/oee/planned-stops/auto  {enabled:bool} ──────────────────
    // 비생산 자동 계산 on/off. on = 10×(14일 평균 CT) 장시간 무변화 정지 자동 비생산 분류. off = 수동 시간대만 적용.
    // 수동 시간대(PlannedStops)는 보존 — 토글만으로 자동↔수동 자유 전환.
    // on 전환 시 패턴을 즉시 계산·캐싱해 uptime 타임라인이 바로 표시되게 한다.
    [HttpPost("planned-stops/auto")]
    public async Task<ActionResult<PlannedStopsDto>> SetPlannedStopsAuto(
        [FromBody] PlannedStopsAutoRequest? req, CancellationToken ct)
    {
        var enabled = req?.Enabled ?? true;
        _settings.SavePlannedStopsAuto(enabled);
        if (enabled)
            await _nonProdPattern.GetOrComputeAsync(null, await ResolveCtThresholdsAsync(), forceRefresh: true, ct);
        return GetPlannedStops();
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

        // CT이상치(14일 평균)는 조회기간과 무관 → 1회 산출해 flow별 BuildSummary 에 공유(중복 풀스캔 방지).
        var thresholds = await ResolveCtThresholdsAsync();

        var result = new List<OeeRankingDto>(byFlow.Count);
        foreach (var (flowName, downtimeMs, count) in byFlow)
        {
            var s = await BuildSummaryAsync(flowName, fromUtc, toUtc, ct, thresholds);
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

        // ── Quality ── 사용자 직접 설정(전반 품질) 우선 ▸ 불량 입력(measured) ▸ 100% 가정(assumed). §12
        var manualQualityPct = _settings.LoadSettings().OeeManual.QualityPercent;
        var (quality, qualNote, qualitySource, rejectOut, goodOut) =
            OeeMath.ResolveQuality(manualQualityPct, totalCount, prodReject, hasReject);

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

    private async Task<OeeSummaryDto> BuildSummaryAsync(
        string? flowName, DateTime fromUtc, DateTime toUtc, CancellationToken ct,
        IReadOnlyDictionary<string, (double AvgMs, double P10Ms, int Sample)>? ctThresholds = null)
    {
        var periodMs = (toUtc - fromUtc).TotalMilliseconds;
        if (periodMs < 0) periodMs = 0;

        var (downtimeMs, downtimeCount) = await _repo.GetDowntimeAggregateAsync(fromUtc, toUtc, flowName, ct);

        // totalCount 자동: dspFlowHistory row count (기간 내, flow 지정 시 그 flow).
        int? totalCount = await CountFlowHistoryAsync(flowName, fromUtc, toUtc);

        // 생산/품질 (로컬일 버킷). 입력 불량(manual 또는 plc 불량신호) 합만 소비 — 품질 분모는 기간 사이클수(§12).
        var (_, _, prodReject, hasReject) =
            await _repo.QueryProductionAsync(fromUtc.ToLocalTime(), toUtc.ToLocalTime(), flowName, ct);

        // ── 사이클기반 집계 (doc/22): CT이상치(14일 평균) → 비가동 판정(MT>thr / 미완료 CT폭주 / 무사이클 dedup)
        //    → Σ실측CT·Σ비가동CT·N·onset/repair. 시간기반 폴백 체인은 표본 부족 시에만 사용(보존). ──
        var thresholds = ctThresholds ?? await ResolveCtThresholdsAsync();
        // 비생산 시간대에 든 비가동은 비생산으로 분류해 A 분모서 제외(표준 OEE). 자동(10× 장시간정지)/수동(시각대 윈도).
        var (plannedWindows, plannedSource, applyLongStop) = ResolvePlannedWindows();
        // 유지보수(isFailure=0 계열 = 계획정비 kind0 + 기타 kind2) 이벤트 구간 → 비가동 ΣCT 고장/유지보수 분리.
        // '정지 구성' 도넛의 2-상태(isFailure)와 동일 기준 — 가용성 바 3분할이 도넛·추이와 같은 언어를 쓴다.
        var evIntervals = await _repo.GetDowntimeIntervalsAsync(fromUtc, toUtc, flowName, ct);
        var maintIv = evIntervals
            .Where(x => x.Kind is 0 or 2 && x.EndMs > x.StartMs)
            .Select(x => ((double)x.StartMs, (double)x.EndMs, x.FlowName))
            .ToList();
        var agg = await ComputeCycleAggregateAsync(flowName, fromUtc, toUtc, thresholds, plannedWindows, applyLongStop, ct,
            maintIntervals: maintIv);

        // ── Availability — 사이클기반 1차(source='cycle'), 표본 부족 시 시간기반 폴백 체인 보존(doc/22 §7) ──
        double? availability; string? availNote; string? availabilitySource; double runtimeMs;
        var (cycleA, cycleANote) = OeeMath.ComputeCycleAvailability(agg.NormalCtMs, agg.IdleCtMs);
        if (agg.HasThreshold && cycleA is not null)
        {
            availability = cycleA;
            availNote = cycleANote;
            availabilitySource = "cycle";
            runtimeMs = agg.NormalCtMs; // 가동시간 ≈ Σ실측CT (사이클기반)
        }
        else
        {
            var av = await ResolveAvailabilityAsync(flowName, fromUtc, toUtc, downtimeMs, periodMs, ct);
            availability = av.Availability;
            availNote = (av.Note ?? "") + " (사이클 표본 부족 — 시간기반 폴백).";
            availabilitySource = av.Source;
            runtimeMs = av.RuntimeMs;
        }

        // ── Performance — 사이클기반 (N × CT이상치) / Σ실측CT, min 1.0 (doc/22 §4) ──
        var (performance, perfNote) = OeeMath.ComputeCyclePerformance(
            agg.NormalCount, agg.CtThresholdMs, agg.NormalCtMs);

        // ── Quality ── 사용자 직접 설정(전반 품질) 우선 ▸ 불량 입력(measured) ▸ 100% 가정(assumed). §12
        var manualQualityPct = _settings.LoadSettings().OeeManual.QualityPercent;
        var (quality, qualNote, qualitySource, rejectOut, goodOut) =
            OeeMath.ResolveQuality(manualQualityPct, totalCount, prodReject, hasReject);

        // ── OEE (A × P × Q) — 순수 함수 단일 소스 ──
        var (oee, oeeNote) = OeeMath.ComputeOee(availability, performance, quality, qualitySource);

        // ── MTBF / MTTR — 사이클 onset 기반 (doc/22 §5). 무고장=null+배지 ──
        var sortedOnsets = agg.OnsetsMs.OrderBy(x => x).ToList();
        var (mtbf, mtbfNote, _) = OeeMath.ComputeMtbf2(sortedOnsets);
        var (mttr, mttrNote) = OeeMath.ComputeMttr(agg.RepairMsList);
        var failureCount = agg.DowntimeEventCount;

        // idealCT (표시용 — 기존 추천/자동기입 설정값). 사이클 모델의 1차 표준은 CtThresholdMs(14일 평균).
        var (idealCT, idealCtSource) = ResolveIdealCycle(flowName);

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
            MttrNote: mttrNote,
            NormalCtMs: agg.NormalCtMs,
            IdleCtMs: agg.IdleCtMs,
            NormalCycleCount: agg.HasThreshold ? agg.NormalCount : (int?)null,
            CtThresholdMs: agg.CtThresholdMs,
            PlannedDownMs: agg.PlannedCtMs,
            PlannedStopSource: agg.HasThreshold ? plannedSource : null,
            CtSampleCount: agg.HasThreshold ? agg.CtSampleMin : (int?)null,
            CtSampleLow: agg.HasThreshold && agg.CtSampleMin < OeeCtStatsService.ConfidentMinCleanCycles,
            IdleMaintCtMs: agg.IdleMaintCtMs,
            IdleCalendarMs: agg.IdleCalendarMs,
            CycleFlowCount: agg.FlowCount,
            UnmeasuredMs: agg.UnmeasuredMs);
    }

    /// <summary>
    /// 비생산 판정 모드 결정(doc/22 §3.3). 자동 계산(PlannedStopsAuto) on → 10× 장시간정지 규칙(source="auto",
    /// 시각대 윈도 없음 — 지속시간만으로 판정), off → 사용자 수동 시각대(source="manual", windows), 둘 다 없으면 "none".
    /// 반환 Windows = 시각대 윈도(수동일 때만 비어있지 않음). ApplyLongStop = 자동(10×) 규칙 적용 여부.
    /// </summary>
    private (List<(int StartMin, int EndMin)> Windows, string Source, bool ApplyLongStop) ResolvePlannedWindows()
    {
        var oee = _settings.LoadSettings().OeeManual;
        if (oee.PlannedStopsAutoEffective)
            return (new List<(int, int)>(), "auto", true);

        var manual = oee.PlannedStops;
        if (manual is { Count: > 0 })
            return (manual.Select(w => (w.StartMinutes, w.EndMinutes)).ToList(), "manual", false);

        return (new List<(int, int)>(), "none", false);
    }

    /// <summary>
    /// 사이클 <b>시작 시각</b>(로컬)의 분(minute-of-day)이 비생산 시간대에 드는지 판정하는 SQL 불리언 식.
    /// recordedAt(UTC·완료시각)에서 ct(ms)를 빼 시작 시각을 구한 뒤 localtime 으로 변환 — C# <see cref="IsPlannedTimeOfDay"/>
    /// (start = rec − cMs 의 ToLocalTime)와 동일 기준. 윈도 분 값은 자체 코드(0~1440 정수)라 인젝션 무관. 윈도 없으면 "0".
    /// </summary>
    private static string BuildNonProductionStartSql(IReadOnlyList<(int StartMin, int EndMin)> windows)
    {
        if (windows.Count == 0) return "0";
        const string startMin =
            "(CAST(strftime('%H', substr(recordedAt,1,19), 'localtime', (-ct/1000.0)||' seconds') AS INTEGER)*60"
            + " + CAST(strftime('%M', substr(recordedAt,1,19), 'localtime', (-ct/1000.0)||' seconds') AS INTEGER))";
        var clauses = windows.Select(w => $"({startMin} >= {w.StartMin} AND {startMin} < {w.EndMin})");
        return "(" + string.Join(" OR ", clauses) + ")";
    }

    /// <summary>UTC epoch ms 가 비생산 시간대(로컬 시각)에 드는지. 시간대 비면 항상 false.</summary>
    private static bool IsPlannedTimeOfDay(double recMs, IReadOnlyList<(int StartMin, int EndMin)> windows)
    {
        if (windows.Count == 0) return false;
        var local = _epochUtc.AddMilliseconds(recMs).ToLocalTime();
        var min = local.Hour * 60 + local.Minute;
        foreach (var w in windows)
            if (min >= w.StartMin && min < w.EndMin) return true;
        return false;
    }

    /// <summary>계획정지 시간대(반복 일일)를 조회기간 [from,to) 위 절대 UTC 구간(ms)으로 펼친다(무사이클 dedup 교집합용).</summary>
    private static List<(double S, double E)> ExpandPlannedIntervalsMs(
        IReadOnlyList<(int StartMin, int EndMin)> windows, DateTime fromUtc, DateTime toUtc)
    {
        var res = new List<(double S, double E)>();
        if (windows.Count == 0) return res;
        // 로컬 날짜 범위를 하루씩 돌며 각 window 를 절대 UTC 구간으로 변환.
        var localFrom = fromUtc.ToLocalTime().Date.AddDays(-1);
        var localToEnd = toUtc.ToLocalTime().Date.AddDays(1);
        for (var d = localFrom; d <= localToEnd; d = d.AddDays(1))
        {
            foreach (var w in windows)
            {
                var sLocal = DateTime.SpecifyKind(d.AddMinutes(w.StartMin), DateTimeKind.Local);
                var eLocal = DateTime.SpecifyKind(d.AddMinutes(w.EndMin), DateTimeKind.Local);
                var s = ToMs(sLocal.ToUniversalTime());
                var e = ToMs(eLocal.ToUniversalTime());
                if (e > s) res.Add((s, e));
            }
        }
        return res;
    }

    /// <summary>
    /// 유효 CT이상치(ms) = flow별 14일 평균(<see cref="OeeCtStatsService.ComputeCtThresholdAsync"/>) 위에
    /// <b>수동 표준CT 오버라이드를 덮어쓴다</b>(doc/22 §2·§7 — 사용자 권위). 자동기입(source='auto'/'auto-median')은
    /// 14일 평균을 그대로 두고, 엔지니어가 직접 입력한 표준CT(그 외 source)만 임계로 승격한다.
    /// </summary>
    private async Task<Dictionary<string, (double AvgMs, double P10Ms, int Sample)>> ResolveCtThresholdsAsync()
    {
        // 오늘(로컬 기준) 사이클을 기준 윈도우에서 제외 + 가중 감쇠(halfLife=7d): 오래된 사이클일수록 표준CT 산출에
        // 더 큰 가중치 → 최근 변화가 기준에 미치는 자기참조순환 영향 감소.
        var thr = await _ctStats.ComputeCtThresholdAsync(
            excludeUntilUtc: DateTime.Today.ToUniversalTime(),
            decayHalfLifeDays: 7.0);
        // 오늘 첫 가동 폴백: 오늘 이전 데이터가 없는 flow 는 기준 윈도우가 비어 P 산출 불가.
        // 당일 데이터만으로 잠정 기준을 산출해 "클린샘플 부족" 대신 오늘 실측값 기반 기준을 제공.
        var thrToday = await _ctStats.ComputeCtThresholdAsync(); // 14d 전체 포함, 가중치 없음
        foreach (var (flow, val) in thrToday)
            thr.TryAdd(flow, val);
        var settings = _settings.LoadSettings();
        foreach (var ov in settings.FlowCycle.Overrides)
        {
            if (string.IsNullOrWhiteSpace(ov.FlowName)) continue;
            var src = ov.IdealCycleTimeSource;
            var isManual = ov.IdealCycleTimeMs is > 0 && src != "auto" && src != "auto-median";
            // 수동 표준CT 는 권위적 단일값 → avg/p10 양 기준 모두 이 값(성능 기준 토글이 무력화 = 의도).
            if (isManual) thr[ov.FlowName] = (ov.IdealCycleTimeMs!.Value, ov.IdealCycleTimeMs!.Value, int.MaxValue);
        }
        return thr;
    }

    // ── 사이클기반 집계 (doc/22 §3·§4·§5) ──────────────────────────────────

    private readonly record struct CycleAgg(
        double NormalCtMs, double IdleCtMs, int NormalCount, int DowntimeEventCount,
        double? CtThresholdMs, List<double> OnsetsMs, List<double> RepairMsList, bool HasThreshold,
        double PlannedCtMs, int CtSampleMin = 0, List<(double S, double E)>? NonProdIntervals = null,
        List<(double S, double E)>? RunIntervals = null, double IdleMaintCtMs = 0,
        double IdleCalendarMs = 0, int FlowCount = 0,
        List<(double S, double E)>? IdleIntervals = null,
        List<(double StartMs, double CtMs)>? NormalCycles = null,
        double UnmeasuredMs = 0,                                    // 미계측(수신 공백, §3.4) 달력시간 — 기간 클립·Union
        List<(double S, double E)>? UnmeasuredIntervals = null);    // 미계측 구간(daily/actual 표시·차집합 공용)

    private sealed class CycleAggRow { public long NormalCt { get; set; } public long NormalCount { get; set; } public long NonProdNormalCt { get; set; } }
    private sealed class DtCycleRaw { public string? RecordedAt { get; set; } public long? Ct { get; set; } public long? Mt { get; set; } }
    private sealed class NocycleRaw { public string? StartAt { get; set; } public string? EndAt { get; set; } }

    /// <summary>
    /// 기간 내 사이클을 CT이상치(14일 평균, flow별)로 정상/비가동 분류해 Σ실측CT·Σ비가동CT·N 을 집계하고,
    /// 비가동 사이클·무사이클 정지에서 onset/repair(MTBF/MTTR 원천)를 도출한다 (doc/22 §3·§5).
    /// 무사이클 정지(③)는 비가동 사이클 구간과 안 겹치는 부분만 가산(dedup — 이중계상 방지, §3.1).
    /// 라인(flowName=null)은 임계 보유 flow 전체를 합산하고 성능은 Σ(N_f×thr_f)/Σ실측CT 가중.
    /// 임계 보유 flow 0 → HasThreshold=false → 상위가 시간기반 폴백.
    /// </summary>
    private async Task<CycleAgg> ComputeCycleAggregateAsync(
        string? flowName, DateTime fromUtc, DateTime toUtc,
        IReadOnlyDictionary<string, (double AvgMs, double P10Ms, int Sample)> thresholds,
        IReadOnlyList<(int StartMin, int EndMin)> plannedWindows, bool applyLongStop, CancellationToken ct,
        bool collectRunIntervals = false,
        IReadOnlyList<(double S, double E, string? Flow)>? maintIntervals = null,
        bool collectNormalCycles = false)
    {
        var onsets = new List<double>();
        var repairs = new List<double>();
        // 미계측(수신 공백, doc/22 §3.4) — 통신 헬스 심박이 보증하지 못한 구간. 가동/비가동/비생산 어디에도
        // 넣지 않는다(모르는 시간을 아는 척 금지). 심박 epoch 이전 기간은 빈 목록(소급 주장 없음) = 기존 동작.
        // trusted=false(조회 실패 폴백)면 카빙 없이 계산은 진행하되 감지 로그 materialize 는 스킵(영구 오염 방지).
        var (unmeasured, unmeasuredTrusted) = await _commHealth.TryGetUnmeasuredIntervalsAsync(fromUtc, toUtc, ct);
        var unmeasuredMs = unmeasured.Sum(u => u.E - u.S);
        var empty = new CycleAgg(0, 0, 0, 0, null, onsets, repairs, false, 0,
            UnmeasuredMs: unmeasuredMs, UnmeasuredIntervals: unmeasured);

        List<string> targetFlows;
        if (!string.IsNullOrWhiteSpace(flowName))
            targetFlows = thresholds.ContainsKey(flowName) ? new List<string> { flowName } : new List<string>();
        else
            targetFlows = thresholds.Keys.ToList();
        if (targetFlows.Count == 0) return empty;

        var dbPath = _pathResolver.GetSharedDbPath();
        if (!System.IO.File.Exists(dbPath)) return empty;

        var fromStr = fromUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var toStr = toUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        double normalCtMs = 0, idleCtMs = 0, plannedCtMs = 0, perfNumerator = 0;
        int normalCount = 0, dtEventCount = 0;
        int ctSampleMin = int.MaxValue;   // 임계 보유 flow 중 최소 클린샘플 — 신뢰선(<5) 미만이면 '샘플 부족' 표시
        bool hasThreshold = false;
        double thrSum = 0; int thrCount = 0; // 자동(10×) 무사이클 갭 판정용 라인 대표 임계(flow별 thr 평균)
        var cycleIdleIntervals = new List<(double S, double E)>(); // 모든 비가동 사이클(계획+미계획) — nocycle dedup 용
        // 비생산(제외) 구간 수집 — 일자별 추이 차트의 '비생산(제외)' 세그먼트용(A 분모 밖 시간 시각화). 수동 윈도는 전 구간,
        //  자동(10×)은 판정된 유휴 사이클/무사이클 갭 구간을 모은다. 마지막에 Union 으로 병합(이중계상 방지).
        var nonProdIntervals = new List<(double S, double E)>(ExpandPlannedIntervalsMs(plannedWindows, fromUtc, toUtc));
        // 자동(10×) 감지분만 로그로 영속화(수동 윈도는 설정이 정본이라 제외). 마지막에 배치 UPSERT(멱등).
        var nonProdDetections = new List<OeeNonProdDetectionLog>();
        // 실측 가동(정상 사이클 [start, recordedAt)) 구간 — 일자별 추이의 '가동 하한'(collectRunIntervals 시에만 수집).
        // 라인 레벨 무사이클 잔여(스턱 flow open 이벤트)가 타 flow 생산 중 시간까지 비생산으로 덮는 과대포함에서
        // 실제 생산시간을 보호하는 용도. flow 병렬이라 ΣCT > 달력시간 가능 → 반환 전 Union(달력 커버리지)으로 접는다.
        var runIntervals = collectRunIntervals ? new List<(double S, double E)>() : null;
        // 정상 사이클 (시작, CT) 목록 — 매트릭스(teep/matrix)가 시간버킷에 귀속시키는 원본. NormalCt(SQL) 분류와
        // 동일하게 비생산 시간대 시작분은 제외해, 버킷 합계 ≈ KPI 가동(NormalCtMs)이 되게 한다.
        var normalCycles = collectNormalCycles ? new List<(double StartMs, double CtMs)>() : null;
        // 유지보수(isFailure=0 계열) 이벤트 구간 — 비가동 ΣCT 를 고장/유지보수로 분리(가용성 바 3분할, 도넛과 동일 2-상태).
        // flow별 Union(사이클 비가동 귀속) + 전체 Union(라인 무사이클 잔여 귀속). 미전달 시 분리 생략(전부 고장 취급).
        Dictionary<string, List<(double S, double E)>>? maintByFlow = null;
        List<(double S, double E)>? maintAll = null;
        if (maintIntervals is { Count: > 0 })
        {
            maintByFlow = maintIntervals.Where(x => !string.IsNullOrEmpty(x.Flow) && x.E > x.S)
                .GroupBy(x => x.Flow!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Intervals.Union(g.Select(x => (x.S, x.E)).ToList()), StringComparer.Ordinal);
            maintAll = Intervals.Union(maintIntervals.Where(x => x.E > x.S).Select(x => (x.S, x.E)).ToList());
        }
        double idleMaintCtMs = 0;
        // 미계획 비가동으로 '계상된' 구간(달력) — ΣCT(설비 합산)와 별개로 벽시계 환산치(Union 후 Total)를 제공.
        // "비가동 13d(설비시간)가 달력에선 얼마인가"를 UI 가 병기해 ΣCT↔달력 오독을 방지(P0 개선).
        var idleCalIntervals = new List<(double S, double E)>();
        static double OverlapMs(List<(double S, double E)>? iv, double s, double e)
        {
            if (iv is null) return 0;
            double sum = 0;
            foreach (var (a, b) in iv) { var o = Math.Min(b, e) - Math.Max(a, s); if (o > 0) sum += o; }
            return sum;
        }

        // dspFlowHistory 비가동 조건 — ① MT>thr ② complete=null(mt null) AND CT>thr (IsIdle 무관, §3.2).
        const string dtCond = "ct > 0 AND ((mt IS NOT NULL AND mt > @Thr) OR (mt IS NULL AND ct > @Thr))";
        // 비생산 시간대(옛 '계획정지') — 이 시간대에 시작한 사이클은 정상/비가동 가리지 않고 전부 OEE 에서 제외(분모 밖).
        // 사이클 시작 시각(로컬)의 분(minute-of-day)이 윈도에 들면 true. 윈도 없으면 "0"(항상 false).
        var nonProdStartSql = BuildNonProductionStartSql(plannedWindows);

        try
        {
            await using var conn = new SqliteConnection(
                $"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync(ct);
            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (exists == 0) return empty;

            foreach (var f in targetFlows)
            {
                var thr = thresholds[f].AvgMs;          // 비가동 판정·가용성 임계 = 항상 14일 평균(불변)
                if (thr <= 0) continue;
                hasThreshold = true;
                thrSum += thr; thrCount++;              // 무사이클 갭 10× 판정용 라인 대표 임계 누적
                ctSampleMin = Math.Min(ctSampleMin, thresholds[f].Sample); // 수동 오버라이드=int.MaxValue(완전 신뢰)라 안 깎임

                var p = new DynamicParameters();
                p.Add("From", fromStr); p.Add("To", toStr); p.Add("Flow", f); p.Add("Thr", thr);

                // 정상 사이클(Σ실측CT·N)은 SQL 집계 — 비생산 시간대 시작분은 분모서 제외(PlannedNormalCt 로 따로 합산).
                // 비가동 CT 는 row 단위로 비생산/생산 분리(아래).
                var aggRow = await conn.QueryFirstOrDefaultAsync<CycleAggRow>($@"
                    SELECT
                      COALESCE(SUM(CASE WHEN ct>0 AND NOT ({dtCond}) AND NOT ({nonProdStartSql}) THEN ct ELSE 0 END),0)  AS NormalCt,
                      COALESCE(SUM(CASE WHEN ct>0 AND NOT ({dtCond}) AND NOT ({nonProdStartSql}) THEN 1  ELSE 0 END),0)  AS NormalCount,
                      COALESCE(SUM(CASE WHEN ct>0 AND NOT ({dtCond}) AND ({nonProdStartSql}) THEN ct ELSE 0 END),0)  AS NonProdNormalCt
                    FROM dspFlowHistory
                    WHERE recordedAt >= @From AND recordedAt < @To AND flowName = @Flow", p);
                if (aggRow is not null)
                {
                    normalCtMs += aggRow.NormalCt;
                    normalCount += (int)aggRow.NormalCount;
                    perfNumerator += aggRow.NormalCount * thr;       // 성능 표준 = 14일 평균(불변) — 분모(Σ실측CT)·분류와 동일 소스
                    plannedCtMs += aggRow.NonProdNormalCt;           // 비생산 시간대 정상 CT — A 분모서 제외(정상/비가동 모두 필터)
                }

                // 실측 가동 구간(정상 사이클) 수집 — 비생산 시간대 시작분도 포함(물리적으로 돌던 시간이므로 하한 대상).
                if (runIntervals is not null || normalCycles is not null)
                {
                    var runRows = await conn.QueryAsync<DtCycleRaw>($@"
                        SELECT recordedAt AS RecordedAt, ct AS Ct, mt AS Mt
                        FROM dspFlowHistory
                        WHERE recordedAt >= @From AND recordedAt < @To AND flowName = @Flow
                          AND ct > 0 AND NOT ({dtCond})", p);
                    foreach (var r in runRows)
                        if (r.Ct is long rc && rc > 0 && ParseUtcMs(r.RecordedAt) is double rrec)
                        {
                            runIntervals?.Add((rrec - rc, rrec));
                            // NormalCt(SQL)와 동일 기준 — 비생산 시간대 시작 사이클은 KPI 가동에서 빠지므로 여기서도 제외.
                            if (normalCycles is not null && !IsPlannedTimeOfDay(rrec - rc, plannedWindows))
                                normalCycles.Add((rrec - rc, rc));
                        }
                }

                // 비가동 사이클 row → 비생산 시간대면 비생산(분모 제외), 아니면 생산 미계획정지(Σ비가동CT + onset/repair).
                // 모든 비가동 구간([start, recordedAt))은 nocycle dedup 용으로 모은다(비생산 여부 무관).
                var rows = await conn.QueryAsync<DtCycleRaw>($@"
                    SELECT recordedAt AS RecordedAt, ct AS Ct, mt AS Mt
                    FROM dspFlowHistory
                    WHERE recordedAt >= @From AND recordedAt < @To AND flowName = @Flow AND {dtCond}
                    ORDER BY recordedAt", p);
                foreach (var r in rows)
                {
                    if (r.Ct is not long ctMsL || ctMsL <= 0) continue;
                    double cMs = ctMsL;
                    var recMs = ParseUtcMs(r.RecordedAt);
                    if (recMs is not double rec) continue;
                    double startMs = rec - cMs;
                    cycleIdleIntervals.Add((startMs, rec));
                    // 미계측 겹침 카빙(§3.4) — 수신 공백과 겹친 부분은 어떤 상태도 주장하지 않는다. 비생산 10× 판정도
                    // 계측된 잔여 길이로만 한다(보수 — 모르는 시간이 임계를 채워 정지를 비생산으로 승격시키지 않게).
                    var rowSegs = unmeasured.Count > 0
                        ? Intervals.Subtract(new List<(double S, double E)> { (startMs, rec) }, unmeasured)
                        : new List<(double S, double E)> { (startMs, rec) };
                    var measuredMs = Intervals.Total(rowSegs);
                    if (measuredMs <= 0) continue;                              // 전 구간 미계측 — 비가동/비생산/onset 전부 미계상
                    if (IsPlannedTimeOfDay(startMs, plannedWindows))
                    {
                        plannedCtMs += measuredMs;                              // 비생산 시간대 — A 분모서 제외, MTBF/MTTR 미반영
                        continue;
                    }
                    // 자동(10×, doc/22 §3.3): 미완료(=변화 없음) 멈춤이 ≥10×평균CT 면 비생산(분모 밖).
                    // 완료된 느린 사이클(mt 있음=움직였음)은 대상 아님 — 다운타임 유지. 고장신호와 무관(순수 CT).
                    if (applyLongStop && r.Mt is null && OeeMath.IsLongStopNonProduction(measuredMs, thr))
                    {
                        plannedCtMs += measuredMs;
                        foreach (var seg in rowSegs)
                        {
                            nonProdIntervals.Add(seg);              // 자동(10×) 유휴 사이클 → 비생산(제외) 시각화
                            nonProdDetections.Add(NewNonProdDetection(f, seg.S, seg.E, thr, "idle-cycle"));
                        }
                        continue;
                    }
                    idleCtMs += measuredMs;
                    idleCalIntervals.AddRange(rowSegs);
                    // 유지보수 이벤트(같은 flow)와 겹친 만큼 유지보수로 귀속(잔여 = 고장) — 가용성 바 3분할용.
                    // 계측 잔여(rowSegs) 기준으로 겹침 계산 — 미계측 안에만 있는 유지보수가 잔여 idle 로 오귀속되지 않게.
                    idleMaintCtMs += Math.Min(measuredMs,
                        rowSegs.Sum(seg => OverlapMs(maintByFlow?.GetValueOrDefault(f), seg.S, seg.E)));
                    onsets.Add(startMs + thr);                                  // 고장 onset = 사이클 시작 + CT이상치
                    // going 회복: complete(MT) 또는 CT 종료. 미계측 카빙된 행은 계측 잔여 기준(공백이 MTTR 을 부풀리지 않게).
                    double repair = r.Mt is long mtL && measuredMs >= cMs ? (mtL - thr) : (measuredMs - thr);
                    if (repair >= 0) repairs.Add(repair);
                    dtEventCount++;
                }
            }

            // ── 무사이클 정지 합산 (dedup) — 비가동 사이클과 안 겹치는 부분만, 다시 계획/미계획 분리 (doc/22 §3.1) ──
            // 자동(10×) 갭 판정 임계 = flow별 thr 평균(flow 지정 시 그 flow thr, 라인=대표 평균). 무사이클 갭은 전부 '변화 없음'.
            var avgThr = thrCount > 0 ? thrSum / thrCount : 0;
            var nocycle = await GetNocycleIntervalsMsAsync(flowName, fromUtc, toUtc);
            if (nocycle.Count > 0)
            {
                var plannedIntervals = ExpandPlannedIntervalsMs(plannedWindows, fromUtc, toUtc);
                foreach (var seg in Intervals.Subtract(nocycle, cycleIdleIntervals))
                {
                    var segList = new List<(double S, double E)> { seg };
                    if (plannedIntervals.Count > 0)
                    {
                        plannedCtMs += Intervals.Total(Intervals.Intersect(segList, plannedIntervals));
                        segList = Intervals.Subtract(segList, plannedIntervals); // 미계획 잔여만
                    }
                    foreach (var u in segList)
                    {
                        // 미계측 카빙(§3.4) — 판정·적립은 계측 잔여(uSegs)로만 하되, 이벤트(onset/repair/건수)는
                        // 원 구간당 1건 유지: 수신 공백이 정지 하나를 N건 고장으로 쪼개 MTBF/MTTR 을 왜곡하지 않게.
                        var uSegs = unmeasured.Count > 0
                            ? Intervals.Subtract(new List<(double S, double E)> { u }, unmeasured)
                            : new List<(double S, double E)> { u };
                        var len = Intervals.Total(uSegs);
                        if (len <= 0) continue;                     // 전 구간 미계측 — 비가동/비생산/onset 전부 미계상
                        // 자동(10×, doc/22 §3.3): 무변화 갭이 ≥10×평균CT 면 비생산(분모 밖) — A 안 깎고 MTBF/MTTR 미반영.
                        if (applyLongStop && OeeMath.IsLongStopNonProduction(len, avgThr))
                        {
                            plannedCtMs += len;
                            foreach (var us in uSegs)
                            {
                                nonProdIntervals.Add(us);           // 자동(10×) 무사이클 갭 → 비생산(제외) 시각화
                                nonProdDetections.Add(NewNonProdDetection(flowName, us.S, us.E, avgThr, "nocycle-gap"));
                            }
                            continue;
                        }
                        idleCtMs += len;
                        idleCalIntervals.AddRange(uSegs);
                        // 무사이클 잔여의 유지보수 귀속 — flow 조회는 그 flow 의 유지보수 구간, 라인은 전체 Union.
                        // 계측 잔여 세그먼트 기준(미계측 안 유지보수의 오귀속 방지).
                        idleMaintCtMs += Math.Min(len, uSegs.Sum(us => OverlapMs(
                            flowName is not null ? maintByFlow?.GetValueOrDefault(flowName) : maintAll, us.S, us.E)));
                        onsets.Add(uSegs[0].S);                     // onset = 첫 계측 세그먼트 시작(공백 안 onset 금지)
                        repairs.Add(len);
                        dtEventCount++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] cycle aggregate failed");
            return empty;
        }

        if (!hasThreshold) return empty;

        // 표시용 CT이상치. 사이클이 있으면 생산수 가중평균, 0이어도 임계는 존재하므로 flow별 임계 평균을
        // 노출한다(이전엔 라인 합산+사이클 0 → null 이라 '표준CT 미설정/클린샘플 0'으로 오인 표시됐음).
        double? displayThr;
        if (normalCount > 0)
            displayThr = perfNumerator / normalCount;
        else
        {
            var thrVals = targetFlows.Select(f => thresholds[f].AvgMs).Where(v => v > 0).ToList();
            displayThr = thrVals.Count > 0 ? thrVals.Average() : (double?)null;
        }
        // 자동 비생산 감지를 로그에 materialize(멱등 UPSERT) — TEEP(생산효율)이 장기간에도 일관·저비용으로 읽는 SSOT.
        // best-effort: 실패해도 OEE 조회는 그대로 성공. 감지 0건이면 skip.
        // 미계측 조회가 실패(비신뢰)한 요청에선 스킵 — 카빙 안 된 블랙아웃 스팬이 비생산으로 영구 기록되는 오염 방지
        // (UPSERT 키가 onset 이라 이후 정상 요청이 자가 치유하지 못함).
        if (applyLongStop && unmeasuredTrusted && nonProdDetections.Count > 0)
        {
            try { await _repo.UpsertNonProdDetectionsAsync(nonProdDetections, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "[OEE] 비생산 감지 로그 materialize 실패"); }
        }

        var idleCalUnion = Intervals.Union(idleCalIntervals);
        return new CycleAgg(normalCtMs, idleCtMs, normalCount, dtEventCount, displayThr, onsets, repairs, true, plannedCtMs,
            ctSampleMin == int.MaxValue ? 0 : ctSampleMin, Intervals.Union(nonProdIntervals),
            runIntervals is not null ? Intervals.Union(runIntervals) : null,
            Math.Min(idleMaintCtMs, idleCtMs),
            Intervals.Total(idleCalUnion), thrCount,
            IdleIntervals: idleCalUnion, NormalCycles: normalCycles,
            UnmeasuredMs: unmeasuredMs, UnmeasuredIntervals: unmeasured);
    }

    // 자동(10×) 비생산 감지 1건 → 로그 엔티티. onset/clear = UTC epoch ms, thrMs = 감지 당시 14일 평균 CT 스냅샷.
    private static OeeNonProdDetectionLog NewNonProdDetection(string? flow, double onsetMs, double clearMs, double thrMs, string reason)
        => new()
        {
            FlowName = flow,
            OnsetAt = DateTimeOffset.FromUnixTimeMilliseconds((long)onsetMs).UtcDateTime,
            ClearAt = DateTimeOffset.FromUnixTimeMilliseconds((long)clearMs).UtcDateTime,
            DurationMs = (long)(clearMs - onsetMs),
            DetectionSource = "auto-10xct",
            DetectionReason = reason,
            CtThresholdMs = thrMs,
            CtMultiplier = OeeMath.NonProductionCtMultiplier,
        };

    /// <summary>
    /// oee.db 의 무사이클 정지(detectSource='nocycle') 구간을 ms 로 (기간 클립). 사이클기반 dedup 소스(§3 ③).
    /// open(endAt NULL) 은 toUtc 로 캡. 시각은 ISO8601 UTC 텍스트 — <see cref="ParseUtcMs"/> 로 파싱.
    /// </summary>
    private async Task<List<(double S, double E)>> GetNocycleIntervalsMsAsync(string? flowName, DateTime fromUtc, DateTime toUtc)
    {
        var result = new List<(double S, double E)>();
        var sharedDb = _pathResolver.GetSharedDbPath();
        var dir = System.IO.Path.GetDirectoryName(sharedDb);
        if (string.IsNullOrEmpty(dir)) return result;
        var oeeDb = System.IO.Path.Combine(dir, "oee.db");
        if (!System.IO.File.Exists(oeeDb)) return result;

        var fromMs = ToMs(fromUtc);
        var toMs = ToMs(toUtc);
        try
        {
            await using var conn = new SqliteConnection($"Data Source={oeeDb};Mode=ReadOnly;Default Timeout=20");
            await conn.OpenAsync();
            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='oeeDowntimeEvent'");
            if (exists == 0) return result;

            var flowClause = string.IsNullOrWhiteSpace(flowName) ? "" : " AND flowName = @Flow ";
            var rows = await conn.QueryAsync<NocycleRaw>(
                $@"SELECT startAt AS StartAt, endAt AS EndAt FROM oeeDowntimeEvent
                   WHERE detectSource = 'nocycle' {flowClause}",
                new { Flow = flowName?.Trim() });
            foreach (var r in rows)
            {
                var s = ParseUtcMs(r.StartAt);
                if (s is not double sMs) continue;
                // open 이벤트는 min(now, 기간 끝)으로 캡 — 기간 끝이 미래(오늘 23:59 등)면 아직 오지 않은
                // 시간까지 정지/비생산으로 칠해지는 것을 막는다(진행 중 정지는 '지금'까지가 사실).
                var eMs = ParseUtcMs(r.EndAt) ?? Math.Min(toMs, ToMs(DateTime.UtcNow));
                var clipS = Math.Max(sMs, fromMs);
                var clipE = Math.Min(eMs, toMs);
                if (clipE > clipS) result.Add((clipS, clipE));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] nocycle intervals query failed");
        }
        return result;
    }

    /// <summary>ISO8601/SQLite DATETIME 문자열(UTC, Z 유무 무관)을 epoch ms 로. 파싱 실패 시 null.</summary>
    private static double? ParseUtcMs(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
            return (dt - _epochUtc).TotalMilliseconds;
        return null;
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

    /// <summary>기간 내 <b>가동한(비가동 제외) flow 수</b>(distinct flowName). 자동(평균) 모드의 분모.</summary>
    private async Task<int> CountDistinctActiveFlowsAsync(DateTime fromUtc, DateTime toUtc)
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
            p.Add("From", fromUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            p.Add("To", toUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            return await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(DISTINCT flowName) FROM dspFlowHistory
                WHERE COALESCE(IsIdle,0) = 0
                  AND recordedAt >= @From AND recordedAt < @To", p);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] dspFlowHistory distinct-flow count failed");
            return 0;
        }
    }

    /// <summary>히스토리에 존재하는 모든 flowName(중복 제거). 출력 Flow 지정 모달의 후보 목록 보강용.</summary>
    private async Task<List<string>> GetDistinctFlowNamesAsync()
    {
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!System.IO.File.Exists(dbPath)) return [];
        try
        {
            await using var conn = new SqliteConnection(
                $"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync();

            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (exists == 0) return [];

            var rows = await conn.QueryAsync<string>(
                "SELECT DISTINCT flowName FROM dspFlowHistory WHERE flowName IS NOT NULL AND flowName <> ''");
            return [.. rows];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] dspFlowHistory distinct flowName query failed");
            return [];
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

        // ① 정지 이벤트 raw 구간(kind별). 시작일 몰빵 대신 각 이벤트를 실제 겹친 슬롯에 overlap 분배(다일·장시간 정지 정확).
        //    자동(nocycle 미분류) vs 비자동(고장비트/사용자 분류) 분리 — 비생산 카빙 우선순위가 다름(아래 BuildDailySlot 참조).
        var intervals = await _repo.GetDowntimeIntervalsAsync(fromUtc, toUtc, flowName, ct);
        var delibKind = new[] { new List<(long S, long E)>(), new List<(long S, long E)>(), new List<(long S, long E)>(), new List<(long S, long E)>() };
        var autoKind = new[] { new List<(long S, long E)>(), new List<(long S, long E)>(), new List<(long S, long E)>(), new List<(long S, long E)>() };
        foreach (var (s, e, kind, isAuto, _) in intervals)
            if (e > s && kind is >= 0 and <= 3) (isAuto ? autoKind : delibKind)[kind].Add((s, e));

        // ② 비생산(제외) 구간 — 사이클 모델(10×CT/수동 시각대, A 분모 밖). 가동(초록)에서 카빙해 별도 세그먼트로 표시.
        //    daily 는 저빈도라 사이클 집계 1회 추가 호출 허용. Union 된 구간이라 자체 겹침 없음(슬롯 overlap 이중계상 없음).
        var thresholds = await ResolveCtThresholdsAsync();
        var (plannedWindows, _, applyLongStop) = ResolvePlannedWindows();
        var agg = await ComputeCycleAggregateAsync(flowName, fromUtc, toUtc, thresholds, plannedWindows, applyLongStop, ct,
            collectRunIntervals: true);
        // 비생산 소스 = 타임라인(planned-stops/actual)과 동일하게 일원화: 로그(자동 10×, SSOT) ∪ 수동 윈도, 비면 방금 계산분 폴백.
        // agg.NonProdIntervals 단독은 라이브 재집계라 로그에 있던 감지분(임계 스냅샷·타 뷰 감지)을 놓쳐 추이가 그 구간을
        // 고장(빨강)으로 새게 만들었음 → 타임라인과 소스를 맞춰 KPI(정본)·타임라인·추이 3화면 일치.
        var nonProdMerged = new List<(double S, double E)>();
        nonProdMerged.AddRange(await _repo.GetNonProdIntervalsFromLogAsync(fromUtc, toUtc, flowName, ct)); // 자동(10×) — 로그
        nonProdMerged.AddRange(ExpandPlannedIntervalsMs(plannedWindows, fromUtc, toUtc));                  // 수동 비생산 시간대 — 설정
        var nonProdSource = nonProdMerged.Count > 0
            ? Intervals.Union(nonProdMerged)
            : (agg.NonProdIntervals ?? new List<(double S, double E)>());

        static List<(long S, long E)> SubtractIv(List<(long S, long E)> src, List<(double S, double E)> cut)
            => Intervals.Subtract(src.Select(x => ((double)x.S, (double)x.E)).ToList(), cut)
                .Select(x => ((long)x.S, (long)x.E)).Where(x => x.Item2 > x.Item1).ToList();

        // ②-b 미계측(수신 공백, §3.4) 최우선 — 정지 이벤트·비생산 모두에서 차집합. 수신이 끊긴 시간은 어떤
        //     상태도 주장하지 않고 회색(미계측)으로만 표시한다. KPI(agg)와 동일 소스라 3화면 일치.
        //     stale 감지 로그(수신 공백을 비생산으로 기록한 과거분)도 여기서 표시상 걸러진다(심박 보유 구간 한정).
        var unmeasuredIv = agg.UnmeasuredIntervals ?? new List<(double S, double E)>();
        if (unmeasuredIv.Count > 0)
        {
            nonProdSource = Intervals.Subtract(nonProdSource, unmeasuredIv);
            for (int k = 0; k < 4; k++)
            {
                delibKind[k] = SubtractIv(delibKind[k], unmeasuredIv);
                autoKind[k] = SubtractIv(autoKind[k], unmeasuredIv);
            }
        }
        var unmeasuredL = unmeasuredIv
            .Select(x => ((long)x.S, (long)x.E)).Where(x => x.Item2 > x.Item1).ToList();

        var nonProd = nonProdSource
            .Select(x => ((long)x.S, (long)x.E)).Where(x => x.Item2 > x.Item1).ToList();
        // 실측 가동(정상 사이클 Union) — 슬롯별 '가동 하한'. 정지/비생산 카빙이 실제 생산시간을 침식하지 못하게 예약.
        var runIv = (agg.RunIntervals ?? new List<(double S, double E)>())
            .Select(x => ((long)x.S, (long)x.E)).Where(x => x.Item2 > x.Item1).ToList();

        // ③ 비생산 우선 — 모든 정지 이벤트 구간에서 비생산 구간을 차집합(구간 연산)으로 제거.
        //    사이클 모델(KPI A)이 비생산으로 분모서 제외한 시간은 사용자가 유지보수로 분류한 정지라도 추이에서
        //    비생산(숨김)으로 채워져 막대가 줄어든다 — 분류색(노랑/빨강)은 비생산이 아닌 잔여 정지에만 칠한다.
        //    (10×CT 규칙은 분류 무관이므로 이벤트 종류 구분 없이 일괄 차집합 — KPI·비생산 배지와 일치)
        var nonProdD = nonProdSource.Where(x => x.E > x.S).ToList();
        if (nonProdD.Count > 0)
        {
            for (int k = 0; k < 4; k++)
            {
                delibKind[k] = SubtractIv(delibKind[k], nonProdD);
                autoKind[k] = SubtractIv(autoKind[k], nonProdD);
            }
        }

        static long SumOverlap(List<(long S, long E)> segs, long slotS, long slotE)
        {
            long sum = 0;
            foreach (var (s, e) in segs) { var o = Math.Min(e, slotE) - Math.Max(s, slotS); if (o > 0) sum += o; }
            return sum;
        }

        // 전체 슬롯 목록 생성 (달력 기준) — 각 슬롯 [slotStart,slotEnd) 을 [from,to] 로 클립 후 overlap 합산.
        var slots = new List<OeeDailySlotDto>();
        void AddSlot(string label, DateTime slotStartUtc, DateTime slotEndUtc)
        {
            var sS = (long)ToMs(Max(fromUtc, slotStartUtc));
            var sE = (long)ToMs(Min(toUtc, slotEndUtc));
            var slotMs = Math.Max(0, sE - sS);
            var delib = new long[4];
            var auto = new long[4];
            for (int k = 0; k < 4; k++)
            {
                delib[k] = SumOverlap(delibKind[k], sS, sE);
                auto[k] = SumOverlap(autoKind[k], sS, sE);
            }
            slots.Add(BuildDailySlot(label, slotMs, delib, auto, SumOverlap(nonProd, sS, sE), SumOverlap(runIv, sS, sE),
                SumOverlap(unmeasuredL, sS, sE)));
        }
        if (hourly)
        {
            var cur = fromUtc.ToLocalTime();
            cur = new DateTime(cur.Year, cur.Month, cur.Day, cur.Hour, 0, 0, DateTimeKind.Local);
            while (cur.ToUniversalTime() < toUtc)
            {
                var next = cur.AddHours(1);
                AddSlot(cur.ToString("yyyy-MM-dd HH:00"), cur.ToUniversalTime(), next.ToUniversalTime());
                cur = next;
            }
        }
        else
        {
            var curLocal = fromUtc.ToLocalTime().Date;
            while (curLocal.ToUniversalTime() < toUtc)
            {
                var nextLocal = curLocal.AddDays(1);
                AddSlot(curLocal.ToString("yyyy-MM-dd"), curLocal.ToUniversalTime(), nextLocal.ToUniversalTime());
                curLocal = nextLocal;
            }
        }

        return new OeeDailyResponse(gran, slots);
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>
    /// 정지 분해를 슬롯 예산(slotMs − 가동하한) 안에 캡한다. 예산 소진 순서가 곧 우선순위(세그먼트 합 ≤ slotMs − runFloorMs).
    /// 가동 = slotMs − (failure+other+unclass+planned+nonprod) 은 클라이언트가 차감 → 항상 ≥ runFloorMs 보장.
    ///
    /// 가동하한(runFloorMs) = 슬롯과 겹친 실측 정상 사이클 시간(Union). 라인 레벨에선 한 flow 의 장기 무사이클 잔여가
    ///   타 flow 생산 중 시간까지 비생산/정지로 덮을 수 있어(과대포함), 실제 생산한 시간은 카빙 대상에서 먼저 제외한다.
    /// 우선순위(핵심): ⓪ 미계측(수신 공백, §3.4 — 모르는 시간은 어떤 상태도 주장 안 함) → ① 실제 기록된 정지
    ///   (비자동 = 고장비트/사용자 분류) → ② 비생산(제외) → ③ 자동(nocycle 미분류) 정지 잔여.
    /// 단, 호출부(Daily ③)가 모든 정지 이벤트 구간에서 비생산 구간을 미리 차집합으로 제거한다 — 사이클 모델(KPI A)이
    ///   비생산으로 분모서 제외한 시간은 사용자 분류(유지보수/고장)와 무관하게 추이에서도 비생산(숨김)으로 채워져
    ///   막대가 줄어들고, 분류색은 비생산이 아닌 잔여 정지에만 칠해진다(10×CT 규칙이 분류 무관인 것과 일치).
    /// delib/auto 배열 인덱스 = Kind: [0]=계획정비 [1]=고장 [2]=기타 [3]=미분류.
    /// </summary>
    private static OeeDailySlotDto BuildDailySlot(
        string label, long slotMs, long[] delib, long[] auto, long nonProdMs, long runFloorMs = 0,
        long unmeasuredMs = 0)
    {
        var budget = Math.Max(0, slotMs - Math.Max(0, runFloorMs));
        long Take(long v) { var t = Math.Min(Math.Max(0, v), Math.Max(0, budget)); budget -= t; return t; }

        // ⓪ 미계측(수신 공백, §3.4) — 최우선 예약: 모르는 시간은 어떤 정지/비생산도 주장하지 않는다.
        //    호출부가 정지/비생산 구간에서 이미 차집합했으므로 여기선 이중계상 없음(예산 캡만 공유).
        var unmeasured = Take(unmeasuredMs);

        // ① 실제 기록된 정지(비자동) 우선 — 진짜 정지는 비생산에 가려지면 안 됨.
        var failure = Take(delib[1]);
        var other = Take(delib[2]);
        var unclass = Take(delib[3]);
        var planned = Take(delib[0]);

        // ② 비생산(제외) 카빙 — 자동 nocycle 정지보다 먼저(같은 유휴를 비생산으로 흡수).
        var nonProd = Take(nonProdMs);

        // ③ 자동(nocycle) 정지 잔여 — 비생산에 흡수되지 않은 부분만 종류대로 표시.
        failure += Take(auto[1]);
        other += Take(auto[2]);
        unclass += Take(auto[3]);
        planned += Take(auto[0]);

        var unplanned = failure + other + unclass;
        return new OeeDailySlotDto(label, slotMs, unplanned, planned, failure, other, unclass, nonProd, unmeasured);
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
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime(),
    };
}

// ── 요청 DTO (camelCase JSON: reasonCode, category, endAt, idealCycleTimeMs ...) ──

public record ClassifyRequest(string? ReasonCode, string? Category);
public record CloseRequest(DateTime? EndAt);
public record BulkClassifyRequest(List<long> Ids, string? ReasonCode, string? Category);
public record BulkCloseRequest(List<long> Ids, DateTime? EndAt);
public record SetFaultRequest(bool IsFault);
public record BulkSetFaultRequest(List<long> Ids, bool IsFault);
public record ProductionRequest(DateTime? Date, string Flow, string? Shift, int Reject);
public record ManualQualityRequest(double? QualityPercent); // 전반 품질(양품률) % 직접 설정. null=해제.
public record PlannedStopsRequest(List<PlannedStopWindowDto>? Windows); // 비생산 시간대 수동 설정(수동 적용=자동 OFF). 빈/null=시간대 없음.
public record PlannedStopsAutoRequest(bool Enabled);                    // 비생산 자동 계산 on/off (10× 장시간정지 규칙).
public record ShiftExceptionRequest(string? Flow, DateTime? StartAt, DateTime? EndAt, string Kind, string? Note);
// Mode: "manual"=사용자 직접 입력(자동이 안 덮음, 값 동일해도 수동 잠금) / "auto"=자동 관리로 해제(수동값 비움→자동기입) / null=레거시(값 변경 시 수동).
public record IdealCycleRequest(string Flow, int? IdealCycleTimeMs, string? Mode = null);
public record IdealCycleBatchRequest(List<IdealCycleRequest> Items);

// 대시보드 "가동횟수" 카드 — 출력(생산) Flow 지정.
public record OutputCountDto(int Count, string Mode);          // Mode: "designated"(지정 합) | "auto"(flow 평균)
public record OutputFlowStateDto(List<string> Flows, List<string> Selected);
public record OutputFlowSaveDto(List<string>? Flows);

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
