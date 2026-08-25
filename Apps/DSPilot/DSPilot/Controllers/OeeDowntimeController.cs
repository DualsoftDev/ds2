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
        HistoryMirrorService mirror,
        ILogger<OeeDowntimeController> logger)
        : base(repo, settings, project, pathResolver, ctStats, shiftInfer, commHealth, nonProdPattern, mirror, logger) { }

    // ── GET /api/oee/downtime?from&to&status&reason&flow[&system] ─────────
    // system = 시스템 스코프(그 시스템 flow 의 정지만, flow 미상 라인 귀속 행은 보존). flow 지정이 우선.
    [HttpGet("downtime")]
    public async Task<ActionResult<List<OeeDowntimeDto>>> Downtime(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] string? status, [FromQuery] string? reason, [FromQuery] string? flow,
        [FromQuery] string? system, CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var flowName = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();
        var flowSet = flowName is null ? ResolveSystemFlowSet(system) : null;
        var rows = await _repo.QueryDowntimeAsync(fromUtc, toUtc, status, reason, flowName, ct);
        var merged = flowSet is null
            ? rows.ToList()
            : rows.Where(d => d.FlowName is null || flowSet.Contains(d.FlowName)).ToList();

        // 이상치 초과 사이클(로그 테이블에 없는 failureCount 사이클 성분)을 합성해 병합 — 내역이 도넛/바 건수와 정합.
        //   status 필터(진행중)엔 해당 없음(합성은 전부 복구됨), reason 필터가 걸리면 합성 행(고정 reason)은 제외.
        // 합성엔 KPI 와 동일한 집계의 flow 귀속 비생산/대기 구간이 딸려 온다 — DB 이벤트 행의 '구분' 판정에
        //   재사용해 팝업 표시와 KPI 카빙이 같은 판단을 공유한다(2026-07-08 당일 판정 모델 + doc/25 flow 스코프).
        var nonProdScoped = new List<(string? Flow, double S, double E)>();
        var waitScoped = new List<(string? Flow, double S, double E)>();
        var slackScoped = new List<(string? Flow, double S, double E)>();
        if (!string.Equals(status, "open", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(reason))
        {
            var (overCycles, npScoped, wScoped, slScoped) =
                await GetOverThresholdCycleDowntimeAsync(flowName, fromUtc, toUtc, ct, flowSet);
            nonProdScoped = npScoped;
            waitScoped = wScoped;
            slackScoped = slScoped;
            // 재분류로 materialize 된 over-cycle 이벤트 행과 겹치는 합성 행 dedup — 같은 사이클이 두 줄로 보이지 않게.
            static bool NearSameStart(DateTime a, DateTime b) => Math.Abs((a - b).TotalSeconds) < 2.0;
            // 같은 정지 이중 표시 흡수(2026-07-16, 사용자 확인) — 하나의 정지가 무가동 이벤트(DB)와 ct 폭주 사이클
            // (합성) 두 소스에 다 잡히면 목록엔 DB 행 하나만 남긴다(체크·재분류 가능한 쪽). KPI 는 집계에서 이미
            // 구간 차감(dedup)되므로 표시 전용 정리. 흡수된 DB 행은 감지 칩에 '+이상치초과' 병기(정보 유실 방지).
            static double OverlapRatioOfSynthetic(OeeDowntimeDto db, OeeDowntimeDto sc, DateTime nowL)
            {
                var s = Math.Max(db.StartAt.Ticks, sc.StartAt.Ticks);
                var e = Math.Min((db.EndAt ?? nowL).Ticks, (sc.EndAt ?? nowL).Ticks);
                var dur = Math.Max(1.0, ((sc.EndAt ?? nowL) - sc.StartAt).Ticks);
                return Math.Max(0, e - s) / dur;
            }
            var nowL = DateTime.Now;
            var absorbedIdx = new HashSet<int>();   // 합성 행을 흡수한 DB 행 index — 감지 칩 병기 마킹
            var keptSynthetic = new List<OeeDowntimeDto>();
            foreach (var sc in overCycles)
            {
                // ① 이미 materialize 된 over-cycle DB 행과 같은 사이클 → 제외(종전 dedup).
                if (merged.Any(d => string.Equals(d.DetectSource, "over-cycle", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(d.FlowName, sc.FlowName, StringComparison.Ordinal)
                        && NearSameStart(d.StartAt, sc.StartAt)))
                    continue;
                // ② 같은 flow 의 무가동 DB 행과 크게 겹침(≥60%) → 흡수(같은 정지의 이중 표시).
                var host = -1;
                for (var i = 0; i < merged.Count; i++)
                {
                    var d = merged[i];
                    if (d.Id <= 0 || !string.Equals(d.FlowName, sc.FlowName, StringComparison.Ordinal)) continue;
                    if (OverlapRatioOfSynthetic(d, sc, nowL) >= 0.6) { host = i; break; }
                }
                if (host >= 0) { absorbedIdx.Add(host); continue; }
                keptSynthetic.Add(sc);
            }
            foreach (var i in absorbedIdx)
                merged[i] = merged[i] with
                {
                    DetectSource = merged[i].DetectSource + "+over-cycle",
                    Note = string.IsNullOrEmpty(merged[i].Note)
                        ? "무가동 이벤트 + 이상치 초과 사이클 동시 감지(같은 정지 — 한 줄로 병합)"
                        : merged[i].Note + " · 이상치 초과 사이클 동시 감지(병합)",
                };
            merged.AddRange(keptSynthetic);
            merged = merged.OrderByDescending(d => d.StartAt).ThenByDescending(d => d.Id).ToList();
        }

        // DB 이벤트 행 구분 판정: 수동 분류가 있으면 그것이 정답(non_production=비생산, 그 외=비가동),
        // 아니면 KPI 비생산/대기 구간과의 겹침 비율(≥50%)로 자동 판정 — 반드시 **그 행의 flow 구간만** 본다
        // (doc/25: 형제 flow 의 대기 비생산이 유발 flow 의 고장 행을 비생산으로 오표시하지 않게).
        var nowLocal = DateTime.Now;
        for (var i = 0; i < merged.Count; i++)
        {
            var d = merged[i];
            if (d.Id <= 0) continue;   // 합성 행은 이미 IsNonProd/IsWait 세팅됨
            bool isNp;
            var isWait = false;
            if (string.Equals(d.ClassifySource, "manual", StringComparison.OrdinalIgnoreCase))
                isNp = string.Equals(d.ReasonCode, OeeMath.NonProductionReasonCode, StringComparison.OrdinalIgnoreCase);
            else
            {
                var sMs = new DateTimeOffset(DateTime.SpecifyKind(d.StartAt, DateTimeKind.Local)).ToUnixTimeMilliseconds();
                var eMs = new DateTimeOffset(DateTime.SpecifyKind(d.EndAt ?? nowLocal, DateTimeKind.Local)).ToUnixTimeMilliseconds();
                var dur = Math.Max(1.0, eMs - sMs);
                static double OverlapFor(List<(string? Flow, double S, double E)> src, string? flow, double sMs, double eMs)
                {
                    double sum = 0;
                    foreach (var (fl, s, e) in src)
                    {
                        if (fl is not null && flow is not null
                            && !string.Equals(fl, flow, StringComparison.OrdinalIgnoreCase)) continue;
                        var o = Math.Min(e, eMs) - Math.Max(s, sMs);
                        if (o > 0) sum += o;
                    }
                    return sum;
                }
                // 대기 두 갈래(2026-07-30): ① 비생산 대기(기준 이상 형제 정지) → "비생산 · 대기"
                //   ② 이벤트성 공백(기준 미만 대기 + 비가동 경계 미만 조각) → "대기(공백)".
                // 판정 규칙은 OeeMath.ResolveLogStopClass 단일 소스(순수·테스트 가능).
                (isNp, isWait) = OeeMath.ResolveLogStopClass(
                    OverlapFor(nonProdScoped, d.FlowName, sMs, eMs) / dur,
                    OverlapFor(waitScoped, d.FlowName, sMs, eMs) / dur,
                    OverlapFor(slackScoped, d.FlowName, sMs, eMs) / dur);
            }
            if (isNp != d.IsNonProd || isWait != d.IsWait) merged[i] = d with { IsNonProd = isNp, IsWait = isWait };
        }
        var classified = await AttachCluesAsync(merged, fromUtc, toUtc, ct);
        LogClassifyTransitions(classified);   // 판정 전이 로그(doc/25 §4.3) — 프로세스 수명 내 구분 변화 계측
        return classified;
    }

    // ── 판정 전이 로그 (doc/25 §4.3) — 같은 정지 행의 구분(비가동/비생산/대기)이 이전 조회와 달라지면 1줄 기록.
    //    스키마 없이 프로세스 수명 캐시로 계측 — "언제 왜 뒤집혔나" 를 다음 테스트에서 즉시 확정하기 위한 진단용.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Np, bool Wait)>
        s_lastClassify = new();

    private void LogClassifyTransitions(List<OeeDowntimeDto> rows)
    {
        foreach (var d in rows)
        {
            var key = d.Id > 0 ? $"id:{d.Id}" : $"{d.FlowName}|{d.StartAt.Ticks}";
            var cur = (d.IsNonProd, d.IsWait);
            if (s_lastClassify.TryGetValue(key, out var prev) && prev != cur)
            {
                static string Label((bool Np, bool Wait) v) =>
                    v.Np && v.Wait ? "비생산·대기" : v.Np ? "비생산" : v.Wait ? "대기(공백)" : "비가동";
                _logger.LogInformation(
                    "[OEE-CLASSIFY] 정지 구분 전이 flow={Flow} ev={Key} {Prev}→{Cur} (시작 {Start:MM-dd HH:mm:ss}, 지속 {Dur}s, 단서={Clue})",
                    d.FlowName, key, Label(prev), Label(cur), d.StartAt,
                    (d.DurationMs ?? 0) / 1000, d.Clue?.Label ?? "-");
            }
            s_lastClassify[key] = cur;
            if (s_lastClassify.Count > 4096) s_lastClassify.Clear();   // 진단 캐시 폭주 방지(정확성 무관)
        }
    }

    // ── POST /api/oee/downtime/reclassify — 비생산↔비가동 보내기 ───────────
    // 자동 판정(당일 10×CT)이 어긋났을 때 사용자가 구간 단위로 확정한다(classifySource='manual' → KPI 오버라이드).
    //   Id>0  : 기존 이벤트 행 분류 변경.
    //   합성행: Flow/StartAt/EndAt 로 over-cycle 이벤트 행을 materialize 한 뒤 분류(이후 오버라이드로 작동).
    // 비가동으로 보내면 이전 분류 복원(유지보수→비생산→비가동 왕복 시 유지보수 유지, prev* 스태시) — 스태시 없으면 기본 '고장'.
    [HttpPost("downtime/reclassify")]
    public async Task<ActionResult<object>> Reclassify([FromBody] ReclassifyDowntimeRequest req, CancellationToken ct)
    {
        long id = req.Id ?? 0;
        if (id <= 0)
        {
            if (string.IsNullOrWhiteSpace(req.Flow) || req.StartAt is null || req.EndAt is null)
                return BadRequest(new { error = "synthetic row requires flow, startAt, endAt" });
            var startUtc = ToUtc(req.StartAt.Value);
            var endUtc = ToUtc(req.EndAt.Value);
            if (endUtc <= startUtc) return BadRequest(new { error = "endAt must be after startAt" });
            id = await _repo.InsertDowntimeAsync(new OeeDowntimeEvent
            {
                SystemName = "",
                FlowName = req.Flow.Trim(),
                StartAt = startUtc,
                EndAt = endUtc,
                DurationMs = (long)(endUtc - startUtc).TotalMilliseconds,
                DetectSource = "over-cycle",
                Note = "사용자 재분류로 확정(계산 유래 사이클)",
            }, ct);
            if (id <= 0) return StatusCode(500, new { error = "materialize failed" });
        }

        // 스태시/복원 방식(repo) — 유지보수였던 정지를 비생산으로 보냈다가 되돌리면 유지보수로 복원된다(고장 강등 없음).
        var n = await _repo.ReclassifyDowntimeAsync(id, req.ToNonProd, ct);
        if (n == 0) return NotFound(new { error = "downtime event not found", id });

        // 비가동 확정 시: 그 구간과 겹치는 자동 비생산 감지 로그를 청소 — actual/추이 표시에 stale 비생산이 남지 않게.
        if (!req.ToNonProd && req.StartAt is not null && req.EndAt is not null)
        {
            try { await _repo.DeleteNonProdDetectionsOverlappingAsync(ToUtc(req.StartAt.Value), ToUtc(req.EndAt.Value), ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "[OEE] 재분류 감지로그 청소 실패(표시만 영향)"); }
        }
        return new { ok = true, id, toNonProd = req.ToNonProd };
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
    // id ≤ 0 = 합성 행(이상치 초과 사이클) — reclassify 와 동일하게 실제 이벤트 행을 materialize 한 뒤 분류
    // (2026-07-16, doc/25): 의도된 정지(유지보수 등)가 이상치로 잡혔을 때도 고장 체크 해제가 가능해야 한다.
    [HttpPost("downtime/{id:long}/set-fault")]
    public async Task<ActionResult<object>> SetFault(long id, [FromBody] SetFaultRequest req, CancellationToken ct)
    {
        if (id <= 0)
        {
            if (string.IsNullOrWhiteSpace(req.Flow) || req.StartAt is null || req.EndAt is null)
                return BadRequest(new { error = "synthetic row requires flow, startAt, endAt" });
            var startUtc = ToUtc(req.StartAt.Value);
            var endUtc = ToUtc(req.EndAt.Value);
            if (endUtc <= startUtc) return BadRequest(new { error = "endAt must be after startAt" });
            id = await _repo.InsertDowntimeAsync(new OeeDowntimeEvent
            {
                SystemName = "",
                FlowName = req.Flow.Trim(),
                StartAt = startUtc,
                EndAt = endUtc,
                DurationMs = (long)(endUtc - startUtc).TotalMilliseconds,
                DetectSource = "over-cycle",
                Note = "사용자 분류로 확정(계산 유래 사이클)",
            }, ct);
            if (id <= 0) return StatusCode(500, new { error = "materialize failed" });
        }
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
