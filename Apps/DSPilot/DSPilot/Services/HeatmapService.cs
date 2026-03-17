using DSPilot.Models.Heatmap;
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// Heatmap 데이터 처리 및 색상 코드 할당 서비스
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

            // 2. CallHeatmapItem으로 변환
            var items = statistics.Select(s => new CallHeatmapItem
            {
                CallName = s.CallName,
                FlowName = s.FlowName,
                WorkName = s.WorkName,
                AverageGoingTime = s.AverageGoingTime,
                StdDevGoingTime = s.StdDevGoingTime,
                GoingCount = s.GoingCount
            }).ToList();

            // 3. 성능 점수 계산
            CalculatePerformanceScores(items);

            // 4. 색상 클래스 할당
            AssignColorClasses(items, selectedMetric);

            // 5. Flow별로 그룹화
            var groups = items
                .GroupBy(item => item.FlowName)
                .Select(g => new FlowHeatmapGroup
                {
                    FlowName = g.Key,
                    Calls = g.OrderBy(c => c.CallName).ToList(),
                    IsExpanded = true
                })
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
    /// 성능 점수 계산 (0-100, 높을수록 좋음)
    /// 가중치: 평균 시간 70%, 변동계수 30%
    /// </summary>
    private void CalculatePerformanceScores(List<CallHeatmapItem> items)
    {
        if (items.Count == 0) return;

        // 정규화를 위한 최소/최대값 계산
        var minAvg = items.Min(i => i.AverageGoingTime);
        var maxAvg = items.Max(i => i.AverageGoingTime);
        var minCV = items.Min(i => i.CoefficientOfVariation);
        var maxCV = items.Max(i => i.CoefficientOfVariation);

        foreach (var item in items)
        {
            // 평균 시간 점수 (낮을수록 좋음 → 역정규화)
            var avgScore = maxAvg > minAvg
                ? (1 - (item.AverageGoingTime - minAvg) / (maxAvg - minAvg)) * 100
                : 100;

            // 변동계수 점수 (낮을수록 좋음 → 역정규화)
            var cvScore = maxCV > minCV
                ? (1 - (item.CoefficientOfVariation - minCV) / (maxCV - minCV)) * 100
                : 100;

            // 가중 평균 (평균 시간 70%, 변동계수 30%)
            item.PerformanceScore = avgScore * 0.7 + cvScore * 0.3;
        }
    }

    /// <summary>
    /// 선택된 메트릭에 따라 색상 클래스 할당
    /// </summary>
    private void AssignColorClasses(List<CallHeatmapItem> items, HeatmapMetric metric)
    {
        if (items.Count == 0) return;

        // 메트릭 값 추출
        var values = items.Select(i => i.GetMetricValue(metric)).ToList();
        var minValue = values.Min();
        var maxValue = values.Max();

        foreach (var item in items)
        {
            var value = item.GetMetricValue(metric);

            // 정규화 (0-1 범위)
            var normalized = maxValue > minValue
                ? (value - minValue) / (maxValue - minValue)
                : 0.5;

            // 성능 점수는 높을수록 좋음 (녹색)
            // 나머지 메트릭은 낮을수록 좋음 (녹색)
            if (metric == HeatmapMetric.PerformanceScore)
            {
                item.ColorClass = GetColorClassForPerformance(normalized);
            }
            else
            {
                item.ColorClass = GetColorClassForTime(normalized);
            }
        }
    }

    /// <summary>
    /// 성능 점수용 색상 클래스 (높을수록 녹색)
    /// </summary>
    private string GetColorClassForPerformance(double normalized)
    {
        return normalized switch
        {
            >= 0.8 => "heatmap-excellent",   // 80-100: 진한 녹색
            >= 0.6 => "heatmap-good",        // 60-80: 연한 녹색
            >= 0.4 => "heatmap-fair",        // 40-60: 노란색
            >= 0.2 => "heatmap-poor",        // 20-40: 주황색
            _ => "heatmap-critical"          // 0-20: 빨간색
        };
    }

    /// <summary>
    /// 시간/변동성 메트릭용 색상 클래스 (낮을수록 녹색)
    /// </summary>
    private string GetColorClassForTime(double normalized)
    {
        return normalized switch
        {
            <= 0.2 => "heatmap-excellent",   // 0-20: 진한 녹색 (빠름/안정적)
            <= 0.4 => "heatmap-good",        // 20-40: 연한 녹색
            <= 0.6 => "heatmap-fair",        // 40-60: 노란색
            <= 0.8 => "heatmap-poor",        // 60-80: 주황색
            _ => "heatmap-critical"          // 80-100: 빨간색 (느림/불안정)
        };
    }

    /// <summary>
    /// 메트릭 표시 이름 반환
    /// </summary>
    public static string GetMetricDisplayName(HeatmapMetric metric) => metric switch
    {
        HeatmapMetric.AverageTime => "평균 시간 (ms)",
        HeatmapMetric.StdDeviation => "표준편차 (ms)",
        HeatmapMetric.CoefficientOfVariation => "변동계수 (CV)",
        HeatmapMetric.PerformanceScore => "성능 점수",
        _ => "Unknown"
    };

    /// <summary>
    /// 메트릭 값 포맷 (소수점 처리)
    /// </summary>
    public static string FormatMetricValue(HeatmapMetric metric, double value) => metric switch
    {
        HeatmapMetric.AverageTime => $"{value:F0}",
        HeatmapMetric.StdDeviation => $"{value:F0}",
        HeatmapMetric.CoefficientOfVariation => $"{value:F2}",
        HeatmapMetric.PerformanceScore => $"{value:F0}",
        _ => value.ToString()
    };
}
