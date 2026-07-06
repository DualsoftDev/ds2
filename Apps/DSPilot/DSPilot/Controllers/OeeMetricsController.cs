// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Infrastructure;
using DSPilot.Models.Oee;
using DSPilot.Repositories;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// OEE 지표 — summary, teep, ranking, shift-summary, ideal-cycle/*, plan-time, daily.
/// </summary>
[ApiController]
[Route("api/oee")]
public class OeeMetricsController : OeeControllerBase
{
    public OeeMetricsController(
        IOeeRepository repo,
        AppSettingsService settings,
        DsProjectService project,
        IDatabasePathResolver pathResolver,
        OeeCtStatsService ctStats,
        OeeAutoShiftInferenceService shiftInfer,
        OeeCommHealthService commHealth,
        OeeNonProdPatternService nonProdPattern,
        ILogger<OeeMetricsController> logger)
        : base(repo, settings, project, pathResolver, ctStats, shiftInfer, commHealth, nonProdPattern, logger) { }

    // ── GET /api/oee/summary?from&to&flow ─────────────────────────────────
    [HttpGet("summary")]
    public async Task<ActionResult<OeeSummaryDto>> Summary(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? flow,
        CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var flowName = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();
        return await BuildSummaryAsync(flowName, fromUtc, toUtc, ct);
    }

    // ── GET /api/oee/teep?from&to&flow ────────────────────────────────────
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
        var (plannedWindows, _, applyLongStop) = await ResolvePlannedWindowsAsync(thresholds, ct);
        var agg = await ComputeCycleAggregateAsync(flowName, fromUtc, toUtc, thresholds, plannedWindows, applyLongStop, ct);

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
        var (plannedWindows, _, applyLongStop) = await ResolvePlannedWindowsAsync(thresholds, ct);

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

        // 계획 기준선 — 가용성 폴백 체인(shift/auto/calendar)의 계획생산시간 ÷ 기간 = 캘린더 대비 계획가동 비율.
        // 라인(flowName=null)/설비 동일 소스라 3D·요약 KPI 와 일관. plan-time 엔드포인트와 같은 ResolveAvailabilityAsync.
        var periodMs = Math.Max(0, (toUtc - fromUtc).TotalMilliseconds);
        var (downtimeMs, _) = await _repo.GetDowntimeAggregateAsync(fromUtc, toUtc, flowName, ct);
        var avr = await ResolveAvailabilityAsync(flowName, fromUtc, toUtc, downtimeMs, periodMs, ct);
        double? plannedFraction = periodMs > 0 ? Math.Clamp(avr.PlannedMs / periodMs, 0, 1) : null;

