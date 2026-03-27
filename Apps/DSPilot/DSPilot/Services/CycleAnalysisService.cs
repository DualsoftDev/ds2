using DSPilot.Models.Analysis;
using DSPilot.Models.Plc;
using DSPilot.Repositories;
using Ds2.Core;
using Ds2.UI.Core;

namespace DSPilot.Services;

/// <summary>
/// 사이클 분석 서비스 - 하이브리드 접근 방식
/// 1. 자동 사이클 경계 탐지 (Head Call InTag 기반)
/// 2. 수동 시간 범위 분석
/// 3. 통합 상세 분석
/// </summary>
public class CycleAnalysisService
{
    private const int MaxRenderedGanttItems = 2000;
    private readonly IDspRepository _dspRepository;
    private readonly IPlcRepository _plcRepository;
    private readonly PlcToCallMapperService _mapperService;
    private readonly DsProjectService _projectService;
    private readonly ILogger<CycleAnalysisService> _logger;

    public CycleAnalysisService(
        IDspRepository dspRepository,
        IPlcRepository plcRepository,
        PlcToCallMapperService mapperService,
        DsProjectService projectService,
        ILogger<CycleAnalysisService> logger)
    {
        _dspRepository = dspRepository;
        _plcRepository = plcRepository;
        _mapperService = mapperService;
        _projectService = projectService;
        _logger = logger;
    }

    #region 1. 자동 사이클 경계 탐지

    /// <summary>
    /// 최신 N개 사이클 경계 자동 탐지
    /// </summary>
    public async Task<List<CycleBoundary>> DetectRecentCyclesAsync(
        string flowName,
        int cycleCount = 5)
    {
        var boundaries = new List<CycleBoundary>();

        // 1. Flow의 Head Call 찾기
        var flow = GetFlowByName(flowName);
        if (flow == null)
        {
            _logger.LogWarning("Flow '{FlowName}' not found", flowName);
            return boundaries;
        }

        var headCall = GetHeadCall(flow);
        if (headCall == null)
        {
            _logger.LogWarning("Flow '{FlowName}' has no head call", flowName);
            return boundaries;
        }

        // 2. Head Call의 InTag 주소 찾기
        var tags = _mapperService.GetCallTagsByCallId(headCall.Id);
        if (!tags.HasValue || string.IsNullOrEmpty(tags.Value.InTag))
        {
            _logger.LogWarning("Head Call '{CallName}' has no InTag", headCall.Name);
            return boundaries;
        }

        // 3. PLC 로그에서 InTag Rising Edge 찾기
        var latestLogTime = await _plcRepository.GetLatestLogDateTimeAsync();
        var oldestLogTime = await _plcRepository.GetOldestLogDateTimeAsync();

        if (!latestLogTime.HasValue || !oldestLogTime.HasValue)
        {
            _logger.LogWarning("No PLC log data available");
            return boundaries;
        }

        var risingEdges = await _plcRepository.FindRisingEdgesAsync(
            tags.Value.InTag,
            oldestLogTime.Value,
            latestLogTime.Value);

        if (risingEdges.Count == 0)
        {
            _logger.LogWarning(
                "No rising edges found for Head Call '{CallName}' (InTag: '{InTag}')",
                headCall.Name, tags.Value.InTag);
            return boundaries;
        }

        _logger.LogInformation(
            "Found {Count} rising edges for Head Call '{CallName}'",
            risingEdges.Count, headCall.Name);

        // 4. 최신 N+1개 선택 (N개 사이클 = N+1개 경계)
        var selectedEdges = risingEdges
            .OrderByDescending(t => t)
            .Take(cycleCount + 1)
            .OrderBy(t => t)
            .ToList();

        // 5. 사이클 경계 생성
        for (int i = 0; i < selectedEdges.Count - 1; i++)
        {
            var boundary = new CycleBoundary
            {
                CycleNumber = selectedEdges.Count - 1 - i,
                FlowName = flowName,
                StartTime = selectedEdges[i],
                EndTime = selectedEdges[i + 1]
            };

            // 요약 정보 계산 (빠른 미리보기용)
            boundary.Summary = await CalculateCycleSummaryAsync(boundary);

            boundaries.Add(boundary);
        }

        // 최신 순 정렬 (CycleNumber 내림차순)
        return boundaries.OrderByDescending(b => b.CycleNumber).ToList();
    }

