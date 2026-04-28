namespace DSPilot.Models.Heatmap;

/// <summary>
/// Flow 단위 Heatmap 그룹.
/// </summary>
public class FlowHeatmapGroup
{
    public string FlowName { get; set; } = string.Empty;
    public List<CallHeatmapItem> Calls { get; set; } = new();
    public bool IsExpanded { get; set; } = true;

    public string FlowColorClassAvg { get; set; } = string.Empty;
    public string FlowColorClassStdDev { get; set; } = string.Empty;
    public string FlowColorClassCV { get; set; } = string.Empty;

    public double FlowAverageTime =>
        Calls.Count == 0 ? 0.0 : Calls.Average(c => c.AverageGoingTime);

    public double FlowAverageStdDev =>
        Calls.Count == 0 ? 0.0 : Calls.Average(c => c.StdDevGoingTime);

    public double FlowAverageCV =>
        Calls.Count == 0 ? 0.0 : Calls.Average(c => c.CoefficientOfVariation);

    public int CallCount => Calls.Count;

    public int IssueCount =>
        Calls.Count(c => c.ColorClassCV == "heatmap-poor" || c.ColorClassCV == "heatmap-critical");
}
