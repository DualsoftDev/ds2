namespace DSPilot.Models;

/// <summary>
/// Call을 고유하게 식별하기 위한 복합 키
/// FlowName과 CallName만으로 고유 키를 구성 (WorkName 불필요)
/// </summary>
public record CallKey
{
    /// <summary>
    /// Flow 이름
    /// </summary>
    public string FlowName { get; init; } = string.Empty;

    /// <summary>
    /// Call 이름
    /// </summary>
    public string CallName { get; init; } = string.Empty;

    /// <summary>
    /// 생성자
    /// </summary>
    public CallKey(string flowName, string callName)
    {
        FlowName = flowName;
        CallName = callName;
    }

    /// <summary>
    /// 문자열 표현 (로깅용)
    /// </summary>
    public override string ToString()
    {
        return $"{FlowName}/{CallName}";
    }

    /// <summary>
    /// 해시 키 생성 (Dictionary 키로 사용)
    /// </summary>
    public string ToHashKey()
    {
        return $"{FlowName}|{CallName}";
    }
}
