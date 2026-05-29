using System.Globalization;
using DSPilot.Models.Analysis;
using DSPilot.Repositories;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 격리형 호스팅용 Cycle-Time Analysis API.
/// Blazor /cycle-time-analysis 가 쓰던 CycleAnalysisService(IO 세그먼트) + PlcToCallMapperService(Flow 목록)
/// + IFlowMetricsService(사이클 경계 Call) + IPlcRepository(rising edge / 최신 로그) + AppSettingsService
/// (비가동 판정 임계) 를 얇게 래핑. 데이터 로직은 서비스 그대로 — 컨트롤러는 직렬화 경계.
///
/// gantt-data 한 번 호출로 페이지가 필요한 모든 것(lanes, bars, axis 정보, 비가동 구간, GAP top-5) 을 내려보냄.
/// SVG 렌더 / 정렬 / 선택 오버레이 / 바 클릭은 전부 클라이언트(static html + JS)에서 처리 — 라운드트립 없음.
/// CSV 는 클라이언트가 로드된 데이터로 직접 빌드(CycleTimeChartExporter.BuildCsvBytes 포맷 동일).
/// 시간 범위 프리셋 해석은 Blazor 페이지와 동일하게 DateTime.Now / DB 최신 로그 기준으로 클라이언트가 계산.
/// </summary>
[ApiController]
[Route("api/cycle-analysis")]
public class CycleAnalysisController : ControllerBase
{
    private readonly PlcToCallMapperService _callMapper;
    private readonly IPlcRepository _plcRepository;
    private readonly CycleAnalysisService _cycleAnalysis;
    private readonly IFlowMetricsService _flowMetrics;
    private readonly AppSettingsService _settings;

    public CycleAnalysisController(
        PlcToCallMapperService callMapper,
        IPlcRepository plcRepository,
        CycleAnalysisService cycleAnalysis,
        IFlowMetricsService flowMetrics,
        AppSettingsService settings)
    {
        _callMapper = callMapper;
        _plcRepository = plcRepository;
        _cycleAnalysis = cycleAnalysis;
        _flowMetrics = flowMetrics;
        _settings = settings;
    }

    /// <summary>Flow 선택 목록 — Blazor LoadFlows() 와 동일(중복 제거 + 이름순).</summary>
    [HttpGet("flows")]
    public ActionResult<List<string>> GetFlows()
    {
        var flows = _callMapper.GetAllMappings()
            .Select(m => m.FlowName)
            .Where(f => !string.IsNullOrEmpty(f))
            .Distinct()
            .OrderBy(f => f)
            .ToList();
        return flows;
    }

