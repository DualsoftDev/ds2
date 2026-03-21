using DSPilot.Models.Analysis;
using DSPilot.Models.Plc;
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// 사이클 분석 서비스 - PlcTagLog 기반 실시간 사이클 분석
/// </summary>
public class CycleAnalysisService
{
    private const int MaxRenderedGanttItems = 1200;
    private readonly ILogger<CycleAnalysisService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DsProjectService _projectService;
    private readonly PlcToCallMapperService _mapper;

    public CycleAnalysisService(
        ILogger<CycleAnalysisService> logger,
        IServiceScopeFactory _scopeFactory,
        DsProjectService projectService,
        PlcToCallMapperService mapper)
    {
        _logger = logger;
        this._scopeFactory = _scopeFactory;
        _projectService = projectService;
        _mapper = mapper;
    }

    /// <summary>
    /// 최신 N개 사이클 경계 탐지
    /// </summary>
    /// <param name="flowName">Flow 이름</param>
    /// <param name="cycleCount">조회할 사이클 개수 (기본 3개)</param>
    /// <returns>사이클 경계 목록 (역순: 최신 = #1)</returns>
    public async Task<List<CycleBoundary>> DetectRecentCyclesAsync(
        string flowName, int cycleCount = 3)
    {
        using var scope = _scopeFactory.CreateScope();
        var plcRepo = scope.ServiceProvider.GetRequiredService<IPlcRepository>();

        // Flow의 Head Call 찾기
        var flow = _projectService.GetFlowByName(flowName);
        if (flow == null)
        {
            _logger.LogWarning("Flow '{FlowName}' not found", flowName);
            return new List<CycleBoundary>();
        }

        var headCall = _projectService.GetHeadCall(flow.Id);
        if (headCall == null)
        {
            _logger.LogWarning("Flow '{FlowName}' has no head call", flowName);
            return new List<CycleBoundary>();
        }

        // Head Call의 InTag 주소 찾기
        var headCallTags = _mapper.GetCallTagsByCallId(headCall.Id);
        if (!headCallTags.HasValue || headCallTags.Value.InTag == null)
        {
            _logger.LogWarning("Head Call '{CallName}' (ID: {CallId}) has no InTag", headCall.Name, headCall.Id);
            return new List<CycleBoundary>();
        }

        // PLC 로그 시간 범위 조회
        var latestLogTime = await plcRepo.GetLatestLogDateTimeAsync();
        var oldestLogTime = await plcRepo.GetOldestLogDateTimeAsync();

        if (!latestLogTime.HasValue || !oldestLogTime.HasValue)
        {
            _logger.LogWarning("No PLC log data available");
            return new List<CycleBoundary>();
        }

        // Head Call InTag의 Rising Edge 찾기
        _logger.LogInformation(
            "🔍 Searching rising edges for Head Call '{CallName}', InTag='{InTag}', Time range: {Start} ~ {End}",
            headCall.Name, headCallTags.Value.InTag, oldestLogTime.Value, latestLogTime.Value);

        var risingEdges = await plcRepo.FindRisingEdgesAsync(
            headCallTags.Value.InTag, oldestLogTime.Value, latestLogTime.Value);

        if (risingEdges.Count == 0)
        {
            _logger.LogWarning(
                "⚠ No rising edges found for Head Call '{CallName}' (InTag: '{InTag}'). " +
                "Try selecting a different Flow or wait for more PLC data.",
                headCall.Name, headCallTags.Value.InTag);
            return new List<CycleBoundary>();
        }

        _logger.LogInformation(
            "✅ Found {Count} rising edges for Head Call '{CallName}'",
            risingEdges.Count, headCall.Name);

        // 최신 N개만 선택 (역순)
        var selectedEdges = risingEdges
            .OrderByDescending(t => t)
            .Take(cycleCount + 1)  // +1: 마지막 사이클의 종료 시점 포함
            .OrderBy(t => t)
            .ToList();

        // 사이클 경계 생성
        var cycles = new List<CycleBoundary>();
        for (int i = 0; i < selectedEdges.Count - 1; i++)
        {
            cycles.Add(new CycleBoundary
            {
                CycleNumber = selectedEdges.Count - 1 - i,  // 역순 번호
                FlowName = flowName,
                StartTime = selectedEdges[i],
                EndTime = selectedEdges[i + 1]
            });
        }

        // 마지막 사이클 (미완료 가능성)
        if (selectedEdges.Count >= cycleCount + 1)
        {
            // cycleCount 개만 반환
            cycles = cycles.Take(cycleCount).ToList();
        }
        else
        {
            // 마지막 미완료 사이클 추가
            cycles.Add(new CycleBoundary
            {
                CycleNumber = 1,
                FlowName = flowName,
                StartTime = selectedEdges.Last(),
                EndTime = null  // 미완료
            });
        }

        // 번호 재조정 (역순: 최신 = 1)
        cycles = cycles.OrderByDescending(c => c.StartTime).ToList();
        for (int i = 0; i < cycles.Count; i++)
        {
            cycles[i].CycleNumber = i + 1;
        }

        _logger.LogInformation(
            "Detected {Count} cycles for Flow '{FlowName}'",
            cycles.Count, flowName);

        return cycles;
    }

