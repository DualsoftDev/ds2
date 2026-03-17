using DSPilot.Engine;
using static DSPilot.Engine.Models;
using DSPilot.Repositories;
using Microsoft.FSharp.Collections;

namespace DSPilot.Services;

/// <summary>
/// Heatmap 데이터 처리 서비스 (F# Performance 및 Models 모듈 사용)
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
    /// Flow별로 그룹화된 Heatmap 데이터 조회
    /// </summary>
    public async Task<List<FlowHeatmapGroup>> GetHeatmapDataAsync(HeatmapMetric selectedMetric)
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

            // 2. CallHeatmapItem으로 변환 (F# record)
            var items = statistics.Select(s => new CallHeatmapItem(
                s.CallName,
                s.FlowName,
                s.WorkName,
                s.AverageGoingTime,
                s.StdDevGoingTime,
                s.GoingCount,
                0.0, // PerformanceScore will be calculated
                ""   // ColorClass will be assigned
            )).ToList();

            // 3. 성능 점수 계산
            CalculatePerformanceScores(items);

            // 4. 색상 클래스 할당
            AssignColorClasses(items, selectedMetric);

            // 5. Flow별로 그룹화
            var groups = items
                .GroupBy(item => item.FlowName)
                .Select(g => new FlowHeatmapGroup(
                    g.Key,
                    ListModule.OfSeq(g.OrderBy(c => c.CallName)),
                    true
                ))
                .OrderBy(g => g.FlowName)
                .ToList();

            _logger.LogInformation(
                "Heatmap data loaded: {FlowCount} flows, {CallCount} calls",
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

        // F# 함수 호출을 위한 데이터 변환 (C# value tuple → F# Tuple)
        var data = items.Select(i => Tuple.Create(i.AverageGoingTime, i.CoefficientOfVariation)).ToList();
        var fsharpList = ListModule.OfSeq(data);

        var scores = Performance.calculatePerformanceScores(fsharpList);
        var scoreList = ListModule.ToArray(scores);

        // F# record는 불변이므로 새 인스턴스 생성
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
                items[i].ColorClass
            );
        }
    }

    /// <summary>
    /// 선택된 메트릭에 따라 색상 클래스 할당 (F# Performance 모듈 사용)
    /// </summary>
    private void AssignColorClasses(List<CallHeatmapItem> items, HeatmapMetric metric)
    {
        if (items.Count == 0) return;

        var values = items.Select(i => i.GetMetricValue(metric)).ToList();
        var minValue = values.Min();
        var maxValue = values.Max();

        // F# record는 불변이므로 새 인스턴스 생성
        for (int i = 0; i < items.Count; i++)
        {
            var value = items[i].GetMetricValue(metric);
            var colorClass = Performance.assignColorClass(metric, value, minValue, maxValue);

            items[i] = new CallHeatmapItem(
                items[i].CallName,
                items[i].FlowName,
                items[i].WorkName,
                items[i].AverageGoingTime,
                items[i].StdDevGoingTime,
                items[i].GoingCount,
                items[i].PerformanceScore,
                colorClass
            );
        }
    }

    /// <summary>
    /// 메트릭 표시 이름 반환 (F# Performance 모듈 사용)
    /// </summary>
    public static string GetMetricDisplayName(HeatmapMetric metric) =>
        Performance.getMetricDisplayName(metric);

    /// <summary>
    /// 메트릭 값 포맷 (F# Performance 모듈 사용)
    /// </summary>
    public static string FormatMetricValue(HeatmapMetric metric, double value) =>
        Performance.formatMetricValue(metric, value);
}
