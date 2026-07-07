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
/// OEE 정지(다운타임) — GET/분류/마감/일괄 처리.
/// </summary>
[ApiController]
[Route("api/oee")]
public class OeeDowntimeController : OeeControllerBase
{
    public OeeDowntimeController(
        IOeeRepository repo,
        AppSettingsService settings,
        DsProjectService project,
        IDatabasePathResolver pathResolver,
        OeeCtStatsService ctStats,
        OeeAutoShiftInferenceService shiftInfer,
        OeeCommHealthService commHealth,
        OeeNonProdPatternService nonProdPattern,
        ILogger<OeeDowntimeController> logger)
        : base(repo, settings, project, pathResolver, ctStats, shiftInfer, commHealth, nonProdPattern, logger) { }

    // ── GET /api/oee/downtime?from&to&status&reason&flow ──────────────────
    [HttpGet("downtime")]
    public async Task<ActionResult<List<OeeDowntimeDto>>> Downtime(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] string? status, [FromQuery] string? reason, [FromQuery] string? flow,
        CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var flowName = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();
        var rows = await _repo.QueryDowntimeAsync(fromUtc, toUtc, status, reason, flowName, ct);

        // 이상치 초과 사이클(로그 테이블에 없는 failureCount 사이클 성분)을 합성해 병합 — 내역이 도넛/바 건수와 정합.
        //   status 필터(진행중)엔 해당 없음(합성은 전부 복구됨), reason 필터가 걸리면 합성 행(고정 reason)은 제외.
        var merged = rows.ToList();
        if (!string.Equals(status, "open", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(reason))
        {
            var overCycles = await GetOverThresholdCycleDowntimeAsync(flowName, fromUtc, toUtc, ct);
            merged.AddRange(overCycles);
            merged = merged.OrderByDescending(d => d.StartAt).ThenByDescending(d => d.Id).ToList();
        }
        return await AttachCluesAsync(merged, fromUtc, toUtc, ct);
    }

    // ── POST /api/oee/downtime/{id}/classify ──────────────────────────────
    [HttpPost("downtime/{id:long}/classify")]
    public async Task<ActionResult<object>> Classify(long id, [FromBody] ClassifyRequest req, CancellationToken ct)
    {
        var category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim().ToLowerInvariant();
        var reasonCode = string.IsNullOrWhiteSpace(req.ReasonCode) ? null : req.ReasonCode.Trim();
        var isFailure = OeeMath.IsFailureReason(reasonCode);

        var n = await _repo.ClassifyDowntimeAsync(id, reasonCode, category, isFailure, classifySource: "manual", ct);
        if (n == 0) return NotFound(new { error = "downtime event not found", id });
        return new { ok = true, id, reasonCode, category, isFailure };
    }

    // ── POST /api/oee/downtime/{id}/set-fault ─────────────────────────────
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

    // ── POST /api/oee/downtime/bulk-set-fault ─────────────────────────────
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

    // ── POST /api/oee/downtime/{id}/close ────────────────────────────────
    [HttpPost("downtime/{id:long}/close")]
    public async Task<ActionResult<object>> Close(long id, [FromBody] CloseRequest? req, CancellationToken ct)
    {
        var endAtUtc = (req?.EndAt) is DateTime e ? ToUtc(e) : DateTime.UtcNow;
        var n = await _repo.CloseDowntimeAsync(id, endAtUtc, ct);
        if (n == 0) return NotFound(new { error = "open downtime event not found", id });
        return new { ok = true, id, endAt = endAtUtc };
    }

    // ── POST /api/oee/downtime/bulk-classify ──────────────────────────────
    [HttpPost("downtime/bulk-classify")]
    public async Task<ActionResult<object>> BulkClassify([FromBody] BulkClassifyRequest req, CancellationToken ct)
    {
        if (req.Ids == null || req.Ids.Count == 0)
            return BadRequest(new { error = "ids is required" });
        if (req.Ids.Count > 500)
            return BadRequest(new { error = "too many ids (max 500)" });

        var category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim().ToLowerInvariant();
        var reasonCode = string.IsNullOrWhiteSpace(req.ReasonCode) ? null : req.ReasonCode.Trim();
        var isFailure = OeeMath.IsFailureReason(reasonCode);

        var n = await _repo.BulkClassifyDowntimeAsync(req.Ids, reasonCode, category, isFailure, classifySource: "manual", ct);
        return new { ok = true, count = n, reasonCode, category, isFailure };
    }

    // ── POST /api/oee/downtime/bulk-close ─────────────────────────────────
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
}