    /// <summary>
    /// "최근 X분/X시간" 의 기준 시각 = DB 최신 로그 시각(없으면 Now).
    /// Blazor GetEffectiveLatestTimeAsync() 와 동일. DateTime.Now 는 DB 로컬 tz.
    /// 시간창 프리셋은 클라이언트가 이 값을 받아 +/- 로 계산한다.
    /// </summary>
    [HttpGet("latest-time")]
    public async Task<ActionResult<CycleLatestTimeDto>> GetLatestTime()
    {
        DateTime latest;
        try
        {
            var v = await _plcRepository.GetLatestLogDateTimeAsync();
            latest = v ?? DateTime.Now;
        }
        catch
        {
            latest = DateTime.Now;
        }
        return new CycleLatestTimeDto(latest.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 갠트 데이터 일괄 조회. Blazor LoadGanttData() 의 서버 측 작업 전부를 한 응답으로:
    ///   - 실제 IO 신호 세그먼트(GetActualIoSignalSegmentsInTimeRangeAsync)
    ///   - 비가동 구간(LoadIdleEdgesAsync + BuildIdleRegionsFromEdges 와 동일)
    ///   - GAP top-5(CalculateLongestGap 와 동일: Lane 내 인접 세그먼트 gap, 비가동 구간 제외)
    /// antiforgery 미적용 평범한 JSON POST.
    /// </summary>
    [HttpPost("gantt-data")]
    public async Task<ActionResult<GanttDataDto>> GetGanttData([FromBody] GanttRequest req)
    {
        if (string.IsNullOrEmpty(req.FlowName))
            return BadRequest("flowName required");

        var start = ParseLocal(req.Start) ?? DateTime.Now.AddMinutes(-1);
        var end = ParseLocal(req.End) ?? DateTime.Now;

        var dataTask = _cycleAnalysis.GetActualIoSignalSegmentsInTimeRangeAsync(req.FlowName, start, end);
        var idleEdgesTask = LoadIdleEdgesAsync(req.FlowName, start, end);
        await Task.WhenAll(dataTask, idleEdgesTask);

        var data = dataTask.Result;
        var idleEdges = idleEdgesTask.Result;

        var idleRegions = BuildIdleRegionsFromEdges(idleEdges, start, end);

        // 차트 x 축 기준 = ActualEvent* (CycleTimeChartRenderer 와 동일하게 narrow)
        var chartStart = data.ActualEventStartTime ?? data.StartTime;
        var chartEnd = data.ActualEventEndTime ?? (data.EndTime ?? data.StartTime.AddSeconds(1));

        var topGaps = CalculateTopGaps(data, idleRegions);

        var items = data.Items
            .Select(i => new GanttItemDto(
                i.CallName,
                i.WorkName,
                i.FlowName,
                i.TagName,
                i.TagAddress,
                i.EventType,
                i.Lane,
                Iso(i.GoingStartTime),
                i.FinishTime.HasValue ? Iso(i.FinishTime.Value) : null,
                CycleTimeChartRenderer.GetItemDurationMs(i)))
            .ToList();

        var dto = new GanttDataDto(
            data.FlowName,
            Iso(data.StartTime),
            data.EndTime.HasValue ? Iso(data.EndTime.Value) : null,
            data.ActualEventStartTime.HasValue ? Iso(data.ActualEventStartTime.Value) : null,
            data.ActualEventEndTime.HasValue ? Iso(data.ActualEventEndTime.Value) : null,
            Iso(chartStart),
            Iso(chartEnd),
            data.CT ?? 0,
            data.TotalLanes,
            data.LaneLabels,
            data.TotalEventCount,
            data.RenderedEventCount,
            data.IsTruncated,
            data.StartTime.ToString("HH:mm:ss"),
            data.EndTime?.ToString("HH:mm:ss") ?? "N/A",
            items,
            idleRegions.Select(r => new IdleRegionDto(Iso(r.Start), Iso(r.End))).ToList(),
            topGaps);

        return dto;
    }

    // ─── Blazor LoadIdleEdgesAsync 와 동일 ────────────────────────────────────
    private async Task<List<DateTime>> LoadIdleEdgesAsync(string flowName, DateTime start, DateTime end)
    {
        var s = _settings.LoadSettings();
        if (s.HistoryView.MaxCycleTimeMs <= 0 && s.HistoryView.MinCycleTimeMs <= 0)
            return new List<DateTime>();

        var (headCallName, _) = _flowMetrics.GetCycleBoundaryCallNames(flowName);
        string? inTagAddress = null;
        if (!string.IsNullOrEmpty(headCallName))
        {
            var allPairs = _callMapper.GetAllCallTagPairs();
            var match = allPairs.FirstOrDefault(p => p.FlowName == flowName && p.CallName == headCallName);
            if (match != default) inTagAddress = match.InTag;
        }

        if (!string.IsNullOrEmpty(inTagAddress))
            return await _plcRepository.FindRisingEdgesAsync(inTagAddress, start, end);
        return await _cycleAnalysis.GetCycleBoundaryTimesAsync(flowName, start, end);
    }

    // ─── Blazor BuildIdleRegionsFromEdges 와 동일 ─────────────────────────────
    private List<(DateTime Start, DateTime End)> BuildIdleRegionsFromEdges(
        List<DateTime> edges, DateTime chartStart, DateTime chartEnd)
    {
        var regions = new List<(DateTime Start, DateTime End)>();
        if (edges.Count == 0) return regions;

        var s = _settings.LoadSettings();
        var maxCT = s.HistoryView.MaxCycleTimeMs;
        var minCT = s.HistoryView.MinCycleTimeMs;
        if (maxCT <= 0 && minCT <= 0) return regions;

        var sorted = edges.OrderBy(t => t).ToList();

        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var cycleStart = sorted[i];
            var cycleEnd = sorted[i + 1];
            var ms = (cycleEnd - cycleStart).TotalMilliseconds;
            if ((maxCT > 0 && ms > maxCT) || (minCT > 0 && ms < minCT))
                regions.Add((cycleStart, cycleEnd));
        }

        if (maxCT > 0)
        {
            var lastEdge = sorted[^1];
            if ((chartEnd - lastEdge).TotalMilliseconds > maxCT)
                regions.Add((lastEdge, chartEnd));

            var firstEdge = sorted[0];
            if ((firstEdge - chartStart).TotalMilliseconds > maxCT)
                regions.Add((chartStart, firstEdge));
        }

        return regions;
    }

    // ─── Blazor CalculateLongestGap 와 동일(상위 5개) ─────────────────────────
    private static List<GapDto> CalculateTopGaps(
        GanttChartData data, List<(DateTime Start, DateTime End)> idleRegions)
    {
        if (data.Items.Count < 2) return new List<GapDto>();

        bool OverlapsIdle(DateTime gs, DateTime ge)
        {
            foreach (var (iStart, iEnd) in idleRegions)
                if (gs < iEnd && ge > iStart) return true;
            return false;
        }

        var allGaps = new List<(DateTime Start, DateTime End, double Duration, int Lane, string LaneName)>();
        var laneGroups = data.Items
            .OrderBy(i => i.GoingStartTime)
            .ThenBy(i => i.FinishTime ?? i.GoingStartTime)
            .GroupBy(i => i.Lane);

        foreach (var group in laneGroups)
        {
            var lane = group.Key;
            var laneName = lane >= 0 && lane < data.LaneLabels.Count ? data.LaneLabels[lane] : $"Lane {lane}";
            var laneItems = group.OrderBy(i => i.GoingStartTime).ToList();

            for (int i = 0; i < laneItems.Count - 1; i++)
            {
                var currentEnd = laneItems[i].FinishTime ?? laneItems[i].GoingStartTime;
                var nextStart = laneItems[i + 1].GoingStartTime;
                var gapMs = (nextStart - currentEnd).TotalMilliseconds;
                if (gapMs > 0 && !OverlapsIdle(currentEnd, nextStart))
                    allGaps.Add((currentEnd, nextStart, gapMs, lane, laneName));
            }
        }

        return allGaps
            .OrderByDescending(g => g.Duration)
            .Take(5)
            .Select(g => new GapDto(Iso(g.Start), Iso(g.End), g.Duration, g.Lane, g.LaneName,
                g.Start.ToString("HH:mm:ss"), g.End.ToString("HH:mm:ss")))
            .ToList();
    }

    private static string Iso(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static DateTime? ParseLocal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dt) ? dt : null;
    }
}

