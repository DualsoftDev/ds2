namespace DSPilot.Models.Dsp;

/// <summary>
/// PLC 태그의 엣지 감지 상태
/// </summary>
public class TagEdgeState
{
    /// <summary>
    /// 태그 이름
    /// </summary>
    public string TagName { get; set; } = string.Empty;

    /// <summary>
    /// 이전 값
    /// </summary>
    public string PreviousValue { get; set; } = "0";

    /// <summary>
    /// 현재 값
    /// </summary>
    public string CurrentValue { get; set; } = "0";

    /// <summary>
    /// 마지막 업데이트 시간
    /// </summary>
    public DateTime LastUpdateTime { get; set; }

    /// <summary>
    /// 라이징 엣지 감지 (0 → 1)
    /// </summary>
    public bool IsRisingEdge()
    {
        return IsLow(PreviousValue) && IsHigh(CurrentValue);
    }

    /// <summary>
    /// 폴링 엣지 감지 (1 → 0)
    /// </summary>
    public bool IsFallingEdge()
    {
        return IsHigh(PreviousValue) && IsLow(CurrentValue);
    }

    /// <summary>
    /// High 값 판정 (1, TRUE, true)
    /// </summary>
    private bool IsHigh(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        return value == "1" || value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Low 값 판정 (0, FALSE, false)
    /// </summary>
    private bool IsLow(string? value)
    {
        if (string.IsNullOrEmpty(value)) return true;
        return value == "0" || value.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString()
    {
        var edge = IsRisingEdge() ? "Rising" : IsFallingEdge() ? "Falling" : "None";
        return $"{TagName}: {PreviousValue} → {CurrentValue} ({edge})";
    }
}