    /// <summary>
    /// 최근 사이클의 시작시간만 빠르게 조회 (프로세스 순서 결정용 경량 버전).
    /// DB에서 최근 2개 rising edge만 가져오므로 전체 로그 스캔 불필요.
    /// </summary>
    public async Task<DateTime?> GetLatestCycleStartTimeAsync(string flowName)
    {
        var flow = GetFlowByName(flowName);
        if (flow == null) return null;

        var headCall = GetHeadCall(flow);
        if (headCall == null) return null;

        var tags = _mapperService.GetCallTagsByCallId(headCall.Id);
        if (!tags.HasValue || string.IsNullOrEmpty(tags.Value.InTag)) return null;

        // 최근 2개 rising edge만 조회 (1개 사이클 = 2개 경계)
        var edges = await _plcRepository.FindRecentRisingEdgesAsync(tags.Value.InTag, 2);
        if (edges.Count < 2) return null;

        return edges[0]; // 가장 최근 완료 사이클의 시작시간
    }

    /// <summary>
    /// 사이클 요약 정보 계산 (경량 버전)
    /// </summary>
    private async Task<CycleSummary> CalculateCycleSummaryAsync(CycleBoundary boundary)
    {
        var summary = new CycleSummary();

        try
        {
            // 간단한 Call 수 계산 및 기본 통계
            var sequence = await GetCallSequenceAsync(
                boundary.FlowName,
                boundary.StartTime,
                boundary.EndTime ?? DateTime.Now);

            summary.TotalCallCount = sequence.Count;
            summary.TotalActiveTime = TimeSpan.FromSeconds(
                sequence.Sum(c => c.Duration.TotalSeconds));
            summary.TotalGapTime = TimeSpan.FromSeconds(
                sequence.Sum(c => c.GapFromPrevious.TotalSeconds));
            summary.LongestGap = sequence.Count > 0
                ? sequence.Max(c => c.GapFromPrevious)
                : TimeSpan.Zero;

            var totalDuration = boundary.Duration.TotalSeconds;
            summary.UtilizationRate = totalDuration > 0
                ? (summary.TotalActiveTime.TotalSeconds / totalDuration) * 100
                : 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to calculate summary for cycle {CycleNumber}", boundary.CycleNumber);
        }

        return summary;
    }

    #endregion

    #region 2. 통합 상세 분석 (수동 + 자동)

    /// <summary>
    /// 특정 사이클 경계에 대한 상세 분석 (자동 탐지된 사이클)
    /// </summary>
    public async Task<CycleAnalysisData> AnalyzeCycleBoundaryAsync(CycleBoundary boundary)
    {
        var data = await AnalyzeCycleAsync(
            boundary.FlowName,
            boundary.StartTime,
            boundary.EndTime ?? DateTime.Now);

        data.Boundary = boundary;
        return data;
    }

    /// <summary>
    /// 사이클 분석 실행 (수동 시간 범위 또는 사이클 경계)
    /// </summary>
    public async Task<CycleAnalysisData> AnalyzeCycleAsync(
        string flowName,
        DateTime cycleStart,
        DateTime cycleEnd)
    {
        var data = new CycleAnalysisData
        {
            FlowName = flowName,
            CycleStartTime = cycleStart,
            CycleEndTime = cycleEnd,
            TotalDuration = cycleEnd - cycleStart
        };

        // 1. Call 실행 정보 수집 (시간순 정렬)
        data.CallSequence = await GetCallSequenceAsync(flowName, cycleStart, cycleEnd);
        data.CallCount = data.CallSequence.Count;

        if (data.CallCount == 0)
        {
            _logger.LogWarning("No calls found for Flow '{FlowName}' in the specified time range", flowName);
            return data;
        }

        // 2. Gap 분석
        data.Gaps = AnalyzeGaps(data.CallSequence);
        data.TopLongGaps = GetTopLongGaps(data.Gaps, 3);
        data.TotalGapDuration = TimeSpan.FromSeconds(data.Gaps.Sum(g => g.GapDuration.TotalSeconds));

        // 3. 장치별 시간 분석
        data.DeviceStats = AnalyzeDeviceTime(data.CallSequence, data.TotalDuration);

        // 4. 병목 탐지
        data.Bottlenecks = DetectBottlenecks(data.CallSequence);

        // 5. 성능 지표 계산
        data.Metrics = CalculatePerformanceMetrics(data);

        return data;
    }

    /// <summary>
    /// 성능 지표 계산
    /// </summary>
    private PerformanceMetrics CalculatePerformanceMetrics(CycleAnalysisData data)
    {
        var metrics = new PerformanceMetrics();

        if (data.CallCount == 0)
            return metrics;

        // 총 동작 시간
        metrics.TotalActiveTime = TimeSpan.FromSeconds(
            data.CallSequence.Sum(c => c.Duration.TotalSeconds));

        // 총 유휴 시간
        metrics.TotalIdleTime = data.TotalGapDuration;

        // 가동률
        metrics.UtilizationRate = data.TotalDuration.TotalSeconds > 0
            ? (metrics.TotalActiveTime.TotalSeconds / data.TotalDuration.TotalSeconds) * 100
            : 0;

        // 처리율 (분당 Call 수)
        metrics.Throughput = data.TotalDuration.TotalMinutes > 0
            ? data.CallCount / data.TotalDuration.TotalMinutes
            : 0;

        // 평균 사이클 시간
        metrics.AverageCycleTime = data.TotalDuration;

        // 병렬 실행 탐지
        metrics.ParallelExecutions = DetectParallelExecutions(data.CallSequence);

        return metrics;
    }

