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
}
