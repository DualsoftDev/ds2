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
    private readonly ILogger<FlowController> _logger;

    public FlowController(
        DsProjectService project,
        DspDbService db,
        AppSettingsService settings,
        IFlowMetricsService flowMetrics,
        PlcToCallMapperService mapper,
        ILogger<FlowController> logger)
    {
        _project = project;
        _db = db;
        _settings = settings;
        _flowMetrics = flowMetrics;
        _mapper = mapper;
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

        return BuildDetail(flow.Name);
    }

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
        int errorCalls = callRows.Count(r => string.Equals(r.State, "Error", StringComparison.OrdinalIgnoreCase));

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
            errorCalls,
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
    int? ErrorCalls,
    double? AvgCallTimeMs);

public record CycleOverrideRequestDto(
    string? StartCallName,
    string? EndCallName);