    /// <summary>
    /// 병렬 실행 횟수 탐지
    /// </summary>
    private int DetectParallelExecutions(List<CallExecutionInfo> sequence)
    {
        int parallelCount = 0;

        for (int i = 0; i < sequence.Count - 1; i++)
        {
            var current = sequence[i];
            var next = sequence[i + 1];

            // 다음 Call이 현재 Call이 끝나기 전에 시작한 경우
            if (next.StartTime < current.EndTime)
            {
                parallelCount++;
            }
        }

        return parallelCount;
    }

    #endregion

    #region 3. Helper Methods

    /// <summary>
    /// Flow 이름으로 Flow 찾기
    /// </summary>
    private Flow? GetFlowByName(string flowName)
    {
        if (!_projectService.IsLoaded)
            return null;

        var systems = _projectService.GetActiveSystems();
        foreach (var system in systems)
        {
            var flows = _projectService.GetFlows(system.Id);
            var flow = flows.FirstOrDefault(f => f.Name == flowName);
            if (flow != null)
                return flow;
        }

        return null;
    }

    /// <summary>
    /// Flow의 Head Call 찾기 (첫 번째 Work의 첫 번째 Call)
    /// </summary>
    private Call? GetHeadCall(Flow flow)
    {
        var works = _projectService.GetWorks(flow.Id);
        if (works.Count == 0)
            return null;

        // 첫 번째 Work의 첫 번째 Call
        var firstWork = works.First();
        var calls = _projectService.GetCalls(firstWork.Id);

        return calls.Count > 0 ? calls.First() : null;
    }

