namespace DSPilot.Models.Dsp;

/// <summary>
/// Flow 히스토리 엔티티 - dsp.db의 dspFlowHistory 테이블
/// 각 사이클 완료 시 MT, WT, CT 값을 기록
/// </summary>
public class DspFlowHistoryEntity
{
    /// <summary>
    /// History ID (자동 증가)
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Flow 이름
    /// </summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>
    /// Machine Time (ms) - 실제 작업 시간
    /// </summary>
    public int? MT { get; set; }

    /// <summary>
    /// Wait Time (ms) - 대기 시간
    /// </summary>
    public int? WT { get; set; }

    /// <summary>
    /// Cycle Time (ms) - MT + WT
    /// </summary>
    public int? CT { get; set; }

    /// <summary>
    /// 사이클 번호
    /// </summary>
    public int? CycleNo { get; set; }

    /// <summary>
    /// 기록 시간
    /// </summary>
    public DateTime RecordedAt { get; set; }

    /// <summary>
    /// 유휴 사이클 여부 (CT > MaxCycleTimeMs)
    /// </summary>
    public bool IsIdle { get; set; } = false;
}
