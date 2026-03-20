using DSPilot.Engine;
using static DSPilot.Engine.Models;
using DSPilot.Repositories;
using Microsoft.FSharp.Collections;

namespace DSPilot.Services;

/// <summary>
/// Heatmap 데이터 처리 서비스 (F# Performance 및 Models 모듈 사용)
/// 매트릭스 히트맵: 4개 메트릭 모두에 대해 색상 클래스를 동시 할당
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
    /// 4개 메트릭 모두에 대해 색상 클래스를 할당
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

            // 2. 중간 아이템 리스트 (성능점수 계산 전)
            var intermediateItems = statistics.Select(s => new CallHeatmapItem(
                s.CallName,
                s.FlowName,
                s.WorkName,
                s.AverageGoingTime,
                s.StdDevGoingTime,
                s.GoingCount,
                0.0, // PerformanceScore - 아래에서 계산
                "", "", "", "" // 색상 클래스 - 아래에서 할당
            )).ToList();

            // 3. 성능 점수 계산 (F# Performance 모듈)
            CalculatePerformanceScores(intermediateItems);

            // 4. 4개 메트릭 각각에 대해 min/max 계산
            var allAvg = intermediateItems.Select(i => i.AverageGoingTime).ToList();
            var allStdDev = intermediateItems.Select(i => i.StdDevGoingTime).ToList();
            var allCV = intermediateItems.Select(i => i.CoefficientOfVariation).ToList();
            var allScore = intermediateItems.Select(i => i.PerformanceScore).ToList();

            var minAvg = allAvg.Min(); var maxAvg = allAvg.Max();
            var minStdDev = allStdDev.Min(); var maxStdDev = allStdDev.Max();
            var minCV = allCV.Min(); var maxCV = allCV.Max();
            var minScore = allScore.Min(); var maxScore = allScore.Max();

            // 5. 4개 메트릭 색상 클래스 동시 할당
            var items = intermediateItems.Select(i => new CallHeatmapItem(
                i.CallName,
                i.FlowName,
                i.WorkName,
                i.AverageGoingTime,
                i.StdDevGoingTime,
                i.GoingCount,
                i.PerformanceScore,
                Performance.assignColorClass(HeatmapMetric.AverageTime, i.AverageGoingTime, minAvg, maxAvg),
                Performance.assignColorClass(HeatmapMetric.StdDeviation, i.StdDevGoingTime, minStdDev, maxStdDev),
                Performance.assignColorClass(HeatmapMetric.CoefficientOfVariation, i.CoefficientOfVariation, minCV, maxCV),
                Performance.assignColorClass(HeatmapMetric.PerformanceScore, i.PerformanceScore, minScore, maxScore)
            )).ToList();

            // 6. Flow별로 그룹화 + Flow 수준 집계 색상
            var groups = items
                .GroupBy(item => item.FlowName)
                .Select(g =>
                {
                    var calls = g.OrderBy(c => c.CallName).ToList();
                    var flowAvg = calls.Average(c => c.AverageGoingTime);
                    var flowStdDev = calls.Average(c => c.StdDevGoingTime);
                    var flowCV = calls.Average(c => c.CoefficientOfVariation);
                    var flowScore = calls.Average(c => c.PerformanceScore);

                    return new FlowHeatmapGroup(
                        g.Key,
                        ListModule.OfSeq(calls),
                        true,
                        Performance.assignColorClass(HeatmapMetric.AverageTime, flowAvg, minAvg, maxAvg),
                        Performance.assignColorClass(HeatmapMetric.StdDeviation, flowStdDev, minStdDev, maxStdDev),
                        Performance.assignColorClass(HeatmapMetric.CoefficientOfVariation, flowCV, minCV, maxCV),
                        Performance.assignColorClass(HeatmapMetric.PerformanceScore, flowScore, minScore, maxScore)
                    );
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
    /// 성능 점수 계산 (F# Performance 모듈 사용)
    /// </summary>
    private void CalculatePerformanceScores(List<CallHeatmapItem> items)
    {
        if (items.Count == 0) return;

        var data = items.Select(i => Tuple.Create(i.AverageGoingTime, i.CoefficientOfVariation)).ToList();
        var fsharpList = ListModule.OfSeq(data);

        var scores = Performance.calculatePerformanceScores(fsharpList);
        var scoreList = ListModule.ToArray(scores);

        for (int i = 0; i < items.Count; i++)
        {
            items[i] = new CallHeatmapItem(
                items[i].CallName,
                items[i].FlowName,
                items[i].WorkName,
                items[i].AverageGoingTime,
                items[i].StdDevGoingTime,
                items[i].GoingCount,
                scoreList[i],
                items[i].ColorClassAvg,
                items[i].ColorClassStdDev,
                items[i].ColorClassCV,
                items[i].ColorClassScore
            );
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
        if (metric.IsPerformanceScore)
            return $"{value:F0}";
        return $"{value:F1}";
    }
}
