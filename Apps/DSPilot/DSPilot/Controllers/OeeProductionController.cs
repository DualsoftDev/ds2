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
/// OEE 생산 — 가동횟수/출력Flow/생산·불량 입력/Excel 내보내기.
/// </summary>
[ApiController]
[Route("api/oee")]
public class OeeProductionController : OeeControllerBase
{
    public OeeProductionController(
        IOeeRepository repo,
        AppSettingsService settings,
        DsProjectService project,
        IDatabasePathResolver pathResolver,
        OeeCtStatsService ctStats,
        OeeAutoShiftInferenceService shiftInfer,
        ILogger<OeeProductionController> logger)
        : base(repo, settings, project, pathResolver, ctStats, shiftInfer, logger) { }

    // ── GET /api/oee/output-count?from&to ─────────────────────────────────
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
    [HttpGet("output-flows")]
    public async Task<ActionResult<OutputFlowStateDto>> GetOutputFlows()
    {
        var selected = _settings.LoadSettings().OeeManual.OutputFlows ?? [];
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
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

    // ── POST /api/oee/production ──────────────────────────────────────────
    [HttpPost("production")]
    public async Task<ActionResult<object>> Production([FromBody] ProductionRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Flow))
            return BadRequest(new { error = "flow is required" });

        var bucketDate = (req.Date ?? DateTime.Now).Date;
        var bucketStr = bucketDate.ToString("yyyy-MM-dd");
        var shift = req.Shift ?? "";

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

    // ── POST /api/oee/quality ─────────────────────────────────────────────
    [HttpPost("quality")]
    public ActionResult<object> SetManualQuality([FromBody] ManualQualityRequest req)
    {
        _settings.SaveManualQualityPercent(req?.QualityPercent);
        var saved = _settings.LoadSettings().OeeManual.QualityPercent;
        return new { ok = true, qualityPercent = saved };
    }
}
