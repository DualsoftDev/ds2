// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Repositories;

namespace DSPilot.Services.EmailBriefing;

/// <summary>
/// "어제"(로컬 자정~자정) 생산·이상 데이터를 모아 <see cref="BriefingData"/> 로 구성한다. Scoped —
/// Scoped 의존(BriefingOeeReader, IUserTagAlertRepository)을 쓰므로 발송마다 새 scope 에서 생성된다.
/// 컨트롤러 요약과 동일 계산 경로(<see cref="BriefingOeeReader"/>)를 재사용해 수치 정합을 보장한다.
/// </summary>
public sealed class BriefingComposer
{
    // 메일이 지나치게 길어지지 않도록 Flow 표는 상위 N 개만.
    private const int MaxFlowRows = 12;
    private const int TopAbnormalRows = 5;

    private readonly BriefingOeeReader _oee;
    private readonly IUserTagAlertRepository _alerts;
    private readonly DsProjectService _project;
    private readonly ILogger<BriefingComposer> _logger;

    public BriefingComposer(
        BriefingOeeReader oee,
        IUserTagAlertRepository alerts,
        DsProjectService project,
        ILogger<BriefingComposer> logger)
    {
        _oee = oee;
        _alerts = alerts;
        _project = project;
        _logger = logger;
    }

    /// <summary>대상 날짜(로컬). null 이면 "어제".</summary>
    public async Task<BriefingData> ComposeAsync(DateOnly? targetDay, CancellationToken ct)
    {
        // 로컬 자정 경계 → UTC. (history/OEE 조회는 모두 UTC 창을 받는다.)
        var dayLocalMidnight = targetDay.HasValue
            ? targetDay.Value.ToDateTime(TimeOnly.MinValue)                    // 00:00 로컬(Kind=Unspecified→Local 취급)
            : DateTime.Today.AddDays(-1);
        var fromLocal = DateTime.SpecifyKind(dayLocalMidnight, DateTimeKind.Local);
        var toLocal = fromLocal.AddDays(1);
        var fromUtc = fromLocal.ToUniversalTime();
        var toUtc = toLocal.ToUniversalTime();
        var day = DateOnly.FromDateTime(fromLocal);

        // ── 생산: 라인 전체 + Flow별 ──
        var line = await _oee.GetSummaryAsync(null, fromUtc, toUtc, ct);
        var lineTeep = await _oee.GetTeepAsync(null, fromUtc, toUtc, ct);

        var flows = new List<FlowBrief>();
        foreach (var flowName in EnumerateFlowNames())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var s = await _oee.GetSummaryAsync(flowName, fromUtc, toUtc, ct);
                flows.Add(new FlowBrief(flowName, s.Oee, s.TotalCount, s.DowntimeMs));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "브리핑 Flow 요약 실패: {Flow}", flowName);
            }
        }
        var topFlows = flows
            .OrderByDescending(f => f.Count ?? -1)
            .ThenByDescending(f => f.Oee ?? -1)
            .Take(MaxFlowRows)
            .ToList();

        // ── 이상: 총계 + 구분 + 상위 항목 ──
        var abnormalTotal = await _alerts.CountAlertsAsync(fromUtc, toUtc, null, null, null, null, ct);
        var byCategory = await _alerts.GetCategoryCountsAsync(fromUtc, toUtc, null, null, null, ct);
        byCategory.TryGetValue("ABNORMAL", out var abnormalCount);
        byCategory.TryGetValue("USERTAG", out var userTagCount);

        var topRows = await _alerts.GetTopByNameAsync(
            fromUtc, toUtc, TopAbnormalRows, null, null, null, "name", ct);
        var top = topRows
            .Select(r => new BriefTopRow(r.Name, r.LogLevel, r.Count))
            .ToList();

        return new BriefingData(day, line, lineTeep, topFlows, abnormalTotal, abnormalCount, userTagCount, top);
    }

    // 프로젝트에 로드된 모든 시스템의 Flow 명(NavController 와 동일 소스). 프로젝트 미로드면 빈 목록.
    private IEnumerable<string> EnumerateFlowNames()
    {
        if (!_project.IsLoaded) yield break;
        foreach (var system in _project.GetActiveSystems())
            foreach (var flow in _project.GetFlows(system.Id))
                yield return flow.Name;
    }
}
