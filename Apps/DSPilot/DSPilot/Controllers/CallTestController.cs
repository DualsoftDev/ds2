using DSPilot.Repositories;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 격리형 호스팅용 Call 간트(Call cycle) API.
/// Blazor /call-test 가 쓰던 PlcToCallMapperService + IPlcRepository + CycleAnalysisService + IFlowMetricsService 를 얇게 래핑.
/// 데이터 로직 추가 없음 — 순수 직렬화 경계. Gantt SVG 는 서버 BuildSvg() 대신 클라이언트(Alpine)에서
/// 동일 레이아웃 수학으로 재구성하므로(상호작용 보존), 여기서는 lane/segment/boundary/tail 의 raw JSON 만 내려보낸다.
/// 시간/시각은 DB-local tz(DateTime.Now) 기준으로 서버에서 해석하고, 이미 로컬화된 ISO("o") 문자열로 emit.
/// POST 들은 antiforgery 미적용(AutoValidate/global filter 없음) — 평범한 tokenless JSON fetch.
/// </summary>
[ApiController]
[Route("api/call-test")]
public class CallTestController : ControllerBase
{
    private readonly PlcToCallMapperService _callMapper;
    private readonly IPlcRepository _plcRepository;
    private readonly CycleAnalysisService _cycleAnalysis;
    private readonly IFlowMetricsService _flowMetrics;
    private readonly ILogger<CallTestController> _logger;

    public CallTestController(
        PlcToCallMapperService callMapper,
        IPlcRepository plcRepository,
        CycleAnalysisService cycleAnalysis,
        IFlowMetricsService flowMetrics,
        ILogger<CallTestController> logger)
    {
        _callMapper = callMapper;
        _plcRepository = plcRepository;
        _cycleAnalysis = cycleAnalysis;
        _flowMetrics = flowMetrics;
        _logger = logger;
    }

    /// <summary>Flow 목록 (CallTagPairs 에서 distinct 추출, 정렬). Blazor OnInitializedAsync 동일.</summary>
    [HttpGet("flows")]
    public ActionResult<List<string>> GetFlows()
    {
        var flows = _callMapper.GetAllCallTagPairs()
            .Select(p => p.FlowName)
            .Where(f => !string.IsNullOrEmpty(f))
            .Distinct()
            .OrderBy(f => f)
            .ToList();
        return flows;
    }

    /// <summary>
    /// 최신 로그 시각 + 기본 시간범위(최신-5분 ~ 최신). 프리셋 계산용 기준점.
    /// Blazor OnInitializedAsync / GetEffectiveLatestAsync 와 동일하게 DateTime.Now fallback.
    /// </summary>
    [HttpGet("latest-time")]
    public async Task<ActionResult<CtLatestTimeDto>> GetLatestTime()
    {
        DateTime end;
        try
        {
            var latest = await _plcRepository.GetLatestLogDateTimeAsync();
            end = latest ?? DateTime.Now;
        }
        catch
        {
            end = DateTime.Now;
        }
        var start = end.AddMinutes(-5);
        return new CtLatestTimeDto(IsoLocal(start), IsoLocal(end));
    }

