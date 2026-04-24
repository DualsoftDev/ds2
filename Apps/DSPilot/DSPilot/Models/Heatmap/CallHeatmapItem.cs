using Ds2.Core;

namespace DSPilot.Models.Heatmap;

/// <summary>
/// Call Heatmap 셀 데이터.
/// </summary>
public class CallHeatmapItem
{
    public Guid CallId { get; set; }
    public string CallName { get; set; } = string.Empty;
    public string FlowName { get; set; } = string.Empty;
    public string WorkName { get; set; } = string.Empty;
    public double AverageGoingTime { get; set; }
    public double StdDevGoingTime { get; set; }
    public int GoingCount { get; set; }

    public string ColorClassAvg { get; set; } = string.Empty;
    public string ColorClassStdDev { get; set; } = string.Empty;
    public string ColorClassCV { get; set; } = string.Empty;

    public double CoefficientOfVariation =>
        AverageGoingTime > 0.0 ? StdDevGoingTime / AverageGoingTime : 0.0;

    public double GetMetricValue(HeatmapMetric metric)
    {
        if (metric.IsAverageTime) return AverageGoingTime;
        if (metric.IsStdDeviation) return StdDevGoingTime;
        if (metric.IsCoefficientOfVariation) return CoefficientOfVariation;
        return 0.0;
    }

    public string GetColorClass(HeatmapMetric metric)
    {
        if (metric.IsAverageTime) return ColorClassAvg;
        if (metric.IsStdDeviation) return ColorClassStdDev;
        if (metric.IsCoefficientOfVariation) return ColorClassCV;
        return string.Empty;
    }

    public string GetTooltipText()
    {
        var cv = CoefficientOfVariation;
        var cvStatus = cv switch
        {
            < 0.1 => "매우 안정적",
            < 0.2 => "안정적",
            < 0.3 => "보통",
            < 0.5 => "불안정",
            _ => "매우 불안정",
        };
        return
            $"[{CallName}]\n━━━━━━━━━━━━━━━━━━━━\n" +
            $"평균 실행시간: {AverageGoingTime:F0} ms\n" +
            $"표준편차: {StdDevGoingTime:F0} ms\n" +
            $"변동계수: {cv:F2} ({cvStatus})\n" +
            $"실행횟수: {GoingCount}회\n" +
            $"━━━━━━━━━━━━━━━━━━━━\n💡 변동계수가 낮을수록 안정적입니다";
    }
}
