namespace DSPilot.Models.Analysis;

/// <summary>
/// 사이클 경계 정보
/// Head Call의 InTag Rising Edge로 구분된 사이클 시작/종료 시점
/// </summary>
public class CycleBoundary
{
    /// <summary>
    /// 사이클 번호 (역순: 최신 = 1, 그 이전 = 2, ...)
    /// </summary>
    public int CycleNumber { get; set; }

    /// <summary>
    /// 사이클 시작 시각 (Head Call InTag Rising Edge)
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// 사이클 종료 시각 (다음 Head Call InTag Rising Edge, null = 미완료)
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// 사이클 완료 여부
    /// </summary>
    public bool IsComplete => EndTime.HasValue;

    /// <summary>
    /// 사이클 타임 (ms)
    /// </summary>
    public int? CycleTimeMs
    {
        get
        {
            if (!IsComplete) return null;
            return (int)(EndTime!.Value - StartTime).TotalMilliseconds;
        }
    }

    /// <summary>
    /// Flow 이름
    /// </summary>
    public string FlowName { get; set; } = string.Empty;
}