    /// <summary>
    /// 시간 범위 기준 IO 이벤트를 읽어 Gantt 렌더 데이터로 변환한다.
    /// </summary>
    public async Task<GanttChartData> GetIOEventsInTimeRangeAsync(
        string flowName,
        DateTime startTime,
        DateTime endTime)
    {
        var flow = GetFlowByName(flowName);
        if (flow == null)
        {
            _logger.LogWarning("Flow '{FlowName}' not found", flowName);
            return new GanttChartData { FlowName = flowName };
        }

        var works = _projectService.GetWorks(flow.Id);
        var tagAddresses = new List<string>();
        var callTagMap = new Dictionary<string, (Guid CallId, string CallName, string WorkName, IOEventType EventType)>(
            StringComparer.OrdinalIgnoreCase);
        var callMetaById = new Dictionary<Guid, (string CallName, string WorkName)>();

        foreach (var work in works)
        {
            var calls = _projectService.GetCalls(work.Id);
            foreach (var call in calls)
            {
                var tags = _mapperService.GetCallTagsByCallId(call.Id);
                if (!tags.HasValue)
                    continue;

                callMetaById[call.Id] = (call.Name, work.Name);

                if (!string.IsNullOrWhiteSpace(tags.Value.InTag))
                {
                    tagAddresses.Add(tags.Value.InTag);
                    callTagMap[tags.Value.InTag] = (call.Id, call.Name, work.Name, IOEventType.InTag);
                }

                if (!string.IsNullOrWhiteSpace(tags.Value.OutTag))
                {
                    tagAddresses.Add(tags.Value.OutTag);
                    callTagMap[tags.Value.OutTag] = (call.Id, call.Name, work.Name, IOEventType.OutTag);
                }
            }
        }

        if (tagAddresses.Count == 0)
        {
            _logger.LogWarning("No IO tag mapping found for Flow '{FlowName}'", flowName);
            return new GanttChartData { FlowName = flowName, StartTime = startTime, EndTime = endTime };
        }

        var logs = await _plcRepository.GetMultipleTagRisingEdgesInRangeAsync(
            tagAddresses.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            startTime,
            endTime);

        _logger.LogInformation(
            "Retrieved {Count} rising-edge logs for Flow '{FlowName}' in range {Start} ~ {End}",
            logs.Count,
            flowName,
            startTime,
            endTime);

        var ioEvents = new List<CallIOEvent>();
        foreach (var log in logs.OrderBy(l => l.DateTime))
        {
            var address = log.PlcTag?.Address;
            if (string.IsNullOrWhiteSpace(address) || !callTagMap.TryGetValue(address, out var mapping))
                continue;

            var tagName = string.IsNullOrWhiteSpace(log.PlcTag?.Name)
                ? address
                : log.PlcTag!.Name;

            ioEvents.Add(new CallIOEvent
            {
                CallId = mapping.CallId,
                CallName = mapping.CallName,
                FlowName = flowName,
                EventType = mapping.EventType,
                Timestamp = log.DateTime,
                RelativeTimeMs = (int)(log.DateTime - startTime).TotalMilliseconds,
                TagName = tagName,
                TagAddress = address
            });
        }

        var totalEventCount = ioEvents.Count;
        var callEvents = ioEvents
            .GroupBy(e => e.CallId)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.RelativeTimeMs).ToList());

        var renderedEvents = ioEvents.Count > MaxRenderedGanttItems
            ? ioEvents
                .OrderByDescending(e => e.RelativeTimeMs)
                .Take(MaxRenderedGanttItems)
                .OrderBy(e => e.RelativeTimeMs)
                .ToList()
            : ioEvents
                .OrderBy(e => e.RelativeTimeMs)
                .ToList();

        var renderedItems = renderedEvents
            .Select(e =>
            {
                var eventsForCall = callEvents.GetValueOrDefault(e.CallId, new List<CallIOEvent>());
                int? duration = null;
                int? relativeEnd = null;
                DateTime? finishTime = null;

                if (e.EventType == IOEventType.InTag)
                {
                    var nextOutTag = eventsForCall
                        .FirstOrDefault(evt => evt.EventType == IOEventType.OutTag && evt.RelativeTimeMs > e.RelativeTimeMs);

                    if (nextOutTag != null)
                    {
                        duration = nextOutTag.RelativeTimeMs - e.RelativeTimeMs;
                        relativeEnd = nextOutTag.RelativeTimeMs;
                        finishTime = nextOutTag.Timestamp;
                    }
                }
                else
                {
                    var prevInTag = eventsForCall
                        .LastOrDefault(evt => evt.EventType == IOEventType.InTag && evt.RelativeTimeMs < e.RelativeTimeMs);

                    if (prevInTag != null)
                    {
                        duration = e.RelativeTimeMs - prevInTag.RelativeTimeMs;
                        relativeEnd = e.RelativeTimeMs;
                        finishTime = e.Timestamp;
                    }
                }

                var callMeta = callMetaById.GetValueOrDefault(e.CallId, (e.CallName, string.Empty));

                return new GanttChartItem
                {
                    CallId = e.CallId,
                    CallName = e.CallName,
                    WorkName = callMeta.Item2,
                    FlowName = flowName,
                    TagName = e.TagName,
                    TagAddress = e.TagAddress,
                    RelativeStart = e.RelativeTimeMs,
                    RelativeEnd = relativeEnd ?? e.RelativeTimeMs,
                    Duration = duration ?? 0,
                    Lane = 0,
                    GoingStartTime = e.Timestamp,
                    FinishTime = finishTime ?? e.Timestamp,
                    EventType = e.EventType
                };
            })
            .ToList();

        var laneDefinitions = BuildLaneDefinitions(flow);
        var laneLabels = AssignConfiguredLanes(renderedItems, laneDefinitions);

        DateTime? actualEventStartTime = null;
        DateTime? actualEventEndTime = null;
        if (renderedItems.Count > 0)
        {
            actualEventStartTime = renderedItems.Min(item => item.GoingStartTime);
            actualEventEndTime = renderedItems.Max(item => item.FinishTime ?? item.GoingStartTime);
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
            CT = (int)(endTime - startTime).TotalMilliseconds,
            TotalLanes = laneLabels.Count,
            LaneLabels = laneLabels,
            Items = renderedItems,
            TotalEventCount = totalEventCount,
            RenderedEventCount = renderedItems.Count,
            IsTruncated = totalEventCount > renderedItems.Count
        };
    }

    /// <summary>
    /// 시간 범위 기준 실제 PLC 태그 상태를 읽어 In/Out의 ON~OFF 구간을 Gantt segment로 변환한다.
    /// cycle-time-analysis 페이지 전용 실제 IO 타임라인.
    /// </summary>
    public async Task<GanttChartData> GetActualIoSignalSegmentsInTimeRangeAsync(
        string flowName,
        DateTime startTime,
        DateTime endTime)
    {
        var flow = GetFlowByName(flowName);
        if (flow == null)
        {
            _logger.LogWarning("Flow '{FlowName}' not found", flowName);
            return new GanttChartData { FlowName = flowName };
        }

        var laneDefinitions = BuildSignalLaneDefinitions(flow);
        if (laneDefinitions.Count == 0)
        {
            _logger.LogWarning("No IO signal mapping found for Flow '{FlowName}'", flowName);
            return new GanttChartData { FlowName = flowName, StartTime = startTime, EndTime = endTime };
        }

        var addresses = laneDefinitions
            .Select(def => def.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var addressSet = new HashSet<string>(addresses, StringComparer.OrdinalIgnoreCase);

        var allTags = await _plcRepository.GetAllTagsAsync();
        var tagById = allTags
            .Where(tag => addressSet.Contains(tag.Address))
            .ToDictionary(tag => tag.Id, tag => tag);
        var tagNameByAddress = allTags
            .Where(tag => addressSet.Contains(tag.Address))
            .GroupBy(tag => tag.Address, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Name,
                StringComparer.OrdinalIgnoreCase);

        var latestBeforeLogs = await _plcRepository.GetLatestLogsByAddressesBeforeAsync(addresses, startTime);
        var initialStateByAddress = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var log in latestBeforeLogs.OrderBy(log => log.DateTime).ThenBy(log => log.Id))
        {
            if (tagById.TryGetValue(log.PlcTagId, out var tag))
            {
                initialStateByAddress[tag.Address] = NormalizePlcBoolValue(log.Value);
            }
        }

        var logs = await _plcRepository.GetMultipleTagLogsInRangeAsync(addresses, startTime, endTime);
        var logsByAddress = logs
            .Where(log => !string.IsNullOrWhiteSpace(log.PlcTag?.Address) || !string.IsNullOrWhiteSpace(log.Address))
            .GroupBy(
                log => log.PlcTag?.Address ?? log.Address,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(log => log.DateTime).ThenBy(log => log.Id).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var items = new List<GanttChartItem>();

        foreach (var lane in laneDefinitions)
        {
            logsByAddress.TryGetValue(lane.Address, out var signalLogs);
            signalLogs ??= new List<PlcTagLogEntity>();

            var currentState = initialStateByAddress.TryGetValue(lane.Address, out var initialState) && initialState;
            DateTime? segmentStart = currentState ? startTime : null;
            var resolvedTagName = tagNameByAddress.GetValueOrDefault(lane.Address, lane.Address);

            foreach (var log in signalLogs)
            {
                if (!string.IsNullOrWhiteSpace(log.PlcTag?.Name))
                {
                    resolvedTagName = log.PlcTag.Name;
                }
                else if (!string.IsNullOrWhiteSpace(log.TagName))
                {
                    resolvedTagName = log.TagName;
                }

                var newState = NormalizePlcBoolValue(log.Value);
                if (newState == currentState)
                {
                    continue;
                }

                if (newState)
                {
                    currentState = true;
                    segmentStart = log.DateTime < startTime ? startTime : log.DateTime;
                    continue;
                }

                if (currentState && segmentStart.HasValue)
                {
                    var segmentEnd = log.DateTime > endTime ? endTime : log.DateTime;
                    if (segmentEnd > segmentStart.Value)
                    {
                        items.Add(BuildSignalSegmentItem(
                            lane,
                            flowName,
                            resolvedTagName,
                            segmentStart.Value,
                            segmentEnd,
                            startTime));
                    }
                }

                currentState = false;
                segmentStart = null;
            }

            if (currentState && segmentStart.HasValue && endTime > segmentStart.Value)
            {
                items.Add(BuildSignalSegmentItem(
                    lane,
                    flowName,
                    resolvedTagName,
                    segmentStart.Value,
                    endTime,
                    startTime));
            }
        }

        var totalEventCount = items.Count;
        var renderedItems = totalEventCount > MaxRenderedGanttItems
            ? items
                .OrderByDescending(item => item.GoingStartTime)
                .Take(MaxRenderedGanttItems)
                .OrderBy(item => item.GoingStartTime)
                .ToList()
            : items
                .OrderBy(item => item.GoingStartTime)
                .ToList();

        var actualEventStartTime = renderedItems.Count > 0
            ? renderedItems.Min(item => item.GoingStartTime)
            : (DateTime?)null;
        var actualEventEndTime = renderedItems.Count > 0
            ? renderedItems.Max(item => item.FinishTime ?? item.GoingStartTime)
            : (DateTime?)null;

        return new GanttChartData
        {
            CycleId = "ActualIoSignals",
            FlowName = flowName,
            CycleNumber = 0,
            StartTime = startTime,
            EndTime = endTime,
            ActualEventStartTime = actualEventStartTime,
            ActualEventEndTime = actualEventEndTime,
            CT = (int)(endTime - startTime).TotalMilliseconds,
            TotalLanes = laneDefinitions
                .Select(def => def.Lane)
                .Distinct()
                .Count(),
            LaneLabels = laneDefinitions
                .OrderBy(def => def.Lane)
                .GroupBy(def => def.Lane)
                .Select(group => group.First().Label)
                .ToList(),
            Items = renderedItems,
            TotalEventCount = totalEventCount,
            RenderedEventCount = renderedItems.Count,
            IsTruncated = totalEventCount > renderedItems.Count
        };
    }

    private List<LaneDefinition> BuildLaneDefinitions(Flow flow)
    {
        var laneDefinitions = new List<LaneDefinition>();
        var works = _projectService.GetWorks(flow.Id);

        foreach (var work in works)
        {
            var calls = _projectService.GetCalls(work.Id);
            foreach (var call in calls)
            {
                var tags = _mapperService.GetCallTagsByCallId(call.Id);
                if (!tags.HasValue)
                    continue;

                if (!string.IsNullOrWhiteSpace(tags.Value.InTag) ||
                    !string.IsNullOrWhiteSpace(tags.Value.OutTag))
                {
                    laneDefinitions.Add(new LaneDefinition(
                        BuildLaneKey(call.Id),
                        call.Name));
                }
            }
        }

        return laneDefinitions;
    }

    private List<SignalLaneDefinition> BuildSignalLaneDefinitions(Flow flow)
    {
        var laneDefinitions = new List<SignalLaneDefinition>();
        var works = _projectService.GetWorks(flow.Id);
        var laneIndex = 0;

        foreach (var work in works)
        {
            var calls = _projectService.GetCalls(work.Id);
            foreach (var call in calls)
            {
                var tags = _mapperService.GetCallTagsByCallId(call.Id);
                if (!tags.HasValue)
                    continue;

                if (string.IsNullOrWhiteSpace(tags.Value.InTag) &&
                    string.IsNullOrWhiteSpace(tags.Value.OutTag))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(tags.Value.InTag))
                {
                    laneDefinitions.Add(new SignalLaneDefinition(
                        tags.Value.InTag,
                        call.Id,
                        call.Name,
                        work.Name,
                        IOEventType.InTag,
                        call.Name,
                        laneIndex));
                }

                if (!string.IsNullOrWhiteSpace(tags.Value.OutTag))
                {
                    laneDefinitions.Add(new SignalLaneDefinition(
                        tags.Value.OutTag,
                        call.Id,
                        call.Name,
                        work.Name,
                        IOEventType.OutTag,
                        call.Name,
                        laneIndex));
                }

                laneIndex++;
            }
        }

        return laneDefinitions;
    }

    private static List<string> AssignConfiguredLanes(
        List<GanttChartItem> items,
        List<LaneDefinition> laneDefinitions)
    {
        var laneLabels = laneDefinitions
            .Select(definition => definition.Label)
            .ToList();

        var laneByKey = laneDefinitions
            .Select((definition, index) => new { definition.LaneKey, Index = index })
            .ToDictionary(x => x.LaneKey, x => x.Index, StringComparer.OrdinalIgnoreCase);

        foreach (var item in items.OrderBy(i => i.RelativeStart))
        {
            var laneKey = BuildLaneKey(item.CallId);
            if (!laneByKey.TryGetValue(laneKey, out var lane))
            {
                lane = laneLabels.Count;
                laneByKey[laneKey] = lane;
                laneLabels.Add(item.CallName);
            }

            item.Lane = lane;
        }

        return laneLabels;
    }

    private static string BuildLaneKey(Guid callId) => callId.ToString("N");

    private static GanttChartItem BuildSignalSegmentItem(
        SignalLaneDefinition lane,
        string flowName,
        string tagName,
        DateTime segmentStart,
        DateTime segmentEnd,
        DateTime chartStartTime)
    {
        var relativeStart = Math.Max(0, (int)(segmentStart - chartStartTime).TotalMilliseconds);
        var relativeEnd = Math.Max(relativeStart, (int)(segmentEnd - chartStartTime).TotalMilliseconds);
        var duration = Math.Max(0, relativeEnd - relativeStart);

        return new GanttChartItem
        {
            CallId = lane.CallId,
            CallName = lane.CallName,
            WorkName = lane.WorkName,
            FlowName = flowName,
            TagName = string.IsNullOrWhiteSpace(tagName) ? lane.Address : tagName,
            TagAddress = lane.Address,
            RelativeStart = relativeStart,
            RelativeEnd = relativeEnd,
            Duration = duration,
            Lane = lane.Lane,
            GoingStartTime = segmentStart,
            FinishTime = segmentEnd,
            EventType = lane.EventType
        };
    }

    private static bool NormalizePlcBoolValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" => true,
            "true" => true,
            "on" => true,
            _ => false
        };
    }

    #endregion

    #region 4. Existing Methods (GetCallSequenceAsync, AnalyzeGaps, etc.)

    /// <summary>
    /// Call 실행 정보 수집 (시간순 정렬)
    /// </summary>
    private async Task<List<CallExecutionInfo>> GetCallSequenceAsync(
        string flowName,
        DateTime cycleStart,
        DateTime cycleEnd)
    {
        var sequence = new List<CallExecutionInfo>();

        // 1. Flow에 속한 모든 Call 가져오기
        if (!_projectService.IsLoaded)
        {
            _logger.LogWarning("Project not loaded");
            return sequence;
        }

        // Flow 찾기: DsProjectService를 통해 모든 Flow를 조회
        var systems = _projectService.GetActiveSystems();
        Flow? targetFlow = null;

        foreach (var system in systems)
        {
            var flows = _projectService.GetFlows(system.Id);
            targetFlow = flows.FirstOrDefault(f => f.Name == flowName);
            if (targetFlow != null)
                break;
        }

        if (targetFlow == null)
        {
            _logger.LogWarning("Flow '{FlowName}' not found", flowName);
            return sequence;
        }

        // 2. Flow의 모든 Work 조회
        var works = _projectService.GetWorks(targetFlow.Id);

        // 3. 각 Work의 Call 조회
        var allCalls = new List<Call>();
        foreach (var work in works)
        {
            var calls = _projectService.GetCalls(work.Id);
            allCalls.AddRange(calls);
        }

        // 4. 각 Call의 InTag/OutTag Rising Edge 이벤트 수집
        var callIOEvents = new List<CallIOEvent>();

        foreach (var call in allCalls)
        {
            var tags = _mapperService.GetCallTagsByCallId(call.Id);
            if (tags == null)
                continue;

            var (inTag, outTag) = tags.Value;

            // InTag Rising Edge 찾기
            if (!string.IsNullOrEmpty(inTag))
            {
                var inTagEvents = await _plcRepository.FindRisingEdgesAsync(inTag, cycleStart, cycleEnd);
                foreach (var timestamp in inTagEvents)
                {
                    callIOEvents.Add(new CallIOEvent
                    {
                        CallId = call.Id,
                        CallName = call.Name,
                        FlowName = flowName,
                        EventType = IOEventType.InTag,
                        Timestamp = timestamp,
                        TagAddress = inTag
                    });
                }
            }

            // OutTag Rising Edge 찾기
            if (!string.IsNullOrEmpty(outTag))
            {
                var outTagEvents = await _plcRepository.FindRisingEdgesAsync(outTag, cycleStart, cycleEnd);
                foreach (var timestamp in outTagEvents)
                {
                    callIOEvents.Add(new CallIOEvent
                    {
                        CallId = call.Id,
                        CallName = call.Name,
                        FlowName = flowName,
                        EventType = IOEventType.OutTag,
                        Timestamp = timestamp,
                        TagAddress = outTag
                    });
                }
            }
        }

        // 3. 시간순 정렬
        callIOEvents = callIOEvents.OrderBy(e => e.Timestamp).ToList();

        // 4. InTag → OutTag 쌍을 매칭하여 Call 실행 정보 생성
        var callStarts = new Dictionary<Guid, (DateTime StartTime, string CallName, string DeviceName)>();
        int sequenceNumber = 0;
        DateTime? previousEndTime = null;

        foreach (var evt in callIOEvents)
        {
            if (evt.EventType == IOEventType.InTag)
            {
                // Call 시작 이벤트
                var deviceName = GetDeviceNameForCall(evt.CallId, allCalls);
                callStarts[evt.CallId] = (evt.Timestamp, evt.CallName, deviceName);
            }
            else if (evt.EventType == IOEventType.OutTag)
            {
                // Call 종료 이벤트
                if (callStarts.TryGetValue(evt.CallId, out var startInfo))
                {
                    sequenceNumber++;
                    var startTime = startInfo.StartTime;
                    var endTime = evt.Timestamp;
                    var duration = endTime - startTime;
                    var relativeStartTime = startTime - cycleStart;
                    var gap = previousEndTime.HasValue ? startTime - previousEndTime.Value : TimeSpan.Zero;

                    sequence.Add(new CallExecutionInfo
                    {
                        SequenceNumber = sequenceNumber,
                        CallName = startInfo.CallName,
                        FlowName = flowName,
                        DeviceName = startInfo.DeviceName,
                        StartTime = startTime,
                        EndTime = endTime,
                        Duration = duration,
                        RelativeStartTime = relativeStartTime,
                        GapFromPrevious = gap,
                        State = CallState.Completed
                    });

                    previousEndTime = endTime;
                    callStarts.Remove(evt.CallId);
                }
            }
        }

        _logger.LogInformation("Found {Count} call executions for Flow '{FlowName}'", sequence.Count, flowName);
        return sequence;
    }

    /// <summary>
    /// Call의 Device 이름 추출
    /// </summary>
    private string GetDeviceNameForCall(Guid callId, List<Call> calls)
    {
        var call = calls.FirstOrDefault(c => c.Id == callId);
        if (call == null)
            return "Unknown";

        // Call의 부모 Work 이름을 Device 이름으로 사용
        var store = _projectService.GetStore();
        var workOpt = DsQuery.getWork(call.ParentId, store);

        if (Microsoft.FSharp.Core.FSharpOption<Work>.get_IsSome(workOpt))
        {
            return workOpt.Value.Name;
        }

        return "Unknown";
    }

    /// <summary>
    /// Gap 분석
    /// </summary>
    public List<GapInfo> AnalyzeGaps(List<CallExecutionInfo> sequence)
    {
        var gaps = new List<GapInfo>();

        for (int i = 0; i < sequence.Count - 1; i++)
        {
            var current = sequence[i];
            var next = sequence[i + 1];

            var gapDuration = next.StartTime - current.EndTime;

            if (gapDuration > TimeSpan.Zero)
            {
                gaps.Add(new GapInfo
                {
                    PreviousCall = current.CallName,
                    NextCall = next.CallName,
                    GapDuration = gapDuration,
                    GapStartTime = current.EndTime,
                    GapEndTime = next.StartTime,
                    IsBottleneck = gapDuration.TotalSeconds > 1.0 // 1초 이상이면 병목으로 간주
                });
            }
        }

        return gaps;
    }

    /// <summary>
    /// Top N 긴 Gap 선정
    /// </summary>
    public List<GapInfo> GetTopLongGaps(List<GapInfo> gaps, int topN = 3)
    {
        return gaps
            .OrderByDescending(g => g.GapDuration)
            .Take(topN)
            .Select((g, index) =>
            {
                g.Rank = index + 1;
                return g;
            })
            .ToList();
    }

    /// <summary>
    /// 장치별 시간 분석
    /// </summary>
    public Dictionary<string, DeviceTimeInfo> AnalyzeDeviceTime(
        List<CallExecutionInfo> sequence,
        TimeSpan totalCycleDuration)
    {
        var deviceGroups = sequence.GroupBy(c => c.DeviceName);
        var deviceStats = new Dictionary<string, DeviceTimeInfo>();

        foreach (var group in deviceGroups)
        {
            var deviceName = group.Key;
            var calls = group.ToList();
            var totalTime = TimeSpan.FromSeconds(calls.Sum(c => c.Duration.TotalSeconds));
            var durations = calls.Select(c => c.Duration).ToList();

            deviceStats[deviceName] = new DeviceTimeInfo
            {
                DeviceName = deviceName,
                TotalTime = totalTime,
                PercentageOfCycle = totalCycleDuration.TotalSeconds > 0
                    ? (totalTime.TotalSeconds / totalCycleDuration.TotalSeconds) * 100
                    : 0,
                CallCount = calls.Count,
                AverageTime = TimeSpan.FromSeconds(totalTime.TotalSeconds / calls.Count),
                MinTime = durations.Min(),
                MaxTime = durations.Max()
            };
        }

        return deviceStats;
    }

    /// <summary>
    /// 병목 탐지
    /// </summary>
    public List<BottleneckInfo> DetectBottlenecks(List<CallExecutionInfo> sequence)
    {
        var bottlenecks = new List<BottleneckInfo>();

        if (sequence.Count == 0)
            return bottlenecks;

        var averageDuration = TimeSpan.FromSeconds(
            sequence.Average(c => c.Duration.TotalSeconds));

        var threshold = averageDuration.TotalSeconds * 2; // 평균의 2배

        foreach (var call in sequence)
        {
            if (call.Duration.TotalSeconds > threshold)
            {
                bottlenecks.Add(new BottleneckInfo
                {
                    CallName = call.CallName,
                    Reason = "평균보다 2배 이상 긴 동작",
                    Duration = call.Duration,
                    ExpectedDuration = averageDuration,
                    DelayRatio = call.Duration.TotalSeconds / averageDuration.TotalSeconds
                });
            }
        }

        return bottlenecks;
    }

    #endregion
}

public class GanttChartData
{
    public string CycleId { get; set; } = string.Empty;
    public string FlowName { get; set; } = string.Empty;
    public int CycleNumber { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public DateTime? ActualEventStartTime { get; set; }
    public DateTime? ActualEventEndTime { get; set; }
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

public class GanttChartItem
{
    public Guid CallId { get; set; }
    public string CallName { get; set; } = string.Empty;
    public string WorkName { get; set; } = string.Empty;
    public string FlowName { get; set; } = string.Empty;
    public string TagName { get; set; } = string.Empty;
    public string TagAddress { get; set; } = string.Empty;
    public int RelativeStart { get; set; }
    public int? RelativeEnd { get; set; }
    public int? Duration { get; set; }
    public int Lane { get; set; }
    public DateTime GoingStartTime { get; set; }
    public DateTime? FinishTime { get; set; }
    public IOEventType EventType { get; set; }
}

internal sealed record LaneDefinition(string LaneKey, string Label);
internal sealed record SignalLaneDefinition(
    string Address,
    Guid CallId,
    string CallName,
    string WorkName,
    IOEventType EventType,
    string Label,
    int Lane);
