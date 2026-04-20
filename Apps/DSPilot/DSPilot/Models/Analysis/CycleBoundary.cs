namespace DSPilot.Models.Analysis;

/// <summary>
/// 사이클 경계 정보
/// </summary>
public class CycleBoundary
{
    public int CycleNumber { get; set; }            // 사이클 번호 (1 = 최신)
    public string FlowName { get; set; } = "";
    public DateTime StartTime { get; set; }         // 사이클 시작 시간 (Head Call InTag Rising Edge)
    public DateTime? EndTime { get; set; }          // 사이클 종료 시간 (다음 사이클 시작 또는 null)

    public TimeSpan Duration => EndTime.HasValue
        ? EndTime.Value - StartTime
        : DateTime.Now - StartTime;

    public bool IsComplete => EndTime.HasValue;
    public string Status => IsComplete ? "완료" : "진행중";

    /// <summary>
    /// 사이클 요약 정보 (빠른 미리보기용)
    /// </summary>
    public CycleSummary? Summary { get; set; }
}

/// <summary>
/// 사이클 요약 정보
/// </summary>
public class CycleSummary
{
    public int TotalCallCount { get; set; }         // 총 Call 수
    public TimeSpan TotalActiveTime { get; set; }   // 총 동작 시간
    public TimeSpan TotalGapTime { get; set; }      // 총 Gap 시간
    public TimeSpan LongestGap { get; set; }        // 가장 긴 Gap
    public double UtilizationRate { get; set; }     // 가동률 (Active / Total * 100)
}
