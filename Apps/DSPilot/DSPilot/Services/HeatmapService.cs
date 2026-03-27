using DSPilot.Engine;
using static DSPilot.Engine.Models;
using DSPilot.Repositories;
using Microsoft.FSharp.Collections;

namespace DSPilot.Services;

/// <summary>
/// Heatmap 데이터 처리 서비스 (F# Performance 및 Models 모듈 사용)
/// 매트릭스 히트맵: 3개 메트릭 모두에 대해 색상 클래스를 동시 할당
/// </summary>
public class HeatmapService
{
    private readonly IDspRepository _dspRepository;
    private readonly IPlcRepository _plcRepository;
    private readonly PlcToCallMapperService _mapperService;
    private readonly ILogger<HeatmapService> _logger;

    public HeatmapService(
        IDspRepository dspRepository,
        IPlcRepository plcRepository,
        PlcToCallMapperService mapperService,
        ILogger<HeatmapService> logger)
    {
        _dspRepository = dspRepository;
        _plcRepository = plcRepository;
        _mapperService = mapperService;
        _logger = logger;
    }

    /// <summary>
    /// Flow별로 그룹화된 매트릭스 Heatmap 데이터 조회
    /// 3개 메트릭 모두에 대해 색상 클래스를 할당
    /// </summary>
    public async Task<List<FlowHeatmapGroup>> GetHeatmapDataAsync()
    {
        try
        {
            // 1. 통계 데이터 조회
            var statistics = await _dspRepository.GetCallStatisticsAsync();

            if (statistics.Count == 0)
            {
                _logger.LogWarning("No Call statistics available for heatmap");
                return new List<FlowHeatmapGroup>();
            }

            // 2. Heatmap 아이템 리스트 생성
            var items = statistics.Select(s => new CallHeatmapItem
            {
                CallId = s.CallId,
                CallName = s.CallName,
                FlowName = s.FlowName,
                WorkName = s.WorkName,
                AverageGoingTime = s.AverageGoingTime,
                StdDevGoingTime = s.StdDevGoingTime,
                GoingCount = s.GoingCount,
                ColorClassAvg = "",
                ColorClassStdDev = "",
                ColorClassCV = ""
            }).ToList();

            // 3. 3개 메트릭 각각에 대해 min/max 계산
            var allAvg = items.Select(i => i.AverageGoingTime).ToList();
            var allStdDev = items.Select(i => i.StdDevGoingTime).ToList();
            var allCV = items.Select(i => i.CoefficientOfVariation).ToList();

            var minAvg = allAvg.Min(); var maxAvg = allAvg.Max();
            var minStdDev = allStdDev.Min(); var maxStdDev = allStdDev.Max();
            var minCV = allCV.Min(); var maxCV = allCV.Max();

            // 4. 3개 메트릭 색상 클래스 동시 할당
            foreach (var item in items)
            {
                item.ColorClassAvg = Performance.assignColorClass(HeatmapMetric.AverageTime, item.AverageGoingTime, minAvg, maxAvg);
                item.ColorClassStdDev = Performance.assignColorClass(HeatmapMetric.StdDeviation, item.StdDevGoingTime, minStdDev, maxStdDev);
                item.ColorClassCV = Performance.assignColorClass(HeatmapMetric.CoefficientOfVariation, item.CoefficientOfVariation, minCV, maxCV);
            }

            // 5. Flow별로 그룹화 + Flow 수준 집계 색상
            var groups = items
                .GroupBy(item => item.FlowName)
                .Select(g =>
                {
                    var calls = g.OrderBy(c => c.CallName).ToList();
                    var flowAvg = calls.Average(c => c.AverageGoingTime);
                    var flowStdDev = calls.Average(c => c.StdDevGoingTime);
                    var flowCV = calls.Average(c => c.CoefficientOfVariation);

                    return new FlowHeatmapGroup
                    {
                        FlowName = g.Key,
                        Calls = ListModule.OfSeq(calls),
                        IsExpanded = true,
                        FlowColorClassAvg = Performance.assignColorClass(HeatmapMetric.AverageTime, flowAvg, minAvg, maxAvg),
                        FlowColorClassStdDev = Performance.assignColorClass(HeatmapMetric.StdDeviation, flowStdDev, minStdDev, maxStdDev),
                        FlowColorClassCV = Performance.assignColorClass(HeatmapMetric.CoefficientOfVariation, flowCV, minCV, maxCV)
                    };
                })
                .OrderBy(g => g.FlowName)
                .ToList();

            _logger.LogInformation(
                "Heatmap matrix data loaded: {FlowCount} flows, {CallCount} calls",
                groups.Count,
                items.Count);

            return groups;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get heatmap data");
            return new List<FlowHeatmapGroup>();
        }
    }

