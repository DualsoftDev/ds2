namespace DSPilot.Models.Heatmap;

/// <summary>
/// Flow별로 그룹화된 Heatmap 데이터
/// </summary>
public class FlowHeatmapGroup
{
    /// <summary>
    /// Flow 이름
    /// </summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>
    /// 이 Flow에 속한 Call 목록
    /// </summary>
    public List<CallHeatmapItem> Calls { get; set; } = new();

    /// <summary>
    /// Flow 평균 Going 시간 (모든 Call의 평균)
    /// </summary>
    public double FlowAverageTime =>
        Calls.Count > 0 ? Calls.Average(c => c.AverageGoingTime) : 0;

    /// <summary>
    /// Flow 평균 표준편차
    /// </summary>
    public double FlowAverageStdDev =>
        Calls.Count > 0 ? Calls.Average(c => c.StdDevGoingTime) : 0;

    /// <summary>
    /// Flow 평균 성능 점수
    /// </summary>
    public double FlowAverageScore =>
        Calls.Count > 0 ? Calls.Average(c => c.PerformanceScore) : 0;

    /// <summary>
    /// Call 개수
    /// </summary>
    public int CallCount => Calls.Count;

    /// <summary>
    /// UI 확장/축소 상태
    /// </summary>
    public bool IsExpanded { get; set; } = true;
}
