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
        OeeCommHealthService commHealth,
        OeeNonProdPatternService nonProdPattern,
        HistoryMirrorService mirror,
        ILogger<OeePlannedStopsController> logger)
        : base(repo, settings, project, pathResolver, ctStats, shiftInfer, commHealth, nonProdPattern, mirror, logger) { }

    // ── GET /api/oee/planned-stops ────────────────────────────────────────
    // 병행 모델(2026-07-08): 당일 자동 판정 상시 + Windows(수동 지정)는 추가 확정 비생산. Source=auto|both.
    [HttpGet("planned-stops")]
    public ActionResult<PlannedStopsDto> GetPlannedStops()
    {
        var manual = _settings.LoadSettings().OeeManual.PlannedStops ?? new List<PlannedStopWindow>();
        var windows = manual.Select(w => new PlannedStopWindowDto(w.StartMinutes, w.EndMinutes, w.Label)).ToList();
        return new PlannedStopsDto(windows.Count > 0 ? "both" : "auto", windows, ResolveNonProdWtMultiplier());
    }

    // ── GET /api/oee/ct-multipliers ───────────────────────────────────────
    // 고장·비생산 판정 기준 배수 + flow별 기준선(중앙 MT/WT/CT·평균 CT·완료 표본 — 임계 환산 표시용, doc/28).
    // 설비효율 현황 '판정 기준' 카드 소스. 경로 이름은 호환 유지.
    [HttpGet("ct-multipliers")]
    public async Task<ActionResult<CtMultipliersDto>> GetCtMultipliers()
    {
        var nonProdMult = ResolveNonProdWtMultiplier();
        var faultMult = _settings.LoadSettings().OeeManual.ResolveFaultMtMultiplier();
        var thresholds = await ResolveCtThresholdsAsync();
        var bounds = BuildFlowBounds(thresholds, await _ctStats.ComputeMtThresholdAsync(), await _ctStats.ComputeWtBaselineAsync(),
            nonProdMult, faultMult);
        var flows = bounds
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new CtMultiplierFlowDto(
                kv.Key, kv.Value.AvgCt, kv.Value.MedianMt, kv.Value.MedianWt, kv.Value.MedianCt,
                kv.Value.Sample, kv.Value.HasMt))
            .ToList();
        return new CtMultipliersDto(nonProdMult, flows, faultMult);
    }

    // ── PUT /api/oee/ct-multipliers ───────────────────────────────────────
    // 판정 배수 저장 (2026-07-13 → 2026-09-11 두 규칙 모델). 검증: 범위만 — 두 배수는 축이 달라 대소 제약이 없다.
    // 저장 즉시 사전계산 저장본 동기 폐기(NotifyInvalidate) — 직후 재조회가 구 기준 수치를 받지 않는다.
    // 과거 KPI 는 조회 시 재계산이라 자동 소급되지만, oeeNonProdDetectionLog 의 기존 행(감지 당시 배수 스냅샷)은
    // 감사·재현성 규약대로 유지된다(OeeNonProdDetectionLog 주석).
    [HttpPut("ct-multipliers")]
    public async Task<ActionResult<CtMultipliersDto>> SetCtMultipliers([FromBody] CtMultipliersRequest? req)
    {
        var curNonProd = ResolveNonProdWtMultiplier();
        var nonProd = req?.NonProdWtMultiplier ?? curNonProd;
        if (!double.IsFinite(nonProd) || nonProd < OeeManualSettings.NonProdMultMin || nonProd > OeeManualSettings.NonProdMultMax)
            return BadRequest(new { error = $"비생산 판정 배수는 {OeeManualSettings.NonProdMultMin:0.#}~{OeeManualSettings.NonProdMultMax:0.#} 범위여야 합니다." });

        var curFault = _settings.LoadSettings().OeeManual.ResolveFaultMtMultiplier();
        var fault = req?.FaultMtMultiplier ?? curFault;
        if (!double.IsFinite(fault) || fault < OeeManualSettings.FaultMultMin || fault > OeeManualSettings.FaultMultMax)
            return BadRequest(new { error = $"고장 판정 배수는 {OeeManualSettings.FaultMultMin:0.#}~{OeeManualSettings.FaultMultMax:0.#} 범위여야 합니다." });

        _settings.SaveStopMultipliers(nonProd, fault);
        OeeChangeSignal.NotifyInvalidate();
        _logger.LogInformation(
            "[OEE] 판정 배수 변경: 비생산 {NonProd}× / 고장 {Fault}× (구 {OldNonProd}/{OldFault})",
            nonProd, fault, curNonProd, curFault);
        return await GetCtMultipliers();
    }

    // ── GET /api/oee/ct-multipliers/preview?nonProd[&fault][&from&to] ────────
    // 저장 없는 what-if 재분류 — 같은 기간을 현재/제안 배수로 각각 집계해 비교(라인 전체 스코프). 축별 건수를 함께 내려
    // 슬라이더 하나가 완료 행·불인정 행 두 축에 걸린 결과를 화면이 구분 표기한다(doc/28 §3).
    // 오버라이드 계산은 감지로그 materialize 를 막고(suppressDetectionLog), 결과는 집계 TTL 캐시를 공유한다.
    // 구 파라미터 idle 은 무시(정지 배수 폐기 — 구 클라이언트 호환).
    [HttpGet("ct-multipliers/preview")]
    public async Task<ActionResult<CtMultipliersPreviewDto>> PreviewCtMultipliers(
        [FromQuery] double? nonProd, [FromQuery] double? fault, [FromQuery] double? idle,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        var curNonProd = ResolveNonProdWtMultiplier();
        var curFault = _settings.LoadSettings().OeeManual.ResolveFaultMtMultiplier();
        var np = nonProd ?? curNonProd;
        var fm = fault ?? curFault;
        if (!double.IsFinite(np) || np < OeeManualSettings.NonProdMultMin || np > OeeManualSettings.NonProdMultMax)
            return BadRequest(new { error = $"비생산 판정 배수는 {OeeManualSettings.NonProdMultMin:0.#}~{OeeManualSettings.NonProdMultMax:0.#} 범위여야 합니다." });
        if (!double.IsFinite(fm) || fm < OeeManualSettings.FaultMultMin || fm > OeeManualSettings.FaultMultMax)
            return BadRequest(new { error = $"고장 판정 배수는 {OeeManualSettings.FaultMultMin:0.#}~{OeeManualSettings.FaultMultMax:0.#} 범위여야 합니다." });

        var (fromUtc, toUtc) = ResolveRange(from, to);
        var thresholds = await ResolveCtThresholdsAsync();
        var (plannedWindows, _, applyLongStop) = await ResolvePlannedWindowsAsync(thresholds, ct);

        async Task<CtMultipliersPreviewSideDto> SideAsync(double nm, double f)
        {
            var agg = await ComputeCycleAggregateAsync(null, fromUtc, toUtc, thresholds, plannedWindows, applyLongStop, ct,
                nonProdMultOverride: nm, faultMultOverride: f);
            var (a, _) = OeeMath.ComputeWallClockAvailability(agg.RunWallMs, agg.AvailableWallMs);
            var (p, _) = OeeMath.ComputeCyclePerformance(agg.NormalCount, agg.CtThresholdMs, agg.NormalCtMs);
            return new CtMultipliersPreviewSideDto(nm, f,
                agg.DowntimeEventCount, agg.NonProdCount, agg.HasThreshold ? agg.NormalCount : 0,
                agg.IdleCtMs, agg.NonProdWallMs, a, p,
                agg.FaultMtCount, agg.FaultCtCount, agg.NonProdWtCount, agg.NonProdCtCount, agg.ReviewPendingCount);
        }

        return new CtMultipliersPreviewDto(
            await SideAsync(curNonProd, curFault),
            await SideAsync(np, fm));
    }

    // ── GET /api/oee/planned-stops/auto-pattern ───────────────────────────
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

    // ── GET /api/oee/planned-stops/actual?from&to&flow[&detected] ────────
    // 병행 모델(2026-07-08)에선 applyLongStop 이 항상 true 라 detected 파라미터는 no-op(하위호환 유지).
    [HttpGet("planned-stops/actual")]
    public async Task<ActionResult<PlannedAutoPatternDto>> GetActualNonProduction(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? flow,
        [FromQuery] bool detected, CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var flowName = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();

        var thresholds = await ResolveCtThresholdsAsync();
        var (plannedWindows, _, applyLongStop) = await ResolvePlannedWindowsAsync(thresholds, ct);
        if (detected) applyLongStop = true;   // 실측 패턴 조회 — 수동 시간대가 감지를 끄지 못하게
        var agg = await ComputeCycleAggregateAsync(flowName, fromUtc, toUtc, thresholds, plannedWindows, applyLongStop, ct);
        var merged = new List<(double S, double E)>();
        merged.AddRange(await _repo.GetNonProdIntervalsFromLogAsync(fromUtc, toUtc, flowName, ct));
        merged.AddRange(ExpandPlannedIntervalsMs(plannedWindows, fromUtc, toUtc));
        if (agg.NonProdIntervals is { Count: > 0 })
            merged.AddRange(agg.NonProdIntervals);   // 방금 감지·강제(사용자 보내기 포함)한 실측 구간 직접 포함 —
                                                     // 로그 왕복·materialize 신뢰게이트 의존 제거 + 수동 non_production 이벤트 표시
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

        var lastDayProbe = toUtc > fromUtc ? toUtc.AddSeconds(-1) : toUtc;
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

    // ── PUT /api/oee/planned-stops ────────────────────────────────────────
    [HttpPut("planned-stops")]
    public ActionResult<PlannedStopsDto> SetPlannedStops([FromBody] PlannedStopsRequest? req)
    {
        var windows = (req?.Windows ?? new List<PlannedStopWindowDto>())
            .Select(w => new PlannedStopWindow { StartMinutes = w.StartMinutes, EndMinutes = w.EndMinutes, Label = w.Label })
            .ToList();
        _settings.SavePlannedStops(windows);
        OeeChangeSignal.NotifyInvalidate();   // 사전계산 저장본 동기 폐기 — 적용 직후 재조회(loadOee)가 구 창 수치를 받지 않게
        return GetPlannedStops();
    }

    // (구 PUT planned-stops/excluded-weekdays[생산 요일] 은 2026-07-08 당일 비생산 판정 모델로 제거 —
    //  쉬는 날은 사이클이 없어 10×CT 규칙이 자동으로 비생산 처리한다.)

    // (구 POST planned-stops/auto[자동/수동 배타 토글] 은 2026-07-08 병행 모델로 제거 — 자동 판정 상시,
    //  지정 시간대는 PUT planned-stops 로 추가/삭제만 한다.)

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
