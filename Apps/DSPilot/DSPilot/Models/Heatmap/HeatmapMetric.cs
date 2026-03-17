namespace DSPilot.Models.Heatmap;

/// <summary>
/// Heatmap에서 표시할 수 있는 성능 메트릭 유형
/// </summary>
public enum HeatmapMetric
{
    /// <summary>
    /// 평균 Going 시간 (ms)
    /// </summary>
    AverageTime,

    /// <summary>
    /// 표준편차 (ms) - 변동성 지표
    /// </summary>
    StdDeviation,

    /// <summary>
    /// 변동계수 (CV = StdDev / Average) - 정규화된 변동성
    /// </summary>
    CoefficientOfVariation,

    /// <summary>
    /// 종합 성능 점수 (0-100, 높을수록 좋음)
    /// Average 70% + CV 30% 가중치
    /// </summary>
    PerformanceScore
}
