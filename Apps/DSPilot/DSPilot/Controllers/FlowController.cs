// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 격리형 호스팅용 Flow Workspace API.
///
/// Blazor /flow (FlowWorkspace.razor) 가 @inject 로 쓰던 DsProjectService(flow/works/calls 구조) +
/// DspDbService(per-flow 런타임 상태/MT/WT/CT + call 상태) + AppSettingsService(per-flow cycle override) +
/// IFlowMetricsService(AASX 기본 boundary 조회 / override 적용 후 재초기화) +
/// PlcToCallMapperService(call → In/Out tag) 를 정적 페이지(/app/flow.html)가 fetch 로 쓸 수 있게 얇게 래핑한다.
/// 신규 데이터 로직 없음(직렬화 경계). 모든 서비스는 싱글톤이라 Blazor 와 동일 인스턴스를 공유한다.
/// 기간별 추이(trend)는 클라이언트가 기존 GET /api/dashboard/flows/{name}/history 를 재사용해 집계하므로 여기서 다루지 않는다.
/// 실시간은 /hubs/monitoring SignalR 이벤트를 트리거로 GET /api/flow/{name} 을 디바운스 refetch 한다.
/// 데모 만료 시 전역 미들웨어가 /api/* 를 503 처리한다(대시보드와 동일, 별도 처리 불필요).
/// </summary>
[ApiController]
[Route("api/flow")]
public class FlowController : ControllerBase
{
    private readonly DsProjectService _project;
    private readonly DspDbService _db;
    private readonly AppSettingsService _settings;
    private readonly IFlowMetricsService _flowMetrics;
    private readonly PlcToCallMapperService _mapper;
    private readonly CycleRecomputeService _recompute;
    private readonly ILogger<FlowController> _logger;

    public FlowController(
        DsProjectService project,
        DspDbService db,
        AppSettingsService settings,
        IFlowMetricsService flowMetrics,
        PlcToCallMapperService mapper,
        CycleRecomputeService recompute,
        ILogger<FlowController> logger)
    {
        _project = project;
        _db = db;
        _settings = settings;
        _flowMetrics = flowMetrics;
        _mapper = mapper;
        _recompute = recompute;
        _logger = logger;
    }

    /// <summary>
    /// 특정 Flow 의 헤더 정보 + Cycle boundary(AASX 기본/현재 override) + KPI 스냅샷.
    /// FlowWorkspace.razor 의 RebuildWorkspace + LoadCycleBoundaryEditor 와 동일한 산출.
    /// </summary>
    [HttpGet("{name}")]
    public ActionResult<FlowDetailDto> Get(string name)
        => BuildDetail(name);

    /// <summary>
    /// Cycle 시작/종료 Call override 저장 후 FlowMetrics 재적용.
    /// FlowWorkspace.razor SaveCycleBoundaryOverrideAsync 와 동일:
    ///  - AASX 기본값과 동일하면 override 제거(null), 다르면 override 저장
    ///  - AppSettingsService.SaveFlowCycleOverride → FlowMetrics.ApplyCycleBoundaryOverrideAsync
    /// 갱신된 FlowDetailDto 반환.
    /// </summary>
    [HttpPost("{name}/cycle-override")]
    public async Task<ActionResult<FlowDetailDto>> SaveCycleOverride(string name, [FromBody] CycleOverrideRequestDto req)
    {
        if (!_project.IsLoaded)
            return BuildDetail(name);

        var flow = _project.GetFlowByName(name);
        if (flow is null)
            return NotFound(new { message = $"Flow '{name}' 을(를) 찾을 수 없습니다." });

        var (defaultStart, defaultEnd) = _flowMetrics.GetAasxCycleBoundaries(flow.Name);

        // 드롭다운 옵션(call 이름)으로 정규화 — 빈 값/미존재 시 AASX 기본값으로 폴백 (Blazor NormalizeCycleSelection 과 동등).
        var options = BuildCallOptions(flow);
        var effectiveStart = NormalizeSelection(req?.StartCallName, options) ?? defaultStart;
        var effectiveEnd = NormalizeSelection(req?.EndCallName, options) ?? defaultEnd;

        // AASX 기본값과 동일하면 override 제거(null).
        var overrideStart = string.Equals(effectiveStart, defaultStart, StringComparison.OrdinalIgnoreCase)
            ? null
            : effectiveStart;
        var overrideEnd = string.Equals(effectiveEnd, defaultEnd, StringComparison.OrdinalIgnoreCase)
            ? null
            : effectiveEnd;

        try
        {
            _settings.SaveFlowCycleOverride(flow.Name, overrideStart, overrideEnd);
            await _flowMetrics.ApplyCycleBoundaryOverrideAsync(flow.Name, effectiveStart, effectiveEnd);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Flow] cycle-override 저장 실패: {Flow}", flow.Name);
            return StatusCode(500, new { message = $"저장 실패: {ex.Message}" });
        }