        return new OeeTeepMatrixDto(fromUtc, toUtc, hourly ? "hour" : "day", quality, qualitySource, buckets, flowRows,
            plannedFraction, avr.Source);
    }

    // ── GET /api/oee/ranking?from&to ──────────────────────────────────────
    [HttpGet("ranking")]
    public async Task<ActionResult<List<OeeRankingDto>>> Ranking(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var byFlow = await _repo.GetDowntimeByFlowAsync(fromUtc, toUtc, ct);
        var thresholds = await ResolveCtThresholdsAsync();

        var result = new List<OeeRankingDto>(byFlow.Count);
        foreach (var (flowName, downtimeMs, count) in byFlow)
        {
            var s = await BuildSummaryAsync(flowName, fromUtc, toUtc, ct, thresholds);
            result.Add(new OeeRankingDto(
                flowName, downtimeMs, count, s.TotalCount,
                s.Availability, s.Performance, s.Quality, s.Oee));
        }
        return result
            .OrderByDescending(r => r.Oee ?? -1)
            .ThenByDescending(r => r.DowntimeMs)
            .ToList();
    }

    // ── GET /api/oee/shift-summary?from&to&flow ───────────────────────────
    [HttpGet("shift-summary")]
    public async Task<ActionResult<OeeShiftSummaryDto>> ShiftSummary(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? flow,
        CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var flowName = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();
        return await BuildShiftSummaryAsync(flowName, fromUtc, toUtc, ct);
    }

    // ── POST /api/oee/ideal-cycle ──────────────────────────────────────────
    [HttpPost("ideal-cycle")]
    public ActionResult<object> SetIdealCycle([FromBody] IdealCycleRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Flow))
            return BadRequest(new { error = "flow is required" });
        _settings.SaveFlowIdealCycleTime(req.Flow.Trim(), req.IdealCycleTimeMs);
        return new { ok = true, flow = req.Flow.Trim(), idealCycleTimeMs = req.IdealCycleTimeMs is > 0 ? req.IdealCycleTimeMs : null };
    }

    // ── POST /api/oee/ideal-cycle/batch ───────────────────────────────────
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
    [HttpGet("ideal-cycle/table")]
    public async Task<ActionResult<List<IdealCycleRowDto>>> IdealCycleTable(
        [FromQuery] double percentile = 10, [FromQuery] int sampleLimit = 2000)
    {
        var p = Math.Clamp(percentile, 0, 100);
        var limit = sampleLimit <= 0 ? 2000 : Math.Min(sampleLimit, 100000);
        var stats = await _ctStats.ComputeAsync(limit, p);

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

    // ── GET /api/oee/plan-time?from&to&flow ───────────────────────────────
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

        // 벽시계 단일모델(2026-07-06): 추이 = 요약 KPI 와 동일 SSOT(ComputeCycleAggregateAsync 벽시계 구간).
        //   슬롯별 [가동 / 고장 / 유지보수 / 비생산 / 미계측] flow 합산 — 세로 합 = 정산, 정지부 = 도넛.
        //   가동·유지보수는 flow별 구간 연결(concat) SumOverlap(=flow 합), 비생산·미계측은 전역이라 ×flow수.
        var thresholds = await ResolveCtThresholdsAsync();
        var (plannedWindows, _, applyLongStop) = await ResolvePlannedWindowsAsync(thresholds, ct);
        var evIntervals = await _repo.GetDowntimeIntervalsAsync(fromUtc, toUtc, flowName, ct);
        var maintIv = evIntervals
            .Where(x => x.Kind is 0 or 2 && x.EndMs > x.StartMs)   // 유지보수(계획정비/기타 = isFailure 0 계열)
            .Select(x => ((double)x.StartMs, (double)x.EndMs, x.FlowName))
            .ToList();
        var agg = await ComputeCycleAggregateAsync(flowName, fromUtc, toUtc, thresholds, plannedWindows, applyLongStop, ct,
            maintIntervals: maintIv);

        int flowCount = Math.Max(1, agg.FlowCount);
        static List<(long S, long E)> ToLong(IEnumerable<(double S, double E)>? iv)
            => (iv ?? Enumerable.Empty<(double S, double E)>())
                .Select(x => ((long)x.S, (long)x.E)).Where(x => x.Item2 > x.Item1).ToList();

        var runWall = ToLong(agg.RunWallIntervals);            // flow별 가동(생산가능 클립) 연결
        var maintWall = ToLong(agg.DownMaintWallIntervals);    // flow별 유지보수(비가동∩유지이벤트) 연결
        var unmeasuredIv = agg.UnmeasuredIntervals ?? new List<(double S, double E)>();
        // 비생산(표시) = 지정/학습 − 미계측(미계측 우선). 전역이라 슬롯에서 ×flowCount.
        var nonProdDisp = ToLong(Intervals.Subtract(agg.NonProdIntervals ?? new List<(double S, double E)>(), unmeasuredIv));
        var unmeasuredL = ToLong(unmeasuredIv);

        static long SumOverlap(List<(long S, long E)> segs, long slotS, long slotE)
        {
            long sum = 0;
            foreach (var (s, e) in segs) { var o = Math.Min(e, slotE) - Math.Max(s, slotS); if (o > 0) sum += o; }
            return sum;
        }

        var slots = new List<OeeDailySlotDto>();
        void AddSlot(string label, DateTime slotStartUtc, DateTime slotEndUtc)
        {
            var sS = (long)ToMs(Max(fromUtc, slotStartUtc));
            var sE = (long)ToMs(Min(toUtc, slotEndUtc));
            var slotWall = Math.Max(0, sE - sS);
            long nonProd = SumOverlap(nonProdDisp, sS, sE) * flowCount;
            long unmeasured = SumOverlap(unmeasuredL, sS, sE) * flowCount;
            long run = SumOverlap(runWall, sS, sE);
            long available = Math.Max(0, slotWall * flowCount - nonProd - unmeasured);
            long down = Math.Max(0, available - run);            // 비가동 = 생산가능 − 가동(잔여)
            long maint = Math.Min(SumOverlap(maintWall, sS, sE), down);
            long fault = Math.Max(0, down - maint);
            // 벽시계 매핑: FailureMs=고장 / PlannedMs=유지보수(Other·Unclassified 미사용) / SlotMs=slotWall×flow수(잔여=가동).
            slots.Add(new OeeDailySlotDto(label, slotWall * flowCount, fault + maint, maint, fault, 0, 0, nonProd, unmeasured));
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

        return new OeeDailyResponse(gran, slots, flowCount);
    }

}
