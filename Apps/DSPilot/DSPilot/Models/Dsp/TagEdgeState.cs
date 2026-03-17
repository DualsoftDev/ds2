using DSPilot.Engine;

namespace DSPilot.Models.Dsp;

/// <summary>
/// PLC 태그의 엣지 감지 상태 (F# EdgeType 사용)
/// </summary>
public class TagEdgeState
{
    public string TagName { get; set; } = string.Empty;
    public string PreviousValue { get; set; } = "0";
    public string CurrentValue { get; set; } = "0";
    public DateTime LastUpdateTime { get; set; }
    public EdgeType EdgeType { get; set; } = EdgeType.NoChange;

    /// <summary>
    /// 라이징 엣지 여부 (F# EdgeDetection 사용)
    /// </summary>
    public bool IsRisingEdge() => EdgeDetection.isRising(EdgeType);

    /// <summary>
    /// 폴링 엣지 여부 (F# EdgeDetection 사용)
    /// </summary>
    public bool IsFallingEdge() => EdgeDetection.isFalling(EdgeType);

    public override string ToString()
    {
        string edgeStr;
        if (EdgeDetection.isRising(EdgeType))
            edgeStr = "Rising";
        else if (EdgeDetection.isFalling(EdgeType))
            edgeStr = "Falling";
        else
            edgeStr = "None";

        return $"{TagName}: {PreviousValue} → {CurrentValue} ({edgeStr})";
    }
}
