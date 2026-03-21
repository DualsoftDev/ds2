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
    private readonly ILogger<HeatmapService> _logger;

    public HeatmapService(
        IDspRepository dspRepository,
        ILogger<HeatmapService> logger)
    {
        _dspRepository = dspRepository;
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
