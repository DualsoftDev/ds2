// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Controllers;
using DSPilot.Infrastructure;
using DSPilot.Models.Oee;
using DSPilot.Repositories;

namespace DSPilot.Services.EmailBriefing;

/// <summary>
/// OEE 요약을 백그라운드/브리핑에서 재사용하기 위한 얇은 어댑터. <see cref="OeeControllerBase"/> 의
/// 순수 계산(<c>BuildSummaryAsync</c> — HttpContext 미사용)만 공개로 노출한다. 컨트롤러(OeeMetricsController)와
/// 완전히 동일한 계산 경로를 타므로 브리핑 수치가 대시보드/OEE 페이지와 어긋나지 않는다.
/// Scoped 로 등록(IOeeRepository 가 Scoped) — EmailBriefingService 는 발송마다 scope 를 연다.
/// </summary>
public sealed class BriefingOeeReader : OeeControllerBase
{
    public BriefingOeeReader(
        IOeeRepository repo,
        AppSettingsService settings,
        DsProjectService project,
        IDatabasePathResolver pathResolver,
        OeeCtStatsService ctStats,
        OeeAutoShiftInferenceService shiftInfer,
        OeeCommHealthService commHealth,
        OeeNonProdPatternService nonProdPattern,
        HistoryMirrorService mirror,
        ILogger<BriefingOeeReader> logger)
        : base(repo, settings, project, pathResolver, ctStats, shiftInfer, commHealth, nonProdPattern, mirror, logger) { }

    /// <summary>지정 UTC 창의 OEE 요약. flow=null 이면 라인 전체.</summary>
    public Task<OeeSummaryDto> GetSummaryAsync(string? flow, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
        => BuildSummaryAsync(string.IsNullOrWhiteSpace(flow) ? null : flow.Trim(), fromUtc, toUtc, ct);

    /// <summary>
    /// 지정 창의 TEEP(생산효율 = 가동(Σ실측CT) ÷ 캘린더 전체). flow=null 이면 라인 전체.
    /// OeeMetricsController.Teep 와 동일 계산 경로 — 표준CT 보유 flow 수로 캘린더를 스케일한다.
    /// 산출 불가(표준CT 보유 flow 0)면 null.
    /// </summary>
    public async Task<double?> GetTeepAsync(string? flow, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
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
        return OeeMath.ComputeTeep(agg.NormalCtMs, calendarMs);
    }
}