    /// <summary>
    /// 특정 사이클의 모든 IO 이벤트 수집
    /// </summary>
    public async Task<CycleData> GetCycleDataAsync(CycleBoundary cycle)
    {
        using var scope = _scopeFactory.CreateScope();
        var plcRepo = scope.ServiceProvider.GetRequiredService<IPlcRepository>();

        // Flow의 모든 Call 가져오기
        var flow = _projectService.GetFlowByName(cycle.FlowName);
        if (flow == null)
        {
            return new CycleData { Cycle = cycle, IOEvents = new List<CallIOEvent>() };
        }

        var works = _projectService.GetWorks(flow.Id);

        // 모든 Call의 InTag/OutTag 주소 수집
        var tagAddresses = new List<string>();
        var callTagMap = new Dictionary<string, (string CallName, string WorkName, IOEventType EventType)>();

        foreach (var work in works)
        {
            var calls = _projectService.GetCalls(work.Id);
            foreach (var call in calls)
            {
                var tags = _mapper.GetCallTagsByCallId(call.Id);
                if (!tags.HasValue) continue;

                if (tags.Value.InTag != null)
                {
                    tagAddresses.Add(tags.Value.InTag);
                    callTagMap[tags.Value.InTag] = (call.Name, work.Name, IOEventType.InTag);
                }

                if (tags.Value.OutTag != null)
                {
                    tagAddresses.Add(tags.Value.OutTag);
                    callTagMap[tags.Value.OutTag] = (call.Name, work.Name, IOEventType.OutTag);
                }
            }
        }

        // 사이클 구간의 Rising Edge 로그만 조회
        var endTime = cycle.EndTime ?? DateTime.Now;  // 미완료 사이클은 현재 시각
        var logs = await plcRepo.GetMultipleTagRisingEdgesInRangeAsync(
            tagAddresses.Distinct().ToList(),
            cycle.StartTime,
            endTime);

        var ioEvents = new List<CallIOEvent>();

        foreach (var log in logs.OrderBy(l => l.DateTime))
        {
            var address = log.PlcTag?.Address;
            if (string.IsNullOrEmpty(address) || !callTagMap.ContainsKey(address))
                continue;

            var (callName, workName, eventType) = callTagMap[address];
            var relativeTimeMs = (int)(log.DateTime - cycle.StartTime).TotalMilliseconds;
            var tagName = string.IsNullOrWhiteSpace(log.PlcTag?.Name) ? address : log.PlcTag!.Name;

            ioEvents.Add(new CallIOEvent
            {
                CallName = callName,
                FlowName = cycle.FlowName,
                EventType = eventType,
                Timestamp = log.DateTime,
                RelativeTimeMs = relativeTimeMs,
                TagName = tagName,
                TagAddress = address
            });
        }

        _logger.LogInformation(
            "Collected {Count} IO events for Cycle #{CycleNumber} of Flow '{FlowName}'",
            ioEvents.Count, cycle.CycleNumber, cycle.FlowName);

        return new CycleData
        {
            Cycle = cycle,
            IOEvents = ioEvents
        };
    }