// ─── DTOs (positional records → camelCase 자동) ───────────────────────────────

public record GanttRequest(string FlowName, string? Start, string? End);

public record CycleLatestTimeDto(string Latest);

/// <summary>
/// 갠트 1회 응답. items 의 시각은 ISO 로컬 문자열(클라이언트가 Date 파싱 → ms 차이로 좌표 계산).
/// chartStart/chartEnd 가 SVG x 축 기준(서버가 ActualEvent* 로 narrow 한 것과 동일).
/// </summary>
public record GanttDataDto(
    string FlowName,
    string StartTime,
    string? EndTime,
    string? ActualEventStartTime,
    string? ActualEventEndTime,
    string ChartStartTime,
    string ChartEndTime,
    int CT,
    int TotalLanes,
    List<string> LaneLabels,
    int TotalEventCount,
    int RenderedEventCount,
    bool IsTruncated,
    string StartTimeDisplay,
    string EndTimeDisplay,
    List<GanttItemDto> Items,
    List<IdleRegionDto> IdleRegions,
    List<GapDto> TopGaps);

/// <summary>durationMs = CycleTimeChartRenderer.GetItemDurationMs 와 동일(서버 계산).</summary>
public record GanttItemDto(
    string CallName,
    string WorkName,
    string FlowName,
    string TagName,
    string TagAddress,
    IOEventType EventType,
    int Lane,
    string GoingStartTime,
    string? FinishTime,
    int DurationMs);

public record IdleRegionDto(string Start, string End);

public record GapDto(
    string Start,
    string End,
    double Duration,
    int Lane,
    string LaneName,
    string StartDisplay,
    string EndDisplay);