        // 적용된 Head/Tail 을 공유 project.aasx 의 Call.SequenceLabel 에 박제(Promaker/모니터링 등 외부 소비자용).
        // best-effort — 실패해도 override 저장/재계산은 그대로 진행한다(아래 recompute 트리거의 관용성과 동일).
        try
        {
            _project.WriteSequenceLabelsAndExport(flow.Id, effectiveStart, effectiveEnd);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Flow] SequenceLabel AASX 박제 실패 (override 저장은 유효): {Flow}", flow.Name);
        }

        // 경계 저장 성공 후, 해당 flow 의 과거 dspFlowHistory 전체를 새 경계로 재도출(백그라운드 + 진행률).
        //   대시보드/평균이 "과거 포함" 새 경계 기준으로 갱신되도록(수용기준). 화면은 응답 후 load() 로 즉시 미리보기,
        //   대시보드는 잡 완료(수초~) 시 갱신. 윈도우-부분 재계산은 대시보드 전체평균을 붕괴시켜 폐기했다.
        //   트리거 실패(다른 잡 진행 중)는 저장 성공을 무효화하지 않는다.
        try
        {
            var started = _recompute.TryStartFullHistoryRecompute(flow.Name, effectiveStart, effectiveEnd);
            if (!started)
                _logger.LogWarning("[Flow] 전체 이력 재계산 시작 실패(다른 잡 진행 중): {Flow}", flow.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Flow] cycle-override 과거 재계산 트리거 실패(저장은 유효): {Flow}", flow.Name);
        }

        return BuildDetail(flow.Name);
    }

    /// <summary>전체-이력 재계산 백그라운드 잡 진행 상태(폴링용). 화면은 적용 후 잡 실행 중에만 폴링한다.</summary>
    [HttpGet("recompute-status")]
    public ActionResult<RecomputeJobStatus> RecomputeStatus() => _recompute.Status;

    // ── helpers ──

    private ActionResult<FlowDetailDto> BuildDetail(string name)
    {
        if (!_project.IsLoaded)
            return new FlowDetailDto(name, null, 0, 0, null, null, null, null, null, false,
                Array.Empty<string>(), null, NowTimestamp());

        var flow = _project.GetFlowByName(name);
        if (flow is null)
            return NotFound(new { message = $"Flow '{name}' 을(를) 찾을 수 없습니다." });

        var systemName = ResolveSystemName(flow);

        var works = _project.GetWorks(flow.Id).OrderBy(w => w.Name).ToList();
        var snapshot = _db.Snapshot;
        var runtimeFlow = snapshot.Flows.FirstOrDefault(f => f.FlowName == flow.Name);

        // Call 인벤토리 (KPI 계산용) — Blazor RebuildWorkspace 와 동일 매핑.
        var runtimeCalls = snapshot.CallsByFlow.TryGetValue(flow.Name, out var byFlow)
            ? byFlow
            : new List<Models.CallState>();

        var callRows = works
            .SelectMany(work => _project.GetCalls(work.Id)
                .OrderBy(call => call.Name)
                .Select(call =>
                {
                    var runtime = runtimeCalls.FirstOrDefault(item => item.CallId == call.Id)
                                  ?? snapshot.Calls.FirstOrDefault(item => item.CallId == call.Id);
                    return new
                    {
                        State = runtime?.State ?? "Ready",
                        AverageGoingTime = runtime?.AverageGoingTime,
                    };
                }))
            .ToList();

        int callsCount = callRows.Count;
        int activeCalls = callRows.Count(r => string.Equals(r.State, "Going", StringComparison.OrdinalIgnoreCase));

        var avgSamples = callRows
            .Where(r => r.AverageGoingTime is > 0)
            .Select(r => r.AverageGoingTime!.Value)
            .ToList();
        double? avgCallTimeMs = avgSamples.Count > 0 ? avgSamples.Average() : null;

        // Cycle boundary (AASX 기본 / 현재 override) — LoadCycleBoundaryEditor 와 동일.
        var (defaultStart, defaultEnd) = _flowMetrics.GetAasxCycleBoundaries(flow.Name);
        var overrideConfig = _settings.GetFlowCycleOverride(flow.Name);
        var options = BuildCallOptions(flow);

        var currentStart = NormalizeSelection(overrideConfig?.StartCallName ?? defaultStart, options);
        var currentEnd = NormalizeSelection(overrideConfig?.EndCallName ?? defaultEnd, options);
        bool isOverride = overrideConfig is not null;

        var kpi = new FlowKpiDto(
            runtimeFlow?.CT,
            runtimeFlow?.MT,
            runtimeFlow?.WT,
            activeCalls,
            avgCallTimeMs);

        return new FlowDetailDto(
            flow.Name,
            systemName,
            works.Count,
            callsCount,
            runtimeFlow?.State,
            defaultStart,
            defaultEnd,
            currentStart,
            currentEnd,
            isOverride,
            options.ToArray(),
            kpi,
            NowTimestamp());
    }

    // Blazor SelectedSystemName: 이 flow 를 포함하는 active system 의 이름.
    private string ResolveSystemName(Ds2.Core.Flow flow)
    {
        try
        {
            return _project.GetActiveSystems()
                .FirstOrDefault(system => _project.GetFlows(system.Id).Any(f => f.Name == flow.Name))
                ?.Name ?? "-";
        }
        catch
        {
            return "-";
        }
    }

    // Blazor LoadCycleBoundaryEditor 의 _cycleCallOptions: works → calls 의 call 이름(정렬).
    private List<string> BuildCallOptions(Ds2.Core.Flow flow)
    {
        return _project.GetWorks(flow.Id)
            .OrderBy(w => w.Name)
            .SelectMany(work => _project.GetCalls(work.Id)
                .OrderBy(call => call.Name)
                .Select(call => call.Name))
            .ToList();
    }

    // Blazor NormalizeCycleSelection: 옵션이 없으면 null, 비었으면 첫 옵션, 미존재면 첫 옵션.
    private static string? NormalizeSelection(string? callName, List<string> options)
    {
        if (options.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(callName)) return options[0];

        var trimmed = callName.Trim();
        var match = options.FirstOrDefault(o => string.Equals(o, trimmed, StringComparison.OrdinalIgnoreCase));
        return match ?? options[0];
    }

    private static DateTimeOffset NowTimestamp() => DateTimeOffset.UtcNow;
}

// ── DTOs (전역 camelCase 정책: flowName, systemName, worksCount, currentStartCall, kpi.currentCt ...) ──

public record FlowDetailDto(
    string FlowName,
    string? SystemName,
    int WorksCount,
    int CallsCount,
    string? State,
    string? AasxDefaultStartCall,
    string? AasxDefaultEndCall,
    string? CurrentStartCall,
    string? CurrentEndCall,
    bool IsOverride,
    string[] CallOptions,
    FlowKpiDto? Kpi,
    DateTimeOffset Timestamp);

public record FlowKpiDto(
    int? CurrentCt,
    int? CurrentMt,
    int? CurrentWt,
    int? ActiveCalls,
    double? AvgCallTimeMs);

public record CycleOverrideRequestDto(
    string? StartCallName,
    string? EndCallName);