    /// <summary>
    /// Gantt 차트 데이터 생성 (InTag/OutTag 각각 별도 바 표시)
    /// </summary>
    public Task<GanttChartData> GenerateGanttChartAsync(CycleData cycleData)
    {
        // InTag와 OutTag를 각각 별도 항목으로 표시
        // Call별로 InTag와 OutTag를 매칭하여 실제 Going 시간 계산
        var callEvents = cycleData.IOEvents
            .GroupBy(e => e.CallName)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.RelativeTimeMs).ToList());

        var ganttItems = cycleData.IOEvents
            .OrderBy(e => e.RelativeTimeMs)
            .Select(e =>
            {
                // 같은 Call의 이벤트 리스트
                var eventsForCall = callEvents.GetValueOrDefault(e.CallName, new List<CallIOEvent>());

                // InTag인 경우: 다음 OutTag까지의 시간을 Duration으로 설정
                // OutTag인 경우: 이전 InTag부터의 시간을 Duration으로 설정
                int? duration = null;
                int? relativeEnd = null;
                DateTime? finishTime = null;

                if (e.EventType == IOEventType.InTag)
                {
                    // 현재 InTag 이후의 첫 번째 OutTag 찾기
                    var nextOutTag = eventsForCall
                        .FirstOrDefault(evt => evt.EventType == IOEventType.OutTag && evt.RelativeTimeMs > e.RelativeTimeMs);

                    if (nextOutTag != null)
                    {
                        duration = nextOutTag.RelativeTimeMs - e.RelativeTimeMs;
                        relativeEnd = nextOutTag.RelativeTimeMs;
                        finishTime = nextOutTag.Timestamp;
                    }
                }
                else // OutTag
                {
                    // 현재 OutTag 이전의 마지막 InTag 찾기
                    var prevInTag = eventsForCall
                        .LastOrDefault(evt => evt.EventType == IOEventType.InTag && evt.RelativeTimeMs < e.RelativeTimeMs);

                    if (prevInTag != null)
                    {
                        duration = e.RelativeTimeMs - prevInTag.RelativeTimeMs;
                        finishTime = e.Timestamp;
                    }
                }

                return new GanttChartItem
                {
                    CallName = e.CallName,
                    FlowName = e.FlowName,
                    TagName = e.TagName,
                    TagAddress = e.TagAddress,
                    RelativeStart = e.RelativeTimeMs,
                    RelativeEnd = relativeEnd ?? e.RelativeTimeMs,
                    Duration = duration ?? 0,
                    Lane = 0,  // 레인은 UI에서 재계산
                    GoingStartTime = e.Timestamp,
                    FinishTime = finishTime ?? e.Timestamp,
                    EventType = e.EventType  // InTag / OutTag 구분
                };
            })
            .ToList();

        // 레인은 Flow의 Call x In/Out 기준으로 고정해 최대 Call*2 레인을 확보한다.
        var laneDefinitions = BuildLaneDefinitions(cycleData.Cycle.FlowName);
        var laneLabels = AssignConfiguredLanes(ganttItems, laneDefinitions);

        var ganttData = new GanttChartData
        {
            CycleId = cycleData.Cycle.CycleNumber.ToString(),
            FlowName = cycleData.Cycle.FlowName,
            CycleNumber = cycleData.Cycle.CycleNumber,
            StartTime = cycleData.Cycle.StartTime,
            EndTime = cycleData.Cycle.EndTime,
            CT = cycleData.Cycle.CycleTimeMs,
            MT = null,  // 향후 구현
            WT = null,  // 향후 구현
            TotalLanes = laneLabels.Count,
            LaneLabels = laneLabels,
            Items = ganttItems,
            CriticalPath = new List<string>()  // 향후 구현
        };

        return Task.FromResult(ganttData);
    }

    /// <summary>
    /// Flow의 Call x In/Out 조합으로 레인 정의 생성
    /// </summary>
    private List<LaneDefinition> BuildLaneDefinitions(string flowName)
    {
        var flow = _projectService.GetFlowByName(flowName);
        if (flow == null)
            return new List<LaneDefinition>();

        var works = _projectService.GetWorks(flow.Id);
        var laneDefinitions = new List<LaneDefinition>();

        foreach (var work in works)
        {
            var calls = _projectService.GetCalls(work.Id);
            foreach (var call in calls)
            {
                var tags = _mapper.GetCallTagsByCallId(call.Id);
                if (!tags.HasValue)
                    continue;

                if (!string.IsNullOrWhiteSpace(tags.Value.InTag))
                {
                    laneDefinitions.Add(new LaneDefinition(
                        BuildLaneKey(call.Name, IOEventType.InTag),
                        $"{call.Name} [IN]"));
                }

                if (!string.IsNullOrWhiteSpace(tags.Value.OutTag))
                {
                    laneDefinitions.Add(new LaneDefinition(
                        BuildLaneKey(call.Name, IOEventType.OutTag),
                        $"{call.Name} [OUT]"));
                }
            }
        }

        return laneDefinitions;
    }

    /// <summary>
    /// Gantt 차트 항목에 Flow 설정 기준 레인 할당
    /// </summary>
    private static List<string> AssignConfiguredLanes(
        List<GanttChartItem> items,
        List<LaneDefinition> laneDefinitions)
    {
        var laneLabels = laneDefinitions
            .Select(definition => definition.Label)
            .ToList();

        var laneByKey = laneDefinitions
            .Select((definition, index) => new { definition.LaneKey, Index = index })
            .ToDictionary(item => item.LaneKey, item => item.Index, StringComparer.OrdinalIgnoreCase);

        foreach (var item in items.OrderBy(i => i.RelativeStart))
        {
            var laneKey = BuildLaneKey(item.CallName, item.EventType);
            if (!laneByKey.TryGetValue(laneKey, out var lane))
            {
                lane = laneLabels.Count;
                laneByKey[laneKey] = lane;
                laneLabels.Add($"{item.CallName} [{(item.EventType == IOEventType.InTag ? "IN" : "OUT")}]");
            }

            item.Lane = lane;
        }

        return laneLabels;
    }

    private static string BuildLaneKey(string callName, IOEventType eventType)
    {
        var direction = eventType == IOEventType.InTag ? "IN" : "OUT";
        return $"{callName}|{direction}";
    }

    /// <summary>
    /// 시간 범위 기반 IO 이벤트 조회 (사이클 개념 없이 직접 조회)
    /// </summary>
    public async Task<GanttChartData> GetIOEventsInTimeRangeAsync(
        string flowName, DateTime startTime, DateTime endTime)
    {
        using var scope = _scopeFactory.CreateScope();
        var plcRepo = scope.ServiceProvider.GetRequiredService<IPlcRepository>();

        // Flow 정보 가져오기
        var flow = _projectService.GetFlowByName(flowName);
        if (flow == null)
        {
            _logger.LogWarning("Flow '{FlowName}' not found", flowName);
            return new GanttChartData { FlowName = flowName };
        }

        var works = _projectService.GetWorks(flow.Id);

        // 모든 Call의 InTag/OutTag 주소 수집
        var tagAddresses = new List<string>();
        var callTagMap = new Dictionary<string, (string CallName, string WorkName, IOEventType EventType)>();

        foreach (var work in works)
        {
            var calls = _projectService.GetCalls(work.Id);
            foreach (var call in calls)
            {
                var tags = _mapper.GetCallTagsByCallId(call.Id);
                if (!tags.HasValue) continue;

                if (tags.Value.InTag != null)
                {
                    tagAddresses.Add(tags.Value.InTag);
                    callTagMap[tags.Value.InTag] = (call.Name, work.Name, IOEventType.InTag);
                }

                if (tags.Value.OutTag != null)
                {
                    tagAddresses.Add(tags.Value.OutTag);
                    callTagMap[tags.Value.OutTag] = (call.Name, work.Name, IOEventType.OutTag);
                }
            }
        }

        if (tagAddresses.Count == 0)
        {
            _logger.LogWarning("No tags found for Flow '{FlowName}'", flowName);
            return new GanttChartData { FlowName = flowName };
        }

        // 시간 범위의 Rising Edge 로그만 조회
        var logs = await plcRepo.GetMultipleTagRisingEdgesInRangeAsync(
            tagAddresses.Distinct().ToList(),
            startTime,
            endTime);

        _logger.LogInformation(
            "Retrieved {Count} logs for Flow '{FlowName}' in range {Start} ~ {End}",
            logs.Count, flowName, startTime, endTime);

        var ioEvents = new List<CallIOEvent>();

        foreach (var log in logs.OrderBy(l => l.DateTime))
        {
            var address = log.PlcTag?.Address;
            if (string.IsNullOrEmpty(address) || !callTagMap.ContainsKey(address))
                continue;

            var (callName, workName, eventType) = callTagMap[address];
            var relativeTimeMs = (int)(log.DateTime - startTime).TotalMilliseconds;
            var tagName = string.IsNullOrWhiteSpace(log.PlcTag?.Name) ? address : log.PlcTag!.Name;

            ioEvents.Add(new CallIOEvent
            {
                CallName = callName,
                FlowName = flowName,
                EventType = eventType,
                Timestamp = log.DateTime,
                RelativeTimeMs = relativeTimeMs,
                TagName = tagName,
                TagAddress = address
            });
        }

        _logger.LogInformation(
            "Detected {Count} IO events for Flow '{FlowName}'",
            ioEvents.Count, flowName);

        // Gantt 차트 렌더 수를 제한해 Blazor/SVG 프리징을 방지
        var totalEventCount = ioEvents.Count;

        // Call별로 InTag와 OutTag를 매칭하여 실제 Going 시간 계산
        var callEvents = ioEvents
            .GroupBy(e => e.CallName)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.RelativeTimeMs).ToList());

        var renderedEvents = ioEvents
            .OrderBy(e => e.RelativeTimeMs)
            .Take(MaxRenderedGanttItems)
            .Select(e =>
            {
                // 같은 Call의 이벤트 리스트
                var eventsForCall = callEvents.GetValueOrDefault(e.CallName, new List<CallIOEvent>());

                // InTag인 경우: 다음 OutTag까지의 시간을 Duration으로 설정
                // OutTag인 경우: 이전 InTag부터의 시간을 Duration으로 설정
                int? duration = null;
                int? relativeEnd = null;
                DateTime? finishTime = null;

                if (e.EventType == IOEventType.InTag)
                {
                    // 현재 InTag 이후의 첫 번째 OutTag 찾기
                    var nextOutTag = eventsForCall
                        .FirstOrDefault(evt => evt.EventType == IOEventType.OutTag && evt.RelativeTimeMs > e.RelativeTimeMs);

                    if (nextOutTag != null)
                    {
                        duration = nextOutTag.RelativeTimeMs - e.RelativeTimeMs;
                        relativeEnd = nextOutTag.RelativeTimeMs;
                        finishTime = nextOutTag.Timestamp;
                    }
                }
                else // OutTag
                {
                    // 현재 OutTag 이전의 마지막 InTag 찾기
                    var prevInTag = eventsForCall
                        .LastOrDefault(evt => evt.EventType == IOEventType.InTag && evt.RelativeTimeMs < e.RelativeTimeMs);

                    if (prevInTag != null)
                    {
                        duration = e.RelativeTimeMs - prevInTag.RelativeTimeMs;
                        finishTime = e.Timestamp;
                    }
                }

                return new GanttChartItem
                {
                    CallName = e.CallName,
                    FlowName = e.FlowName,
                    TagName = e.TagName,
                    TagAddress = e.TagAddress,
                    RelativeStart = e.RelativeTimeMs,
                    RelativeEnd = relativeEnd ?? e.RelativeTimeMs,
                    Duration = duration ?? 0,
                    Lane = 0,  // 레인은 나중에 할당
                    GoingStartTime = e.Timestamp,
                    FinishTime = finishTime ?? e.Timestamp,
                    EventType = e.EventType
                };
            })
            .ToList();

        // 레인은 Flow의 Call x In/Out 기준으로 고정해 최대 Call*2 레인을 확보한다.
        var laneDefinitions = BuildLaneDefinitions(flowName);
        var laneLabels = AssignConfiguredLanes(renderedEvents, laneDefinitions);

        var totalTimeMs = (int)(endTime - startTime).TotalMilliseconds;

        // 실제 이벤트 발생 시간 범위 계산
        DateTime? actualEventStartTime = null;
        DateTime? actualEventEndTime = null;
        if (ioEvents.Any())
        {
            actualEventStartTime = ioEvents.Min(e => e.Timestamp);
            actualEventEndTime = ioEvents.Max(e => e.Timestamp);
        }

        return new GanttChartData
        {
            CycleId = "TimeRange",
            FlowName = flowName,
            CycleNumber = 0,
            StartTime = startTime,
            EndTime = endTime,
            ActualEventStartTime = actualEventStartTime,
            ActualEventEndTime = actualEventEndTime,
            CT = totalTimeMs,
            MT = null,
            WT = null,
            TotalLanes = laneLabels.Count,
            LaneLabels = laneLabels,
            Items = renderedEvents,
            TotalEventCount = totalEventCount,
            RenderedEventCount = renderedEvents.Count,
            IsTruncated = totalEventCount > renderedEvents.Count,
            CriticalPath = new List<string>()
        };
    }
}

