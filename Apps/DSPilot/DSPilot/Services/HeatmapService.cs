using System.Collections.Concurrent;
using DSPilot.Engine;
using static DSPilot.Engine.Models;
using DSPilot.Models;
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
    private readonly AppSettingsService _settingsService;
    private readonly ILogger<HeatmapService> _logger;

    public HeatmapService(
        IDspRepository dspRepository,
        IPlcRepository plcRepository,
        PlcToCallMapperService mapperService,
        AppSettingsService settingsService,
        ILogger<HeatmapService> logger)
    {
        _dspRepository = dspRepository;
        _plcRepository = plcRepository;
        _mapperService = mapperService;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// Flow별로 그룹화된 매트릭스 Heatmap 데이터 조회 (전체 기간, 사전 계산 통계)
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

            return AssignColorsAndGroup(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get heatmap data");
            return new List<FlowHeatmapGroup>();
        }
    }

    /// <summary>
    /// 히스토리 필터가 적용된 Heatmap 데이터 조회 (PLC 로그에서 재계산)
    /// </summary>
    public async Task<List<FlowHeatmapGroup>> GetHeatmapDataFilteredAsync(HistoryViewSettings filter)
    {
        try
        {
            // 1. PLC 로그 시간 범위 조회
            var latest = await _plcRepository.GetLatestLogDateTimeAsync();
            var oldest = await _plcRepository.GetOldestLogDateTimeAsync();
            if (!latest.HasValue || !oldest.HasValue)
            {
                _logger.LogWarning("No PLC log data available for filtered heatmap");
                return new List<FlowHeatmapGroup>();
            }

            // 2. 필터 모드에 따른 시간 범위 결정
            DateTime startTime = oldest.Value;
            DateTime endTime = latest.Value;
            int? maxCycles = null;

            switch (filter.FilterMode)
            {
                case "Days":
                    startTime = endTime.AddDays(-filter.MaxDays);
                    break;
                case "StartTime" when filter.StartTime.HasValue:
                    startTime = AsLocal(filter.StartTime.Value);
                    break;
                case "Cycles":
                    maxCycles = filter.MaxCycles;
                    break;
            }

            // 3. 모든 Call의 태그 쌍 조회
            var callPairs = _mapperService.GetAllCallTagPairs()
                .Where(c => c.InTag != null && c.OutTag != null)
                .ToList();

            if (callPairs.Count == 0)
            {
                _logger.LogWarning("No call tag pairs available for filtered heatmap");
                return new List<FlowHeatmapGroup>();
            }

            // 4. 병렬로 각 Call의 실행 기록 조회 및 통계 계산
            var results = new ConcurrentBag<CallHeatmapItem>();

            await Parallel.ForEachAsync(callPairs,
                new ParallelOptions { MaxDegreeOfParallelism = 4 },
                async (callPair, ct) =>
                {
                    var records = await ComputeExecutionRecordsAsync(
                        callPair.InTag!, callPair.OutTag!, startTime, endTime, maxCycles);

                    if (records.Count > 0)
                    {
                        var avg = records.Average(r => r.GoingTimeMs);
                        var stddev = Math.Sqrt(records.Average(r => Math.Pow(r.GoingTimeMs - avg, 2)));

                        results.Add(new CallHeatmapItem
                        {
                            CallId = callPair.CallId,
                            CallName = callPair.CallName,
                            FlowName = callPair.FlowName,
                            WorkName = callPair.WorkName,
                            AverageGoingTime = avg,
                            StdDevGoingTime = stddev,
                            GoingCount = records.Count,
                            ColorClassAvg = "",
                            ColorClassStdDev = "",
                            ColorClassCV = ""
                        });
                    }
                });

            var items = results.ToList();
            if (items.Count == 0)
            {
                _logger.LogInformation("No execution records found for filter: {FilterMode}", filter.FilterMode);
                return new List<FlowHeatmapGroup>();
            }

            _logger.LogInformation(
                "Filtered heatmap data computed: {CallCount} calls, filter={FilterMode}",
                items.Count, filter.FilterMode);

            return AssignColorsAndGroup(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get filtered heatmap data");
            return new List<FlowHeatmapGroup>();
        }
    }

    /// <summary>
    /// Call의 실행 이력 조회 (PLCTagLog에서 InTag↔OutTag 매칭) - 전체 기간
    /// </summary>
    public async Task<List<CallExecutionRecord>> GetCallExecutionHistoryAsync(Guid callId)
    {
        return await GetCallExecutionHistoryAsync(callId, null, null, null);
    }

    /// <summary>
    /// Call의 실행 이력 조회 (PLCTagLog에서 InTag↔OutTag 매칭) - 필터 적용
    /// </summary>
    public async Task<List<CallExecutionRecord>> GetCallExecutionHistoryAsync(
        Guid callId, DateTime? startTime, DateTime? endTime, int? maxCycles)
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

            // 2. 시간 범위 결정 (Unspecified Kind는 Local로 보정 → UTC 변환 보장)
            var queryStart = startTime.HasValue ? AsLocal(startTime.Value) : (DateTime?)null;
            var queryEnd = endTime.HasValue ? AsLocal(endTime.Value) : (DateTime?)null;

            if (!queryStart.HasValue || !queryEnd.HasValue)
            {
                var oldest = await _plcRepository.GetOldestLogDateTimeAsync();
                var latest = await _plcRepository.GetLatestLogDateTimeAsync();
                if (!oldest.HasValue || !latest.HasValue)
                {
                    _logger.LogWarning("Call {CallId}: No PLC log data available", callId);
                    return records;
                }
                queryStart ??= oldest.Value;
                queryEnd ??= latest.Value;
            }

            records = await ComputeExecutionRecordsAsync(inTag, outTag, queryStart.Value, queryEnd.Value, maxCycles);

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

    // ===== Private Methods =====

    /// <summary>
    /// CallHeatmapItem 리스트에 색상 클래스를 할당하고 Flow별로 그룹화
    /// </summary>
    private List<FlowHeatmapGroup> AssignColorsAndGroup(List<CallHeatmapItem> items)
    {
        if (items.Count == 0)
            return new List<FlowHeatmapGroup>();

        // 3개 메트릭 각각에 대해 min/max 계산
        var allAvg = items.Select(i => i.AverageGoingTime).ToList();
        var allStdDev = items.Select(i => i.StdDevGoingTime).ToList();
        var allCV = items.Select(i => i.CoefficientOfVariation).ToList();

        var minAvg = allAvg.Min(); var maxAvg = allAvg.Max();
        var minStdDev = allStdDev.Min(); var maxStdDev = allStdDev.Max();
        var minCV = allCV.Min(); var maxCV = allCV.Max();

        // 3개 메트릭 색상 클래스 동시 할당
        foreach (var item in items)
        {
            item.ColorClassAvg = Performance.assignColorClass(HeatmapMetric.AverageTime, item.AverageGoingTime, minAvg, maxAvg);
            item.ColorClassStdDev = Performance.assignColorClass(HeatmapMetric.StdDeviation, item.StdDevGoingTime, minStdDev, maxStdDev);
            item.ColorClassCV = Performance.assignColorClass(HeatmapMetric.CoefficientOfVariation, item.CoefficientOfVariation, minCV, maxCV);
        }

        // Flow별로 그룹화 + Flow 수준 집계 색상
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
            groups.Count, items.Count);

        return groups;
    }

    /// <summary>
    /// InTag/OutTag Rising Edge를 매칭하여 실행 기록 리스트를 생성
    /// </summary>
    private async Task<List<CallExecutionRecord>> ComputeExecutionRecordsAsync(
        string inTag, string outTag, DateTime startTime, DateTime endTime, int? maxCycles)
    {
        var records = new List<CallExecutionRecord>();

        var inTagEdges = await _plcRepository.FindRisingEdgesAsync(inTag, startTime, endTime);
        var outTagEdges = await _plcRepository.FindRisingEdgesAsync(outTag, startTime, endTime);

        // InTag↔OutTag 순서 매칭 → GoingTime 계산
        int outIndex = 0;
        int executionNumber = 0;
        var maxGoingTime = _settingsService.LoadSettings().HistoryView.MaxCallGoingTimeMs;

        foreach (var inTime in inTagEdges)
        {
            while (outIndex < outTagEdges.Count && outTagEdges[outIndex] <= inTime)
            {
                outIndex++;
            }

            if (outIndex >= outTagEdges.Count)
                break;

            var outTime = outTagEdges[outIndex];
            var goingTimeMs = (int)(outTime - inTime).TotalMilliseconds;

            // 비정상적으로 긴 시간 필터링 (설정값 초과 시 제외)
            if (goingTimeMs > 0 && goingTimeMs < maxGoingTime)
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

        // Cycles 모드: 최근 N개만 유지
        if (maxCycles.HasValue && records.Count > maxCycles.Value)
        {
            records = records.Skip(records.Count - maxCycles.Value).ToList();
            // ExecutionNumber 재할당
            for (int i = 0; i < records.Count; i++)
                records[i].ExecutionNumber = i + 1;
        }

        return records;
    }

    /// <summary>
    /// Unspecified Kind의 DateTime을 Local로 지정 (toSqliteUtcString에서 UTC 변환이 정상 동작하도록)
    /// </summary>
    private static DateTime AsLocal(DateTime dt) =>
        dt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dt, DateTimeKind.Local)
            : dt;
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