    /// <summary>
    /// 메인 로드 — segment(lane), Call별 (InTag/OutTag) lookup, head/tail 경계, cycle 통계 를 한 번에.
    /// Blazor LoadAsync 의 segment 빌드 + MergeIntervals + ApplyProjectHeadTail + Resolve(Head|Tail) + ComputeCycleStats 를 재현.
    /// headCallId/tailCallId 가 명시되면 그 값으로, 아니면 프로젝트 정의(AASX) 기본 Head/Tail 을 적용.
    /// </summary>
    [HttpPost("load")]
    public async Task<ActionResult<CtLoadDto>> Load([FromBody] CtLoadRequest req)
    {
        if (string.IsNullOrEmpty(req.FlowName))
            return BadRequest("flowName is required");

        // datetime-local(JSON 미한정 ISO) → Kind=Unspecified 로 역직렬화됨.
        // Blazor 주 경로(프리셋 = DateTime.Now, Kind=Local)와 동일하게 Local 로 마킹해야
        // 리포지토리의 ToSqliteUtcString(Local→UTC) 변환이 올바르게 작동한다.
        var start = AsLocal(req.Start);
        var end = AsLocal(req.End);
        if (end <= start)
            return BadRequest("종료 시각은 시작 시각보다 커야 합니다.");

        // segment 데이터는 H/T 와 무관하게 먼저 가져온다.
        var data = await _cycleAnalysis.GetActualIoSignalSegmentsInTimeRangeAsync(req.FlowName, start, end);

        var chartStart = data.ActualEventStartTime ?? start;
        var chartEnd = data.ActualEventEndTime ?? end;
        if (chartEnd <= chartStart) chartEnd = chartStart.AddSeconds(1);

        // lane 단위 grouping + interval merge (Blazor 동일).
        var lanes = data.Items
            .GroupBy(i => i.Lane)
            .Select(g =>
            {
                var first = g.First();
                var intervals = MergeIntervals(
                    g.Select(i => (i.GoingStartTime, i.FinishTime ?? i.GoingStartTime)).ToList());
                var tags = _callMapper.GetCallTagsByCallId(first.CallId);
                return new CtLaneDto(
                    first.CallId.ToString(),
                    first.CallName,
                    first.WorkName,
                    first.Lane,
                    intervals.Select(iv => new CtIntervalDto(IsoLocal(iv.Start), IsoLocal(iv.End))).ToList(),
                    tags?.InTag,
                    tags?.OutTag);
            })
            .OrderBy(l => l.LaneIndex)
            .ToList();

        // 프로젝트 정의 Head/Tail (override 안 했을 때 적용할 기본값) — 클라이언트가 _userOverrodeHeadTail
        // 플래그로 적용 여부를 결정하지만, 서버는 항상 "현재 요청에서 어떤 H/T 로 boundary 를 구할지" 를 확정해야 한다.
        var (projHeadId, projTailId) = ResolveProjectHeadTail(req.FlowName, lanes);

        // 요청에 H/T 명시 여부. headCallId/tailCallId 가 빈 문자열이면 명시적 "해제"(null),
        // null(미전송)이면 프로젝트 기본값을 적용.
        Guid? headId = ResolveRequestedId(req.HeadCallId, projHeadId, req.HeadSpecified);
        Guid? tailId = ResolveRequestedId(req.TailCallId, projTailId, req.TailSpecified);
        if (headId == tailId) tailId = null;

        var (cycleBoundaries, tailEdges) = await ResolveBoundariesAsync(req.FlowName, start, end, headId, tailId, lanes);

        var stats = ComputeCycleStats(cycleBoundaries, tailEdges, chartEnd);

        return new CtLoadDto(
            req.FlowName,
            IsoLocal(chartStart),
            IsoLocal(chartEnd),
            lanes,
            headId?.ToString(),
            tailId?.ToString(),
            projHeadId?.ToString(),
            projTailId?.ToString(),
            cycleBoundaries.Select(IsoLocal).ToList(),
            tailEdges.Select(IsoLocal).ToList(),
            stats.AvgCycleMs,
            stats.AvgActiveMs);
    }

    /// <summary>
    /// 프로젝트 정의 Head 기준 사이클 경계 시각 목록(rising edge). Blazor CycleAnalysis.GetCycleBoundaryTimesAsync 래핑.
    /// H/T 미지정(기본) 경계 source. 결과는 로컬 ISO 문자열.
    /// </summary>
    [HttpPost("cycle-boundaries")]
    public async Task<ActionResult<List<string>>> CycleBoundaries([FromBody] CtCycleBoundaryRequest req)
    {
        if (string.IsNullOrEmpty(req.FlowName))
            return BadRequest("flowName is required");
        var edges = await _cycleAnalysis.GetCycleBoundaryTimesAsync(req.FlowName, AsLocal(req.Start), AsLocal(req.End));
        return edges.OrderBy(t => t).Select(IsoLocal).ToList();
    }