/// <summary>
/// Gantt 차트 데이터
/// </summary>
public class GanttChartData
{
    public string CycleId { get; set; } = string.Empty;
    public string FlowName { get; set; } = string.Empty;
    public int CycleNumber { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public DateTime? ActualEventStartTime { get; set; }  // 실제 이벤트 발생 시작 시간
    public DateTime? ActualEventEndTime { get; set; }    // 실제 이벤트 발생 종료 시간
    public int? CT { get; set; }
    public int? MT { get; set; }
    public int? WT { get; set; }
    public int TotalLanes { get; set; }
    public List<string> LaneLabels { get; set; } = new();
    public int TotalEventCount { get; set; }
    public int RenderedEventCount { get; set; }
    public bool IsTruncated { get; set; }
    public List<GanttChartItem> Items { get; set; } = new();
    public List<string> CriticalPath { get; set; } = new();
}

/// <summary>
/// Gantt 차트 항목 (InTag/OutTag 이벤트)
/// </summary>
public class GanttChartItem
{
    public string CallName { get; set; } = string.Empty;
    public string FlowName { get; set; } = string.Empty;
    public string TagName { get; set; } = string.Empty;
    public string TagAddress { get; set; } = string.Empty;
    public int RelativeStart { get; set; }
    public int? RelativeEnd { get; set; }
    public int? Duration { get; set; }
    public int Lane { get; set; }
    public DateTime GoingStartTime { get; set; }
    public DateTime? FinishTime { get; set; }
    public IOEventType EventType { get; set; }  // InTag / OutTag 구분
}

internal sealed record LaneDefinition(string LaneKey, string Label);
