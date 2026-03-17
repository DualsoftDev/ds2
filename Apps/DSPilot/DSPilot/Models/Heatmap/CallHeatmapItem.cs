namespace DSPilot.Models.Heatmap;

/// <summary>
/// Heatmap에 표시될 개별 Call 통계 정보
/// </summary>
public class CallHeatmapItem
{
    /// <summary>
    /// Call 이름
    /// </summary>
    public string CallName { get; set; } = string.Empty;

    /// <summary>
    /// Flow 이름
    /// </summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>
    /// Work 이름
    /// </summary>
    public string WorkName { get; set; } = string.Empty;

    /// <summary>
    /// 평균 Going 시간 (ms)
    /// </summary>
    public double AverageGoingTime { get; set; }

    /// <summary>
    /// 표준편차 (ms)
    /// </summary>
    public double StdDevGoingTime { get; set; }

    /// <summary>
    /// Going 횟수 (샘플 개수)
    /// </summary>
    public int GoingCount { get; set; }

    /// <summary>
    /// 변동계수 (CV = StdDev / Average)
    /// </summary>
    public double CoefficientOfVariation =>
        AverageGoingTime > 0 ? StdDevGoingTime / AverageGoingTime : 0;

    /// <summary>
    /// 종합 성능 점수 (0-100)
    /// 평균이 낮고 변동이 적을수록 높은 점수
    /// </summary>
    public double PerformanceScore { get; set; }

    /// <summary>
    /// 현재 선택된 메트릭에 따른 표시 값
    /// </summary>
    public double GetMetricValue(HeatmapMetric metric) => metric switch
    {
        HeatmapMetric.AverageTime => AverageGoingTime,
        HeatmapMetric.StdDeviation => StdDevGoingTime,
        HeatmapMetric.CoefficientOfVariation => CoefficientOfVariation,
        HeatmapMetric.PerformanceScore => PerformanceScore,
        _ => 0
    };

    /// <summary>
    /// 색상 코드 (CSS 클래스)
    /// </summary>
    public string ColorClass { get; set; } = string.Empty;

    /// <summary>
    /// 툴팁 표시 텍스트
    /// </summary>
    public string GetTooltipText() =>
        $"{CallName}\n" +
        $"평균: {AverageGoingTime:F0}ms\n" +
        $"표준편차: {StdDevGoingTime:F0}ms\n" +
        $"변동계수: {CoefficientOfVariation:F2}\n" +
        $"성능점수: {PerformanceScore:F0}\n" +
        $"실행횟수: {GoingCount}";
}
