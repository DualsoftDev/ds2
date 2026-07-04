// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Infrastructure;
using DSPilot.Models;
using DSPilot.Models.Oee;
using DSPilot.Repositories;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// OEE 비생산 시간대 / 시프트 예외 — planned-stops/* + shift-exception/*.
/// </summary>
[ApiController]
[Route("api/oee")]
public class OeePlannedStopsController : OeeControllerBase
{
    public OeePlannedStopsController(
        IOeeRepository repo,
        AppSettingsService settings,
        DsProjectService project,
        IDatabasePathResolver pathResolver,
        OeeCtStatsService ctStats,
        OeeAutoShiftInferenceService shiftInfer,
        ILogger<OeePlannedStopsController> logger)
        : base(repo, settings, project, pathResolver, ctStats, shiftInfer, logger) { }

    // ── GET /api/oee/planned-stops ────────────────────────────────────────
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

    // ── GET /api/oee/planned-stops/auto-pattern ───────────────────────────
    [HttpGet("planned-stops/auto-pattern")]
    public async Task<ActionResult<PlannedAutoPatternDto>> GetPlannedAutoPattern(
        [FromQuery] string? flow, CancellationToken ct)
    {
        var flowName = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();

        if (flowName is null)
        {
            var cache = _settings.LoadSettings().OeeManual.AutoPatternCache;
            if (cache != null && (DateTime.UtcNow - cache.ComputedAt).TotalHours < 24)
            {
                var w = cache.Windows.Select(x => new PlannedStopWindowDto(x.StartMinutes, x.EndMinutes, x.Label)).ToList();
                return new PlannedAutoPatternDto(w, cache.DataFrom, cache.DataTo, 14);
            }
        }

        return await ComputeAndCacheAutoPatternAsync(flowName, ct);
    }

    // ── GET /api/oee/planned-stops/actual?from&to&flow ───────────────────
    [HttpGet("planned-stops/actual")]
    public async Task<ActionResult<PlannedAutoPatternDto>> GetActualNonProduction(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? flow, CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var flowName = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();

        var thresholds = await ResolveCtThresholdsAsync();
        var (plannedWindows, _, applyLongStop) = ResolvePlannedWindows();
        var agg = await ComputeCycleAggregateAsync(flowName, fromUtc, toUtc, thresholds, plannedWindows, applyLongStop, ct);
        var merged = new List<(double S, double E)>();
        merged.AddRange(await _repo.GetNonProdIntervalsFromLogAsync(fromUtc, toUtc, flowName, ct));
        merged.AddRange(ExpandPlannedIntervalsMs(plannedWindows, fromUtc, toUtc));
        List<(double S, double E)> intervals = merged.Count > 0
            ? Intervals.Union(merged)
            : (agg.NonProdIntervals ?? new List<(double S, double E)>());

        var lastDayProbe = toUtc > fromUtc ? toUtc.AddSeconds(-1) : toUtc;
        var dayStartUtc = DateTime.SpecifyKind(lastDayProbe.ToLocalTime().Date, DateTimeKind.Local).ToUniversalTime();
        if (dayStartUtc < fromUtc) dayStartUtc = fromUtc;
        double clipS = ToMs(dayStartUtc), clipE = ToMs(toUtc);

        var covered = new bool[1440];
        foreach (var (s0, e0) in intervals)
        {
            var s = Math.Max(s0, clipS);
            var e = Math.Min(e0, clipE);
            if (e <= s) continue;
            var durMin = (e - s) / 60000.0;
            if (durMin >= 1440) { for (int m = 0; m < 1440; m++) covered[m] = true; continue; }
            var startLocal = DateTimeOffset.FromUnixTimeMilliseconds((long)s).LocalDateTime;
            int startMin = startLocal.Hour * 60 + startLocal.Minute;
            int span = (int)Math.Ceiling(durMin);
            for (int k = 0; k < span; k++) covered[(startMin + k) % 1440] = true;
        }

        var windows = new List<PlannedStopWindowDto>();
        int? wStart = null;
        for (int m = 0; m <= 1440; m++)
        {
            bool has = m < 1440 && covered[m];
            if (has && wStart == null) wStart = m;
            else if (!has && wStart != null) { windows.Add(new PlannedStopWindowDto(wStart.Value, m, null)); wStart = null; }
        }

        var nowUtc = DateTime.UtcNow;
        var isLive = toUtc >= nowUtc.AddMinutes(-5);
        var probeMs = ToMs(nowUtc);
        var currentlyNonProd = isLive && intervals.Any(iv => iv.S <= probeMs && iv.E >= probeMs - 60000);

        return new PlannedAutoPatternDto(windows, fromUtc.ToLocalTime(), toUtc.ToLocalTime(), 0, currentlyNonProd);
    }

    // ── PUT /api/oee/planned-stops ────────────────────────────────────────
    [HttpPut("planned-stops")]
    public ActionResult<PlannedStopsDto> SetPlannedStops([FromBody] PlannedStopsRequest? req)
    {
        var windows = (req?.Windows ?? new List<PlannedStopWindowDto>())
            .Select(w => new PlannedStopWindow { StartMinutes = w.StartMinutes, EndMinutes = w.EndMinutes, Label = w.Label })
            .ToList();
        _settings.SavePlannedStops(windows);
        return GetPlannedStops();
    }

    // ── POST /api/oee/planned-stops/auto ──────────────────────────────────
    [HttpPost("planned-stops/auto")]
    public async Task<ActionResult<PlannedStopsDto>> SetPlannedStopsAuto(
        [FromBody] PlannedStopsAutoRequest? req, CancellationToken ct)
    {
        var enabled = req?.Enabled ?? true;
        _settings.SavePlannedStopsAuto(enabled);
        if (enabled)
            await ComputeAndCacheAutoPatternAsync(null, ct);
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

    // ── POST /api/oee/shift-exception ─────────────────────────────────────
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
}