    /// <summary>
    /// Call의 실행 이력 조회 (PLCTagLog에서 InTag↔OutTag 매칭)
    /// </summary>
    public async Task<List<CallExecutionRecord>> GetCallExecutionHistoryAsync(Guid callId)
    {
        var records = new List<CallExecutionRecord>();

        try
        {
            // 1. Call의 InTag/OutTag 주소 조회
            var tags = _mapperService.GetCallTagsByCallId(callId);
            if (!tags.HasValue)
            {
                _logger.LogWarning("Call {CallId}: No tag mapping found", callId);
                return records;
            }

            var (inTag, outTag) = tags.Value;
            if (string.IsNullOrEmpty(inTag) || string.IsNullOrEmpty(outTag))
            {
                _logger.LogWarning("Call {CallId}: InTag or OutTag is missing (InTag={InTag}, OutTag={OutTag})",
                    callId, inTag, outTag);
                return records;
            }

            // 2. 전체 시간 범위 조회
            var oldest = await _plcRepository.GetOldestLogDateTimeAsync();
            var latest = await _plcRepository.GetLatestLogDateTimeAsync();
            if (!oldest.HasValue || !latest.HasValue)
            {
                _logger.LogWarning("Call {CallId}: No PLC log data available", callId);
                return records;
            }

            // 3. InTag/OutTag Rising Edge 조회
            var inTagEdges = await _plcRepository.FindRisingEdgesAsync(inTag, oldest.Value, latest.Value);
            var outTagEdges = await _plcRepository.FindRisingEdgesAsync(outTag, oldest.Value, latest.Value);

            _logger.LogInformation(
                "Call {CallId}: Found {InCount} InTag edges, {OutCount} OutTag edges",
                callId, inTagEdges.Count, outTagEdges.Count);

            // 4. InTag↔OutTag 순서 매칭 → GoingTime 계산
            // InTag(Going 시작) 후 가장 가까운 OutTag(Going 종료)를 매칭
            int outIndex = 0;
            int executionNumber = 0;

            foreach (var inTime in inTagEdges)
            {
                // inTime 이후의 첫 번째 outTag를 찾기
                while (outIndex < outTagEdges.Count && outTagEdges[outIndex] <= inTime)
                {
                    outIndex++;
                }

                if (outIndex >= outTagEdges.Count)
                    break;

                var outTime = outTagEdges[outIndex];
                var goingTimeMs = (int)(outTime - inTime).TotalMilliseconds;

                // 비정상적으로 긴 시간 필터링 (30초 이상은 제외)
                if (goingTimeMs > 0 && goingTimeMs < 30000)
                {
                    executionNumber++;
                    records.Add(new CallExecutionRecord
                    {
                        ExecutionNumber = executionNumber,
                        Timestamp = inTime,
                        GoingTimeMs = goingTimeMs
                    });
                }

                outIndex++;
            }

            _logger.LogInformation(
                "Call {CallId}: Matched {Count} executions", callId, records.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get execution history for Call {CallId}", callId);
        }

        return records;
    }

    /// <summary>
    /// 메트릭 표시 이름 반환
    /// </summary>
    public static string GetMetricDisplayName(HeatmapMetric metric) =>
        Performance.getMetricDisplayName(metric);

    /// <summary>
    /// 메트릭 값 포맷
    /// </summary>
    public static string FormatMetricValue(HeatmapMetric metric, double value) =>
        Performance.formatMetricValue(metric, value);

    /// <summary>
    /// 컴팩트 셀용 짧은 포맷
    /// </summary>
    public static string FormatMetricValueShort(HeatmapMetric metric, double value)
    {
        if (metric.IsAverageTime)
            return value < 1000 ? $"{value:F0}" : $"{value / 1000.0:F1}s";
        if (metric.IsStdDeviation)
            return value < 1000 ? $"{value:F0}" : $"{value / 1000.0:F1}s";
        if (metric.IsCoefficientOfVariation)
            return $"{value:F2}";
        return $"{value:F1}";
    }
}

/// <summary>
/// Call 개별 실행 기록
/// </summary>
public class CallExecutionRecord
{
    public int ExecutionNumber { get; set; }
    public DateTime Timestamp { get; set; }
    public int GoingTimeMs { get; set; }
}
