using DSPilot.Models.Analysis;
using DSPilot.Repositories;
using Ds2.Core;
using Ds2.UI.Core;

namespace DSPilot.Services;

/// <summary>
/// 사이클 분석 서비스
/// </summary>
public class CycleAnalysisService
{
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

    /// <summary>
    /// 사이클 분석 실행
    /// </summary>
    public async Task<CycleAnalysisData> AnalyzeCycleAsync(
        string flowName,
        DateTime cycleStart,
        DateTime cycleEnd)
    {
        var data = new CycleAnalysisData
        {
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

        return data;
    }

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
}
