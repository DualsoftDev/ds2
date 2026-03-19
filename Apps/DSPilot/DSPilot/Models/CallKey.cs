namespace DSPilot.Models;

/// <summary>
/// Call을 고유하게 식별하기 위한 복합 키
/// DB 스키마: UNIQUE(CallName, FlowName, WorkName)와 일치
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
    /// Work 이름 (선택적)
    /// </summary>
    public string? WorkName { get; init; }

    /// <summary>
    /// FlowName과 CallName을 사용한 기본 생성자
    /// </summary>
    public CallKey(string flowName, string callName)
    {
        FlowName = flowName;
        CallName = callName;
    }

    /// <summary>
    /// FlowName, CallName, WorkName을 모두 사용한 생성자
    /// </summary>
    public CallKey(string flowName, string callName, string? workName)
    {
        FlowName = flowName;
        CallName = callName;
        WorkName = workName;
    }

    /// <summary>
    /// 문자열 표현 (로깅용)
    /// </summary>
    public override string ToString()
    {
        return WorkName != null
            ? $"{FlowName}/{WorkName}/{CallName}"
            : $"{FlowName}/{CallName}";
    }

    /// <summary>
    /// 해시 키 생성 (Dictionary 키로 사용)
    /// </summary>
    public string ToHashKey()
    {
        return WorkName != null
            ? $"{FlowName}|{WorkName}|{CallName}"
            : $"{FlowName}|{CallName}";
    }
}
