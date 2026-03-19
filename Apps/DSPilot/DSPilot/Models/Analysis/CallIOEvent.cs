namespace DSPilot.Models.Analysis;

/// <summary>
/// Call IO 이벤트 (InTag 또는 OutTag의 Rising Edge)
/// </summary>
public class CallIOEvent
{
    /// <summary>
    /// Call Key (FlowName, CallName, WorkName)
    /// </summary>
    public CallKey CallKey { get; set; } = new CallKey("", "", null);

    /// <summary>
    /// Call 이름 (Legacy)
    /// </summary>
    public string CallName
    {
        get => CallKey.CallName;
        set => CallKey = CallKey with { CallName = value };
    }

    /// <summary>
    /// Work 이름 (Legacy)
    /// </summary>
    public string WorkName
    {
        get => CallKey.WorkName ?? "";
        set => CallKey = CallKey with { WorkName = value };
    }

    /// <summary>
    /// Flow 이름 (Legacy)
    /// </summary>
    public string FlowName
    {
        get => CallKey.FlowName;
        set => CallKey = CallKey with { FlowName = value };
    }

    /// <summary>
    /// IO 이벤트 타입 (InTag / OutTag)
    /// </summary>
    public IOEventType EventType { get; set; }

    /// <summary>
    /// InTag인지 여부 (EventType 대신 사용)
    /// </summary>
    public bool IsInTag
    {
        get => EventType == IOEventType.InTag;
        set => EventType = value ? IOEventType.InTag : IOEventType.OutTag;
    }

    /// <summary>
    /// 이벤트 발생 시각 (절대 시간)
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Going 시간 (OutTag일 때만, ms)
    /// </summary>
    public int? GoingTime { get; set; }

    /// <summary>
    /// 사이클 시작 대비 상대 시간 (ms) (Legacy)
    /// </summary>
    public int RelativeTimeMs { get; set; }

    /// <summary>
    /// PLC 태그 이름
    /// </summary>
    public string TagName { get; set; } = string.Empty;

    /// <summary>
    /// PLC 태그 주소
    /// </summary>
    public string TagAddress { get; set; } = string.Empty;
}

/// <summary>
/// IO 이벤트 타입
/// </summary>
public enum IOEventType
{
    /// <summary>
    /// InTag Rising Edge (Call 시작)
    /// </summary>
    InTag,

    /// <summary>
    /// OutTag Rising Edge (Call 종료)
    /// </summary>
    OutTag
}