    /// <summary>
    /// 임의 태그의 rising edge 시각 목록. Blazor PlcRepository.FindRisingEdgesAsync 래핑.
    /// 사용자가 Head(InTag) 또는 Tail(OutTag) 을 직접 지정했을 때의 경계/마커 source.
    /// </summary>
    [HttpPost("boundaries")]
    public async Task<ActionResult<List<string>>> Boundaries([FromBody] CtBoundaryRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Tag))
            return BadRequest("tag is required");
        var edges = await _plcRepository.FindRisingEdgesAsync(req.Tag, AsLocal(req.Start), AsLocal(req.End));
        return edges.OrderBy(t => t).Select(IsoLocal).ToList();
    }

    /// <summary>
    /// H/T 토글에 반응해 오버레이(boundary + tail edge)만 재조회. Blazor ResolveOverlaysAsync 와 동일.
    /// segment(lane) 는 재사용하므로 다시 보내지 않는다. lane 정보는 InTag/OutTag lookup 용으로만 사용.
    /// </summary>
    [HttpPost("resolve-overlays")]
    public async Task<ActionResult<CtOverlayDto>> ResolveOverlays([FromBody] CtOverlayRequest req)
    {
        if (string.IsNullOrEmpty(req.FlowName))
            return BadRequest("flowName is required");

        var start = AsLocal(req.Start);
        var end = AsLocal(req.End);

        Guid? headId = ParseGuid(req.HeadCallId);
        Guid? tailId = ParseGuid(req.TailCallId);

        // Head 경계
        List<DateTime> cycleBoundaries;
        if (headId.HasValue && !string.IsNullOrWhiteSpace(req.HeadInTag))
        {
            cycleBoundaries = await _plcRepository.FindRisingEdgesAsync(req.HeadInTag!, start, end);
        }
        else
        {
            cycleBoundaries = await _cycleAnalysis.GetCycleBoundaryTimesAsync(req.FlowName, start, end);
        }

        // Tail 마커
        List<DateTime> tailEdges;
        if (tailId.HasValue && !string.IsNullOrWhiteSpace(req.TailOutTag))
        {
            tailEdges = await _plcRepository.FindRisingEdgesAsync(req.TailOutTag!, start, end);
        }
        else
        {
            tailEdges = new List<DateTime>();
        }

        cycleBoundaries = cycleBoundaries.OrderBy(t => t).ToList();
        tailEdges = tailEdges.OrderBy(t => t).ToList();

        var chartEnd = end;
        var stats = ComputeCycleStats(cycleBoundaries, tailEdges, chartEnd);

        return new CtOverlayDto(
            cycleBoundaries.Select(IsoLocal).ToList(),
            tailEdges.Select(IsoLocal).ToList(),
            stats.AvgCycleMs,
            stats.AvgActiveMs);
    }

    // ── helpers (Blazor @code 1:1 이식) ───────────────────────────────────────

    /// <summary>프로젝트(AASX) 정의 Head/Tail 을 lane 매칭해서 CallId 로 변환. Blazor ApplyProjectHeadTail 동일.</summary>
    private (Guid? headId, Guid? tailId) ResolveProjectHeadTail(string flowName, List<CtLaneDto> lanes)
    {
        try
        {
            var (headName, tailName) = _flowMetrics.GetAasxCycleBoundaries(flowName);
            Guid? headId = !string.IsNullOrEmpty(headName)
                ? MatchLaneId(lanes, headName)
                : null;
            Guid? tailId = !string.IsNullOrEmpty(tailName)
                ? MatchLaneId(lanes, tailName)
                : null;
            if (headId == tailId) tailId = null;
            return (headId, tailId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CallTest] failed to apply project head/tail for flow '{Flow}'", flowName);
            return (null, null);
        }
    }

    private static Guid? MatchLaneId(List<CtLaneDto> lanes, string callName)
    {
        var lane = lanes.FirstOrDefault(l => string.Equals(l.CallName, callName, StringComparison.OrdinalIgnoreCase));
        return lane is null ? null : ParseGuid(lane.CallId);
    }

    /// <summary>
    /// 요청 H/T id 해석: specified=false → 프로젝트 기본값; specified=true + 빈/null → 명시적 해제(null);
    /// specified=true + 값 → 그 값.
    /// </summary>
    private static Guid? ResolveRequestedId(string? requested, Guid? projectDefault, bool specified)
    {
        if (!specified) return projectDefault;
        return ParseGuid(requested);
    }

    private async Task<(List<DateTime> cycleBoundaries, List<DateTime> tailEdges)> ResolveBoundariesAsync(
        string flowName, DateTime start, DateTime end, Guid? headId, Guid? tailId, List<CtLaneDto> lanes)
    {
        var headInTag = headId.HasValue ? lanes.FirstOrDefault(l => l.CallId == headId.Value.ToString())?.InTag : null;
        var tailOutTag = tailId.HasValue ? lanes.FirstOrDefault(l => l.CallId == tailId.Value.ToString())?.OutTag : null;

        Task<List<DateTime>> headTask = headId.HasValue && !string.IsNullOrWhiteSpace(headInTag)
            ? _plcRepository.FindRisingEdgesAsync(headInTag!, start, end)
            : _cycleAnalysis.GetCycleBoundaryTimesAsync(flowName, start, end);

        Task<List<DateTime>> tailTask = tailId.HasValue && !string.IsNullOrWhiteSpace(tailOutTag)
            ? _plcRepository.FindRisingEdgesAsync(tailOutTag!, start, end)
            : Task.FromResult(new List<DateTime>());

        await Task.WhenAll(headTask, tailTask);

        return (
            headTask.Result.OrderBy(t => t).ToList(),
            tailTask.Result.OrderBy(t => t).ToList());
    }

    /// <summary>사이클 경계 간 CT 평균 + (Head↑ → 사이클 내 첫 Tail↑) 활성구간 평균. Blazor ComputeCycleStats 동일.</summary>
    private static (double? AvgCycleMs, double? AvgActiveMs) ComputeCycleStats(
        List<DateTime> cycleBoundaries, List<DateTime> tailEdges, DateTime chartEnd)
    {
        double? avgCycleMs = null;
        double? avgActiveMs = null;

        if (cycleBoundaries.Count >= 2)
        {
            var diffs = new List<double>();
            for (int i = 0; i < cycleBoundaries.Count - 1; i++)
                diffs.Add((cycleBoundaries[i + 1] - cycleBoundaries[i]).TotalMilliseconds);
            if (diffs.Count > 0) avgCycleMs = diffs.Average();
        }

        if (tailEdges.Count > 0 && cycleBoundaries.Count > 0)
        {
            var actives = new List<double>();
            int ti = 0;
            for (int i = 0; i < cycleBoundaries.Count; i++)
            {
                var cStart = cycleBoundaries[i];
                var cEnd = i + 1 < cycleBoundaries.Count ? cycleBoundaries[i + 1] : chartEnd;
                while (ti < tailEdges.Count && tailEdges[ti] <= cStart) ti++;
                if (ti < tailEdges.Count && tailEdges[ti] < cEnd)
                {
                    actives.Add((tailEdges[ti] - cStart).TotalMilliseconds);
                    ti++;
                }
            }
            if (actives.Count > 0) avgActiveMs = actives.Average();
        }

        return (avgCycleMs, avgActiveMs);
    }

    private static List<(DateTime Start, DateTime End)> MergeIntervals(List<(DateTime Start, DateTime End)> intervals)
    {
        var merged = new List<(DateTime Start, DateTime End)>();
        if (intervals.Count == 0) return merged;

        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        var curS = intervals[0].Start;
        var curE = intervals[0].End;
        for (int i = 1; i < intervals.Count; i++)
        {
            var (s, e) = intervals[i];
            if (s <= curE)
            {
                if (e > curE) curE = e;
            }
            else
            {
                if (curE > curS) merged.Add((curS, curE));
                curS = s;
                curE = e;
            }
        }
        if (curE > curS) merged.Add((curS, curE));
        return merged;
    }

    private static Guid? ParseGuid(string? s)
        => Guid.TryParse(s, out var g) ? g : null;

    /// <summary>
    /// 미한정(Unspecified) DateTime 을 Local 로 마킹. JSON 으로 들어온 datetime-local 값은
    /// Kind=Unspecified 인데, 리포지토리(ToSqliteUtcString)는 Local 만 UTC 변환하므로
    /// Blazor 프리셋 경로(DateTime.Now=Local)와 일치시키기 위해 Local 로 지정한다.
    /// </summary>
    private static DateTime AsLocal(DateTime dt)
        => dt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dt, DateTimeKind.Local)
            : dt;

    /// <summary>로컬 tz ISO("o"). 클라이언트는 new Date() 로 파싱 후 표시.</summary>
    private static string IsoLocal(DateTime dt) => dt.ToString("o");
}

