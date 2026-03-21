namespace DSPilot.Models.Analysis;

/// <summary>
/// 사이클 데이터 (사이클 경계 + IO 이벤트 목록)
/// </summary>
public class CycleData
{
    /// <summary>
    /// 사이클 경계 정보
    /// </summary>
    public CycleBoundary Cycle { get; set; } = new();

    /// <summary>
    /// 사이클 내 발생한 모든 IO 이벤트 목록 (InTag + OutTag)
    /// </summary>
    public List<CallIOEvent> IOEvents { get; set; } = new();

    /// <summary>
    /// 사이클 내 실행된 고유 Call 개수
    /// </summary>
    public int UniqueCallCount
    {
        get => IOEvents.Select(e => e.CallName).Distinct().Count();
    }

    /// <summary>
    /// 사이클 내 전체 IO 이벤트 개수
    /// </summary>
    public int TotalEventCount => IOEvents.Count;
}
