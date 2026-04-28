using Ds2.Core;

namespace DSPilot.Services;

/// <summary>
/// Heatmap 색상·표시 계산 헬퍼. F# Performance 모듈의 pure C# 포팅.
/// </summary>
public static class HeatmapPerformance
{
    private static double NormalizeValue(double value, double minValue, double maxValue)
        => maxValue > minValue ? (value - minValue) / (maxValue - minValue) : 0.5;

    private static string GetColorClassForTime(double normalized) => normalized switch
    {
        <= 0.2 => "heatmap-excellent",
        <= 0.4 => "heatmap-good",
        <= 0.6 => "heatmap-fair",
        <= 0.8 => "heatmap-poor",
        _ => "heatmap-critical",
    };

    public static string AssignColorClass(HeatmapMetric metric, double value, double minValue, double maxValue)
    {
        var normalized = NormalizeValue(value, minValue, maxValue);
        return GetColorClassForTime(normalized);
    }

    public static string GetMetricDisplayName(HeatmapMetric metric)
    {
        if (metric.IsAverageTime) return "평균 시간 (ms)";
        if (metric.IsStdDeviation) return "표준편차 (ms)";
        if (metric.IsCoefficientOfVariation) return "변동계수 (CV)";
        return string.Empty;
    }

    public static string FormatMetricValue(HeatmapMetric metric, double value)
    {
        if (metric.IsAverageTime) return value.ToString("F0");
        if (metric.IsStdDeviation) return value.ToString("F0");
        if (metric.IsCoefficientOfVariation) return value.ToString("F2");
        return value.ToString("F1");
    }
}