// ── DTOs (positional records → camelCase 자동) ─────────────────────────────────
// 전역 네임스페이스(DSPilot.Controllers) 충돌 방지를 위해 Ct* 접두. JSON 출력은 타입명과 무관.

public record CtLatestTimeDto(string Start, string End);

public record CtLoadRequest(
    string FlowName,
    DateTime Start,
    DateTime End,
    string? HeadCallId,
    string? TailCallId,
    bool HeadSpecified,
    bool TailSpecified);

public record CtOverlayRequest(
    string FlowName,
    DateTime Start,
    DateTime End,
    string? HeadCallId,
    string? TailCallId,
    string? HeadInTag,
    string? TailOutTag);

public record CtCycleBoundaryRequest(string FlowName, DateTime Start, DateTime End);

public record CtBoundaryRequest(string Tag, DateTime Start, DateTime End);

public record CtIntervalDto(string Start, string End);

public record CtLaneDto(
    string CallId,
    string CallName,
    string WorkName,
    int LaneIndex,
    List<CtIntervalDto> Intervals,
    string? InTag,
    string? OutTag);

public record CtLoadDto(
    string FlowName,
    string ChartStart,
    string ChartEnd,
    List<CtLaneDto> Lanes,
    string? HeadCallId,
    string? TailCallId,
    string? ProjectHeadCallId,
    string? ProjectTailCallId,
    List<string> CycleBoundaries,
    List<string> TailEdges,
    double? AvgCycleMs,
    double? AvgActiveMs);

public record CtOverlayDto(
    List<string> CycleBoundaries,
    List<string> TailEdges,
    double? AvgCycleMs,
    double? AvgActiveMs);
